using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Lumisense;

// Уведомление о смене трека. Единственный экземпляр переиспользуется через Show()/Hide(),
// чтобы быстрое переключение не плодило окна. Не масштабируется: позиция уже в физ. координатах.
public partial class TrackChangeToastWindow : Window
{
    private static readonly TimeSpan VisibleDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(200);

    // Те же RGB, что у фона мини-плеера (MiniPlayerWindow.ApplyBackground), для согласованности двух плавающих окон.
    private static readonly Color DarkBackground = Color.FromRgb(0x1C, 0x1C, 0x1E);
    private static readonly Color LightBackground = Color.FromRgb(0xF2, 0xF2, 0xF2);

    private readonly DispatcherTimer _hideTimer;
    private bool _overlayCompatibilityMode;
    private readonly System.Windows.Media.Effects.Effect? _normalEffect;

    public TrackChangeToastWindow()
    {
        InitializeComponent();

        _hideTimer = new DispatcherTimer { Interval = VisibleDuration };
        _hideTimer.Tick += HideTimer_Tick;
        _normalEffect = RootBorder.Effect;
    }

    // isLightTheme приходит от вызывающего кода (фон рисуется вручную, не через Mica/Acrylic); screen уже разрешён
    // MainWindow.ResolveToastScreen; остальные параметры — см. AppSettings.TrackChangeToast*.
    public void ShowToast(string title, string artist, Brush? art, bool isLightTheme,
        System.Windows.Forms.Screen screen, string position, string size, double width,
        string artSide, string textAlignment)
    {
        ToastTitleText.Text = title;

        bool hasArtist = !string.IsNullOrWhiteSpace(artist) && artist != "—";
        ToastArtistText.Text = artist;
        ToastArtistText.Visibility = hasArtist ? Visibility.Visible : Visibility.Collapsed;

        ApplySizePreset(size);
        ApplyWidth(width, size);
        ApplyLayout(artSide, textAlignment);

        if (art is ImageBrush { ImageSource: not null } imageBrush)
        {
            // Не используем ImageBrush как Background: на небольшой обложке WPF может выбрать низкокачественное масштабирование.
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

        // Останавливаем и таймер, и идущую анимацию: иначе Completed fade-out предыдущего трека сработал бы после
        // показа нового уведомления и спрятал бы его раньше времени.
        _hideTimer.Stop();
        RootBorder.BeginAnimation(UIElement.OpacityProperty, null);

        // HWND нужен для SetWindowPos в физических пикселях нужного монитора; первое создание невидимо, поэтому
        // промежуточное размещение на основном дисплее пользователь не увидит.
        if (!IsVisible)
        {
            RootBorder.Opacity = 0;
            Show();
        }
        PositionOnScreen(screen, position);

        if (_overlayCompatibilityMode)
        {
            RootBorder.Opacity = 1;
            _hideTimer.Start();
            return;
        }

        RootBorder.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, FadeDuration));
        _hideTimer.Start();
    }

    // В compatibility mode toast остаётся простым непрозрачным слоем без DropShadow и
    // fade-анимаций, чтобы не конкурировать с композицией Steam Overlay/DWM.
    public void ApplyOverlayCompatibilityLive(bool enabled)
    {
        _overlayCompatibilityMode = enabled;
        RootBorder.Effect = enabled ? null : _normalEffect;
        if (enabled)
        {
            RootBorder.BeginAnimation(UIElement.OpacityProperty, null);
            if (IsVisible) RootBorder.Opacity = 1;
        }
    }

    private void FadeOutAndHide()
    {
        if (_overlayCompatibilityMode)
        {
            RootBorder.Opacity = 0;
            Hide();
            return;
        }

        var fadeOut = new DoubleAnimation(0, FadeDuration);
        fadeOut.Completed += (_, _) => Hide();
        RootBorder.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    private void RootBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Уведомление не является интерактивным элементом. Любой клик по карточке должен
        // закрывать её сразу, не дожидаясь окончания видимой задержки или fade-out.
        _hideTimer.Stop();
        RootBorder.BeginAnimation(UIElement.OpacityProperty, null);
        RootBorder.Opacity = 0;
        Hide();
        e.Handled = true;
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

    // Три готовых размера карточки (высота, обложка, шрифты, запас под не-текст, см. ApplyWidth); ширину окна не
    // трогает. Применяется на каждый показ: размер мог смениться в настройках, а окно переиспользуется.
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
        // Border не обрезает Image по CornerRadius, поэтому нужен явный Clip (пересчитывается для каждого размера).
        double cornerRadius = Math.Min(8.0, preset.Art / 6.0);
        ArtImage.Clip = new RectangleGeometry(new Rect(0, 0, preset.Art, preset.Art), cornerRadius, cornerRadius);
        ArtIcon.Size = preset.Art * 0.42; // та же пропорция иконки к обложке, что и раньше (20/48)
        ToastTitleText.FontSize = preset.TitleFont;
        ToastArtistText.FontSize = preset.ArtistFont;
    }

    // Ширина — отдельный от размера ползунок (AppSettings.TrackChangeToastWidth): меняет только ширину окна и
    // сколько текста влезает до многоточия; NonTextWidth берётся из размера, чтобы отступы оставались пропорциональными.
    private void ApplyWidth(double width, string size)
    {
        Width = width;
        ToastTextPanel.MaxWidth = Math.Max(width - GetSizePreset(size).NonTextWidth, 40.0);
    }

    // artSide — какой стороне докается обложка; отступ текстовой колонки — на противоположную
    // сторону. textAlignment — где колонка сидит в оставшемся (LastChildFill) месте.
    private void ApplyLayout(string artSide, string textAlignment)
    {
        bool artOnRight = artSide == "Right";
        DockPanel.SetDock(ArtBorder, artOnRight ? Dock.Right : Dock.Left);
        ToastTextPanel.Margin = artOnRight ? new Thickness(0, 0, 12, 0) : new Thickness(12, 0, 0, 0);

        var alignment = textAlignment switch
        {
            "Center" => HorizontalAlignment.Center,
            "Right" => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Left
        };
        ToastTextPanel.HorizontalAlignment = alignment;
        var textAlign = textAlignment switch
        {
            "Center" => TextAlignment.Center,
            "Right" => TextAlignment.Right,
            _ => TextAlignment.Left
        };
        ToastTitleText.TextAlignment = textAlign;
        ToastArtistText.TextAlignment = textAlign;
    }

    // Рабочая область Screen — в физических пикселях: берём DPI именно выбранного монитора и позиционируем
    // HWND через SetWindowPos, чтобы mixed-DPI не зависел от монитора прошлого показа.
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

    // Масштаб окна (1.0 = 100%); до первого показа PresentationSource нет и берём 100% — не страшно, так как
    // PositionOnScreen пересчитывается перед каждым показом.
    private double GetDpiScale()
    {
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget != null ? source.CompositionTarget.TransformToDevice.M11 : 1.0;
    }
}
