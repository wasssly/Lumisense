using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace AudioPlayer;

public partial class SettingsWindow : FluentWindow
{
    private enum HotkeyTarget { None, PlayPause, Next, Previous, Stop, VolumeUp, VolumeDown, Mute, Shuffle, Repeat, DeleteTrack, SeekForward, SeekBackward }

    private readonly AppSettings _settings;
    private readonly MainWindow _owner;
    private bool _isInitializing = true;

    // См. LoadDeveloperAvatar — держит BitmapImage живым на время асинхронной загрузки, чтобы
    // его не собрал GC до того, как скачивание завершится.
    private BitmapImage? _developerAvatarBitmap;

    // Пока не None — окно "слушает" следующее нажатие клавиш и запишет его как новую комбинацию
    private HotkeyTarget _recordingTarget = HotkeyTarget.None;

    // ---------- Поиск настроек ----------
    // Индекс не читает разметку, а просто перечисляет каждую настраиваемую опцию вручную:
    // подпись, к какой странице она относится, ссылку на сам элемент управления (чтобы потом
    // прокрутить к нему и подсветить) и ключевые слова для поиска.
    private sealed record SettingsSearchEntry(string Label, string PageTitle, string PageKey, string Keywords, FrameworkElement Target);

    private readonly List<SettingsSearchEntry> _searchIndex = new();
    private readonly ObservableCollection<SettingsSearchEntry> _searchResults = new();

    // Переключает страницу настроек по строковому ключу — используется и при первом открытии
    // окна (initialPage в конструкторе), и когда окно настроек открывают повторно, пока оно
    // уже висит открытым на какой-то другой странице (см. MainWindow.ShowSettingsWindow) —
    // например, кнопка "Настройки" в контекстном меню мини-плеера должна вести на страницу
    // "Мини-плеер", даже если окно настроек уже было открыто на другой вкладке.
    public void NavigateToPage(string? pageKey)
    {
        (pageKey switch
        {
            "About" => NavAbout,
            "Updates" => NavUpdates,
            "Window" => NavWindow,
            "Playback" => NavPlayback,
            "Notifications" => NavNotifications,
            "Equalizer" => NavEqualizer,
            "MiniPlayer" => NavMiniPlayer,
            "Hotkeys" => NavHotkeys,
            "Profile" => NavProfile,
            _ => NavAppearance
        }).IsChecked = true;
    }

    // То же самое, что и MainWindow.ApplyWindowBackdrop — этому окну нужна собственная копия,
    // а не вызов чужого метода, потому что применяется к его СОБСТВЕННОМУ HWND, а не к HWND
    // главного окна.
    private void ApplyWindowBackdrop(AppSettings settings)
    {
        WindowBackdropType = settings.WindowBackdropType == "Acrylic"
            ? Wpf.Ui.Controls.WindowBackdropType.Acrylic
            : Wpf.Ui.Controls.WindowBackdropType.Mica;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyWindowBackdrop(_settings);
    }

    public SettingsWindow(AppSettings settings, MainWindow owner, string? initialPage = null)
    {
        InitializeComponent();

        ApplyWindowBackdrop(settings);

        // Выбираем стартовую страницу здесь, а не через IsChecked="True" в XAML — на этот
        // момент все страницы уже гарантированно созданы, обработчик NavItem_Checked
        // отработает без NullReferenceException.
        NavigateToPage(initialPage);

        _settings = settings;
        _owner = owner;

        // WPF-свойство Owner намеренно НЕ выставляется: Windows не даёт окну-владельцу
        // оказаться в z-порядке выше своего owned-окна, пока то открыто (это на уровне
        // диспетчера окон, обойти нельзя) — клик по перекрытому главному окну не мог поднять
        // его поверх настроек. Без Owner оба окна независимы, обычное поведение Windows
        // работает само. Позиционирование при первом открытии — RestoreOrCenterPosition ниже;
        // закрытие вместе с главным окном — явный вызов в MainWindow.OnClosed; ShowInTaskbar
        // ниже — своя иконка на панели задач вместо ShowInTaskbar = false, что раньше стояло
        // здесь.
        ShowInTaskbar = true;
        RestoreOrCenterPosition(owner);

        ThemeLightRadio.IsChecked = _settings.Theme == "Light";
        ThemeDarkRadio.IsChecked = !ThemeLightRadio.IsChecked.GetValueOrDefault();

        AccentManualRadio.IsChecked = _settings.AccentColorMode == "Manual";
        AccentSystemRadio.IsChecked = !AccentManualRadio.IsChecked.GetValueOrDefault();
        AccentSwatchesPanel.Visibility = AccentManualRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RefreshAccentSwatchSelection();

        BackdropAcrylicRadio.IsChecked = _settings.WindowBackdropType == "Acrylic";
        BackdropMicaRadio.IsChecked = !BackdropAcrylicRadio.IsChecked.GetValueOrDefault();

        AlwaysOnTopCheckBox.IsChecked = _settings.AlwaysOnTop;
        RememberVolumeCheckBox.IsChecked = _settings.RememberVolume;
        LogarithmicVolumeCheckBox.IsChecked = _settings.UseLogarithmicVolume;
        TrackChangeToastCheckBox.IsChecked = _settings.ShowTrackChangeToast;
        InitializeToastPositionAndSize();
        InitializeToastMonitorCombo();
        MinimizeToTrayCheckBox.IsChecked = _settings.MinimizeToTrayOnClose;

        // Источник истины для автозапуска — сам реестр (см. StartupManager), а не settings.json —
        // чекбокс всегда показывает то, что реально настроено, а не могло устареть.
        LaunchOnStartupCheckBox.IsChecked = StartupManager.IsEnabled();
        StartHiddenInTrayCheckBox.IsChecked = _settings.StartHiddenInTray;

        MiniOpacitySlider.Value = _settings.MiniPlayerOpacity;
        MiniOpacityValueText.Text = $"{(int)Math.Round(_settings.MiniPlayerOpacity * 100)}%";
        MiniAlwaysOnTopCheckBox.IsChecked = _settings.MiniPlayerAlwaysOnTop;
        MiniPinnedCheckBox.IsChecked = _settings.MiniPlayerPinned;
        MiniSnapToEdgesCheckBox.IsChecked = _settings.MiniPlayerSnapToEdges;
        MiniSecondaryShuffleRadio.IsChecked = _settings.MiniPlayerSecondaryButton == "Shuffle";
        MiniSecondaryFavoriteRadio.IsChecked = _settings.MiniPlayerSecondaryButton == "Favorite";
        MiniSecondaryRepeatRadio.IsChecked = !MiniSecondaryShuffleRadio.IsChecked.GetValueOrDefault()
                                              && !MiniSecondaryFavoriteRadio.IsChecked.GetValueOrDefault();
        MiniButtonsOverlayRadio.IsChecked = _settings.MiniPlayerButtonsLayout == "Overlay";
        MiniButtonsBelowRadio.IsChecked = !MiniButtonsOverlayRadio.IsChecked.GetValueOrDefault();
        MiniShowProgressCheckBox.IsChecked = _settings.MiniPlayerShowProgress;
        MiniInfoOnlyTitleRadio.IsChecked = _settings.MiniPlayerInfoMode == "TitleOnly";
        MiniInfoRemainingRadio.IsChecked = _settings.MiniPlayerInfoMode == "TitleRemaining";
        MiniInfoArtistRadio.IsChecked = !MiniInfoOnlyTitleRadio.IsChecked.GetValueOrDefault()
                                         && !MiniInfoRemainingRadio.IsChecked.GetValueOrDefault();

        ImprovedShuffleCheckBox.IsChecked = _settings.UseImprovedShuffle;
        ProgressBarWaveformRadio.IsChecked = _settings.ProgressBarStyle == "Waveform";
        ProgressBarSliderRadio.IsChecked = !ProgressBarWaveformRadio.IsChecked.GetValueOrDefault();
        ReplayGainCheckBox.IsChecked = _settings.ReplayGainEnabled;
        AlbumArtTransitionOnRadio.IsChecked = _owner.IsAlbumArtTransitionEnabled;
        AlbumArtTransitionOffRadio.IsChecked = !_owner.IsAlbumArtTransitionEnabled;

        EqualizerEnabledCheckBox.IsChecked = _owner.IsEqualizerEnabled;
        for (int band = 0; band < EqualizerSampleProvider.BandFrequencies.Length; band++)
        {
            double gain = _owner.GetEqualizerBandGain(band);
            GetEqBandSlider(band).Value = gain;
            GetEqBandValueText(band).Text = FormatEqGain(gain);
        }
        RefreshEqualizerPresetsList();

        (_settings.UpdateDownloadSource switch
        {
            "GhProxy" => UpdateSourceGhProxyRadio,
            "GhProxyV4" => UpdateSourceGhProxyV4Radio,
            "GhProxyV6" => UpdateSourceGhProxyV6Radio,
            "GhProxyCdn" => UpdateSourceGhProxyCdnRadio,
            _ => UpdateSourceGitHubRadio
        }).IsChecked = true;

        RefreshViewModeRadios();

        RefreshHotkeyButtonText(HotkeyTarget.PlayPause);
        RefreshHotkeyButtonText(HotkeyTarget.Next);
        RefreshHotkeyButtonText(HotkeyTarget.Previous);
        RefreshHotkeyButtonText(HotkeyTarget.Stop);
        RefreshHotkeyButtonText(HotkeyTarget.VolumeUp);
        RefreshHotkeyButtonText(HotkeyTarget.VolumeDown);
        RefreshHotkeyButtonText(HotkeyTarget.Mute);
        RefreshHotkeyButtonText(HotkeyTarget.Shuffle);
        RefreshHotkeyButtonText(HotkeyTarget.Repeat);
        RefreshHotkeyButtonText(HotkeyTarget.DeleteTrack);
        RefreshHotkeyButtonText(HotkeyTarget.SeekForward);
        RefreshHotkeyButtonText(HotkeyTarget.SeekBackward);

        SearchResultsList.ItemsSource = _searchResults;
        BuildSearchIndex();

        RefreshAppVersionText();
        LoadDeveloperAvatar();

        _isInitializing = false;
    }

    // WindowStartupLocation="CenterOwner" не подходит — Owner не выставляется (см. начало
    // конструктора), поэтому центрируем вручную. Если пользователь уже передвигал окно сам —
    // открываем на том же месте (AppSettings.SettingsWindowLeft/Top) вместо центрирования; см.
    // также ShowInTaskbar в конструкторе — вместе с этим чинит случай, когда окно оказывалось
    // унесённым за пределы экрана (отключённый монитор) и было невозможно вернуть.
    private void RestoreOrCenterPosition(Window owner)
    {
        if (_settings.SettingsWindowLeft is double savedLeft && _settings.SettingsWindowTop is double savedTop
            && IsPositionOnAnyScreen(savedLeft, savedTop))
        {
            SetPositionProgrammatically(savedLeft, savedTop);
            return;
        }

        double ownerWidth = owner.ActualWidth > 0 ? owner.ActualWidth : owner.Width;
        double ownerHeight = owner.ActualHeight > 0 ? owner.ActualHeight : owner.Height;

        double left = owner.Left + (ownerWidth - Width) / 2;
        double top = owner.Top + (ownerHeight - Height) / 2;

        var ownerBounds = new System.Drawing.Rectangle(
            (int)owner.Left, (int)owner.Top,
            (int)Math.Max(ownerWidth, 1), (int)Math.Max(ownerHeight, 1));
        var workArea = System.Windows.Forms.Screen.FromRectangle(ownerBounds).WorkingArea;

        left = Math.Clamp(left, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
        top = Math.Clamp(top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));

        SetPositionProgrammatically(left, top);
    }

    // Сохранённая позиция может больше не попадать ни на один подключённый монитор (например,
    // если её запомнили на мониторе, который с тех пор отключили) — проверяем пересечение с
    // рабочей областью любого из них, а не просто "Screen.FromRectangle нашёл ближайший".
    private bool IsPositionOnAnyScreen(double left, double top)
    {
        var bounds = new System.Drawing.Rectangle((int)left, (int)top,
            (int)Math.Max(Width, 1), (int)Math.Max(Height, 1));
        return System.Windows.Forms.Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(bounds));
    }

    // Признак того, что Left/Top сейчас правятся кодом (центрирование/восстановление), а не
    // пользователем — см. OnLocationChanged ниже: запоминать в настройки нужно только реальное
    // перетаскивание окна пользователем, а не эти программные перестановки при каждом открытии.
    private bool _isApplyingProgrammaticPosition;

    private void SetPositionProgrammatically(double left, double top)
    {
        _isApplyingProgrammaticPosition = true;
        try
        {
            Left = left;
            Top = top;
        }
        finally
        {
            _isApplyingProgrammaticPosition = false;
        }
    }

    // Запоминаем позицию в AppSettings при любом перемещении окна пользователем (перетаскивание
    // за заголовок) — как и MiniPlayerLeft/Top у мини-плеера, само значение пишется только в
    // память; на диск оно попадёт вместе со всеми остальными настройками при следующем
    // SettingsManager.Save (в частности — гарантированно при закрытии приложения, см.
    // MainWindow.PersistPlaybackAndPlaylistState).
    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (_isApplyingProgrammaticPosition) return;
        if (_isInitializing) return;

        _settings.SettingsWindowLeft = Left;
        _settings.SettingsWindowTop = Top;
    }

    // Вызывается извне (из контекстного меню мини-плеера), когда закрепление, "поверх окон"
    // или прозрачность переключили не через это окно, а прямо на мини-плеере. Флаг
    // _isInitializing глушит Changed-обработчики чекбоксов/слайдера, чтобы не вызвать
    // повторное, уже ненужное применение настройки и не уйти в цикл обновлений.
    public void RefreshMiniPlayerToggles()
    {
        _isInitializing = true;
        MiniAlwaysOnTopCheckBox.IsChecked = _settings.MiniPlayerAlwaysOnTop;
        MiniPinnedCheckBox.IsChecked = _settings.MiniPlayerPinned;
        MiniSnapToEdgesCheckBox.IsChecked = _settings.MiniPlayerSnapToEdges;
        MiniOpacitySlider.Value = _settings.MiniPlayerOpacity;
        MiniOpacityValueText.Text = $"{(int)Math.Round(_settings.MiniPlayerOpacity * 100)}%";
        _isInitializing = false;
    }

    // Ставит галочку на миниатюре, соответствующей текущему виду плеера — вызывается и при
    // открытии окна настроек, и извне (из MainWindow), когда вид сменили другим способом:
    // контекстным меню по заголовку или кнопкой мини-плеера, — чтобы страница настроек не
    // "отставала" от реального состояния, если уже открыта.
    public void RefreshViewModeRadios()
    {
        _isInitializing = true;
        switch (_owner.CurrentViewModeName)
        {
            case "Square": ViewModeSquareRadio.IsChecked = true; break;
            case "Rectangular": ViewModeRectangularRadio.IsChecked = true; break;
            case "Mini": ViewModeMiniRadio.IsChecked = true; break;
        }
        _isInitializing = false;
    }

    private void PlayerViewModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        if (sender is not System.Windows.Controls.RadioButton { Tag: string modeName }) return;
        _owner.SetPlayerViewModeByName(modeName);
    }

    // Номер версии в карточке "О плеере" берётся не из отдельного захардкоженного текста,
    // а из того же changelog.json, что и окно "Список изменений" — самая первая (самая новая,
    // см. ChangelogLoader) запись и есть текущая версия программы. Так номер версии задаётся
    // ровно в одном месте — в changelog.json — и не может разъехаться с тем, что показывает
    // окно списка изменений.
    private void RefreshAppVersionText()
    {
        var entries = ChangelogLoader.Load();
        var current = entries.FirstOrDefault(e => e.IsCurrent) ?? entries.FirstOrDefault();
        AppVersionText.Text = current != null ? $"Версия {current.Version}" : "Версия";
    }

    // Ручная проверка обновлений (кнопка на странице "О плеере"). В отличие от тихой
    // проверки на старте (см. MainWindow.CheckForUpdatesOnStartupAsync) всегда показывает
    // результат — в том числе "версия уже последняя" и текст ошибки, если GitHub недоступен —
    // и не учитывает AppSettings.SkippedUpdateVersion: раз пользователь сам нажал кнопку,
    // значит явно хочет узнать актуальный статус, а не увидеть тишину из-за ранее нажатого
    // "Позже".
    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        CheckUpdatesButtonSubtitle.Text = "Проверяем…";

        try
        {
            var result = await UpdateChecker.CheckAsync();

            switch (result.Status)
            {
                case UpdateCheckStatus.UpdateAvailable:
                    CheckUpdatesButtonSubtitle.Text = $"Доступна версия {result.LatestVersion}";
                    new UpdateAvailableWindow(result, _settings) { Owner = this }.ShowDialog();
                    break;

                case UpdateCheckStatus.UpToDate:
                    CheckUpdatesButtonSubtitle.Text = $"У вас последняя версия ({result.CurrentVersion})";
                    break;

                case UpdateCheckStatus.Error:
                default:
                    CheckUpdatesButtonSubtitle.Text = $"Не удалось проверить: {result.ErrorMessage}";
                    break;
            }
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    // Переключатель источника загрузки обновления (GitHub напрямую / одно из зеркал
    // gh-proxy, см. UpdateChecker.DownloadSources) — сама проверка версии (кнопка выше) этой
    // настройкой не затрагивается, она влияет только на скачивание ZIP-архива в UpdateAvailableWindow.
    private void UpdateSourceRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        if (sender is not System.Windows.Controls.RadioButton { Tag: string key }) return;

        _settings.UpdateDownloadSource = key;
    }

    // ---------- "Все версии" (страница "О плеере") ----------
    // Один элемент списка версий (см. AllVersionsList в XAML) — обёртка над ReleaseListItem
    // с уже готовыми под UI строками, чтобы DataTemplate был просто набором биндингов без
    // конвертеров.
    private sealed record VersionListItemViewModel(
        string TitleText, string SubtitleText, string ActionText, bool CanInstall, ReleaseListItem Release);

    private bool _allVersionsLoaded;

    // Список подгружается лениво — только при первом реальном раскрытии аккордеона (а не сразу
    // при каждом открытии окна настроек, где эта страница даже не обязательно будет открыта) —
    // и только один раз за время жизни окна: повторные раскрытия/схлопывания уже не бьют по сети
    // заново.
    private async void AllVersionsExpander_Expanded(object sender, RoutedEventArgs e)
    {
        if (_allVersionsLoaded) return;
        _allVersionsLoaded = true;

        var (releases, errorMessage) = await UpdateChecker.GetAllReleasesAsync();

        AllVersionsLoadingText.Visibility = Visibility.Collapsed;

        if (errorMessage != null)
        {
            AllVersionsErrorText.Text = $"Не удалось загрузить список версий: {errorMessage}";
            AllVersionsErrorText.Visibility = Visibility.Visible;
            return;
        }

        if (releases.Count == 0)
        {
            AllVersionsErrorText.Text = "На GitHub пока нет ни одного опубликованного релиза.";
            AllVersionsErrorText.Visibility = Visibility.Visible;
            return;
        }

        string currentVersion = UpdateChecker.GetCurrentVersion();

        AllVersionsList.ItemsSource = releases
            // GitHub отдаёт релизы уже в порядке "сначала новые", но не гарантирует это явно —
            // сортируем сами по дате публикации, чтобы порядок не зависел от их API.
            .OrderByDescending(r => r.PublishedAt ?? System.DateTimeOffset.MinValue)
            .Select(r =>
            {
                bool isCurrent = string.Equals(r.Version, currentVersion, System.StringComparison.OrdinalIgnoreCase);

                string title = $"v{r.Version}" + (isCurrent ? " · текущая версия" : "") + (r.IsPrerelease ? " · пререлиз" : "");
                string subtitle = r.PublishedAt is { } published
                    ? published.LocalDateTime.ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("ru-RU"))
                    : "Дата публикации неизвестна";

                bool canInstall = !string.IsNullOrEmpty(r.ExeDownloadUrl);
                if (!canInstall) subtitle += " · в релизе нет .exe-установщика";

                string action = isCurrent ? "Переустановить" : "Установить";

                return new VersionListItemViewModel(title, subtitle, action, canInstall, r);
            })
            .ToList();
    }

    // Тот же диалог, что и при обычном обнаружении обновления — не проверяет, новее ли
    // выбранная версия текущей, поэтому подходит и для отката. CurrentVersion — настоящая
    // текущая версия, а не версия из списка: диалог сам покажет обе рядом, это и есть
    // предупреждение об откате, отдельный диалог подтверждения не нужен.
    private void VersionListItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: VersionListItemViewModel item }) return;

        var result = new UpdateCheckResult
        {
            Status = UpdateCheckStatus.UpdateAvailable,
            CurrentVersion = UpdateChecker.GetCurrentVersion(),
            LatestVersion = item.Release.Version,
            DownloadUrl = item.Release.ExeDownloadUrl,
            ReleaseNotesUrl = item.Release.ReleaseNotesUrl,
            ReleaseNotes = item.Release.ReleaseNotes,
        };

        new UpdateAvailableWindow(result, _settings) { Owner = this }.ShowDialog();
    }

    // ---------- Поиск настроек ----------

    private void BuildSearchIndex()
    {
        void Add(string label, string pageTitle, string pageKey, FrameworkElement target, string extraKeywords = "")
            => _searchIndex.Add(new SettingsSearchEntry(label, pageTitle, pageKey, $"{label} {extraKeywords}".ToLowerInvariant(), target));

        Add("Тема", "Оформление", "Appearance", ThemeDarkRadio, "тёмная светлая цвет тема оформление dark light");
        Add("Акцентный цвет", "Оформление", "Appearance", AccentSystemRadio, "акцент цвет палитра accent color");
        Add("Основа окна", "Оформление", "Appearance", BackdropMicaRadio, "mica acrylic blur акрил размытие блюр подложка фон backdrop");
        Add("Анимация смены обложки", "Оформление", "Appearance", AlbumArtTransitionOnRadio, "анимация обложка переход трек itunes слайд fly transition album art cover");
        Add("Вид плеера", "Окно", "Window", PlayerViewModeCard, "квадратный прямоугольный мини плеер вид размер окна square rectangular mini");
        Add("Поверх всех окон", "Окно", "Window", AlwaysOnTopCheckBox, "topmost всегда сверху главное окно");
        Add("Сворачивать в трей при закрытии", "Окно", "Window", MinimizeToTrayCheckBox, "трей закрытие свернуть tray");
        Add("Запускать вместе с Windows", "Окно", "Window", LaunchOnStartupCheckBox, "автозапуск запуск windows автозагрузка startup");
        Add("Запускать свёрнутым в трей", "Окно", "Window", StartHiddenInTrayCheckBox, "запуск свёрнутым трей автозапуск скрыто hidden startup tray");
        Add("Запоминать громкость между запусками", "Воспроизведение", "Playback", RememberVolumeCheckBox, "громкость запуск volume");
        Add("Логарифмическая регулировка громкости", "Воспроизведение", "Playback", LogarithmicVolumeCheckBox, "громкость логарифм слух дБ db volume logarithmic");
        Add("Эквалайзер", "Эквалайзер", "Equalizer", EqualizerEnabledCheckBox, "equalizer эквалайзер частоты полосы бас звук eq");
        Add("Прозрачность окна мини-плеера", "Мини-плеер", "MiniPlayer", MiniOpacitySlider, "прозрачность opacity мини плеер");
        Add("Поверх всех окон (мини-плеер)", "Мини-плеер", "MiniPlayer", MiniAlwaysOnTopCheckBox, "topmost мини плеер");
        Add("Закрепить положение (мини-плеер)", "Мини-плеер", "MiniPlayer", MiniPinnedCheckBox, "закрепить перетаскивание pin мини плеер");
        Add("Прилипание к краям экрана (мини-плеер)", "Мини-плеер", "MiniPlayer", MiniSnapToEdgesCheckBox, "прилипание магнит края экран snap edge мини плеер");
        Add("Вторая кнопка в мини-плеере", "Мини-плеер", "MiniPlayer", MiniSecondaryRepeatRadio, "вторая кнопка повтор перемешать избранное сердечко favorite shuffle repeat мини плеер");
        Add("Показывать полосу прогресса (мини-плеер)", "Мини-плеер", "MiniPlayer", MiniShowProgressCheckBox, "полоса прогресс progress bar скрыть мини плеер");
        Add("Пуск / пауза", "Горячие клавиши", "Hotkeys", HotkeyPlayPauseButton, "play pause горячая клавиша");
        Add("Следующий трек", "Горячие клавиши", "Hotkeys", HotkeyNextButton, "next горячая клавиша");
        Add("Предыдущий трек", "Горячие клавиши", "Hotkeys", HotkeyPreviousButton, "previous горячая клавиша");
        Add("Стоп", "Горячие клавиши", "Hotkeys", HotkeyStopButton, "stop горячая клавиша");
        Add("Громкость +", "Горячие клавиши", "Hotkeys", HotkeyVolumeUpButton, "volume up громкость горячая клавиша");
        Add("Громкость -", "Горячие клавиши", "Hotkeys", HotkeyVolumeDownButton, "volume down громкость горячая клавиша");
        Add("Без звука", "Горячие клавиши", "Hotkeys", HotkeyMuteButton, "mute без звука горячая клавиша");
        Add("Перемешать", "Горячие клавиши", "Hotkeys", HotkeyShuffleButton, "shuffle перемешать горячая клавиша");
        Add("Режим повтора", "Горячие клавиши", "Hotkeys", HotkeyRepeatButton, "repeat повтор горячая клавиша");
        Add("Удалить трек с диска", "Горячие клавиши", "Hotkeys", HotkeyDeleteTrackButton, "delete удалить трек диск горячая клавиша");
        Add("Шаффл без повторов", "Воспроизведение", "Playback", ImprovedShuffleCheckBox, "шаффл перемешать shuffle bag колода без повторов");
        Add("Полоса воспроизведения", "Воспроизведение", "Playback", ProgressBarWaveformRadio, "waveform форма звука soundcloud полоса прогресс seek слайдер");
        Add("ReplayGain", "Воспроизведение", "Playback", ReplayGainCheckBox, "replaygain громкость выравнивание нормализация gain");
        Add("Уведомление о смене трека", "Уведомления", "Notifications", TrackChangeToastCheckBox, "уведомление тост смена трека toast notification");
        Add("Расположение уведомления", "Уведомления", "Notifications", ToastPosTopLeftRadio, "уведомление угол расположение позиция монитор экран размер position monitor screen size");
        Add("Размер уведомления", "Уведомления", "Notifications", ToastSizeSmallRadio, "размер уведомление тост маленький средний большой size toast notification");
        Add("Ширина уведомления", "Уведомления", "Notifications", ToastWidthSlider, "ширина уведомление тост размер width toast notification size");
        Add("Экспортировать настройки", "Профиль", "Profile", ExportProfileButton, "экспорт настройки профиль lumi файл backup export profile");
        Add("Импортировать настройки", "Профиль", "Profile", ImportProfileButton, "импорт настройки профиль lumi файл backup import restore profile");
        Add("Сбросить плеер к исходному состоянию", "Профиль", "Profile", ResetPlayerButton, "сброс сбросить умолчание reset default настройки factory");
        Add("О плеере", "О плеере", "About", AboutInfoCard, "версия lumisense о программе о плеере");
        Add("Источник загрузки обновлений", "Обновления", "Updates", UpdateSourceGitHubRadio, "update mirror зеркало gh-proxy обновление скачать источник");
        Add("Все версии", "Обновления", "Updates", AllVersionsExpanderControl, "версии история версия откат downgrade install version releases обновление скачать установить zip exe установщик");
        Add("Проверить обновления", "Обновления", "Updates", CheckUpdatesButton, "обновление update github версия проверить");
        Add("Список изменений", "О плеере", "About", ChangelogButton, "патчноуты changelog версии история изменений");
        Add("Разработчик", "О плеере", "About", DeveloperGitHubButton, "разработчик автор github telegram wasssly ссылки контакты аватар");
        Add("Открыть папку с логами", "О плеере", "About", OpenLogsButton, "логи log ошибка краш crash диагностика");
    }

    private void SettingsSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        string query = SettingsSearchBox.Text.Trim();

        if (query.Length == 0)
        {
            NavCategoriesPanel.Visibility = Visibility.Visible;
            SearchResultsHost.Visibility = Visibility.Collapsed;
            return;
        }

        NavCategoriesPanel.Visibility = Visibility.Collapsed;
        SearchResultsHost.Visibility = Visibility.Visible;

        string queryLower = query.ToLowerInvariant();
        _searchResults.Clear();
        foreach (var entry in _searchIndex)
        {
            if (entry.Keywords.Contains(queryLower, StringComparison.Ordinal))
                _searchResults.Add(entry);
        }

        bool hasResults = _searchResults.Count > 0;
        SearchResultsList.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;
        SearchEmptyState.Visibility = hasResults ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SearchResultItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SettingsSearchEntry entry }) return;

        System.Windows.Controls.RadioButton navButton = entry.PageKey switch
        {
            "Appearance" => NavAppearance,
            "Window" => NavWindow,
            "Playback" => NavPlayback,
            "Notifications" => NavNotifications,
            "Equalizer" => NavEqualizer,
            "MiniPlayer" => NavMiniPlayer,
            "Hotkeys" => NavHotkeys,
            "Profile" => NavProfile,
            "Updates" => NavUpdates,
            "About" => NavAbout,
            _ => NavAppearance
        };
        navButton.IsChecked = true;

        // Возвращаемся к обычному виду навигации — поиск своё дело сделал
        SettingsSearchBox.Text = string.Empty;

        // Ждём, пока страница станет видимой и разложится по месту, и только потом
        // прокручиваем к нужному элементу и подсвечиваем его
        Dispatcher.InvokeAsync(() =>
        {
            // Некоторые результаты поиска (см. "Источник загрузки обновлений" и "Все версии")
            // лежат внутри Expander (см. FluentExpanderStyle в App.xaml), свёрнутого по
            // умолчанию — BringIntoView и подсветка элемента, который сейчас физически скрыт
            // (Visibility=Collapsed у содержимого свёрнутого аккордеона), ничего не покажут
            // пользователю. Разворачиваем все Expander-предки найденного элемента заранее.
            ExpandAncestorExpanders(entry.Target);

            // Раскрытие аккордеона меняет раскладку страницы (появляется скрытое раньше
            // содержимое) — ждём ещё один цикл, пока это отразится на макете, и только потом
            // считаем прокрутку/позицию для подсветки, иначе используем ещё не обновлённые
            // координаты.
            Dispatcher.InvokeAsync(() =>
            {
                entry.Target.BringIntoView();
                SearchHighlightAdorner.Flash(entry.Target);
            }, DispatcherPriority.Loaded);
        }, DispatcherPriority.Loaded);
    }

    private static void ExpandAncestorExpanders(DependencyObject element)
    {
        var current = System.Windows.Media.VisualTreeHelper.GetParent(element);
        while (current != null)
        {
            if (current is System.Windows.Controls.Expander expander) expander.IsExpanded = true;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
    }

    private void ThemeRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.Theme = ThemeLightRadio.IsChecked == true ? "Light" : "Dark";

        ApplicationThemeManager.Apply(_settings.IsLightThemeResolved() ? ApplicationTheme.Light : ApplicationTheme.Dark);
        _owner.ApplyAccentColor(); // акцент пересчитывает светлые/тёмные варианты под новую тему
        _owner.ApplyTrayTheme(_settings.IsLightThemeResolved());
        _owner.ApplyMiniPlayerThemeLive();
    }

    private void AccentModeRadio_Changed(object sender, RoutedEventArgs e)
    {
        AccentSwatchesPanel.Visibility = AccentManualRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (_isInitializing) return;

        _settings.AccentColorMode = AccentManualRadio.IsChecked == true ? "Manual" : "System";
        _owner.ApplyAccentColor();
    }

    private static readonly string[] AccentPresetHexes =
    {
        "#0078D4", "#8764B8", "#E3008C", "#E81123", "#FF8C00", "#FFB900", "#107C10", "#00B7C3"
    };

    private void AccentSwatch_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.Border { Background: SolidColorBrush brush }) return;

        // brush.Color.ToString() дал бы 8-значный "#AARRGGBB" (WPF всегда включает альфа-канал
        // в ToString()), а пресеты в AccentPresetHexes и формат из ColorDialog ниже — 6-значные
        // "#RRGGBB". Несовпадение форматов не сломало бы применение цвета (ColorConverter
        // одинаково понимает оба), но тихо сломало бы подсветку выбранного пресета в
        // RefreshAccentSwatchSelection — она сравнивает строки как есть.
        var c = brush.Color;
        ApplyAccentHex($"#{c.R:X2}{c.G:X2}{c.B:X2}");
    }

    private void AccentCustomButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            Color = System.Drawing.ColorTranslator.FromHtml(_settings.AccentColorHex),
            FullOpen = true
        };

        // WinForms-диалог — модальный поверх этого же (WPF) окна настроек; хендл окна нужен
        // явно, иначе диалог мог бы открыться за плеером вместо поверх него.
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (dialog.ShowDialog(new Wpf32Window(handle)) != System.Windows.Forms.DialogResult.OK) return;

        var c = dialog.Color;
        ApplyAccentHex($"#{c.R:X2}{c.G:X2}{c.B:X2}");
    }

    private void ApplyAccentHex(string hex)
    {
        _settings.AccentColorHex = hex;
        RefreshAccentSwatchSelection();
        if (_isInitializing) return;

        _owner.ApplyAccentColor();
    }

    // Подсвечивает рамкой тот пресет-квадратик, который совпадает с текущим AccentColorHex —
    // если сейчас выбран цвет через палитру (не совпадающий ни с одним пресетом), рамки не
    // будет ни у одного квадратика, это ожидаемо.
    private void RefreshAccentSwatchSelection()
    {
        System.Windows.Controls.Border[] swatches = { AccentSwatch0, AccentSwatch1, AccentSwatch2, AccentSwatch3,
                               AccentSwatch4, AccentSwatch5, AccentSwatch6, AccentSwatch7 };

        for (int i = 0; i < swatches.Length; i++)
        {
            bool selected = string.Equals(AccentPresetHexes[i], _settings.AccentColorHex,
                StringComparison.OrdinalIgnoreCase);
            swatches[i].BorderBrush = selected
                ? (Brush)FindResource("TextFillColorPrimaryBrush")
                : Brushes.Transparent;
        }
    }

    // Обёртка над HWND для System.Windows.Forms.IWin32Window — ColorDialog просит именно
    // этот интерфейс в качестве owner, а не голый IntPtr.
    private sealed class Wpf32Window : System.Windows.Forms.IWin32Window
    {
        public Wpf32Window(nint handle) => Handle = handle;
        public nint Handle { get; }
    }

    private void WindowBackdropRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.WindowBackdropType = BackdropAcrylicRadio.IsChecked == true ? "Acrylic" : "Mica";

        _owner.ApplyWindowBackdrop();
        ApplyWindowBackdrop(_settings); // то же самое — и у этого окна настроек тоже
    }

    private void AlwaysOnTopCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.AlwaysOnTop = AlwaysOnTopCheckBox.IsChecked == true;

        // Пока активен мини-плеер, поверх окон управляет отдельная мини-настройка —
        // обычную применяем только когда плеер в обычном виде
        if (!_owner.IsMiniMode)
            _owner.Topmost = _settings.AlwaysOnTop;
    }

    private void RememberVolumeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.RememberVolume = RememberVolumeCheckBox.IsChecked == true;
    }

    private void LogarithmicVolumeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.UseLogarithmicVolume = LogarithmicVolumeCheckBox.IsChecked == true;
        _owner.RefreshVolumeCurve();
    }

    private void TrackChangeToastCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.ShowTrackChangeToast = TrackChangeToastCheckBox.IsChecked == true;
    }

    private void InitializeToastPositionAndSize()
    {
        ToastPosTopLeftRadio.IsChecked = _settings.TrackChangeToastPosition == "TopLeft";
        ToastPosTopCenterRadio.IsChecked = _settings.TrackChangeToastPosition == "TopCenter";
        ToastPosTopRightRadio.IsChecked = _settings.TrackChangeToastPosition == "TopRight";
        ToastPosBottomLeftRadio.IsChecked = _settings.TrackChangeToastPosition == "BottomLeft";
        ToastPosBottomCenterRadio.IsChecked = _settings.TrackChangeToastPosition == "BottomCenter";
        ToastPosBottomRightRadio.IsChecked = !ToastPosTopLeftRadio.IsChecked.GetValueOrDefault()
                                              && !ToastPosTopCenterRadio.IsChecked.GetValueOrDefault()
                                              && !ToastPosTopRightRadio.IsChecked.GetValueOrDefault()
                                              && !ToastPosBottomLeftRadio.IsChecked.GetValueOrDefault()
                                              && !ToastPosBottomCenterRadio.IsChecked.GetValueOrDefault();

        ToastSizeSmallRadio.IsChecked = _settings.TrackChangeToastSize == "Small";
        ToastSizeLargeRadio.IsChecked = _settings.TrackChangeToastSize == "Large";
        ToastSizeMediumRadio.IsChecked = !ToastSizeSmallRadio.IsChecked.GetValueOrDefault()
                                          && !ToastSizeLargeRadio.IsChecked.GetValueOrDefault();

        ToastWidthSlider.Value = Math.Clamp(_settings.TrackChangeToastWidth, ToastWidthSlider.Minimum, ToastWidthSlider.Maximum);
        UpdateToastWidthValueText();
    }

    // Список мониторов собирается заново при каждом открытии окна настроек — состав/порядок
    // экранов мог измениться с прошлого раза (подключили/отключили монитор), а окно настроек
    // всё равно создаётся заново при каждом показе (см. MainWindow.ShowSettingsWindow), так
    // что кэшировать список между открытиями смысла нет.
    private void InitializeToastMonitorCombo()
    {
        ToastMonitorCombo.Items.Clear();

        var autoItem = new System.Windows.Controls.ComboBoxItem
        {
            Content = "Автоматически (тот же монитор, что и окно плеера)", Tag = ""
        };
        ToastMonitorCombo.Items.Add(autoItem);

        var screens = System.Windows.Forms.Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            string label = $"Монитор {i + 1} — {s.Bounds.Width}×{s.Bounds.Height}" + (s.Primary ? " (основной)" : "");
            ToastMonitorCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = label, Tag = s.DeviceName });
        }

        var selected = ToastMonitorCombo.Items.Cast<System.Windows.Controls.ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Tag == _settings.TrackChangeToastMonitor);
        ToastMonitorCombo.SelectedItem = selected ?? autoItem;
    }

    private void ToastPositionRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.TrackChangeToastPosition = ToastPosTopLeftRadio.IsChecked == true ? "TopLeft"
            : ToastPosTopCenterRadio.IsChecked == true ? "TopCenter"
            : ToastPosTopRightRadio.IsChecked == true ? "TopRight"
            : ToastPosBottomLeftRadio.IsChecked == true ? "BottomLeft"
            : ToastPosBottomCenterRadio.IsChecked == true ? "BottomCenter"
            : "BottomRight";
    }

    private void ToastSizeRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.TrackChangeToastSize = ToastSizeSmallRadio.IsChecked == true ? "Small"
            : ToastSizeLargeRadio.IsChecked == true ? "Large"
            : "Medium";
    }

    private void ToastMonitorCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.TrackChangeToastMonitor =
            ToastMonitorCombo.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string tag } ? tag : "";
    }

    // Слайдер целочисленный (IsSnapToTickEnabled, шаг 10px) — плавнее шагами в 1px пользователю
    // не нужно, а подпись рядом остаётся короткой и читаемой ("300 px").
    private void ToastWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateToastWidthValueText();
        if (_isInitializing) return;

        _settings.TrackChangeToastWidth = ToastWidthSlider.Value;
    }

    private void UpdateToastWidthValueText()
    {
        ToastWidthValueText.Text = $"{(int)Math.Round(ToastWidthSlider.Value)} px";
    }

    private void MinimizeToTrayCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.MinimizeToTrayOnClose = MinimizeToTrayCheckBox.IsChecked == true;
    }

    private void LaunchOnStartupCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        StartupManager.SetEnabled(LaunchOnStartupCheckBox.IsChecked == true);
    }

    private void StartHiddenInTrayCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.StartHiddenInTray = StartHiddenInTrayCheckBox.IsChecked == true;
    }

    // ---------- Прозрачность мини-плеера — тот же приём, что и громкость в главном окне:
    // сам Slider не ловит мышь (IsHitTestVisible="False" в XAML), поверх него прозрачный
    // Border обрабатывает клик и перетаскивание в любой точке полосы целиком. ----------

    private bool _isDraggingOpacityOverlay;

    private void MiniOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        MiniOpacityValueText.Text = $"{(int)Math.Round(e.NewValue * 100)}%";

        if (_isInitializing) return;

        _settings.MiniPlayerOpacity = e.NewValue;
        _owner.ApplyMiniPlayerOpacityLive(e.NewValue);
    }

    private void MiniOpacityOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var overlay = (FrameworkElement)sender;
        overlay.CaptureMouse();
        _isDraggingOpacityOverlay = true;
        MiniOpacitySlider.Focus();
        UpdateSliderValueFromMouse(MiniOpacitySlider, e.GetPosition(overlay).X, overlay.ActualWidth);
    }

    private void MiniOpacityOverlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingOpacityOverlay) return;
        var overlay = (FrameworkElement)sender;
        UpdateSliderValueFromMouse(MiniOpacitySlider, e.GetPosition(overlay).X, overlay.ActualWidth);
    }

    private void MiniOpacityOverlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var overlay = (FrameworkElement)sender;
        overlay.ReleaseMouseCapture();
        _isDraggingOpacityOverlay = false;
    }

    private static void UpdateSliderValueFromMouse(System.Windows.Controls.Slider slider, double positionX, double width)
    {
        if (width <= 0) return;

        double ratio = Math.Clamp(positionX / width, 0.0, 1.0);
        slider.Value = slider.Minimum + ratio * (slider.Maximum - slider.Minimum);
    }

    private void MiniAlwaysOnTopCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.MiniPlayerAlwaysOnTop = MiniAlwaysOnTopCheckBox.IsChecked == true;
        _owner.ApplyMiniPlayerTopmostLive(_settings.MiniPlayerAlwaysOnTop);
    }

    private void MiniPinnedCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.MiniPlayerPinned = MiniPinnedCheckBox.IsChecked == true;
    }

    private void MiniSnapToEdgesCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.MiniPlayerSnapToEdges = MiniSnapToEdgesCheckBox.IsChecked == true;
    }

    private void MiniSecondaryButtonRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.MiniPlayerSecondaryButton = MiniSecondaryShuffleRadio.IsChecked == true ? "Shuffle"
            : MiniSecondaryFavoriteRadio.IsChecked == true ? "Favorite"
            : "Repeat";
        _owner.ApplyMiniPlayerSecondaryButtonLive();
    }

    private void MiniButtonsLayoutRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.MiniPlayerButtonsLayout = MiniButtonsOverlayRadio.IsChecked == true ? "Overlay" : "Below";
        _owner.ApplyMiniPlayerButtonsLayoutLive();
    }

    private void MiniShowProgressCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.MiniPlayerShowProgress = MiniShowProgressCheckBox.IsChecked == true;
        _owner.ApplyMiniPlayerProgressBarVisibilityLive();
    }

    private void MiniInfoModeRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.MiniPlayerInfoMode = MiniInfoOnlyTitleRadio.IsChecked == true ? "TitleOnly"
            : MiniInfoRemainingRadio.IsChecked == true ? "TitleRemaining"
            : "TitleArtist";
        _owner.ApplyMiniPlayerInfoModeLive();
    }

    // ---------- Эквалайзер ----------

    private void EqualizerEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _owner.SetEqualizerEnabled(EqualizerEnabledCheckBox.IsChecked == true);
    }

    // Общий обработчик для всех 10 слайдеров полос — номер полосы передаётся через Tag
    // (см. SettingsWindow.xaml), а не десятью одинаковыми по сути методами.
    private void EqualizerBandSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing) return;
        if (sender is not System.Windows.Controls.Slider { Tag: string tagStr } slider) return;
        if (!int.TryParse(tagStr, out int band)) return;

        GetEqBandValueText(band).Text = FormatEqGain(slider.Value);
        _owner.SetEqualizerBandGain(band, slider.Value);
    }

    // Прокрутка колесом над полосой эквалайзера — на SmallChange за деление, WPF не делает
    // этого сам для Slider. Value ниже сама поднимет EqualizerBandSlider_ValueChanged, тем же
    // путём обновляя текст и звук, что и обычное перетаскивание. e.Handled — чтобы прокрутка
    // не листала страницу настроек дальше.
    private void EqualizerBandSlider_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not System.Windows.Controls.Slider slider) return;

        double step = slider.SmallChange > 0 ? slider.SmallChange : 0.5;
        double newValue = slider.Value + Math.Sign(e.Delta) * step;
        slider.Value = Math.Clamp(newValue, slider.Minimum, slider.Maximum);

        e.Handled = true;
    }

    private void EqualizerResetButton_Click(object sender, RoutedEventArgs e)
    {
        _owner.ResetEqualizer();

        // _isInitializing глушит ValueChanged на время, пока слайдеры переставляются в 0 —
        // иначе каждый из десяти сбросов по отдельности снова вызвал бы SetEqualizerBandGain,
        // хотя ResetEqualizer выше уже сделал это разом одним махом.
        _isInitializing = true;
        for (int band = 0; band < EqualizerSampleProvider.BandFrequencies.Length; band++)
        {
            GetEqBandSlider(band).Value = 0;
            GetEqBandValueText(band).Text = FormatEqGain(0);
        }
        _isInitializing = false;
    }

    private static string FormatEqGain(double gainDb) =>
        gainDb == 0 ? "0 дБ" : $"{(gainDb > 0 ? "+" : "")}{gainDb:0.#} дБ";

    // ---------- Пресеты эквалайзера ----------

    private void RefreshEqualizerPresetsList()
    {
        string? previouslySelected = (EqualizerPresetComboBox.SelectedItem as EqualizerPreset)?.Name;

        EqualizerPresetComboBox.ItemsSource = null;
        EqualizerPresetComboBox.ItemsSource = _owner.EqualizerPresets;

        if (previouslySelected != null)
            EqualizerPresetComboBox.SelectedItem = _owner.EqualizerPresets.FirstOrDefault(p => p.Name == previouslySelected);

        UpdateEqualizerPresetButtonsState();
    }

    private void UpdateEqualizerPresetButtonsState()
    {
        bool hasSelection = EqualizerPresetComboBox.SelectedItem != null;
        EqualizerApplyPresetButton.IsEnabled = hasSelection;
        EqualizerDeletePresetButton.IsEnabled = hasSelection;
        EqualizerExportPresetButton.IsEnabled = hasSelection;
    }

    private void EqualizerPresetComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => UpdateEqualizerPresetButtonsState();

    private void EqualizerSavePresetButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedName = (EqualizerPresetComboBox.SelectedItem as EqualizerPreset)?.Name ?? "";
        var dialog = new TextInputDialog("Сохранить пресет", "Название пресета:", selectedName) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.ResultText.Length == 0) return;

        _owner.SaveEqualizerPreset(dialog.ResultText);
        RefreshEqualizerPresetsList();
        EqualizerPresetComboBox.SelectedItem = _owner.EqualizerPresets.FirstOrDefault(p => p.Name == dialog.ResultText.Trim());
    }

    private void EqualizerApplyPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (EqualizerPresetComboBox.SelectedItem is not EqualizerPreset preset) return;

        _owner.ApplyEqualizerPreset(preset);

        // Пресет применился в MainWindow — подтягиваем актуальные значения обратно в слайдеры,
        // так же как при обычном открытии окна (см. _isInitializing выше).
        _isInitializing = true;
        for (int band = 0; band < EqualizerSampleProvider.BandFrequencies.Length; band++)
        {
            double gain = _owner.GetEqualizerBandGain(band);
            GetEqBandSlider(band).Value = gain;
            GetEqBandValueText(band).Text = FormatEqGain(gain);
        }
        _isInitializing = false;

        if (EqualizerEnabledCheckBox.IsChecked != true)
        {
            EqualizerEnabledCheckBox.IsChecked = true;
            _owner.SetEqualizerEnabled(true);
        }
    }

    private void EqualizerDeletePresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (EqualizerPresetComboBox.SelectedItem is not EqualizerPreset preset) return;

        var confirm = System.Windows.MessageBox.Show(
            this,
            $"Удалить пресет \"{preset.Name}\"?",
            "Удаление пресета",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);

        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        _owner.DeleteEqualizerPreset(preset);
        RefreshEqualizerPresetsList();
    }

    // "Поделиться" — сохраняет выбранный пресет в отдельный .json-файл, который можно переслать
    // как обычный файл (мессенджер, почта, флешка); получатель добавляет его через "Импортировать".
    private void EqualizerExportPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (EqualizerPresetComboBox.SelectedItem is not EqualizerPreset preset) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Поделиться пресетом эквалайзера",
            Filter = "Пресет эквалайзера (*.json)|*.json",
            FileName = $"{preset.Name}.json"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            _owner.ExportEqualizerPreset(preset, dialog.FileName);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Не удалось сохранить пресет:\n{ex.Message}", "Ошибка",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void EqualizerImportPresetButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Импортировать пресет эквалайзера",
            Filter = "Пресет эквалайзера (*.json)|*.json|Все файлы (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        var imported = _owner.ImportEqualizerPresetFromFile(dialog.FileName);
        if (imported == null)
        {
            System.Windows.MessageBox.Show(this, "Не удалось прочитать пресет — файл повреждён или это не пресет Lumisense.",
                "Ошибка", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return;
        }

        RefreshEqualizerPresetsList();
        EqualizerPresetComboBox.SelectedItem = imported;
    }

    private System.Windows.Controls.Slider GetEqBandSlider(int band) => band switch
    {
        0 => EqBand0Slider,
        1 => EqBand1Slider,
        2 => EqBand2Slider,
        3 => EqBand3Slider,
        4 => EqBand4Slider,
        5 => EqBand5Slider,
        6 => EqBand6Slider,
        7 => EqBand7Slider,
        8 => EqBand8Slider,
        _ => EqBand9Slider
    };

    private System.Windows.Controls.TextBlock GetEqBandValueText(int band) => band switch
    {
        0 => EqBand0ValueText,
        1 => EqBand1ValueText,
        2 => EqBand2ValueText,
        3 => EqBand3ValueText,
        4 => EqBand4ValueText,
        5 => EqBand5ValueText,
        6 => EqBand6ValueText,
        7 => EqBand7ValueText,
        8 => EqBand8ValueText,
        _ => EqBand9ValueText
    };

    private void ImprovedShuffleCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.UseImprovedShuffle = ImprovedShuffleCheckBox.IsChecked == true;

        // Колода/история от предыдущего режима шаффла не имеет смысла в новом —
        // начинаем с чистого листа, а не пытаемся домешать её в новую логику.
        _owner.ResetShuffleState();
    }

    // См. AppSettings.ProgressBarStyle / WaveformView. MainWindow.ApplyProgressBarStyle сама
    // разбирается, нужно ли при этом (пере)считать форму волны для уже загруженного трека.
    private void ProgressBarStyleRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.ProgressBarStyle = ProgressBarWaveformRadio.IsChecked == true ? "Waveform" : "Slider";
        _owner.ApplyProgressBarStyle();
    }

    // См. AppSettings.ReplayGainEnabled / ReplayGainReader.
    private void ReplayGainCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.ReplayGainEnabled = ReplayGainCheckBox.IsChecked == true;
        _owner.RefreshReplayGain();
    }

    private void AlbumArtTransitionRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _owner.SetAlbumArtTransitionEnabled(AlbumArtTransitionOnRadio.IsChecked == true);
    }

    // ---------- Экспорт/импорт настроек (.lumi) ----------
    // См. LumiProfile.cs — формат файла. Плейлист и избранное сюда не входят, переносятся
    // только настройки (тема, акцент, эквалайзер, хоткеи и т.п.) — см. LumiProfileIO.

    private void ExportProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var saveDialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = LumiProfileIO.FileFilter,
            DefaultExt = LumiProfileIO.FileExtension,
            FileName = "Lumisense" + LumiProfileIO.FileExtension
        };
        if (saveDialog.ShowDialog(this) != true) return;

        try
        {
            LumiProfileIO.Export(saveDialog.FileName, _settings);

            System.Windows.MessageBox.Show(this, "Настройки сохранены.", "Экспорт завершён",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Не удалось сохранить файл:\n{ex.Message}", "Ошибка экспорта",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void ImportProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var openDialog = new Microsoft.Win32.OpenFileDialog { Filter = LumiProfileIO.FileFilter };
        if (openDialog.ShowDialog(this) != true) return;

        var profile = LumiProfileIO.TryReadFile(openDialog.FileName);
        if (profile == null)
        {
            System.Windows.MessageBox.Show(this, "Не удалось прочитать этот файл — он повреждён или это не .lumi-профиль.",
                "Ошибка импорта", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return;
        }

        LumiProfileIO.Apply(profile.Settings, _settings);
        _owner.ApplyImportedSettingsLive();
        SettingsManager.Save(_settings);

        System.Windows.MessageBox.Show(this,
            "Настройки импортированы.\n\nЧасть из них (хоткеи, эквалайзер, поведение трея и мини-плеера) применится полностью после перезапуска плеера.",
            "Импорт завершён", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);

        // Поля этого окна настроек читаются из _settings только один раз, в конструкторе —
        // после импорта они не переприменяются сами. Проще переоткрыть окно (переиспользует
        // уже готовый MainWindow.ShowSettingsWindow), чем гоняться за каждым изменившимся
        // полем формы по отдельности.
        Close();
        _owner.ShowSettingsWindow("Profile");
    }

    // См. AppSettings/LumiProfileIO.ResetToDefaults и кнопку ResetPlayerButton в
    // SettingsWindow.xaml (страница "Профиль"). Того же типа необратимое массовое изменение
    // настроек, что и импорт чужого профиля выше — поэтому и обработчик устроен так же:
    // подтверждение, сброс, сохранение, частичное живое применение с предупреждением о
    // перезапуске для остального, переоткрытие этого окна.
    private void ResetPlayerButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = System.Windows.MessageBox.Show(this,
            "Сбросить тему, акцент, подложку окна, вид и размер плеера, громкость, шафл/повтор, горячие клавиши, мини-плеер и остальные настройки к значениям по умолчанию?\n\n" +
            "Плейлист, избранное, история прослушиваний, статистика и сохранённые пресеты эквалайзера затронуты не будут.",
            "Сбросить плеер?", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        LumiProfileIO.ResetToDefaults(_settings);
        _owner.ApplyImportedSettingsLive();
        SettingsManager.Save(_settings);

        System.Windows.MessageBox.Show(this,
            "Плеер сброшен к исходным настройкам.\n\nЧасть из них (хоткеи, эквалайзер, поведение трея и мини-плеера, размер и положение окна) применится полностью после перезапуска плеера.",
            "Сброс завершён", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);

        Close();
        _owner.ShowSettingsWindow("Profile");
    }

    // ---------- Навигация по страницам настроек ----------
    // Каждый пункт слева — RadioButton с Tag = ключ страницы; Checked-обработчик прячет
    // все страницы и показывает ту, что соответствует выбранному пункту. Патчноуты и
    // информация о программе — это просто ещё одна страница ("About"), а не отдельное окно.

    private void NavItem_Checked(object sender, RoutedEventArgs e)
    {
        // Полное имя типа, а не using System.Windows.Controls, чтобы не столкнуть RadioButton
        // с Wpf.Ui.Controls.Button, который в этом файле используется как просто "Button".
        if (sender is not System.Windows.Controls.RadioButton { Tag: string key }) return;

        // На всякий случай: если обработчик почему-то сработает раньше, чем InitializeComponent
        // успеет присвоить поля страниц (например, из-за IsChecked, выставленного в XAML),
        // просто ничего не делаем вместо падения с NullReferenceException.
        if (PageAppearance is null) return;

        PageAppearance.Visibility = key == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
        PageWindow.Visibility = key == "Window" ? Visibility.Visible : Visibility.Collapsed;
        PagePlayback.Visibility = key == "Playback" ? Visibility.Visible : Visibility.Collapsed;
        PageNotifications.Visibility = key == "Notifications" ? Visibility.Visible : Visibility.Collapsed;
        PageEqualizer.Visibility = key == "Equalizer" ? Visibility.Visible : Visibility.Collapsed;
        PageMiniPlayer.Visibility = key == "MiniPlayer" ? Visibility.Visible : Visibility.Collapsed;
        PageHotkeys.Visibility = key == "Hotkeys" ? Visibility.Visible : Visibility.Collapsed;
        PageProfile.Visibility = key == "Profile" ? Visibility.Visible : Visibility.Collapsed;
        PageUpdates.Visibility = key == "Updates" ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = key == "About" ? Visibility.Visible : Visibility.Collapsed;

        // Один и тот же ScrollViewer используется для всех страниц (см. комментарий в
        // SettingsWindow.xaml) — без явного сброса он "помнил" бы прокрутку с предыдущей
        // вкладки. Клик по результату поиска (см. SearchResultItem_Click) следом ещё раз
        // прокрутит к конкретному найденному элементу через отложенный Dispatcher.InvokeAsync —
        // тот вызов случится позже этого и просто переопределит позицию, никакого конфликта.
        PART_ContentScroll.ScrollToTop();

        FrameworkElement? activePage = key switch
        {
            "Appearance" => PageAppearance,
            "Window" => PageWindow,
            "Playback" => PagePlayback,
            "Notifications" => PageNotifications,
            "Equalizer" => PageEqualizer,
            "MiniPlayer" => PageMiniPlayer,
            "Hotkeys" => PageHotkeys,
            "Profile" => PageProfile,
            "Updates" => PageUpdates,
            "About" => PageAbout,
            _ => null
        };
        if (activePage is not null)
            AnimateSettingsPage(activePage);
    }

    private static void AnimateSettingsPage(FrameworkElement page)
    {
        page.Opacity = 0;
        var translate = new System.Windows.Media.TranslateTransform(0, 10);
        page.RenderTransform = translate;
        page.RenderTransformOrigin = new Point(0.5, 0);

        var easing = new System.Windows.Media.Animation.CubicEase
        {
            EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
        };
        page.BeginAnimation(UIElement.OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = easing
            });
        translate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new System.Windows.Media.Animation.DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = easing
            });
    }

    // ---------- Список изменений ----------

    // По просьбе: список изменений и настройки не должны быть открыты одновременно.
    // Само открытие/закрытие и переключение окон централизовано в MainWindow.ShowChangelogWindow
    // (симметрично ShowSettingsWindow) — оно же закроет это окно настроек.
    private void ChangelogButton_Click(object sender, RoutedEventArgs e) => _owner.ShowChangelogWindow();

    // ---------- Карточка разработчика (страница "О плеере") ----------
    // Тот же приём, что и у "Подробнее" в UpdateAvailableWindow.MoreButton_Click:
    // Process.Start с UseShellExecute=true — с .NET Core Process.Start больше не открывает
    // URL напрямую без этого флага. try/catch на случай отсутствия браузера по умолчанию —
    // не критично, просто ничего не откроется.
    private void DeveloperGitHubButton_Click(object sender, RoutedEventArgs e) => OpenUrl("https://github.com/wasssly");

    private void OpenRepositoryButton_Click(object sender, RoutedEventArgs e) => OpenUrl("https://github.com/wasssly/Lumisense");

    private void DeveloperTelegramButton_Click(object sender, RoutedEventArgs e) => OpenUrl("https://t.me/dontwritetoblame");

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e) => Logger.OpenLogsFolder();

    // Загружает аватар один раз и сохраняет его локально, чтобы последующие открытия Settings
    // не зависели от сети и не создавали новый HTTP-запрос каждый раз.
    private async void LoadDeveloperAvatar()
    {
        const string avatarUrl = "https://github.com/wasssly.png?size=96";
        string cacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumisense", "Cache");
        string cachePath = Path.Combine(cacheDirectory, "developer-avatar-96.png");
        string temporaryPath = Path.Combine(cacheDirectory, $"developer-avatar-{Guid.NewGuid():N}.part");

        try
        {
            byte[] bytes;
            bool cacheIsFresh = File.Exists(cachePath) &&
                                DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < TimeSpan.FromDays(30);
            if (cacheIsFresh)
            {
                bytes = await File.ReadAllBytesAsync(cachePath);
            }
            else
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Lumisense/1.0");
                bytes = await client.GetByteArrayAsync(avatarUrl);
                if (bytes.Length == 0 || bytes.Length > 2 * 1024 * 1024) return;

                Directory.CreateDirectory(cacheDirectory);
                await File.WriteAllBytesAsync(temporaryPath, bytes);
                File.Move(temporaryPath, cachePath, true);
            }

            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            _developerAvatarBitmap = bitmap;
            DeveloperAvatarBrush.ImageSource = bitmap;
        }
        catch
        {
            // При недоступной сети или повреждённом кэше остаётся XAML placeholder.
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch { }
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Нет браузера по умолчанию и т.п. — не критично, просто ничего не открылось
        }
    }

    // ---------- Горячие клавиши: запись пользовательской комбинации ----------
    // Клик по кнопке комбинации переводит окно в режим "записи": следующее нажатие
    // клавиши (вместе с зажатыми Ctrl/Alt/Shift) сохраняется как новая глобальная
    // комбинация и сразу же перерегистрируется в GlobalMediaHotKeys — без перезапуска.

    private void HotkeyPlayPauseButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.PlayPause);
    private void HotkeyNextButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.Next);
    private void HotkeyPreviousButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.Previous);
    private void HotkeyStopButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.Stop);
    private void HotkeyVolumeUpButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.VolumeUp);
    private void HotkeyVolumeDownButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.VolumeDown);
    private void HotkeyMuteButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.Mute);
    private void HotkeyShuffleButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.Shuffle);
    private void HotkeyRepeatButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.Repeat);
    private void HotkeyDeleteTrackButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.DeleteTrack);
    private void HotkeySeekForwardButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.SeekForward);
    private void HotkeySeekBackwardButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.SeekBackward);

    private void HotkeyPlayPauseClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.PlayPause);
    private void HotkeyNextClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.Next);
    private void HotkeyPreviousClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.Previous);
    private void HotkeyStopClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.Stop);
    private void HotkeyVolumeUpClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.VolumeUp);
    private void HotkeyVolumeDownClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.VolumeDown);
    private void HotkeyMuteClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.Mute);
    private void HotkeyShuffleClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.Shuffle);
    private void HotkeyRepeatClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.Repeat);
    private void HotkeyDeleteTrackClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.DeleteTrack);
    private void HotkeySeekForwardClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.SeekForward);
    private void HotkeySeekBackwardClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.SeekBackward);

    private void BeginRecording(HotkeyTarget target)
    {
        // Если уже что-то записывали, но не закончили — просто отменяем ту запись
        CancelRecording();

        _recordingTarget = target;
        GetHotkeyButton(target).Content = "Нажмите комбинацию…";
    }

    private void CancelRecording()
    {
        if (_recordingTarget == HotkeyTarget.None) return;

        var target = _recordingTarget;
        _recordingTarget = HotkeyTarget.None;
        RefreshHotkeyButtonText(target);
    }

    private void SettingsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recordingTarget == HotkeyTarget.None) return;

        e.Handled = true;

        // Alt-комбинации в WPF приходят как Key.System, реальная клавиша — в SystemKey
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            CancelRecording();
            return;
        }

        // Одни только модификаторы не считаем нажатием — ждём клавишу вместе с ними
        if (IsModifierKey(key)) return;

        // ВАЖНО: не используем Keyboard.Modifiers напрямую. Это агрегированное
        // свойство в WPF ненадёжно определяет ПРАВЫЕ варианты модификаторов
        // (правый Ctrl/Alt/Shift) в обработчике PreviewKeyDown — на некоторых
        // клавиатурах/раскладках оно корректно видит только левую клавишу.
        // Опрашиваем состояние каждой клавиши (лево+право) напрямую через
        // Keyboard.IsKeyDown — это надёжный, не зависящий от стороны способ.
        var isCtrl = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
        var isAlt = Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt);
        var isShift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
        var isWinDown = Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin);

        if (!isCtrl && !isAlt && !isShift && !isWinDown)
        {
            // Глобальная комбинация без модификатора будет перехватывать обычный ввод
            // во всех остальных окнах и приложениях — не даём её записать
            GetHotkeyButton(_recordingTarget).Content = "Нужен Ctrl/Alt/Shift/Win…";
            return;
        }

        var binding = new HotkeyBinding
        {
            Ctrl = isCtrl,
            Alt = isAlt,
            Shift = isShift,
            Win = isWinDown,
            Key = key.ToString()
        };

        var target = _recordingTarget;
        _recordingTarget = HotkeyTarget.None;

        SetHotkeyBinding(target, binding);
        RefreshHotkeyButtonText(target);
        _owner.ReapplyHotkeys();
    }

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;

    private void ClearHotkey(HotkeyTarget target)
    {
        CancelRecording();

        SetHotkeyBinding(target, new HotkeyBinding());
        RefreshHotkeyButtonText(target);
        _owner.ReapplyHotkeys();
    }

    private void SetHotkeyBinding(HotkeyTarget target, HotkeyBinding binding)
    {
        switch (target)
        {
            case HotkeyTarget.PlayPause: _settings.HotkeyPlayPause = binding; break;
            case HotkeyTarget.Next: _settings.HotkeyNext = binding; break;
            case HotkeyTarget.Previous: _settings.HotkeyPrevious = binding; break;
            case HotkeyTarget.Stop: _settings.HotkeyStop = binding; break;
            case HotkeyTarget.VolumeUp: _settings.HotkeyVolumeUp = binding; break;
            case HotkeyTarget.VolumeDown: _settings.HotkeyVolumeDown = binding; break;
            case HotkeyTarget.Mute: _settings.HotkeyMute = binding; break;
            case HotkeyTarget.Shuffle: _settings.HotkeyShuffle = binding; break;
            case HotkeyTarget.Repeat: _settings.HotkeyRepeat = binding; break;
            case HotkeyTarget.DeleteTrack: _settings.HotkeyDeleteTrack = binding; break;
            case HotkeyTarget.SeekForward: _settings.HotkeySeekForward = binding; break;
            case HotkeyTarget.SeekBackward: _settings.HotkeySeekBackward = binding; break;
        }
    }

    private HotkeyBinding GetHotkeyBinding(HotkeyTarget target) => target switch
    {
        HotkeyTarget.PlayPause => _settings.HotkeyPlayPause,
        HotkeyTarget.Next => _settings.HotkeyNext,
        HotkeyTarget.Previous => _settings.HotkeyPrevious,
        HotkeyTarget.Stop => _settings.HotkeyStop,
        HotkeyTarget.VolumeUp => _settings.HotkeyVolumeUp,
        HotkeyTarget.VolumeDown => _settings.HotkeyVolumeDown,
        HotkeyTarget.Mute => _settings.HotkeyMute,
        HotkeyTarget.Shuffle => _settings.HotkeyShuffle,
        HotkeyTarget.Repeat => _settings.HotkeyRepeat,
        HotkeyTarget.DeleteTrack => _settings.HotkeyDeleteTrack,
        HotkeyTarget.SeekForward => _settings.HotkeySeekForward,
        HotkeyTarget.SeekBackward => _settings.HotkeySeekBackward,
        _ => new HotkeyBinding()
    };

    private Button GetHotkeyButton(HotkeyTarget target) => target switch
    {
        HotkeyTarget.PlayPause => HotkeyPlayPauseButton,
        HotkeyTarget.Next => HotkeyNextButton,
        HotkeyTarget.Previous => HotkeyPreviousButton,
        HotkeyTarget.Stop => HotkeyStopButton,
        HotkeyTarget.VolumeUp => HotkeyVolumeUpButton,
        HotkeyTarget.VolumeDown => HotkeyVolumeDownButton,
        HotkeyTarget.Mute => HotkeyMuteButton,
        HotkeyTarget.Shuffle => HotkeyShuffleButton,
        HotkeyTarget.Repeat => HotkeyRepeatButton,
        HotkeyTarget.DeleteTrack => HotkeyDeleteTrackButton,
        HotkeyTarget.SeekForward => HotkeySeekForwardButton,
        HotkeyTarget.SeekBackward => HotkeySeekBackwardButton,
        _ => throw new ArgumentOutOfRangeException(nameof(target))
    };

    private void RefreshHotkeyButtonText(HotkeyTarget target)
    {
        GetHotkeyButton(target).Content = FormatBinding(GetHotkeyBinding(target));
    }

    private static string FormatBinding(HotkeyBinding binding)
    {
        if (binding.IsEmpty) return "Не задано";

        var parts = new List<string>();
        if (binding.Ctrl) parts.Add("Ctrl");
        if (binding.Alt) parts.Add("Alt");
        if (binding.Shift) parts.Add("Shift");
        if (binding.Win) parts.Add("Win");
        parts.Add(DisplayKeyName(binding.Key));

        return string.Join(" + ", parts);
    }

    // Немного облагораживаем отображение некоторых клавиш, чьи имена в System.Windows.Input.Key
    // не совсем очевидны пользователю (например, Key.Next — это на самом деле PageDown)
    private static string DisplayKeyName(string keyName) => keyName switch
    {
        "Left" => "←",
        "Right" => "→",
        "Up" => "↑",
        "Down" => "↓",
        "Next" => "PageDown",
        "Prior" => "PageUp",
        "OemPlus" => "+",
        "OemMinus" => "-",
        "OemComma" => ",",
        "OemPeriod" => ".",
        "Escape" => "Esc",
        _ => keyName
    };
}
