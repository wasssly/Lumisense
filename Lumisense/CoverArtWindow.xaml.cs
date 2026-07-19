using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace AudioPlayer;

/// <summary>
/// Отдельное окно для просмотра обложки трека крупно.
///
/// Управление:
///  - клик (без движения мыши) приближает картинку ещё сильнее относительно точки клика;
///    можно кликать много раз подряд, чтобы приблизиться всё сильнее (до разумного предела);
///  - зажатая левая кнопка мыши с движением — панорамирование (перетаскивание) картинки;
///  - правая кнопка мыши — сброс к размеру, вписанному в окно.
///
/// Окно можно свободно растягивать (ResizeMode="CanResize" в XAML) — масштаб "по размеру
/// окна" пересчитывается при изменении размеров, пока пользователь не приблизил картинку сам.
/// </summary>
public partial class CoverArtWindow : FluentWindow
{
    private double _naturalWidth;
    private double _naturalHeight;

    private double _scale = 1.0;
    private double _fitScale = 1.0;

    private const double MaxScaleAbsolute = 8.0;
    private const double ZoomStepFactor = 1.6;

    private bool _isDragging;
    private Point _dragStartMouse;
    private double _dragStartOffsetX;
    private double _dragStartOffsetY;
    private const double DragThresholdPixels = 4.0;

    // Клик, которым пользователь открывает это окно (по обложке в главном окне), обычно
    // завершается уже после того, как окно показано: физический MouseUp долетает не до
    // главного окна, а до ArtImage этого окна, если оно оказалось под курсором в момент
    // отпускания кнопки. Без этой защиты такой "осиротевший" MouseUp (без своего MouseDown
    // в этом окне) воспринимался как клик и сразу же приближал картинку — окно как будто
    // открывалось уже увеличенным. Считаем клик настоящим, только если у него было своё
    // MouseDown именно в этом окне.
    private bool _receivedMouseDownHere;

    public CoverArtWindow(BitmapImage art, string trackTitle)
    {
        InitializeComponent();

        ArtImage.Source = art;
        _naturalWidth = art.PixelWidth;
        _naturalHeight = art.PixelHeight;

        if (!string.IsNullOrWhiteSpace(trackTitle))
        {
            Title = trackTitle;
            ArtTitleBar.Title = trackTitle;
        }

        // На момент конструктора окно ещё не отмерено — размер "по окну" считаем,
        // когда контейнер обложки реально получит свои финальные габариты
        Loaded += (_, _) => ResetToFit();
    }

    private void ArtHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Если пользователь ещё не приближал вручную — просто следуем за размером окна.
        // Если уже приблизил — не сбрасываем масштаб, только обновляем, что значит "fit"
        // (пригодится, если он потом нажмёт правую кнопку мыши).
        if (Math.Abs(_scale - _fitScale) < 0.001)
            ResetToFit();
        else
            UpdateFitScale();
    }

    private void UpdateFitScale()
    {
        if (_naturalWidth <= 0 || _naturalHeight <= 0) return;
        if (ArtHost.ActualWidth <= 0 || ArtHost.ActualHeight <= 0) return;

        _fitScale = Math.Min(ArtHost.ActualWidth / _naturalWidth, ArtHost.ActualHeight / _naturalHeight);
    }

    private void ResetToFit()
    {
        UpdateFitScale();
        ApplyScale(_fitScale);

        // Картинка была скрыта (Opacity=0 в XAML), пока не появился первый достоверный
        // расчёт масштаба "по окну" — иначе на долю секунды был бы виден кадр с картинкой
        // в натуральную величину (обычно крупнее окна), что выглядело как самопроизвольный
        // зум сразу при открытии. Показываем её только теперь, когда контейнер обложки уже
        // реально отмерен и масштаб посчитан правильно.
        if (ArtImage.Opacity == 0 && ArtHost.ActualWidth > 0 && ArtHost.ActualHeight > 0)
            ArtImage.Opacity = 1;
    }

    private void ApplyScale(double scale)
    {
        _scale = scale;
        ArtImage.Width = _naturalWidth * scale;
        ArtImage.Height = _naturalHeight * scale;
    }

    // ---------- Панорамирование перетаскиванием ----------

    private void ArtImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;
        _receivedMouseDownHere = true;
        _dragStartMouse = e.GetPosition(ArtScrollViewer);
        _dragStartOffsetX = ArtScrollViewer.HorizontalOffset;
        _dragStartOffsetY = ArtScrollViewer.VerticalOffset;
        ArtImage.CaptureMouse();
    }

    private void ArtImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !ArtImage.IsMouseCaptured) return;

        var current = e.GetPosition(ArtScrollViewer);
        double deltaX = current.X - _dragStartMouse.X;
        double deltaY = current.Y - _dragStartMouse.Y;

        if (!_isDragging && (Math.Abs(deltaX) > DragThresholdPixels || Math.Abs(deltaY) > DragThresholdPixels))
            _isDragging = true;

        if (_isDragging)
        {
            ArtScrollViewer.ScrollToHorizontalOffset(_dragStartOffsetX - deltaX);
            ArtScrollViewer.ScrollToVerticalOffset(_dragStartOffsetY - deltaY);
        }
    }

    private void ArtImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ArtImage.ReleaseMouseCapture();

        // "Осиротевший" MouseUp — оставшийся от клика, которым окно было открыто, а не
        // настоящий клик по уже открытой картинке — игнорируем (см. комментарий у поля).
        if (!_receivedMouseDownHere)
        {
            _isDragging = false;
            return;
        }

        // Если мышь не сдвинулась дальше порога — это был клик, а не перетаскивание
        if (!_isDragging)
            ZoomInAt(e.GetPosition(ArtImage));

        _isDragging = false;
        _receivedMouseDownHere = false;
    }

    private void ArtImage_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        ResetToFit();
    }

    // ---------- Приближение по клику ----------

    private void ZoomInAt(Point clickPositionInImage)
    {
        if (ArtImage.ActualWidth <= 0 || ArtImage.ActualHeight <= 0) return;

        // Доля клика внутри картинки (0..1) — не зависит от текущего масштаба,
        // поэтому её можно применить и после изменения размера картинки
        double fractionX = clickPositionInImage.X / ArtImage.ActualWidth;
        double fractionY = clickPositionInImage.Y / ArtImage.ActualHeight;

        double maxScale = Math.Max(MaxScaleAbsolute, _fitScale * 6);
        double newScale = Math.Min(_scale * ZoomStepFactor, maxScale);
        if (newScale <= _scale) return; // уже на пределе — дальше приближать некуда

        ApplyScale(newScale);

        // Ждём, пока ScrollViewer пересчитает раскладку под новый размер картинки,
        // и только потом прокручиваем — иначе Viewport/Offset ещё будут старыми
        Dispatcher.InvokeAsync(() =>
        {
            ArtScrollViewer.UpdateLayout();

            double targetX = fractionX * ArtImage.ActualWidth - ArtScrollViewer.ViewportWidth / 2;
            double targetY = fractionY * ArtImage.ActualHeight - ArtScrollViewer.ViewportHeight / 2;

            ArtScrollViewer.ScrollToHorizontalOffset(Math.Max(0, targetX));
            ArtScrollViewer.ScrollToVerticalOffset(Math.Max(0, targetY));
        }, DispatcherPriority.Loaded);
    }
}
