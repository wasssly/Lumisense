using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Wpf.Ui.Controls;

namespace AudioPlayer;

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
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
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
            string artist = "Неизвестный исполнитель";

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
            StatsSinceText.Text = $"Статистика собирается с {since:d MMMM yyyy}";
            StatsSinceText.Visibility = Visibility.Visible;
        }

        LoadingState.Visibility = Visibility.Collapsed;
        ContentScroll.Visibility = Visibility.Visible;
    }

    private static string FormatListenDuration(double totalSeconds)
    {
        var span = TimeSpan.FromSeconds(totalSeconds);

        if (span.TotalDays >= 1) return $"{(int)span.TotalDays} дн {span.Hours} ч";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours} ч {span.Minutes} мин";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes} мин";
        return "меньше минуты";
    }

    // Русское склонение "прослушивание/прослушивания/прослушиваний" по числу — те же три
    // формы, что и у слова "год"/"файл" и т.п.: 1 (но не 11) — единственное число; 2-4
    // (кроме 12-14) — "прослушивания"; всё остальное, включая 11-14, — "прослушиваний".
    private static string PluralizeListens(int count)
    {
        int tens = count % 100;
        int last = count % 10;

        string word = last == 1 && tens != 11 ? "прослушивание"
            : last is >= 2 and <= 4 && tens is < 12 or > 14 ? "прослушивания"
            : "прослушиваний";

        return $"{count} {word}";
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
