using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Wpf.Ui.Controls;

namespace Lumisense;

// Окно "Статистика" — сводка по PlayCountManager (число прослушиваний по пути файла) и
// AppSettings.TotalListenSeconds (суммарное время реального воспроизведения, копится в
// MainWindow.ProgressTimer_Tick). Названия/исполнители для топ-списков читаются из тегов
// файлов асинхронно (см. LoadAsync) — на большой истории прослушиваний это не мгновенно,
// поэтому окно сначала показывает индикатор загрузки, а не блокирует UI-поток.
public partial class StatisticsWindow : FluentWindow
{
    private readonly AppSettings _settings;

    public StatisticsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        AccessibilityPreferences.ApplyToWindow(this, _settings);

        ApplyWindowBackdrop();

        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => LocalizationService.LanguageChanged -= LocalizationService_LanguageChanged;
        LocalizationService.LanguageChanged += LocalizationService_LanguageChanged;
    }

    private void LocalizationService_LanguageChanged(object? sender, EventArgs e)
    {
        _ = LoadAsync();
    }

    // То же самое, что и MainWindow.ApplyWindowBackdrop/SettingsWindow.ApplyWindowBackdrop —
    // своя копия, потому что применяется к собственному HWND этого окна.
    private void ApplyWindowBackdrop()
    {
        WindowBackdropType = _settings.WindowBackdropType == "Acrylic"
            ? Wpf.Ui.Controls.WindowBackdropType.Acrylic
            : Wpf.Ui.Controls.WindowBackdropType.Mica;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyWindowBackdrop();
    }

    private async Task LoadAsync()
    {
        // На повторный вызов (см. ResetStatsButton_Click) состояние с прошлой загрузки могло
        // остаться "показан контент" или "показано пустое состояние" — приводим к единому
        // стартовому виду, как при самом первом открытии окна.
        LoadingState.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;
        ContentScroll.Visibility = Visibility.Collapsed;
        StatsSinceText.Visibility = Visibility.Collapsed;

        var played = PlayCountManager.GetAll().Where(kv => kv.Value > 0).ToList();

        if (played.Count == 0)
        {
            LoadingState.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
            return;
        }

        int totalPlays = played.Sum(kv => kv.Value);
        int distinctTracks = played.Count;

        // Чтение тегов десятков-сотен файлов — заметная по времени операция ввода-вывода,
        // поэтому в фоновом потоке, а не прямо здесь на UI-потоке: тот же класс проблемы
        // (тяжёлая операция на каждый трек, вызванная в цикле), что и раньше с зажатой
        // клавишей "следующий трек" (см. MainWindow.HandleHotkeyTrackStep) — там лечили
        // дебаунсом, здесь лечим переносом самой работы с UI-потока.
        var trackInfos = await Task.Run(() => played.Select(kv =>
        {
            string title = Path.GetFileNameWithoutExtension(kv.Key);
            string artist = LocalizationService.Translate("Неизвестный исполнитель");

            try
            {
                using var tagFile = TagLib.File.Create(kv.Key);
                if (!string.IsNullOrWhiteSpace(tagFile.Tag.Title)) title = tagFile.Tag.Title;

                var performer = tagFile.Tag.FirstPerformer;
                if (!string.IsNullOrWhiteSpace(performer)) artist = performer;
            }
            catch
            {
                // Трек мог быть удалён, перемещён или повреждён уже после того, как его
                // прослушали в прошлый раз — просто показываем то, что осталось (имя файла,
                // "неизвестный исполнитель"), без падения всего окна статистики из-за одного
                // проблемного файла.
            }

            return (Path: kv.Key, Count: kv.Value, Title: title, Artist: artist);
        }).ToList());

        var topTracks = trackInfos
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(5)
            .Select((t, i) => new TopTrackRow
            {
                Rank = i + 1,
                Title = t.Title,
                Artist = t.Artist,
                CountText = PluralizeListens(t.Count)
            })
            .ToList();

        var topArtists = trackInfos
            .GroupBy(t => t.Artist, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new { Name = g.First().Artist, Count = g.Sum(t => t.Count) })
            .OrderByDescending(a => a.Count)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(5)
            .Select((a, i) => new TopArtistRow
            {
                Rank = i + 1,
                Name = a.Name,
                CountText = PluralizeListens(a.Count)
            })
            .ToList();

        TotalPlaysValue.Text = totalPlays.ToString();
        DistinctTracksValue.Text = distinctTracks.ToString();
        HoursValue.Text = FormatListenDuration(_settings.TotalListenSeconds);

        TopArtistsList.ItemsSource = topArtists;
        TopTracksList.ItemsSource = topTracks;

        if (DateTime.TryParse(_settings.StatsStartedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var since))
        {
            StatsSinceText.Text = LocalizationService.Format("Статистика собирается с {0}",
                since.ToString("d MMMM yyyy", CultureInfo.CurrentCulture));
            StatsSinceText.Visibility = Visibility.Visible;
        }

        LoadingState.Visibility = Visibility.Collapsed;
        ContentScroll.Visibility = Visibility.Visible;
    }

    // Секунды видны на всех масштабах, вплоть до "0 сек" — чтобы пара секунд прослушивания
    // была видна в статистике сразу же, а не терялась за обобщённым "меньше минуты": сумма
    // копится в MainWindow.ProgressTimer_Tick на каждый тик таймера прогресса (250 мс), а не
    // только после какого-то порога прослушанности трека — в отличие от PlayCountManager
    // (который считает "прослушивания" только при достижении половины трека, чтобы не
    // накручивать счётчик от короткого предпросмотра), суммарное время должно отражать
    // ровно то время, что реально играло, вплоть до пары секунд.
    private static string FormatListenDuration(double totalSeconds)
    {
        var span = TimeSpan.FromSeconds(totalSeconds);

        if (span.TotalDays >= 1)
            return LocalizationService.IsEnglish
                ? $"{(int)span.TotalDays} d {span.Hours} h"
                : $"{(int)span.TotalDays} дн {span.Hours} ч";
        if (span.TotalHours >= 1)
            return LocalizationService.IsEnglish
                ? $"{(int)span.TotalHours} h {span.Minutes} min"
                : $"{(int)span.TotalHours} ч {span.Minutes} мин";
        if (span.TotalMinutes >= 1)
            return LocalizationService.IsEnglish
                ? $"{(int)span.TotalMinutes} min {span.Seconds} sec"
                : $"{(int)span.TotalMinutes} мин {span.Seconds} сек";
        return LocalizationService.IsEnglish
            ? $"{(int)span.TotalSeconds} sec"
            : $"{(int)span.TotalSeconds} сек";
    }

    // Формы прослушиваний определяются централизованно в LocalizationService: для русского
    // это one/few/many, для английского — one/other. Окно статистики не хранит собственную
    // лингвистическую логику и использует тот же ключевой механизм, что новые динамические UI.
    private static string PluralizeListens(int count) =>
        LocalizationService.FormatPlural(LocalizationKey.StatisticsListens, count);

    // Сброс необратим (счётчики прослушиваний по трекам теряются безвозвратно), поэтому —
    // MessageBox с YesNo и предупреждающей иконкой, тот же паттерн подтверждения, что и у
    // MainWindow.ClearPlaylistButton_Click/DeleteTrackFromDiskMenuItem_Click, с той же
    // осторожностью: результат по умолчанию — No, чтобы случайный Enter не сработал как
    // согласие. В отличие от ResetStatsButton_Click ниже — трогает только счётчики
    // прослушиваний (PlayCountManager), не суммарное время и не дату начала отсчёта.
    private void ResetPlayCountsButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = LocalizedMessageBox.Show(
            this,
            "Сбросить счётчики прослушиваний по всем трекам?\n\nЭто обнулит \"Прослушано треков\", " +
            "\"Разных треков\" и оба топ-списка. Суммарное время прослушивания не изменится. " +
            "Отменить это действие нельзя.",
            "Сброс прослушиваний",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);

        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        PlayCountManager.Reset();
        _settings.PlayCounts = PlayCountManager.GetAll();
        SettingsManager.Save(_settings);

        _ = LoadAsync();
    }

    // Сброс необратим (счётчики прослушиваний по трекам и суммарное время теряются
    // безвозвратно), поэтому — MessageBox с YesNo и предупреждающей иконкой, тот же паттерн
    // подтверждения, что и у MainWindow.ClearPlaylistButton_Click/DeleteTrackFromDiskMenuItem_Click,
    // с той же осторожностью: результат по умолчанию — No, чтобы случайный Enter не сработал
    // как согласие.
    private void ResetStatsButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = LocalizedMessageBox.Show(
            this,
            "Сбросить всю статистику прослушивания?\n\nСчётчики прослушиваний по всем трекам и суммарное " +
            "время обнулятся. Сами файлы и плейлист не затрагиваются. Отменить это действие нельзя.",
            "Сброс статистики",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);

        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        PlayCountManager.Reset();
        _settings.TotalListenSeconds = 0;
        _settings.StatsStartedAt = null;
        _settings.PlayCounts = PlayCountManager.GetAll();
        SettingsManager.Save(_settings);

        _ = LoadAsync();
    }

    private sealed class TopArtistRow
    {
        public int Rank { get; init; }
        public string Name { get; init; } = "";
        public string CountText { get; init; } = "";
    }

    private sealed class TopTrackRow
    {
        public int Rank { get; init; }
        public string Title { get; init; } = "";
        public string Artist { get; init; } = "";
        public string CountText { get; init; } = "";
    }
}
