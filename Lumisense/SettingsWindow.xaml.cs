using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace AudioPlayer;

public partial class SettingsWindow : FluentWindow
{
    private enum HotkeyTarget { None, PlayPause, Next, Previous, Stop, VolumeUp, VolumeDown, Mute, Shuffle, Repeat, DeleteTrack }

    private readonly AppSettings _settings;
    private readonly MainWindow _owner;
    private bool _isInitializing = true;

    // Пока не None — окно "слушает" следующее нажатие клавиш и запишет его как новую комбинацию
    private HotkeyTarget _recordingTarget = HotkeyTarget.None;

    // ---------- Поиск настроек ----------
    // Индекс не читает разметку, а просто перечисляет каждую настраиваемую опцию вручную:
    // подпись, к какой странице она относится, ссылку на сам элемент управления (чтобы потом
    // прокрутить к нему и подсветить) и ключевые слова для поиска.
    private sealed record SettingsSearchEntry(string Label, string PageTitle, string PageKey, string Keywords, FrameworkElement Target);

    private readonly List<SettingsSearchEntry> _searchIndex = new();
    private readonly ObservableCollection<SettingsSearchEntry> _searchResults = new();

    public SettingsWindow(AppSettings settings, MainWindow owner, string? initialPage = null)
    {
        InitializeComponent();

        // Выбираем стартовую страницу здесь, а не через IsChecked="True" в XAML — на этот
        // момент все страницы (PageAppearance, PageWindow и т.д.) уже гарантированно созданы,
        // так что обработчик NavItem_Checked отработает без NullReferenceException.
        //
        // initialPage позволяет сразу открыть окно на нужной странице вместо страницы по
        // умолчанию — используется, когда настройки открываются автоматически после закрытия
        // окна списка изменений: тогда логично сразу оказаться на "О плеере", а не снова
        // листать до неё вручную.
        (initialPage switch
        {
            "About" => NavAbout,
            "Window" => NavWindow,
            "Playback" => NavPlayback,
            "MiniPlayer" => NavMiniPlayer,
            "Hotkeys" => NavHotkeys,
            "Experimental" => NavExperimental,
            _ => NavAppearance
        }).IsChecked = true;

        _settings = settings;
        _owner = owner;
        Owner = owner;

        LightThemeCheckBox.IsChecked = _settings.Theme == "Light";
        AlwaysOnTopCheckBox.IsChecked = _settings.AlwaysOnTop;
        RememberVolumeCheckBox.IsChecked = _settings.RememberVolume;
        LogarithmicVolumeCheckBox.IsChecked = _settings.UseLogarithmicVolume;
        MinimizeToTrayCheckBox.IsChecked = _settings.MinimizeToTrayOnClose;

        MiniOpacitySlider.Value = _settings.MiniPlayerOpacity;
        MiniOpacityValueText.Text = $"{(int)Math.Round(_settings.MiniPlayerOpacity * 100)}%";
        MiniAlwaysOnTopCheckBox.IsChecked = _settings.MiniPlayerAlwaysOnTop;
        MiniPinnedCheckBox.IsChecked = _settings.MiniPlayerPinned;

        ImprovedShuffleCheckBox.IsChecked = _settings.UseImprovedShuffle;

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

        SearchResultsList.ItemsSource = _searchResults;
        BuildSearchIndex();

        RefreshAppVersionText();

        _isInitializing = false;
    }

    // Вызывается извне (из контекстного меню мини-плеера), когда закрепление или "поверх окон"
    // переключили не через это окно, а прямо на мини-плеере. Флаг _isInitializing глушит
    // Changed-обработчики чекбоксов, чтобы не вызвать повторное, уже ненужное применение
    // настройки и не уйти в цикл обновлений.
    public void RefreshMiniPlayerToggles()
    {
        _isInitializing = true;
        MiniAlwaysOnTopCheckBox.IsChecked = _settings.MiniPlayerAlwaysOnTop;
        MiniPinnedCheckBox.IsChecked = _settings.MiniPlayerPinned;
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

    // Переключатель источника загрузки установщика (GitHub напрямую / одно из зеркал
    // gh-proxy, см. UpdateChecker.DownloadSources) — сама проверка версии (кнопка выше) этой
    // настройкой не затрагивается, она влияет только на скачивание файла в UpdateAvailableWindow.
    private void UpdateSourceRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        if (sender is not System.Windows.Controls.RadioButton { Tag: string key }) return;

        _settings.UpdateDownloadSource = key;
    }

    // ---------- Поиск настроек ----------

    private void BuildSearchIndex()
    {
        void Add(string label, string pageTitle, string pageKey, FrameworkElement target, string extraKeywords = "")
            => _searchIndex.Add(new SettingsSearchEntry(label, pageTitle, pageKey, $"{label} {extraKeywords}".ToLowerInvariant(), target));

        Add("Светлая тема", "Оформление", "Appearance", LightThemeCheckBox, "тёмная светлая цвет тема оформление");
        Add("Вид плеера", "Окно", "Window", PlayerViewModeCard, "квадратный прямоугольный мини плеер вид размер окна square rectangular mini");
        Add("Поверх всех окон", "Окно", "Window", AlwaysOnTopCheckBox, "topmost всегда сверху главное окно");
        Add("Сворачивать в трей при закрытии", "Окно", "Window", MinimizeToTrayCheckBox, "трей закрытие свернуть tray");
        Add("Запоминать громкость между запусками", "Воспроизведение", "Playback", RememberVolumeCheckBox, "громкость запуск volume");
        Add("Логарифмическая регулировка громкости", "Воспроизведение", "Playback", LogarithmicVolumeCheckBox, "громкость логарифм слух дБ db volume logarithmic");
        Add("Прозрачность окна мини-плеера", "Мини-плеер", "MiniPlayer", MiniOpacitySlider, "прозрачность opacity мини плеер");
        Add("Поверх всех окон (мини-плеер)", "Мини-плеер", "MiniPlayer", MiniAlwaysOnTopCheckBox, "topmost мини плеер");
        Add("Закрепить положение (мини-плеер)", "Мини-плеер", "MiniPlayer", MiniPinnedCheckBox, "закрепить перетаскивание pin мини плеер");
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
        Add("Улучшенный шаффл", "Экспериментальное", "Experimental", ImprovedShuffleCheckBox, "шаффл перемешать shuffle экспериментальное bag колода");
        Add("О плеере", "О плеере", "About", AboutInfoCard, "версия lumisense о программе о плеере");
        Add("Источник загрузки обновлений", "О плеере", "About", UpdateSourceGitHubRadio, "update mirror зеркало gh-proxy обновление скачать источник");
        Add("Проверить обновления", "О плеере", "About", CheckUpdatesButton, "обновление update github версия проверить");
        Add("Список изменений", "О плеере", "About", ChangelogButton, "патчноуты changelog версии история изменений");
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
            "MiniPlayer" => NavMiniPlayer,
            "Hotkeys" => NavHotkeys,
            "Experimental" => NavExperimental,
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
            entry.Target.BringIntoView();
            SearchHighlightAdorner.Flash(entry.Target);
        }, DispatcherPriority.Loaded);
    }

    private void ThemeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.Theme = LightThemeCheckBox.IsChecked == true ? "Light" : "Dark";
        ApplicationThemeManager.Apply(_settings.Theme == "Light" ? ApplicationTheme.Light : ApplicationTheme.Dark);
        _owner.ApplyTrayTheme(_settings.Theme == "Light");
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

    private void MinimizeToTrayCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.MinimizeToTrayOnClose = MinimizeToTrayCheckBox.IsChecked == true;
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

    // ---------- Экспериментальное ----------

    private void ImprovedShuffleCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.UseImprovedShuffle = ImprovedShuffleCheckBox.IsChecked == true;

        // Колода/история от предыдущего режима шаффла не имеет смысла в новом —
        // начинаем с чистого листа, а не пытаемся домешать её в новую логику.
        _owner.ResetShuffleState();
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
        PageMiniPlayer.Visibility = key == "MiniPlayer" ? Visibility.Visible : Visibility.Collapsed;
        PageHotkeys.Visibility = key == "Hotkeys" ? Visibility.Visible : Visibility.Collapsed;
        PageExperimental.Visibility = key == "Experimental" ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = key == "About" ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- Список изменений ----------

    // По просьбе: список изменений и настройки не должны быть открыты одновременно.
    // Само открытие/закрытие и переключение окон централизовано в MainWindow.ShowChangelogWindow
    // (симметрично ShowSettingsWindow) — оно же закроет это окно настроек.
    private void ChangelogButton_Click(object sender, RoutedEventArgs e) => _owner.ShowChangelogWindow();

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
