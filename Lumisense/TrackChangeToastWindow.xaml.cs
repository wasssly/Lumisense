using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace AudioPlayer;

// Всплывающее уведомление о смене трека, в углу экрана — см. подробный комментарий в
// TrackChangeToastWindow.xaml. Единственный экземпляр переиспользуется на каждую смену
// трека (см. MainWindow._trackChangeToastWindow) — Show()/Hide(), а не создание нового
// окна на каждый трек: так быстрое переключение (следующий/предыдущий несколько раз
// подряд) не плодит окна и не мигает, а просто перезапускает анимацию и таймер.
public partial class TrackChangeToastWindow : Window
{
    private static readonly TimeSpan VisibleDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(200);
    private const double ScreenMargin = 20;

    // Те же самые RGB, что и у фона мини-плеера (см. MiniPlayerWindow.ApplyBackground) —
    // визуальная согласованность между двумя "плавающими поверх рабочего стола" окнами
    // приложения.
    private static readonly Color DarkBackground = Color.FromRgb(0x1C, 0x1C, 0x1E);
    private static readonly Color LightBackground = Color.FromRgb(0xF2, 0xF2, 0xF2);

    private readonly DispatcherTimer _hideTimer;

    public TrackChangeToastWindow()
    {
        InitializeComponent();

        _hideTimer = new DispatcherTimer { Interval = VisibleDuration };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            FadeOutAndHide();
        };
    }

    // isLightTheme — та же логика, что и у MiniPlayerWindow.ApplyTheme: карточка не связана
    // с системной темой автоматически (фон рисуется вручную, не через Mica/Acrylic), поэтому
    // текущую тему приложения передаёт вызывающий код (см. MainWindow.ShowTrackChangeToast).
    public void ShowToast(string title, string artist, Brush? art, bool isLightTheme)
    {
        ToastTitleText.Text = title;

        bool hasArtist = !string.IsNullOrWhiteSpace(artist) && artist != "—";
        ToastArtistText.Text = artist;
        ToastArtistText.Visibility = hasArtist ? Visibility.Visible : Visibility.Collapsed;

        if (art != null)
        {
            ArtBorder.Background = art;
            ArtIcon.Visibility = Visibility.Collapsed;
        }
        else
        {
            ArtBorder.Background = (Brush)FindResource("ControlFillColorSecondaryBrush");
            ArtIcon.Visibility = Visibility.Visible;
        }

        ToastBackgroundBrush.Color = isLightTheme ? LightBackground : DarkBackground;

        PositionAtBottomRight();

        // Останавливаем и таймер, и любую уже идущую анимацию (например, недоигравший
        // fade-out от предыдущего, слишком быстро сменившегося трека) — иначе её Completed
        // мог бы сработать уже ПОСЛЕ того, как мы только что показали уведомление для нового
        // трека, и спрятать его раньше времени.
        _hideTimer.Stop();
        RootBorder.BeginAnimation(UIElement.OpacityProperty, null);

        if (!IsVisible) Show();

        RootBorder.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, FadeDuration));
        _hideTimer.Start();
    }

    private void FadeOutAndHide()
    {
        var fadeOut = new DoubleAnimation(0, FadeDuration);
        fadeOut.Completed += (_, _) => Hide();
        RootBorder.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    // Нижний правый угол рабочей области экрана (без учёта панели задач) — тот же угол,
    // где обычно всплывают системные уведомления Windows и тосты Spotify.
    private void PositionAtBottomRight()
    {
        Left = SystemParameters.WorkArea.Right - Width - ScreenMargin;
        Top = SystemParameters.WorkArea.Bottom - Height - ScreenMargin;
    }
}
