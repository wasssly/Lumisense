using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Wpf.Ui.Controls;

namespace Lumisense;

// Окно "Статистика" — сводка по PlayCountManager и AppSettings.TotalListenSeconds. Названия
// и исполнители читаются из тегов асинхронно (LoadAsync), с индикатором загрузки.
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
        // При повторном вызове (ResetStatsButton_Click) возвращаем единый стартовый вид, как при первом открытии окна.
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

        // Чтение тегов сотен файлов — заметный ввод-вывод, поэтому в фоновом потоке, а не на UI-потоке (та же проблема,
        // что с зажатой клавишей "следующий трек": MainWindow.HandleHotkeyTrackStep лечит её дебаунсом).
        var trackInfos = await Task.Run(() => played.Select(kv =>
        {
            string title = Path.GetFileNameWithoutExtension(kv.Key);
            string artist = LocalizationService.Translate("Неизвестный исполнитель");

            try
            {
                var atlTrack = new ATL.Track(kv.Key);
                if (!string.IsNullOrWhiteSpace(atlTrack.Title)) title = atlTrack.Title;

                var performer = atlTrack.Artist;
                if (!string.IsNullOrWhiteSpace(performer)) artist = performer;
            }
            catch
            {
                // Файл мог быть удалён, перемещён или повреждён после прошлого прослушивания: показываем то, что осталось,
                // чтобы один проблемный файл не ронял окно статистики.
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

    // Секунды видны на всех масштабах (вплоть до "0 сек"): сумма копится на каждом тике таймера прогресса (250 мс)
    // в MainWindow.ProgressTimer_Tick и отражает реально игравшее время, а не порог в полтрека, как PlayCountManager.
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

    // Формы прослушиваний определяются в LocalizationService (ru: one/few/many, en: one/other) — окно
    // статистики своей лингвистической логики не хранит.
    private static string PluralizeListens(int count) =>
        LocalizationService.FormatPlural(LocalizationKey.StatisticsListens, count);

    // Сброс необратим: MessageBox YesNo с предупреждающей иконкой и результатом по умолчанию No (как в
    // MainWindow.ClearPlaylistButton_Click); в отличие от ResetStatsButton_Click трогает только счётчики прослушиваний.
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

    // Сброс необратим: MessageBox YesNo с предупреждающей иконкой и результатом по умолчанию No, чтобы
    // случайный Enter не сработал как согласие (тот же паттерн, что у MainWindow.ClearPlaylistButton_Click).
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
