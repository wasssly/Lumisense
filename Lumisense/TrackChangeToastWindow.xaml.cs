using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Lumisense;

// Всплывающее уведомление о смене трека, в углу экрана — см. подробный комментарий в
// TrackChangeToastWindow.xaml. Единственный экземпляр переиспользуется на каждую смену
// трека (см. MainWindow._trackChangeToastWindow) — Show()/Hide(), а не создание нового
// окна на каждый трек: так быстрое переключение (следующий/предыдущий несколько раз
// подряд) не плодит окна и не мигает, а просто перезапускает анимацию и таймер.
//
// Осознанно НЕ масштабируется через AccessibilityPreferences: позиция карточки уже
// вычислена по физическим координатам монитора, и наложение масштаба сдвинуло бы её.
public partial class TrackChangeToastWindow : Window
{
    private static readonly TimeSpan VisibleDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(200);

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
        _hideTimer.Tick += HideTimer_Tick;
    }

    // isLightTheme — та же логика, что и у MiniPlayerWindow.ApplyTheme: карточка не связана
    // с системной темой автоматически (фон рисуется вручную, не через Mica/Acrylic), поэтому
    // текущую тему приложения передаёт вызывающий код (см. MainWindow.ShowTrackChangeToast).
    // screen/position/size/width — см. AppSettings.TrackChangeToastMonitor/
    // TrackChangeToastPosition/TrackChangeToastSize/TrackChangeToastWidth; screen уже полностью
    // разрешён вызывающим кодом (см. MainWindow.ResolveToastScreen) — это окно само не решает,
    // "какой монитор", только "где на нём" и "какого размера".
    public void ShowToast(string title, string artist, Brush? art, bool isLightTheme,
        System.Windows.Forms.Screen screen, string position, string size, double width)
    {
        ToastTitleText.Text = title;

        bool hasArtist = !string.IsNullOrWhiteSpace(artist) && artist != "—";
        ToastArtistText.Text = artist;
        ToastArtistText.Visibility = hasArtist ? Visibility.Visible : Visibility.Collapsed;

        ApplySizePreset(size);
        ApplyWidth(width, size);

        if (art is ImageBrush { ImageSource: not null } imageBrush)
        {
            // Не используем ImageBrush как Background: на небольшой обложке уведомления WPF
            // может выбрать низкокачественное масштабирование. Явный Image в XAML работает
            // так же, как проверенное отображение в мини-плеере.
            ArtImage.Source = imageBrush.ImageSource;
            ArtImage.Visibility = Visibility.Visible;
            ArtBorder.Background = Brushes.Transparent;
            ArtIcon.Visibility = Visibility.Collapsed;
        }
        else
        {
            ArtImage.Source = null;
            ArtImage.Visibility = Visibility.Collapsed;
            ArtBorder.Background = art ?? (Brush)FindResource("ControlFillColorSecondaryBrush");
            ArtIcon.Visibility = art is null ? Visibility.Visible : Visibility.Collapsed;
        }

        ToastBackgroundBrush.Color = isLightTheme ? LightBackground : DarkBackground;

        // Останавливаем и таймер, и любую уже идущую анимацию (например, недоигравший
        // fade-out от предыдущего, слишком быстро сменившегося трека) — иначе её Completed
        // мог бы сработать уже ПОСЛЕ того, как мы только что показали уведомление для нового
        // трека, и спрятать его раньше времени.
        _hideTimer.Stop();
        RootBorder.BeginAnimation(UIElement.OpacityProperty, null);

        // HWND нужен для точного SetWindowPos в физических пикселях конкретного монитора.
        // Первое создание остаётся невидимым, поэтому пользователь не увидит промежуточное
        // размещение на основном дисплее до per-monitor DPI-позиционирования.
        if (!IsVisible)
        {
            RootBorder.Opacity = 0;
            Show();
        }
        PositionOnScreen(screen, position);

        RootBorder.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, FadeDuration));
        _hideTimer.Start();
    }

    private void FadeOutAndHide()
    {
        var fadeOut = new DoubleAnimation(0, FadeDuration);
        fadeOut.Completed += (_, _) => Hide();
        RootBorder.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    protected override void OnClosed(EventArgs e)
    {
        _hideTimer.Stop();
        _hideTimer.Tick -= HideTimer_Tick;
        RootBorder.BeginAnimation(UIElement.OpacityProperty, null);
        base.OnClosed(e);
    }

    private void HideTimer_Tick(object? sender, EventArgs e)
    {
        _hideTimer.Stop();
        FadeOutAndHide();
    }

    // Три готовых размера карточки — высота, размер обложки, размер шрифтов и запас, который
    // ширина карточки тратит на всё, ЧТО НЕ текст (обложка + отступы), см. ApplyWidth ниже.
    // Не трогает саму ширину окна — она задаётся отдельно, см. AppSettings.TrackChangeToastSize
    // (комментарий там же поясняет, почему это два независимых значения). Применяется заново
    // на каждый показ (не только при создании окна) — размер мог смениться в настройках между
    // двумя прослушиваниями, а окно переиспользуется одно на всё время работы приложения.
    private (double Height, double Art, double TitleFont, double ArtistFont, double NonTextWidth) GetSizePreset(string size) => size switch
    {
        "Small" => (60.0, 38.0, 12.0, 10.0, 90.0),
        "Large" => (92.0, 62.0, 16.0, 13.0, 120.0),
        _ => (72.0, 48.0, 13.0, 11.0, 100.0) // "Medium"
    };

    private void ApplySizePreset(string size)
    {
        var preset = GetSizePreset(size);

        Height = preset.Height;
        ArtBorder.Width = preset.Art;
        ArtBorder.Height = preset.Art;
        // Border не обрезает дочерний Image по CornerRadius автоматически. Явный Clip нужен,
        // чтобы обложка не оставалась квадратной при HighQuality-отрисовке; пересчитываем его
        // для каждого выбранного размера уведомления.
        double cornerRadius = Math.Min(8.0, preset.Art / 6.0);
        ArtImage.Clip = new RectangleGeometry(new Rect(0, 0, preset.Art, preset.Art), cornerRadius, cornerRadius);
        ArtIcon.Size = preset.Art * 0.42; // та же пропорция иконки к обложке, что и раньше (20/48)
        ToastTitleText.FontSize = preset.TitleFont;
        ToastArtistText.FontSize = preset.ArtistFont;
    }

    // Ширина карточки — отдельный от размера ползунок в настройках (см. AppSettings.
    // TrackChangeToastWidth): меняет ТОЛЬКО ширину самого окна и то, сколько текста влезает в
    // строку до многоточия, высота/обложка/шрифты не трогает — их уже выставил ApplySizePreset
    // выше. NonTextWidth (запас под обложку и отступы) берётся из текущего размера, поэтому
    // отступы вокруг текста выглядят одинаково пропорционально при любой выбранной ширине.
    private void ApplyWidth(double width, string size)
    {
        Width = width;
        ToastTextPanel.MaxWidth = Math.Max(width - GetSizePreset(size).NonTextWidth, 40.0);
    }

    // Рабочая область Screen задаётся физическими пикселями. Получаем DPI именно выбранного
    // монитора, рассчитываем физический прямоугольник и передаём его HWND через SetWindowPos.
    // Поэтому 100% + 150% и другие mixed-DPI конфигурации не зависят от монитора, на котором
    // toast находился при предыдущем показе.
    private void PositionOnScreen(System.Windows.Forms.Screen screen, string position)
    {
        double scale = ToastMonitorDpi.GetScale(screen, GetDpiScale());
        ToastPlacement placement = ToastPlacementCalculator.Calculate(screen.WorkingArea, Width, Height, scale, position);
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        if (hwnd != IntPtr.Zero)
        {
            WindowSnapHelper.SetWindowPos(hwnd, IntPtr.Zero, placement.X, placement.Y, placement.Width, placement.Height,
                WindowSnapHelper.SWP_NOZORDER | WindowSnapHelper.SWP_NOACTIVATE);
            return;
        }

        // Страховка для необычного жизненного цикла окна без HWND; обычный ShowToast создаёт
        // handle до этого вызова, поэтому основной путь всегда использует физические координаты.
        Left = placement.X / scale;
        Top = placement.Y / scale;
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
