using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace AudioPlayer;

// Отдельное окно на весь экран с боковой панелью (Библиотека / Сейчас играет) — см. подробный
// комментарий в начале FullScreenPlayerWindow.xaml. Полностью синхронизируется с MainWindow
// через её публичные события/методы, тем же способом, что уже отработан в MiniPlayerWindow —
// этот код-behind сознательно построен по образцу того файла (события подписываются в
// конструкторе с немедленной начальной синхронизацией, отписываются в Closed).
public partial class FullScreenPlayerWindow : FluentWindow
{
    private readonly MainWindow _owner;

    private bool _isDraggingProgress;
    private bool _isDraggingVolume;

    public FullScreenPlayerWindow(MainWindow owner)
    {
        InitializeComponent();
        _owner = owner;

        // Живая привязка прямо к коллекции MainWindow — см. комментарий у PlaylistFolders
        LibraryFoldersList.ItemsSource = _owner.PlaylistFolders;

        _owner.TrackInfoChanged += OnTrackInfoChanged;
        _owner.ProgressChanged += OnProgressChanged;
        _owner.PlaybackStateChanged += OnPlaybackStateChanged;
        _owner.RepeatModeChanged += OnRepeatModeChanged;
        _owner.ShuffleStateChanged += OnShuffleStateChanged;
        _owner.VolumeChanged += OnVolumeChanged;

        // События выше стреляют только на ИЗМЕНЕНИЯ — при открытии окна их ещё не было ни
        // разу, поэтому текущее состояние сразу после конструктора синхронизируем вручную.
        OnTrackInfoChanged(_owner.CurrentTitle, _owner.CurrentArtist, _owner.CurrentArtBrush);
        OnPlaybackStateChanged(_owner.IsPlayingNow);
        OnProgressChanged(_owner.CurrentProgressSeconds, _owner.CurrentDurationSeconds);
        OnRepeatModeChanged(_owner.CurrentRepeatModeName);
        OnShuffleStateChanged(_owner.CurrentIsShuffleEnabled);
        OnVolumeChanged(_owner.CurrentVolume);

        Closed += (_, _) =>
        {
            _owner.TrackInfoChanged -= OnTrackInfoChanged;
            _owner.ProgressChanged -= OnProgressChanged;
            _owner.PlaybackStateChanged -= OnPlaybackStateChanged;
            _owner.RepeatModeChanged -= OnRepeatModeChanged;
            _owner.ShuffleStateChanged -= OnShuffleStateChanged;
            _owner.VolumeChanged -= OnVolumeChanged;
        };

        // Ширина заливки прогресса/громкости считается от ActualWidth дорожки (см.
        // SetProgressFillRatio/SetVolumeFillRatio) — на момент конструктора layout ещё не
        // посчитан (ActualWidth равен 0), поэтому досчитываем актуальные значения ещё раз
        // сразу после первого прохода раскладки.
        Loaded += (_, _) =>
        {
            OnProgressChanged(_owner.CurrentProgressSeconds, _owner.CurrentDurationSeconds);
            OnVolumeChanged(_owner.CurrentVolume);
        };
    }

    // ---------- Навигация по боковой панели ----------

    private void NavItem_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string key }) return;

        PageNowPlaying.Visibility = key == "NowPlaying" ? Visibility.Visible : Visibility.Collapsed;
        PageLibrary.Visibility = key == "Library" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ExitFullScreenButton_Click(object sender, RoutedEventArgs e) => _owner.ExitFullScreenMode();

    // Escape и F11 оба выходят из полноэкранного режима — F11 как симметричный обратный ход
    // тому же входу (см. MainWindow_PreviewKeyDown/EnterFullScreenMode), Escape — привычная
    // клавиша выхода из полноэкранного режима в браузерах и других плеерах.
    private void FullScreenPlayerWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.F11)
        {
            e.Handled = true;
            _owner.ExitFullScreenMode();
        }
    }

    // ---------- Синхронизация с MainWindow ----------

    private void OnTrackInfoChanged(string title, string artist, Brush? art)
    {
        bool hasArtist = !string.IsNullOrWhiteSpace(artist) && artist != "—";

        NowPlayingTitleText.Text = title;
        NowPlayingArtistText.Text = artist;
        NowPlayingArtistText.Visibility = hasArtist ? Visibility.Visible : Visibility.Collapsed;

        BarTitleText.Text = title;
        BarArtistText.Text = hasArtist ? artist : "—";

        if (art != null)
        {
            NowPlayingArtBorder.Background = art;
            NowPlayingArtIcon.Visibility = Visibility.Collapsed;
            BarArtBorder.Background = art;
            BarArtIcon.Visibility = Visibility.Collapsed;
        }
        else
        {
            var placeholder = (Brush)FindResource("ControlFillColorSecondaryBrush");
            NowPlayingArtBorder.Background = placeholder;
            NowPlayingArtIcon.Visibility = Visibility.Visible;
            BarArtBorder.Background = placeholder;
            BarArtIcon.Visibility = Visibility.Visible;
        }
    }

    private void OnPlaybackStateChanged(bool isPlaying) =>
        BarPlayPauseButton.Icon = IconResources.MakeOnAccent(isPlaying ? "IconPause" : "IconPlay", 16);

    private void OnProgressChanged(double positionSeconds, double durationSeconds)
    {
        // Пока пользователь сам тащит ползунок — не перебиваем его собственным положением
        // трека, обновлённым уже ПОСЛЕ клика (тот же приём, что и в MiniPlayerWindow/MainWindow)
        if (_isDraggingProgress) return;

        BarCurrentTimeText.Text = System.TimeSpan.FromSeconds(System.Math.Max(positionSeconds, 0)).ToString(@"mm\:ss");
        BarTotalTimeText.Text = System.TimeSpan.FromSeconds(System.Math.Max(durationSeconds, 0)).ToString(@"mm\:ss");

        double ratio = durationSeconds > 0 ? System.Math.Clamp(positionSeconds / durationSeconds, 0, 1) : 0;
        SetProgressFillRatio(ratio);
    }

    private void OnRepeatModeChanged(string modeName)
    {
        BarRepeatButton.Icon = modeName switch
        {
            "All" => IconResources.MakeOnAccent("IconRepeatAll", 15),
            "One" => IconResources.MakeOnAccent("IconRepeatOne", 15),
            _ => IconResources.Make("IconRepeatAll", 15)
        };
        BarRepeatButton.ToolTip = modeName switch
        {
            "All" => "Повтор: весь плейлист",
            "One" => "Повтор: один трек",
            _ => "Повтор: выключен"
        };
        SetAccentButtonActive(BarRepeatButton, modeName != "Off");
    }

    private void OnShuffleStateChanged(bool enabled)
    {
        BarShuffleButton.Icon = enabled ? IconResources.MakeOnAccent("IconShuffle", 15) : IconResources.Make("IconShuffle", 15);
        BarShuffleButton.ToolTip = enabled ? "Перемешать: включено" : "Перемешать: выключено";
        SetAccentButtonActive(BarShuffleButton, enabled);
    }

    private void OnVolumeChanged(double volume)
    {
        BarMuteButton.Icon = IconResources.Make(volume <= 0 ? "IconSpeakerMute" : "IconSpeaker", 15);

        if (_isDraggingVolume) return;
        SetVolumeFillRatio(volume);
    }

    // Тот же приём "покрасить Background вручную", что и в MainWindow.SetAccentButtonActive и
    // MiniPlayerWindow — обходит нерабочее автообновление Appearance=Primary у WPF-UI при
    // смене акцента вживую (см. подробный комментарий в MainWindow.xaml.cs).
    private void SetAccentButtonActive(Wpf.Ui.Controls.Button button, bool active)
    {
        button.Appearance = ControlAppearance.Secondary;

        if (active)
            button.Background = new SolidColorBrush(_owner.GetResolvedAccentColor());
        else
            button.ClearValue(Control.BackgroundProperty);
    }

    // ---------- Транспорт ----------

    private void ShuffleButton_Click(object sender, RoutedEventArgs e) => _owner.ExternalToggleShuffle();
    private void RepeatButton_Click(object sender, RoutedEventArgs e) => _owner.ExternalToggleRepeat();
    private void PrevButton_Click(object sender, RoutedEventArgs e) => _owner.ExternalPrev();
    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => _owner.ExternalPlayPause();
    private void NextButton_Click(object sender, RoutedEventArgs e) => _owner.ExternalNext();
    private void MuteButton_Click(object sender, RoutedEventArgs e) => _owner.ExternalToggleMute();

    // ---------- Перемотка (тот же приём "заливка Border-ом + прозрачная накладка", что и в
    // MiniPlayerWindow.ProgressBar_MouseLeftButtonDown/MouseMove/Up) ----------

    private void SetProgressFillRatio(double ratio)
    {
        double trackWidth = BarProgressTrack.ActualWidth;
        BarProgressFill.Width = trackWidth > 0 ? trackWidth * System.Math.Clamp(ratio, 0, 1) : 0;
    }

    private void BarProgress_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingProgress = true;
        ((UIElement)sender).CaptureMouse();
        SetProgressFillRatio(e.GetPosition(BarProgressTrack).X / System.Math.Max(BarProgressTrack.ActualWidth, 1));
    }

    private void BarProgress_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingProgress) return;
        SetProgressFillRatio(e.GetPosition(BarProgressTrack).X / System.Math.Max(BarProgressTrack.ActualWidth, 1));
    }

    private void BarProgress_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingProgress) return;

        _isDraggingProgress = false;
        ((UIElement)sender).ReleaseMouseCapture();

        double ratio = BarProgressTrack.ActualWidth > 0 ? BarProgressFill.Width / BarProgressTrack.ActualWidth : 0;
        _owner.ExternalSeekRatio(ratio);
    }

    // ---------- Громкость (тот же приём, что и перемотка, но с абсолютной установкой
    // значения через MainWindow.ExternalSetVolume вместо ExternalSeekRatio) ----------

    private void SetVolumeFillRatio(double ratio)
    {
        double trackWidth = BarVolumeTrack.ActualWidth;
        BarVolumeFill.Width = trackWidth > 0 ? trackWidth * System.Math.Clamp(ratio, 0, 1) : 0;
    }

    private void BarVolume_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingVolume = true;
        ((UIElement)sender).CaptureMouse();
        UpdateVolumeFromMouse(e.GetPosition(BarVolumeTrack).X);
    }

    private void BarVolume_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingVolume) return;
        UpdateVolumeFromMouse(e.GetPosition(BarVolumeTrack).X);
    }

    private void BarVolume_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingVolume = false;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    private void UpdateVolumeFromMouse(double x)
    {
        double width = BarVolumeTrack.ActualWidth;
        if (width <= 0) return;

        double ratio = System.Math.Clamp(x / width, 0, 1);
        SetVolumeFillRatio(ratio);
        _owner.ExternalSetVolume(ratio);
    }

    // ---------- Библиотека ----------

    // ClickCount >= 2 — запускаем только по двойному клику, как и в обычном плейлисте
    // главного окна (см. PlaylistTrackList_MouseDoubleClick), одиночный клик по строке ничего
    // не делает (кроме клика по самому сердечку — см. LibraryFavoriteButton_Click ниже,
    // у него собственный обработчик, а не проверка ClickCount здесь).
    private void LibraryTrack_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2) return;
        if (sender is FrameworkElement { Tag: string filePath })
            _owner.ExternalPlayTrack(filePath);
    }

    private void LibraryFavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string filePath })
            FavoritesManager.Toggle(filePath);
    }
}
