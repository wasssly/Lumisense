using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using WpfBorder = System.Windows.Controls.Border;
using WpfPanel = System.Windows.Controls.Panel;

namespace Lumisense;

// Особенности видов: Glass — размытая обложка за интерфейсом и кнопки в капсуле, Vinyl — вращающаяся круглая обложка,
// Compact — обложка слева, текст справа, Poster — обложка-баннер на всю ширину с названием поверх.
public partial class MainWindow
{
    private BitmapSource? _mainArtwork;
    private bool _coverSpinning;

    // Вызывается из ApplyContentScale после расчёта размеров: приводит все особенности видов к выбранному, остальные выключает.
    private void ApplyLayoutVariant(double artSize)
    {
        string layout = LayoutName;
        ApplyGlassLayout(layout == "Glass");
        ApplyVinylLayout(layout == "Vinyl", artSize);
        ApplyCompactLayout(layout == "Compact");
        ApplyPosterLayout(layout == "Poster");
        if (layout is not ("Compact" or "Poster")) RestoreTrackText();
        UpdateCoverSpin();
    }

    // Обложка трека изменилась (или пропала): фон вида «Стекло» перестраивается.
    private void OnMainArtworkChanged(BitmapSource? artwork)
    {
        _mainArtwork = artwork;
        if (LayoutName == "Glass") UpdateGlassBackdrop();
    }

    private void UpdateGlassBackdrop()
    {
        BitmapSource? blurred = BackdropBlur.Create(_mainArtwork);
        GlassBackdrop.Source = blurred;
        GlassBackdrop.Visibility = blurred is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyGlassLayout(bool on)
    {
        if (on)
        {
            UpdateGlassBackdrop();
            // Подложку у кнопок можно убрать в настройках: тогда они лежат прямо на размытом фоне.
            bool capsule = _settings.GlassControlsBackground;
            ControlsCapsule.CornerRadius = new CornerRadius(capsule ? 34 : 0);
            ControlsCapsule.Padding = capsule ? new Thickness(14, 8, 14, 8) : new Thickness(0);
            if (capsule)
                ControlsCapsule.SetResourceReference(WpfBorder.BackgroundProperty, "ControlFillColorSecondaryBrush");
            else
                ControlsCapsule.Background = Brushes.Transparent;
        }
        else
        {
            GlassBackdrop.Source = null;
            GlassBackdrop.Visibility = Visibility.Collapsed;
            ControlsCapsule.CornerRadius = new CornerRadius(0);
            ControlsCapsule.Padding = new Thickness(0);
            ControlsCapsule.Background = Brushes.Transparent;
        }
    }

    private void ApplyVinylLayout(bool on, double artSize)
    {
        VinylHole.Visibility = VinylRim.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on)
        {
            VinylHole.Width = VinylHole.Height = Math.Max(14, artSize * 0.1);
            VinylRim.Width = VinylRim.Height = artSize;
        }
        else
        {
            // Вне вида «Винил» обложка не должна остаться повёрнутой.
            StopCoverSpin();
            AlbumArtSpin.Angle = 0;
        }
    }

    private FrameworkElement[] TrackTextElements => [TrackTitleText, TrackArtistText, TrackStateBadge];

    // Переносит название, исполнителя и состояние трека в другую панель (справа от обложки или поверх неё).
    private void MoveTrackTextTo(WpfPanel target)
    {
        foreach (FrameworkElement element in TrackTextElements)
        {
            if (element.Parent is WpfPanel parent && parent != target) parent.Children.Remove(element);
            if (!target.Children.Contains(element)) target.Children.Add(element);
            element.HorizontalAlignment = HorizontalAlignment.Left;
        }

        TrackTitleText.Margin = new Thickness(0, 0, 0, 2);
    }

    // Возвращает название, исполнителя и состояние на прежние места под обложкой (после скрытого CompactInfoHost).
    private void RestoreTrackText()
    {
        if (ReferenceEquals(TrackTitleText.Parent, AlbumArtPanel)) return;

        foreach (FrameworkElement element in TrackTextElements)
        {
            if (element.Parent is WpfPanel parent) parent.Children.Remove(element);
        }

        AlbumArtPanel.Children.Insert(2, TrackTitleText);
        AlbumArtPanel.Children.Insert(3, TrackArtistText);
        AlbumArtPanel.Children.Insert(4, TrackStateBadge);
        foreach (FrameworkElement element in TrackTextElements) element.HorizontalAlignment = HorizontalAlignment.Center;
        TrackTitleText.Margin = new Thickness(0, 12, 0, 2);
        TrackTitleText.ClearValue(System.Windows.Controls.TextBlock.ForegroundProperty);
        TrackArtistText.ClearValue(System.Windows.Controls.TextBlock.ForegroundProperty);
    }

    // «Компакт»: обложка с текстом стоит у левого края окна ("Left", по умолчанию) или группой по центру ("Center").
    private void ApplyCompactLayout(bool on)
    {
        AlbumArtPanel.Orientation = on ? System.Windows.Controls.Orientation.Horizontal : System.Windows.Controls.Orientation.Vertical;
        CompactInfoHost.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        bool left = on && _settings.CompactCoverAlignment != "Center";
        AlbumArtPanel.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        if (!on) return;

        MoveTrackTextTo(CompactInfoHost);
        if (left)
        {
            AlbumArtPanel.Margin = new Thickness(24, AlbumArtPanel.Margin.Top, 24, AlbumArtPanel.Margin.Bottom);
            // Текст занимает всё место справа от обложки, но не шире 320 px.
            double available = ContentHost.MaxWidth - AlbumArtBorder.Width - 24 * 2 - CompactInfoHost.Margin.Left;
            CompactInfoHost.MaxWidth = Math.Clamp(available, 140, 320);
        }
        else
        {
            CompactInfoHost.MaxWidth = 250;
        }
    }

    // «Постер»: текст лежит на затемнённом низе обложки, поэтому он всегда белый, независимо от темы оформления.
    private void ApplyPosterLayout(bool on)
    {
        PosterOverlay.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) return;

        MoveTrackTextTo(PosterOverlayText);
        TrackTitleText.Foreground = Brushes.White;
        TrackArtistText.Foreground = Brushes.White;
    }

    // Пластинка вращается, пока играет трек: один оборот за 18 с. Анимации раздела «Обложка» и «Снизить движение» её отключают.
    // Вызывается при каждом изменении состояния воспроизведения и при смене вида.
    private void UpdateCoverSpin()
    {
        bool shouldSpin = LayoutName == "Vinyl" && _isPlaying
            && !AccessibilityPreferences.ShouldReduceMotion(_settings, AnimationAreas.Cover);
        if (shouldSpin == _coverSpinning) return;

        if (shouldSpin)
        {
            _coverSpinning = true;
            double from = AlbumArtSpin.Angle % 360;
            var spin = new DoubleAnimation(from, from + 360, TimeSpan.FromSeconds(18)) { RepeatBehavior = RepeatBehavior.Forever };
            AlbumArtSpin.BeginAnimation(RotateTransform.AngleProperty, spin);
        }
        else
        {
            StopCoverSpin();
        }
    }

    // Останавливает вращение, оставляя обложку под тем углом, где она была.
    private void StopCoverSpin()
    {
        if (!_coverSpinning) return;

        _coverSpinning = false;
        double current = AlbumArtSpin.Angle;
        AlbumArtSpin.BeginAnimation(RotateTransform.AngleProperty, null);
        AlbumArtSpin.Angle = current % 360;
    }
}
