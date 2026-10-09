using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using static Lumisense.BackgroundTask;

namespace Lumisense;

// Панель текста песни в главном окне: загрузка (локально + онлайн), синхронный LRC-скролл и анимации строк. Часть partial-класса
// MainWindow — вынесено в отдельный файл только ради размера MainWindow.xaml.cs, без изменения логики и видимости.
public partial class MainWindow
{
    // Панель текста занимает место плейлиста, не создавая второго окна. Отдельный CTS
    // отменяет локальное чтение и онлайн-поиск при смене трека, скрытии панели или закрытии.
    private bool _isLyricsPanelActive;
    private CancellationTokenSource? _mainWindowLyricsCts;
    private string? _mainWindowLyricsTrackPath;
    private LyricsDocument _mainWindowLyrics = LyricsDocument.Empty;
    private readonly ObservableCollection<MainWindowLyricLine> _mainWindowSyncedLyrics = new();

    // У ScrollViewer нет анимируемого DependencyProperty для VerticalOffset: attached-свойство проксирует анимацию в
    // ScrollToVerticalOffset, чтобы LRC-строка двигалась плавно, а не перескакивала.
    private static readonly DependencyProperty AnimatedScrollOffsetProperty = DependencyProperty.RegisterAttached(
        "AnimatedScrollOffset", typeof(double), typeof(MainWindow),
        new PropertyMetadata(0.0, OnAnimatedScrollOffsetChanged));
    private int _activeMainWindowLyricIndex = -2;

    private sealed class MainWindowLyricLine : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isActive;
        private double _fontSize = 14;
        private double _lineHeight = 23;

        public required TimeSpan Time { get; init; }
        public required string Text { get; init; }
        public SolidColorBrush Foreground { get; } = new(Color.FromRgb(142, 142, 142));
        public ScaleTransform ScaleTransform { get; } = new(1, 1);
        public System.Windows.Media.Effects.DropShadowEffect GlowEffect { get; } = new()
        {
            Color = Colors.White,
            BlurRadius = 16,
            ShadowDepth = 0,
            Opacity = 0
        };

        public bool IsActive
        {
            get => _isActive;
            set => Set(ref _isActive, value, nameof(IsActive));
        }

        public double FontSize
        {
            get => _fontSize;
            set => Set(ref _fontSize, value, nameof(FontSize));
        }

        public double LineHeight
        {
            get => _lineHeight;
            set => Set(ref _lineHeight, value, nameof(LineHeight));
        }

        private void Set<T>(ref T field, T value, string propertyName)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    void IIntegrationHost.LyricsPanelButton_Click(object sender, RoutedEventArgs e) => LyricsPanelButton_Click(sender, e);

    private void LyricsPanelButton_Click(object sender, RoutedEventArgs e)
    {
        SetLyricsPanelActive(!_isLyricsPanelActive);
    }

    private void SetLyricsPanelActive(bool active)
    {
        if (active && !_isPlaylistVisible)
            SetPlaylistVisibility(true);

        _isLyricsPanelActive = active;
        bool wasFavoritesView = _isFavoritesView;
        if (active)
        {
            _isFavoritesView = false;
            FavoritesButtonIcon.Icon = "IconHeart";
            IconResources.SetOnAccent(FavoritesButtonIcon, false);
        }

        UpdatePlaylistSurface();
        if (active && wasFavoritesView)
            QueuePlaylistSearch();

        if (active)
            FireAndForget(LoadMainWindowLyricsAsync(_currentTrackPath), "LoadMainWindowLyricsAsync");
        else
            CancelMainWindowLyricsLoad();
    }

    private void CancelMainWindowLyricsLoad()
    {
        CancellationTokenSource? previous = Interlocked.Exchange(ref _mainWindowLyricsCts, null);
        previous?.Cancel();
    }

    private async Task LoadMainWindowLyricsAsync(string? trackPath)
    {
        CancelMainWindowLyricsLoad();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _mainWindowLyricsCts = cts;
        CancellationToken token = cts.Token;
        _mainWindowLyricsTrackPath = trackPath;
        _mainWindowLyrics = LyricsDocument.Empty;
        ApplyMainWindowLyricsLoading();

        try
        {
            LyricsDocument document = await LyricsService.LoadAsync(trackPath, token);
            if (!IsMainWindowLyricsRequestCurrent(trackPath, token)) return;

            // Такое же безопасное автодополнение, как в Now Playing: онлайн-результат берём
            // только при точном совпадении title + artist и сохраняем в соседний LRC/TXT.
            if (document.Kind == LyricsKind.None && !string.IsNullOrWhiteSpace(trackPath))
            {
                LyricsPanelSourceText.Text = LocalizationService.Translate("Ищем текст…");
                LyricsService.AutomaticLyricsLookup lookup = await LyricsService.FindAutomaticAsync(
                    CurrentTitle, CurrentArtist, CurrentTrackDurationSeconds, token);
                if (!IsMainWindowLyricsRequestCurrent(trackPath, token)) return;

                OnlineLyricsResult? exact = lookup.Best;
                if (exact is not null)
                {
                    await LyricsService.SaveOnlineResultAsync(trackPath, exact, token);
                    if (!IsMainWindowLyricsRequestCurrent(trackPath, token)) return;
                    document = LyricsService.CreateDocumentFromOnlineResult(exact);
                }
            }

            _mainWindowLyrics = document;
            ApplyMainWindowLyricsDocument(document);
        }
        catch (LyricsRateLimitException)
        {
            if (IsMainWindowLyricsRequestCurrent(trackPath, token))
                ApplyMainWindowLyricsEmpty("Поиск временно ограничен");
        }
        catch (OperationCanceledException)
        {
            // Нормально при смене трека, закрытии панели или завершении приложения.
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось загрузить текст для панели главного окна", ex);
            if (IsMainWindowLyricsRequestCurrent(trackPath, token))
                ApplyMainWindowLyricsEmpty("Текст не найден");
        }
        finally
        {
            if (ReferenceEquals(_mainWindowLyricsCts, cts))
                _mainWindowLyricsCts = null;
            cts.Dispose();
        }
    }

    private bool IsMainWindowLyricsRequestCurrent(string? trackPath, CancellationToken token) =>
        _isLyricsPanelActive && !token.IsCancellationRequested &&
        string.Equals(trackPath, _mainWindowLyricsTrackPath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(trackPath, _currentTrackPath, StringComparison.OrdinalIgnoreCase);

    private void ApplyMainWindowLyricsLoading()
    {
        _mainWindowSyncedLyrics.Clear();
        _activeMainWindowLyricIndex = -2;
        ResetMainWindowSyncedLyricsScroll();
        LyricsPanelTitleText.Text = LocalizationService.Translate("Текст песни");
        LyricsPanelSourceText.Text = LocalizationService.Translate("Загружаем текст…");
        LyricsPanelText.Text = string.Empty;
        LyricsPanelSyncedList.Visibility = Visibility.Collapsed;
        LyricsPanelScrollViewer.Visibility = Visibility.Visible;
        LyricsPanelEmptyState.Visibility = Visibility.Collapsed;
    }

    private void ApplyMainWindowLyricsDocument(LyricsDocument document)
    {
        _mainWindowSyncedLyrics.Clear();
        _activeMainWindowLyricIndex = -2;
        ResetMainWindowSyncedLyricsScroll();
        if (document.Kind == LyricsKind.None)
        {
            ApplyMainWindowLyricsEmpty("Текст не найден");
            return;
        }

        LyricsPanelTitleText.Text = LocalizationService.Translate(document.Kind == LyricsKind.Synced
            ? "Синхронный текст" : "Текст песни");
        LyricsPanelSourceText.Text = LocalizationService.Translate(document.SourceLabel);
        LyricsPanelEmptyState.Visibility = Visibility.Collapsed;

        if (document.Kind == LyricsKind.Synced)
        {
            foreach (LyricLine line in document.Lines)
            {
                var lyricLine = new MainWindowLyricLine { Time = line.Time, Text = line.Text };
                ApplySyncedLyricsLineAppearance(lyricLine, active: false, animate: false);
                _mainWindowSyncedLyrics.Add(lyricLine);
            }

            LyricsPanelText.Text = string.Empty;
            LyricsPanelScrollViewer.Visibility = Visibility.Collapsed;
            LyricsPanelSyncedList.Visibility = Visibility.Visible;

            // Новый документ начинается с первой LRC-строки: прежнюю позицию не считываем, иначе песня визуально открывалась
            // в середине; после layout повторяем ScrollToTop для уже видимого ListBox.
            UpdateMainWindowSyncedLyrics(TimeSpan.Zero, forceScroll: true);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (_isLyricsPanelActive && _mainWindowLyrics.Kind == LyricsKind.Synced)
                    ResetMainWindowSyncedLyricsScroll();
            }));
            return;
        }

        LyricsPanelText.Text = document.PlainText;
        LyricsPanelSyncedList.Visibility = Visibility.Collapsed;
        LyricsPanelScrollViewer.Visibility = Visibility.Visible;
        LyricsPanelScrollViewer.ScrollToTop();
    }

    private void ApplyMainWindowLyricsEmpty(string status)
    {
        _mainWindowSyncedLyrics.Clear();
        _activeMainWindowLyricIndex = -2;
        ResetMainWindowSyncedLyricsScroll();
        LyricsPanelTitleText.Text = LocalizationService.Translate("Текст песни");
        LyricsPanelSourceText.Text = LocalizationService.Translate(status);
        LyricsPanelText.Text = string.Empty;
        LyricsPanelSyncedList.Visibility = Visibility.Collapsed;
        LyricsPanelScrollViewer.Visibility = Visibility.Collapsed;
        LyricsPanelEmptyState.Visibility = Visibility.Visible;
    }

    private void UpdateMainWindowSyncedLyrics(TimeSpan position, bool forceScroll = false)
    {
        if (!_isLyricsPanelActive || _mainWindowLyrics.Kind != LyricsKind.Synced || _mainWindowSyncedLyrics.Count == 0)
            return;

        int activeIndex = LyricsService.FindActiveLineIndex(_mainWindowLyrics.Lines, position);
        if (!forceScroll && activeIndex == _activeMainWindowLyricIndex)
            return;

        if (_activeMainWindowLyricIndex >= 0 && _activeMainWindowLyricIndex < _mainWindowSyncedLyrics.Count)
        {
            MainWindowLyricLine previousLine = _mainWindowSyncedLyrics[_activeMainWindowLyricIndex];
            previousLine.IsActive = false;
            ApplySyncedLyricsLineAppearance(previousLine, active: false, animate: true);
        }

        _activeMainWindowLyricIndex = activeIndex;
        if (activeIndex < 0 || activeIndex >= _mainWindowSyncedLyrics.Count)
            return;

        MainWindowLyricLine activeLine = _mainWindowSyncedLyrics[activeIndex];
        activeLine.IsActive = true;
        ApplySyncedLyricsLineAppearance(activeLine, active: true, animate: true);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_isLyricsPanelActive && _activeMainWindowLyricIndex == activeIndex)
                SmoothScrollLyricsToActiveLine(activeLine);
        }));
    }

    private void ResetMainWindowSyncedLyricsScroll()
    {
        System.Windows.Controls.ScrollViewer? scrollViewer = FindVisualChild<System.Windows.Controls.ScrollViewer>(LyricsPanelSyncedList);
        if (scrollViewer is null) return;

        scrollViewer.BeginAnimation(AnimatedScrollOffsetProperty, null);
        scrollViewer.ScrollToTop();
    }

    private static void OnAnimatedScrollOffsetChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is System.Windows.Controls.ScrollViewer scrollViewer && args.NewValue is double offset)
            scrollViewer.ScrollToVerticalOffset(offset);
    }

    private void SmoothScrollLyricsToActiveLine(MainWindowLyricLine activeLine)
    {
        // CanContentScroll=False переводит ScrollViewer в пиксели: ExtentHeight, ViewportHeight и смещение в одной системе
        // координат, и анимация не смешивает индексы элементов с пикселями (прежняя причина скачка в конец).
        LyricsPanelSyncedList.UpdateLayout();
        FrameworkElement? container = LyricsPanelSyncedList.ItemContainerGenerator.ContainerFromItem(activeLine) as FrameworkElement;
        if (container is null)
        {
            LyricsPanelSyncedList.ScrollIntoView(activeLine);
            LyricsPanelSyncedList.UpdateLayout();
            container = LyricsPanelSyncedList.ItemContainerGenerator.ContainerFromItem(activeLine) as FrameworkElement;
            if (container is null) return;
        }

        System.Windows.Controls.ScrollViewer? scrollViewer = FindVisualChild<System.Windows.Controls.ScrollViewer>(LyricsPanelSyncedList);
        if (scrollViewer is null || scrollViewer.ViewportHeight <= 0 || container.ActualHeight <= 0) return;

        try
        {
            Point itemTop = container.TranslatePoint(new Point(0, 0), LyricsPanelSyncedList);
            double viewportHeight = Math.Min(scrollViewer.ViewportHeight, LyricsPanelSyncedList.ActualHeight);
            double maxOffset = Math.Max(0, scrollViewer.ExtentHeight - scrollViewer.ViewportHeight);
            double targetOffset = Math.Clamp(
                scrollViewer.VerticalOffset + itemTop.Y - (viewportHeight - container.ActualHeight) / 2,
                0, maxOffset);

            var animation = new DoubleAnimation(scrollViewer.VerticalOffset, targetOffset,
                new Duration(TimeSpan.FromMilliseconds(360)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            scrollViewer.BeginAnimation(AnimatedScrollOffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }
        catch (InvalidOperationException)
        {
            // В момент пересоздания контейнеров ListBox WPF может временно разорвать visual tree.
        }
    }

    private void LyricsPanelSyncedList_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_audioFile is null) return;
        if (System.Windows.Controls.ItemsControl.ContainerFromElement(LyricsPanelSyncedList, e.OriginalSource as DependencyObject)
            is not System.Windows.Controls.ListBoxItem { DataContext: MainWindowLyricLine line })
            return;

        TimeSpan target = line.Time;
        if (_audioFile.TotalTime > TimeSpan.Zero)
            target = target > _audioFile.TotalTime ? _audioFile.TotalTime : target;

        SeekCurrentAudioFile(target);

        // Обновляем полосу и активную строку сразу, не дожидаясь следующего тика таймера.
        _isSyncingProgressFromPlayback = true;
        try
        {
            ProgressSlider.Value = Math.Clamp(target.TotalSeconds, ProgressSlider.Minimum, ProgressSlider.Maximum);
        }
        finally
        {
            _isSyncingProgressFromPlayback = false;
        }
        UpdateMainWindowSyncedLyrics(target, forceScroll: true);
        LyricsPanelSyncedList.SelectedItem = null;
        e.Handled = true;
    }

    // Настройки применяются к уже созданным строкам без перезапуска: активная строка белая, неактивные серые; акцент
    // не используем, чтобы текст оставался нейтральным при любой теме.
    public void ApplySyncedLyricsAppearance()
    {
        ApplyLyricsTextAlignment();
        UpdateCoverForLyrics(_isPlaylistVisible && _isLyricsPanelActive);
        _nowPlayingWindow?.ApplyLyricsAppearance();
        for (int index = 0; index < _mainWindowSyncedLyrics.Count; index++)
            ApplySyncedLyricsLineAppearance(_mainWindowSyncedLyrics[index], index == _activeMainWindowLyricIndex, animate: true);
    }

    private bool _isCoverCollapsedForLyrics;

    // Пока открыта панель «Текст песни», обложка сжимается до нуля, а текст получает её высоту; название, исполнитель и
    // состояние остаются. Включается настройкой HideCoverInLyricsPanel; при «Меньше анимации» — без анимации.
    private void UpdateCoverForLyrics(bool lyricsVisible)
    {
        bool collapse = lyricsVisible && _settings.HideCoverInLyricsPanel;
        if (collapse == _isCoverCollapsedForLyrics) return;
        _isCoverCollapsedForLyrics = collapse;

        // Нулевой масштаб даёт вырожденную матрицу, поэтому анимируем до малого значения и затем скрываем элемент.
        const double collapsed = 0.001;
        double target = collapse ? collapsed : 1.0;
        if (!collapse) AlbumArtContainer.Visibility = Visibility.Visible;

        void Finish()
        {
            AlbumArtCollapseScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            AlbumArtCollapseScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            AlbumArtCollapseScale.ScaleX = target;
            AlbumArtCollapseScale.ScaleY = target;
            if (_isCoverCollapsedForLyrics == collapse && collapse)
                AlbumArtContainer.Visibility = Visibility.Collapsed;
        }

        if (AccessibilityPreferences.ShouldReduceMotion(_settings) || !IsLoaded)
        {
            Finish();
            return;
        }

        var duration = new Duration(TimeSpan.FromMilliseconds(240));
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var scaleX = new DoubleAnimation(AlbumArtCollapseScale.ScaleX, target, duration) { EasingFunction = ease };
        var scaleY = new DoubleAnimation(AlbumArtCollapseScale.ScaleY, target, duration) { EasingFunction = ease };
        scaleY.Completed += (_, _) =>
        {
            // Если за время анимации состояние сменилось обратно, итог уже другой — не затираем его.
            if (_isCoverCollapsedForLyrics == collapse) Finish();
        };
        AlbumArtCollapseScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX, HandoffBehavior.SnapshotAndReplace);
        AlbumArtCollapseScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY, HandoffBehavior.SnapshotAndReplace);
    }

    // Выравнивание текста песни (AppSettings.LyricsTextAlignment): у списка синхронного текста оно задаётся через
    // TextBlock.TextAlignment на ListBox, TextBlock в шаблоне строки наследует его; обычный текст — прямо у TextBlock.
    private void ApplyLyricsTextAlignment()
    {
        TextAlignment alignment = LyricsTextAlignmentMode.ToTextAlignment(_settings.LyricsTextAlignment);
        LyricsPanelSyncedList.SetValue(System.Windows.Controls.TextBlock.TextAlignmentProperty, alignment);
        LyricsPanelText.TextAlignment = alignment;
    }

    private void ApplySyncedLyricsLineAppearance(MainWindowLyricLine line, bool active, bool animate)
    {
        double fontSize = Math.Clamp(_settings.SyncedLyricsFontSize, 12, 20);
        line.FontSize = fontSize;
        line.LineHeight = Math.Max(21, Math.Round(fontSize * 1.58));

        // Масштабирование больше не предлагается в настройках: остаются нейтральный режим
        // и мягкое свечение. Старые значения Scale/GlowScale безопасно читаются как Glow.
        bool useScale = false;
        bool useGlow = active && _settings.SyncedLyricsHighlightEffect != "None";
        Color foreground = active ? Colors.White : Color.FromRgb(142, 142, 142);
        double scale = useScale ? 1.055 : 1.0;
        double glowOpacity = useGlow ? 0.62 : 0.0;

        if (!animate)
        {
            line.Foreground.BeginAnimation(SolidColorBrush.ColorProperty, null);
            line.Foreground.Color = foreground;
            line.ScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            line.ScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            line.ScaleTransform.ScaleX = scale;
            line.ScaleTransform.ScaleY = scale;
            line.GlowEffect.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, null);
            line.GlowEffect.Opacity = glowOpacity;
            return;
        }

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(230));
        line.Foreground.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(foreground, duration) { EasingFunction = easing });
        line.ScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(scale, duration) { EasingFunction = easing });
        line.ScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(scale, duration) { EasingFunction = easing });
        line.GlowEffect.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty,
            new DoubleAnimation(glowOpacity, duration) { EasingFunction = easing });
    }
}
