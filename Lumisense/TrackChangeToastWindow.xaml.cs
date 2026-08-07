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
    // screen/position/width — см. AppSettings.TrackChangeToastMonitor/TrackChangeToastPosition/
    // TrackChangeToastWidth; screen уже полностью разрешён вызывающим кодом (см.
    // MainWindow.ResolveToastScreen) — это окно само не решает, "какой монитор", только "где
    // на нём" и "какого размера".
    public void ShowToast(string title, string artist, Brush? art, bool isLightTheme,
        System.Windows.Forms.Screen screen, string position, double width)
    {
        ToastTitleText.Text = title;

        bool hasArtist = !string.IsNullOrWhiteSpace(artist) && artist != "—";
        ToastArtistText.Text = artist;
        ToastArtistText.Visibility = hasArtist ? Visibility.Visible : Visibility.Collapsed;

        ApplyWidth(width);

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

        PositionOnScreen(screen, position);

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

    // Ширина карточки — единственное, что пользователь крутит напрямую (ползунок в настройках,
    // см. AppSettings.TrackChangeToastWidth); высота, размер обложки, размер шрифтов и
    // максимальная ширина текстовой колонки (чтобы длинные названия обрезались многоточием в
    // разумном месте, а не растягивали карточку) пересчитываются от неё пропорционально —
    // коэффициенты подобраны так, чтобы при ширине 300 (прежний фиксированный размер, единственный
    // до появления ползунка) получались те же самые числа, что были раньше. Применяется заново
    // на каждый показ (не только при создании окна) — ширина могла смениться в настройках между
    // двумя прослушиваниями, а окно переиспользуется одно на всё время работы приложения.
    private void ApplyWidth(double width)
    {
        double scale = width / 300.0;

        Width = width;
        Height = 72.0 * scale;
        ArtBorder.Width = 48.0 * scale;
        ArtBorder.Height = 48.0 * scale;
        ArtIcon.Size = ArtBorder.Width * 0.42; // та же пропорция иконки к обложке, что и раньше (20/48)
        ToastTitleText.FontSize = 13.0 * scale;
        ToastArtistText.FontSize = 11.0 * scale;
        ToastTextPanel.MaxWidth = width - 100.0;
    }

    // Выбранный угол рабочей области ВЫБРАННОГО монитора (без учёта панели задач на нём).
    // Экран передаётся уже разрешённым (см. MainWindow.ResolveToastScreen) — это окно только
    // переводит его WorkingArea (физические пиксели конкретного монитора) в WPF-единицы.
    //
    // Пересчёт через один общий коэффициент масштаба (см. GetDpiScale) корректен, когда все
    // мониторы работают с одинаковым масштабированием в Windows — это подавляющее большинство
    // реальных многомониторных настроек. На смешанном DPI (разный масштаб на разных мониторах)
    // расстояние от края экрана может оказаться чуть неточным на мониторах с масштабом,
    // отличным от того, на котором в этот момент физически находится само окно уведомления —
    // корректный по-honestly монитор-DPI-aware пересчёт потребовал бы работы с HWND и
    // Win32 API уровня GetDpiForMonitor, что для всплывающей карточки, которая и так исчезает
    // через 3 секунды, явно избыточно.
    private void PositionOnScreen(System.Windows.Forms.Screen screen, string position)
    {
        double scale = GetDpiScale();
        var area = screen.WorkingArea;

        double areaLeft = area.Left / scale;
        double areaTop = area.Top / scale;
        double areaRight = area.Right / scale;
        double areaBottom = area.Bottom / scale;

        double centerLeft = areaLeft + (areaRight - areaLeft - Width) / 2;

        (Left, Top) = position switch
        {
            "TopLeft" => (areaLeft + ScreenMargin, areaTop + ScreenMargin),
            "TopRight" => (areaRight - Width - ScreenMargin, areaTop + ScreenMargin),
            "TopCenter" => (centerLeft, areaTop + ScreenMargin),
            "BottomLeft" => (areaLeft + ScreenMargin, areaBottom - Height - ScreenMargin),
            "BottomCenter" => (centerLeft, areaBottom - Height - ScreenMargin),
            _ => (areaRight - Width - ScreenMargin, areaBottom - Height - ScreenMargin) // "BottomRight"
        };
    }

    // Масштаб текущего окна (1.0 = 100%, 1.25 = 125% и т.д.). До первого показа
    // (PresentationSource ещё нет — окно ни разу не рендерилось) считаем масштаб равным 100%;
    // на практике это не имеет значения, потому что PositionOnScreen всё равно пересчитывается
    // заново перед каждым показом, когда PresentationSource уже есть.
    private double GetDpiScale()
    {
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget != null ? source.CompositionTarget.TransformToDevice.M11 : 1.0;
    }
}
