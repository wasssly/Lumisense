using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Media.Imaging;

namespace AudioPlayer;

public partial class NowPlayingWindow : Window
{
    private readonly record struct PalettePoint(double Red, double Green, double Blue, double Weight);

    private sealed class FlowCloud
    {
        public required Point Entry { get; init; }
        public required Point Exit { get; init; }
        public double TravelRate { get; init; }
        public double HiddenPauseDuration { get; init; }
        public double WaveAmplitude { get; init; }
        public double WaveCycles { get; init; }
        public double VerticalDriftAmplitude { get; init; }
        public double VerticalDriftCycles { get; init; }
        public double MotionPhase { get; init; }
        public double ScalePhase { get; init; }
        public double OpacityPhase { get; init; }
    }

    private readonly MainWindow _owner;
    private readonly ObservableCollection<LyricLine> _syncedLines = new();
    private readonly ObservableCollection<OnlineLyricsResult> _onlineResults = new();
    private CancellationTokenSource? _lyricsLoadCts;
    private CancellationTokenSource? _onlineSearchCts;
    private DispatcherTimer? _ambientMotionTimer;
    private double _ambientMotionTime;
    private double _ambientSpeed = 1.0;
    private double _ambientTargetSpeed = 1.0;
    private readonly FlowCloud?[] _flowClouds = new FlowCloud?[5];
    private bool _lyricsPanelVisible = true;
    private LyricsDocument _lyrics = LyricsDocument.Empty;
    private string? _lyricsTrackPath;
    private int _activeLyricIndex = -2;

    public NowPlayingWindow(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();

        SyncedLyricsList.ItemsSource = _syncedLines;
        OnlineLyricsResultsList.ItemsSource = _onlineResults;
        Loaded += NowPlayingWindow_Loaded;
        Closed += NowPlayingWindow_Closed;
        LocalizationService.LanguageChanged += LocalizationService_LanguageChanged;
    }

    private void NowPlayingWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _owner.TrackInfoChanged += Owner_TrackInfoChanged;
        _owner.PlaybackStateChanged += Owner_PlaybackStateChanged;
        _owner.ProgressChanged += Owner_ProgressChanged;

        InitializeAmbientAnimation();
        RefreshTrackPresentation();
        UpdatePlaybackState(_owner.IsPlayingNow);
        UpdateProgress(_owner.CurrentPlaybackSeconds, _owner.CurrentTrackDurationSeconds);
        UpdateLyricsLayout();
    }

    private void NowPlayingWindow_Closed(object? sender, EventArgs e)
    {
        _owner.TrackInfoChanged -= Owner_TrackInfoChanged;
        _owner.PlaybackStateChanged -= Owner_PlaybackStateChanged;
        _owner.ProgressChanged -= Owner_ProgressChanged;
        LocalizationService.LanguageChanged -= LocalizationService_LanguageChanged;
        CancelLyricsLoad();
        CancelOnlineSearch();
        if (_ambientMotionTimer is not null)
        {
            _ambientMotionTimer.Stop();
            _ambientMotionTimer.Tick -= AmbientMotionTimer_Tick;
            _ambientMotionTimer = null;
        }
    }

    private void Owner_TrackInfoChanged(string title, string artist, System.Windows.Media.Brush? _) => RefreshTrackPresentation();
    private void Owner_PlaybackStateChanged(bool isPlaying) => UpdatePlaybackState(isPlaying);
    private void Owner_ProgressChanged(double currentSeconds, double totalSeconds) => UpdateProgress(currentSeconds, totalSeconds);

    private void LocalizationService_LanguageChanged(object? sender, EventArgs e)
    {
        if (!IsLoaded) return;

        // Заголовок, источник текста и подсказка play/pause формируются программно,
        // поэтому их нужно обновить отдельно от обхода статического visual tree.
        ApplyLyricsDocument(_lyrics);
        UpdatePlaybackState(_owner.IsPlayingNow);
    }

    private void NowPlayingWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_lyricsPanelVisible)
            UpdateLyricsLayout();
    }

    private void ToggleLyricsButton_Click(object sender, RoutedEventArgs e)
    {
        _lyricsPanelVisible = !_lyricsPanelVisible;
        UpdateLyricsLayout();
    }

    private void UpdateLyricsLayout()
    {
        LyricsPanel.Visibility = _lyricsPanelVisible ? Visibility.Visible : Visibility.Collapsed;
        ShowLyricsOnArtworkButton.Visibility = _lyricsPanelVisible ? Visibility.Collapsed : Visibility.Visible;

        if (_lyricsPanelVisible)
        {
            ArtworkColumn.Width = new GridLength(560);
            ArtworkGapColumn.Width = new GridLength(52);
            LyricsColumn.Width = new GridLength(1, GridUnitType.Star);
            System.Windows.Controls.Grid.SetColumn(NowPlayingInfoPanel, 2);
            System.Windows.Controls.Grid.SetColumnSpan(NowPlayingInfoPanel, 1);
            NowPlayingInfoPanel.Width = 900;
            NowPlayingInfoPanel.MinWidth = 900;
            NowPlayingInfoPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            NowPlayingTitleText.HorizontalAlignment = HorizontalAlignment.Stretch;
            NowPlayingTitleText.TextAlignment = TextAlignment.Left;
            NowPlayingArtistText.HorizontalAlignment = HorizontalAlignment.Stretch;
            NowPlayingArtistText.TextAlignment = TextAlignment.Left;
            SetArtworkSize(540);
            return;
        }

        ArtworkColumn.Width = new GridLength(1, GridUnitType.Star);
        ArtworkGapColumn.Width = new GridLength(0);
        LyricsColumn.Width = new GridLength(0);
        System.Windows.Controls.Grid.SetColumn(NowPlayingInfoPanel, 0);
        System.Windows.Controls.Grid.SetColumnSpan(NowPlayingInfoPanel, 3);
        NowPlayingInfoPanel.Width = Math.Min(900, Math.Max(540, ActualWidth - 120));
        NowPlayingInfoPanel.MinWidth = 0;
        NowPlayingInfoPanel.HorizontalAlignment = HorizontalAlignment.Center;
        NowPlayingTitleText.HorizontalAlignment = HorizontalAlignment.Center;
        NowPlayingTitleText.TextAlignment = TextAlignment.Center;
        NowPlayingArtistText.HorizontalAlignment = HorizontalAlignment.Center;
        NowPlayingArtistText.TextAlignment = TextAlignment.Center;

        double availableWidth = Math.Max(540, Math.Min(ActualWidth - 140, 780));
        double availableHeight = Math.Max(540, ActualHeight - 250);
        SetArtworkSize(Math.Min(availableWidth, availableHeight));
    }

    private void SetArtworkSize(double size)
    {
        double safeSize = Math.Max(540, Math.Min(size, 780));
        ArtworkSurface.Width = safeSize;
        ArtworkSurface.Height = safeSize;
        ArtworkProgressGrid.Width = safeSize;
        ArtworkImage.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(1, safeSize - 2), Math.Max(1, safeSize - 2)), 22, 22);
    }

    private void RefreshTrackPresentation()
    {
        NowPlayingTitleText.Text = _owner.CurrentTitle;
        NowPlayingArtistText.Text = _owner.CurrentArtist;
        ApplyArtwork(_owner.CurrentAlbumArt);

        string? path = _owner.CurrentTrackPath;
        if (!string.Equals(path, _lyricsTrackPath, StringComparison.OrdinalIgnoreCase))
        {
            CancelOnlineSearch();
            HideOnlineLyricsSearch();
            _ = LoadLyricsAsync(path);
        }
    }

    private void ApplyArtwork(BitmapImage? artwork)
    {
        ArtworkImage.Source = artwork;
        ArtworkImage.Visibility = artwork is null ? Visibility.Collapsed : Visibility.Visible;
        ArtworkPlaceholder.Visibility = artwork is null ? Visibility.Visible : Visibility.Collapsed;
        ApplyArtworkPalette(artwork);
    }

    private void ApplyArtworkPalette(BitmapSource? artwork)
    {
        // У каждой видимой сферы — один самостоятельный цвет, вычисленный из RGB-кластеров обложки.
        // Цвет становится мягче к периферии, чтобы яркие края не выглядели сплошной заливкой.
        System.Windows.Media.Color[] cloudColors = artwork is null ? CreateFallbackPalette() : ExtractArtworkPalette(artwork);

        AmbientBlobOne.Fill = CreateSoftCloudBrush(cloudColors[0]);
        AmbientBlobTwo.Fill = CreateSoftCloudBrush(cloudColors[1]);
        AmbientBlobThree.Fill = CreateSoftCloudBrush(cloudColors[2]);
        AmbientBlobFour.Fill = CreateSoftCloudBrush(cloudColors[3]);
        AmbientBlobFive.Fill = CreateSoftCloudBrush(cloudColors[4]);
        AmbientBlobSix.Fill = CreateSoftCloudBrush(cloudColors[0]);
        AmbientBlobSeven.Fill = CreateSoftCloudBrush(cloudColors[2]);
        AmbientBlobEight.Fill = CreateSoftCloudBrush(cloudColors[4]);
    }

    private static RadialGradientBrush CreateSoftCloudBrush(System.Windows.Media.Color color)
    {
        System.Windows.Media.Color muted = System.Windows.Media.Color.FromRgb(
            (byte)(color.R * 0.48), (byte)(color.G * 0.48), (byte)(color.B * 0.48));
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.42, 0.40),
            GradientOrigin = new Point(0.42, 0.40),
            RadiusX = 0.74,
            RadiusY = 0.74
        };
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(225, color.R, color.G, color.B), 0.0));
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(152, color.R, color.G, color.B), 0.40));
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(48, muted.R, muted.G, muted.B), 0.74));
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0, muted.R, muted.G, muted.B), 1.0));
        brush.Freeze();
        return brush;
    }

    private static System.Windows.Media.Color[] ExtractArtworkPalette(BitmapSource artwork)
    {
        try
        {
            double scale = Math.Min(1.0, 128.0 / Math.Max(artwork.PixelWidth, artwork.PixelHeight));
            var small = new TransformedBitmap(artwork, new System.Windows.Media.ScaleTransform(scale, scale));
            var pixels = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int width = pixels.PixelWidth;
            int height = pixels.PixelHeight;
            if (width <= 0 || height <= 0) return CreateFallbackPalette();

            int stride = width * 4;
            byte[] buffer = new byte[stride * height];
            pixels.CopyPixels(buffer, stride, 0);
            var samples = new List<PalettePoint>();
            for (int index = 0; index + 3 < buffer.Length; index += 12)
            {
                byte alpha = buffer[index + 3];
                if (alpha < 32) continue;
                samples.Add(new PalettePoint(buffer[index + 2], buffer[index + 1], buffer[index], alpha / 255.0));
            }
            if (samples.Count == 0) return CreateFallbackPalette();

            const int colorStep = 32;
            var buckets = new Dictionary<int, PalettePoint>();
            foreach (PalettePoint sample in samples)
            {
                int key = ((int)sample.Red / colorStep << 16) | ((int)sample.Green / colorStep << 8) | (int)sample.Blue / colorStep;
                if (buckets.TryGetValue(key, out PalettePoint bucket))
                    buckets[key] = new PalettePoint(bucket.Red + sample.Red * sample.Weight, bucket.Green + sample.Green * sample.Weight,
                        bucket.Blue + sample.Blue * sample.Weight, bucket.Weight + sample.Weight);
                else
                    buckets[key] = new PalettePoint(sample.Red * sample.Weight, sample.Green * sample.Weight,
                        sample.Blue * sample.Weight, sample.Weight);
            }

            var seeds = buckets.Values
                .Where(bucket => bucket.Weight > 0)
                .Select(bucket => new PalettePoint(bucket.Red / bucket.Weight, bucket.Green / bucket.Weight,
                    bucket.Blue / bucket.Weight, bucket.Weight))
                .OrderByDescending(bucket => bucket.Weight)
                .ToList();
            if (seeds.Count == 0) return CreateFallbackPalette();

            var centers = new List<PalettePoint>();
            while (centers.Count < 5 && centers.Count < seeds.Count)
            {
                PalettePoint choice = seeds
                    .Where(seed => !centers.Contains(seed))
                    .OrderByDescending(seed => centers.Count == 0
                        ? seed.Weight
                        : seed.Weight * Math.Sqrt(centers.Min(center => ColorDistanceSquared(seed, center))))
                    .First();
                centers.Add(new PalettePoint(choice.Red, choice.Green, choice.Blue, 1.0));
            }
            while (centers.Count < 5)
                centers.Add(centers[centers.Count % Math.Max(1, centers.Count)]);

            for (int iteration = 0; iteration < 5; iteration++)
            {
                double[] redTotals = new double[5];
                double[] greenTotals = new double[5];
                double[] blueTotals = new double[5];
                double[] weights = new double[5];
                foreach (PalettePoint sample in samples)
                {
                    int nearest = Enumerable.Range(0, centers.Count)
                        .OrderBy(index => ColorDistanceSquared(sample, centers[index]))
                        .First();
                    redTotals[nearest] += sample.Red * sample.Weight;
                    greenTotals[nearest] += sample.Green * sample.Weight;
                    blueTotals[nearest] += sample.Blue * sample.Weight;
                    weights[nearest] += sample.Weight;
                }
                for (int index = 0; index < centers.Count; index++)
                {
                    if (weights[index] > 0)
                        centers[index] = new PalettePoint(redTotals[index] / weights[index], greenTotals[index] / weights[index],
                            blueTotals[index] / weights[index], weights[index]);
                }
            }

            return centers
                .OrderByDescending(center => center.Weight)
                .Select(ToColor)
                .ToArray();
        }
        catch
        {
            return CreateFallbackPalette();
        }
    }

    private static System.Windows.Media.Color ToColor(PalettePoint point) =>
        System.Windows.Media.Color.FromRgb((byte)Math.Clamp(point.Red, 0, 255),
            (byte)Math.Clamp(point.Green, 0, 255), (byte)Math.Clamp(point.Blue, 0, 255));

    private static double ColorDistanceSquared(PalettePoint first, PalettePoint second)
    {
        double red = first.Red - second.Red;
        double green = first.Green - second.Green;
        double blue = first.Blue - second.Blue;
        return red * red + green * green + blue * blue;
    }

    private static System.Windows.Media.Color[] CreateFallbackPalette() => new[]
    {
        System.Windows.Media.Color.FromRgb(82, 120, 255),
        System.Windows.Media.Color.FromRgb(82, 178, 235),
        System.Windows.Media.Color.FromRgb(158, 98, 224),
        System.Windows.Media.Color.FromRgb(240, 142, 104),
        System.Windows.Media.Color.FromRgb(92, 212, 166)
    };

    private static System.Windows.Media.Color MixColors(System.Windows.Media.Color first,
        System.Windows.Media.Color second, double secondWeight)
    {
        double weight = Math.Clamp(secondWeight, 0, 1);
        return System.Windows.Media.Color.FromRgb(
            (byte)(first.R * (1 - weight) + second.R * weight),
            (byte)(first.G * (1 - weight) + second.G * weight),
            (byte)(first.B * (1 - weight) + second.B * weight));
    }

    private void CancelLyricsLoad()
    {
        _lyricsLoadCts?.Cancel();
        _lyricsLoadCts?.Dispose();
        _lyricsLoadCts = null;
    }

    private void CancelOnlineSearch()
    {
        _onlineSearchCts?.Cancel();
        _onlineSearchCts?.Dispose();
        _onlineSearchCts = null;
    }

    private async Task LoadLyricsAsync(string? trackPath)
    {
        CancelLyricsLoad();
        _lyricsLoadCts = new CancellationTokenSource();
        CancellationToken token = _lyricsLoadCts.Token;

        _lyricsTrackPath = trackPath;
        _lyrics = LyricsDocument.Empty;
        _activeLyricIndex = -2;
        ApplyLyricsDocument(_lyrics);

        try
        {
            LyricsDocument document = await LyricsService.LoadAsync(trackPath, token);
            if (token.IsCancellationRequested || !string.Equals(trackPath, _lyricsTrackPath, StringComparison.OrdinalIgnoreCase))
                return;

            _lyrics = document;
            ApplyLyricsDocument(document);
            UpdateProgress(_owner.CurrentPlaybackSeconds, _owner.CurrentTrackDurationSeconds);

            // Если локального LRC/TXT и текста в теге нет, пробуем автоматически найти
            // только точное совпадение в LRCLIB. Неоднозначные варианты не подставляются
            // молча: их пользователь по-прежнему может выбрать через встроенную лупу.
            if (document.Kind == LyricsKind.None && !string.IsNullOrWhiteSpace(trackPath))
                await TryAutoFindLyricsAsync(trackPath, token);
        }
        catch (OperationCanceledException)
        {
            // Обычный переход между треками: более новая задача уже загрузит следующий текст.
        }
        catch
        {
            _lyrics = LyricsDocument.Empty;
            ApplyLyricsDocument(_lyrics);
        }
    }

    private async Task TryAutoFindLyricsAsync(string trackPath, CancellationToken lifetimeToken)
    {
        CancelOnlineSearch();
        var searchCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        _onlineSearchCts = searchCts;
        CancellationToken token = searchCts.Token;

        try
        {
            LyricsModeText.Text = LocalizationService.Translate("Ищем текст…");
            IReadOnlyList<OnlineLyricsResult> results = await LyricsService.SearchOnlineAsync(
                _owner.CurrentTitle, _owner.CurrentArtist, token);
            if (token.IsCancellationRequested || !string.Equals(trackPath, _owner.CurrentTrackPath, StringComparison.OrdinalIgnoreCase))
                return;

            OnlineLyricsResult? exact = results.FirstOrDefault(result =>
                SameTrackField(result.TrackName, _owner.CurrentTitle) &&
                SameTrackField(result.ArtistName, _owner.CurrentArtist));
            if (exact is null)
            {
                LyricsModeText.Text = LocalizationService.Translate(results.Count > 0 ? "Нужен выбор варианта" : "Нет текста");
                return;
            }

            await LyricsService.SaveOnlineResultAsync(trackPath, exact, token);
            if (token.IsCancellationRequested || !string.Equals(trackPath, _owner.CurrentTrackPath, StringComparison.OrdinalIgnoreCase))
                return;

            _lyrics = LyricsService.CreateDocumentFromOnlineResult(exact);
            ApplyLyricsDocument(_lyrics);
            UpdateProgress(_owner.CurrentPlaybackSeconds, _owner.CurrentTrackDurationSeconds);
        }
        catch (LyricsRateLimitException)
        {
            LyricsModeText.Text = LocalizationService.Translate("Поиск временно ограничен");
        }
        catch (OperationCanceledException)
        {
            // Нормально при смене трека или запуске ручного поиска.
        }
        catch
        {
            LyricsModeText.Text = LocalizationService.Translate("Нет текста");
        }
        finally
        {
            if (ReferenceEquals(_onlineSearchCts, searchCts))
                _onlineSearchCts = null;
            searchCts.Dispose();
        }
    }

    private static bool SameTrackField(string left, string right)
    {
        static string Normalize(string value) => new(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

        string normalizedLeft = Normalize(left);
        string normalizedRight = Normalize(right);
        return normalizedLeft.Length > 0 && normalizedLeft == normalizedRight;
    }

    private void ApplyLyricsDocument(LyricsDocument document)
    {
        _syncedLines.Clear();
        foreach (LyricLine line in document.Lines)
            _syncedLines.Add(line);

        LyricsHeaderText.Text = LocalizationService.Translate(document.Kind == LyricsKind.Synced ? "Синхронный текст" : "Текст песни");
        LyricsModeText.Text = LocalizationService.Translate(document.SourceLabel);
        SyncedLyricsList.Visibility = document.Kind == LyricsKind.Synced ? Visibility.Visible : Visibility.Collapsed;
        PlainLyricsScroll.Visibility = document.Kind == LyricsKind.Plain ? Visibility.Visible : Visibility.Collapsed;
        NoLyricsPanel.Visibility = document.Kind == LyricsKind.None ? Visibility.Visible : Visibility.Collapsed;
        PlainLyricsText.Text = document.Kind == LyricsKind.Plain ? document.PlainText : string.Empty;
        _activeLyricIndex = -2;
    }

    private void UpdatePlaybackState(bool isPlaying)
    {
        UpdateAmbientAnimation(isPlaying);
        string icon = isPlaying ? "IconPause" : "IconPlay";
        string toolTip = LocalizationService.Translate(isPlaying ? "Пауза" : "Воспроизвести");
        ArtworkPlayPauseIcon.Icon = icon;
        ArtworkPlayPauseButton.ToolTip = toolTip;
    }

    private void InitializeAmbientAnimation()
    {
        if (_ambientMotionTimer is not null) return;

        _ambientMotionTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _ambientMotionTimer.Tick += AmbientMotionTimer_Tick;
        UpdateAmbientAnimation(_owner.IsPlayingNow);
    }

    private void AmbientMotionTimer_Tick(object? sender, EventArgs e)
    {
        // Каждая сфера медленно проходит сцену по собственной линии и мягко растворяется у края.
        // Прозрачность зависит от фактически видимой части окружности, поэтому исчезновение не выглядит резким.
        _ambientSpeed += (_ambientTargetSpeed - _ambientSpeed) * 0.032;
        _ambientMotionTime += 0.015 * _ambientSpeed;
        double width = Math.Max(1, ActualWidth);
        double height = Math.Max(1, ActualHeight);
        double[] radii = { 270, 270, 270, 270, 270 };
        double[] baseOpacities = { 0.82, 0.76, 0.68, 0.64, 0.54 };
        var transforms = new[] { AmbientBlobOneTransform, AmbientBlobTwoTransform, AmbientBlobThreeTransform, AmbientBlobFourTransform, AmbientBlobFiveTransform };
        var scales = new[] { AmbientBlobOneScale, AmbientBlobTwoScale, AmbientBlobThreeScale, AmbientBlobFourScale, AmbientBlobFiveScale };
        var blobs = new[] { AmbientBlobOne, AmbientBlobTwo, AmbientBlobThree, AmbientBlobFour, AmbientBlobFive };

        for (int index = 0; index < _flowClouds.Length; index++)
        {
            FlowCloud cloud = GetFlowCloud(index, width, height);
            double travelDuration = 1.0 / cloud.TravelRate;
            double cycleDuration = travelDuration + cloud.HiddenPauseDuration;
            double cycleOffset = (_ambientMotionTime + cloud.MotionPhase * cycleDuration) % cycleDuration;
            if (cycleOffset > travelDuration)
            {
                // Интервал отдыха за экраном. Переход на новую стартовую точку происходит при нулевой прозрачности.
                blobs[index].Opacity = 0;
                continue;
            }

            double travel = cycleOffset / travelDuration;
            double baseX = (cloud.Entry.X + (cloud.Exit.X - cloud.Entry.X) * travel) * width;
            double baseY = (cloud.Entry.Y + (cloud.Exit.Y - cloud.Entry.Y) * travel) * height;
            double lineX = (cloud.Exit.X - cloud.Entry.X) * width;
            double lineY = (cloud.Exit.Y - cloud.Entry.Y) * height;
            double lineLength = Math.Max(1.0, Math.Sqrt(lineX * lineX + lineY * lineY));
            double normalX = -lineY / lineLength;
            double normalY = lineX / lineLength;
            double wave = Math.Sin((travel * cloud.WaveCycles + cloud.MotionPhase) * Math.PI * 2.0) * cloud.WaveAmplitude;
            double verticalDrift = Math.Sin((travel * cloud.VerticalDriftCycles + cloud.MotionPhase * 1.37) * Math.PI * 2.0)
                * cloud.VerticalDriftAmplitude;
            double positionX = baseX + normalX * wave;
            double positionY = baseY + normalY * wave + verticalDrift;

            double scale = 0.93 + (Math.Sin(_ambientMotionTime * (0.070 + index * 0.012) + cloud.ScalePhase) + 1.0) * 0.055;
            scales[index].ScaleX = scale;
            scales[index].ScaleY = scale;
            double edgeFade = Math.Sqrt(EdgeVisibility(positionX, width, radii[index])
                * EdgeVisibility(positionY, height, radii[index]));
            double breathing = 0.74 + (Math.Sin(_ambientMotionTime * (0.060 + index * 0.010) + cloud.OpacityPhase) + 1.0) * 0.09;
            blobs[index].Opacity = baseOpacities[index] * breathing * edgeFade;
            SetAmbientPosition(transforms[index], positionX - radii[index], positionY - radii[index]);
        }
    }

    private FlowCloud GetFlowCloud(int index, double width, double height)
    {
        FlowCloud? existing = _flowClouds[index];
        if (existing is not null) return existing;

        Point[] entries =
        {
            new(-0.18, 0.18), new(1.16, 0.30), new(0.14, -0.20), new(0.82, 1.18), new(-0.16, 0.72)
        };
        Point[] exits =
        {
            new(1.18, 0.72), new(-0.18, 0.76), new(0.78, 1.18), new(0.20, -0.18), new(1.16, 0.42)
        };
        var cloud = new FlowCloud
        {
            Entry = entries[index],
            Exit = exits[index],
            TravelRate = 0.112 + index * 0.010,
            HiddenPauseDuration = 0.22 + index * 0.05,
            WaveAmplitude = 28 + index * 5,
            WaveCycles = 0.34 + index * 0.08,
            VerticalDriftAmplitude = 54 + index * 9,
            VerticalDriftCycles = 0.54 + index * 0.11,
            MotionPhase = 0.11 + index * 0.173,
            ScalePhase = index * 1.41 + 0.37,
            OpacityPhase = index * 2.17 + 0.91
        };
        _flowClouds[index] = cloud;
        return cloud;
    }

    private static double EdgeVisibility(double center, double length, double radius)
    {
        // Отсчёт от ближайшей границы видимой окружности, а не от центра сферы.
        double enter = SmoothStep(0, radius * 1.55, center + radius);
        double leave = SmoothStep(0, radius * 1.55, length - center + radius);
        return enter * leave;
    }

    private static double SmoothStep(double start, double end, double value)
    {
        double normalized = Math.Clamp((value - start) / Math.Max(0.001, end - start), 0, 1);
        return normalized * normalized * (3.0 - 2.0 * normalized);
    }

    private static void SetAmbientPosition(System.Windows.Media.TranslateTransform transform, double x, double y)
    {
        transform.X = x;
        transform.Y = y;
    }

    private void UpdateAmbientAnimation(bool isPlaying)
    {
        _ambientTargetSpeed = isPlaying ? 1.0 : 0.15;
        if (_ambientMotionTimer is not null)
            _ambientMotionTimer.IsEnabled = true;
    }

    private void UpdateProgress(double currentSeconds, double totalSeconds)
    {
        double current = Math.Max(currentSeconds, 0);
        double total = Math.Max(totalSeconds, 0);
        PlaybackTimeText.Text = TimeSpan.FromSeconds(current).ToString(@"mm\:ss");
        PlaybackDurationText.Text = TimeSpan.FromSeconds(total).ToString(@"mm\:ss");
        ArtworkProgressBar.Maximum = total > 0 ? total : 1;
        ArtworkProgressBar.Value = Math.Min(current, ArtworkProgressBar.Maximum);

        if (_lyrics.Kind != LyricsKind.Synced) return;
        int index = LyricsService.FindActiveLineIndex(_lyrics.Lines, TimeSpan.FromSeconds(current));
        if (index == _activeLyricIndex) return;

        _activeLyricIndex = index;
        SyncedLyricsList.SelectedIndex = index;
        if (index >= 0 && index < _syncedLines.Count)
            SyncedLyricsList.ScrollIntoView(_syncedLines[index]);
    }

    private void ArtworkProgressBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ArtworkProgressBar.ActualWidth <= 0 || _owner.CurrentTrackDurationSeconds <= 0) return;

        double clickX = e.GetPosition(ArtworkProgressBar).X;
        double ratio = Math.Clamp(clickX / ArtworkProgressBar.ActualWidth, 0.0, 1.0);
        _owner.ExternalSeekRatio(ratio);
        e.Handled = true;
    }

    private void ArtworkSurface_MouseEnter(object sender, MouseEventArgs e) => AnimateArtworkControls(visible: true);
    private void ArtworkSurface_MouseLeave(object sender, MouseEventArgs e) => AnimateArtworkControls(visible: false);

    private void AnimateArtworkControls(bool visible)
    {
        var animation = new DoubleAnimation(visible ? 1.0 : 0.0, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        ArtworkControlsOverlay.BeginAnimation(OpacityProperty, animation);
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => _owner.ExternalPlayPause();
    private void NextButton_Click(object sender, RoutedEventArgs e) => _owner.ExternalNext();
    private void PreviousButton_Click(object sender, RoutedEventArgs e) => _owner.ExternalPrev();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void FindLyricsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetCurrentTrackPath(out _)) return;

        CancelOnlineSearch();
        _onlineResults.Clear();
        OnlineLyricsSearchPanel.Visibility = Visibility.Visible;
        LyricsSearchQueryTextBox.Text = BuildDefaultLyricsSearchQuery();
        OnlineLyricsSearchStatusText.Text = LocalizationService.Translate("Проверьте запрос и нажмите «Искать» или Enter.");
        LyricsSearchQueryTextBox.Focus();
        LyricsSearchQueryTextBox.SelectAll();
    }

    private async void RunLyricsSearchButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetCurrentTrackPath(out string trackPath)) return;
        await SearchLyricsAsync(trackPath);
    }

    private async void LyricsSearchQueryTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (!TryGetCurrentTrackPath(out string trackPath)) return;
        await SearchLyricsAsync(trackPath);
    }

    private bool TryGetCurrentTrackPath(out string trackPath)
    {
        trackPath = _owner.CurrentTrackPath ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(trackPath) && File.Exists(trackPath)) return true;

        LocalizedMessageBox.Show(this, "Сначала выберите или запустите трек.", "Поиск текста",
            MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private string BuildDefaultLyricsSearchQuery()
    {
        string title = _owner.CurrentTitle.Trim();
        string artist = _owner.CurrentArtist.Trim();
        return string.IsNullOrWhiteSpace(artist) || artist == "—"
            ? title
            : $"{artist} — {title}";
    }

    private static (string TrackName, string ArtistName) ParseLyricsSearchQuery(string query)
    {
        string text = query.Trim();
        int separator = text.IndexOf(" — ", StringComparison.Ordinal);
        int separatorLength = 3;
        if (separator < 0)
        {
            separator = text.IndexOf(" - ", StringComparison.Ordinal);
            separatorLength = 3;
        }

        return separator > 0 && separator + separatorLength < text.Length
            ? (text[(separator + separatorLength)..].Trim(), text[..separator].Trim())
            : (text, string.Empty);
    }

    private async Task SearchLyricsAsync(string trackPath)
    {
        (string trackName, string artistName) = ParseLyricsSearchQuery(LyricsSearchQueryTextBox.Text ?? string.Empty);
        if (string.IsNullOrWhiteSpace(trackName))
        {
            OnlineLyricsSearchStatusText.Text = LocalizationService.Translate("Введите название трека или «исполнитель — название».");
            LyricsSearchQueryTextBox.Focus();
            return;
        }

        CancelOnlineSearch();
        var searchCts = new CancellationTokenSource();
        _onlineSearchCts = searchCts;
        CancellationToken token = searchCts.Token;
        _onlineResults.Clear();
        OnlineLyricsSearchPanel.Visibility = Visibility.Visible;
        OnlineLyricsSearchStatusText.Text = LocalizationService.Translate("Ищу текст…");
        FindLyricsButton.IsEnabled = false;
        RunLyricsSearchButton.IsEnabled = false;
        LyricsSearchQueryTextBox.IsEnabled = false;

        try
        {
            IReadOnlyList<OnlineLyricsResult> results = await LyricsService.SearchOnlineVariantsAsync(trackName, artistName, token);
            if (token.IsCancellationRequested || !string.Equals(trackPath, _owner.CurrentTrackPath, StringComparison.OrdinalIgnoreCase))
                return;

            foreach (OnlineLyricsResult result in results)
                _onlineResults.Add(result);

            OnlineLyricsSearchStatusText.Text = _onlineResults.Count == 0
                ? LocalizationService.Translate("Во встроенной базе совпадения не найдены. Измените запрос или откройте Genius ниже.")
                : LocalizationService.Format("Найдено вариантов: {0}. Дважды кликните нужный вариант, чтобы сохранить его рядом с аудиофайлом.", _onlineResults.Count);
        }
        catch (LyricsRateLimitException ex)
        {
            string wait = ex.RetryAfter is { } delay && delay > TimeSpan.Zero
                ? LocalizationService.Format(" Попробуйте снова через {0} сек.", Math.Ceiling(delay.TotalSeconds))
                : LocalizationService.Translate(" Попробуйте снова немного позже.");
            OnlineLyricsSearchStatusText.Text = LocalizationService.Translate("Сервис временно ограничил запросы.") + wait;
        }
        catch (OperationCanceledException)
        {
            // Поиск отменён при смене трека, новом запросе или закрытии окна.
        }
        catch
        {
            OnlineLyricsSearchStatusText.Text = LocalizationService.Translate("Не удалось выполнить поиск. Проверьте подключение к интернету.");
        }
        finally
        {
            if (ReferenceEquals(_onlineSearchCts, searchCts))
                _onlineSearchCts = null;
            searchCts.Dispose();
            FindLyricsButton.IsEnabled = true;
            RunLyricsSearchButton.IsEnabled = true;
            LyricsSearchQueryTextBox.IsEnabled = true;
        }
    }

    private void OpenGeniusSearchButton_Click(object sender, RoutedEventArgs e) =>
        OpenExternalLyricsSearch("https://genius.com/search?q=");

    private async void PasteLyricsFromClipboardButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetCurrentTrackPath(out string trackPath)) return;

        string text;
        try
        {
            if (!Clipboard.ContainsText())
            {
                OnlineLyricsSearchStatusText.Text = LocalizationService.Translate("Сначала скопируйте текст песни в буфер обмена.");
                return;
            }

            text = Clipboard.GetText().Trim();
        }
        catch
        {
            OnlineLyricsSearchStatusText.Text = LocalizationService.Translate("Не удалось прочитать буфер обмена. Скопируйте текст ещё раз.");
            return;
        }

        const int maxLyricsBytes = 2 * 1024 * 1024;
        if (string.IsNullOrWhiteSpace(text))
        {
            OnlineLyricsSearchStatusText.Text = LocalizationService.Translate("В буфере нет текста песни.");
            return;
        }
        if (Encoding.UTF8.GetByteCount(text) > maxLyricsBytes)
        {
            OnlineLyricsSearchStatusText.Text = LocalizationService.Translate("Скопированный текст больше 2 МБ и не был сохранён.");
            return;
        }

        try
        {
            string destination = Path.ChangeExtension(trackPath, ".txt");
            await File.WriteAllTextAsync(destination, text + Environment.NewLine);
            await LyricsService.SavePastedLyricsAsync(trackPath, text);
            if (!string.Equals(trackPath, _owner.CurrentTrackPath, StringComparison.OrdinalIgnoreCase))
                return;

            HideOnlineLyricsSearch();
            _lyricsTrackPath = null;
            await LoadLyricsAsync(trackPath);
        }
        catch (Exception ex)
        {
            OnlineLyricsSearchStatusText.Text = LocalizationService.Translate($"Не удалось сохранить текст: {ex.Message}");
        }
    }

    private string GetLyricsSearchQuery() =>
        (LyricsSearchQueryTextBox.Text ?? BuildDefaultLyricsSearchQuery()).Trim();

    private void OpenExternalLyricsSearch(string baseUrl)
    {
        string query = GetLyricsSearchQuery();
        if (string.IsNullOrWhiteSpace(query))
        {
            OnlineLyricsSearchStatusText.Text = LocalizationService.Translate("Введите запрос, чтобы открыть внешний поиск.");
            LyricsSearchQueryTextBox.Focus();
            return;
        }

        try
        {
            string url = baseUrl + Uri.EscapeDataString(query);
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            OnlineLyricsSearchStatusText.Text = LocalizationService.Translate("Не удалось открыть браузер для внешнего поиска.");
        }
    }

    private async void OnlineLyricsResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (OnlineLyricsResultsList.SelectedItem is not OnlineLyricsResult result) return;
        string? trackPath = _owner.CurrentTrackPath;
        if (string.IsNullOrWhiteSpace(trackPath) || !File.Exists(trackPath)) return;

        OnlineLyricsSearchStatusText.Text = LocalizationService.Translate("Сохраняю выбранный текст рядом с аудиофайлом…");
        OnlineLyricsResultsList.IsEnabled = false;
        try
        {
            await LyricsService.SaveOnlineResultAsync(trackPath, result, CancellationToken.None);
            if (!string.Equals(trackPath, _owner.CurrentTrackPath, StringComparison.OrdinalIgnoreCase))
                return;

            HideOnlineLyricsSearch();
            _lyricsTrackPath = null;
            await LoadLyricsAsync(trackPath);
        }
        catch (Exception ex)
        {
            OnlineLyricsSearchStatusText.Text = LocalizationService.Translate($"Не удалось сохранить текст: {ex.Message}");
            OnlineLyricsResultsList.IsEnabled = true;
        }
    }

    private void CloseOnlineLyricsSearchButton_Click(object sender, RoutedEventArgs e)
    {
        CancelOnlineSearch();
        HideOnlineLyricsSearch();
    }

    private void HideOnlineLyricsSearch()
    {
        OnlineLyricsSearchPanel.Visibility = Visibility.Collapsed;
        OnlineLyricsResultsList.IsEnabled = true;
        FindLyricsButton.IsEnabled = true;
        RunLyricsSearchButton.IsEnabled = true;
        LyricsSearchQueryTextBox.IsEnabled = true;
    }

    private async void ImportLyricsButton_Click(object sender, RoutedEventArgs e)
    {
        string? trackPath = _owner.CurrentTrackPath;
        if (string.IsNullOrWhiteSpace(trackPath) || !File.Exists(trackPath))
        {
            LocalizedMessageBox.Show(this, "Сначала выберите или запустите трек.", "Загрузка текста",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = LocalizationService.Translate("Выберите текст песни"),
            Filter = LocalizationService.Translate("Синхронный текст LRC (*.lrc)|*.lrc|Обычный текст (*.txt)|*.txt"),
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var source = new FileInfo(dialog.FileName);
            const long maxLyricsFileSize = 2 * 1024 * 1024;
            if (source.Length > maxLyricsFileSize)
            {
                LocalizedMessageBox.Show(this, "Файл текста больше 2 МБ и не был добавлен.", "Загрузка текста",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string extension = Path.GetExtension(source.Name).Equals(".lrc", StringComparison.OrdinalIgnoreCase)
                ? ".lrc"
                : ".txt";
            string destination = Path.ChangeExtension(trackPath, extension);
            if (!string.Equals(source.FullName, destination, StringComparison.OrdinalIgnoreCase))
                File.Copy(source.FullName, destination, overwrite: true);

            // Нельзя применять текст, если за время выбора файла успел смениться трек.
            if (!string.Equals(trackPath, _owner.CurrentTrackPath, StringComparison.OrdinalIgnoreCase))
                return;

            HideOnlineLyricsSearch();
            _lyricsTrackPath = null;
            await LoadLyricsAsync(trackPath);
        }
        catch (Exception ex)
        {
            LocalizedMessageBox.Show(this, $"Не удалось добавить текст песни.\n\n{ex.Message}", "Загрузка текста",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void NowPlayingWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.F11)
        {
            if (OnlineLyricsSearchPanel.Visibility == Visibility.Visible && e.Key == Key.Escape)
                CloseOnlineLyricsSearchButton_Click(this, e);
            else
                Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Space)
        {
            _owner.ExternalPlayPause();
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            _owner.ExternalNext();
            e.Handled = true;
        }
        else if (e.Key == Key.Left)
        {
            _owner.ExternalPrev();
            e.Handled = true;
        }
    }

    // Клик по строке лишь фиксирует её в прокрутке: чтение текста не меняет позицию трека.
    private void SyncedLyricsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
    }
}
