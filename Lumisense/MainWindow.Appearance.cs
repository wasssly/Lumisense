using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls.Primitives;
using WpfSlider = System.Windows.Controls.Slider;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Lumisense;

// Внешний вид главного окна: доступность, масштаб контента, акцент и подложка окна.
// Вынесено из MainWindow.xaml.cs только ради навигации, логика не менялась.
public partial class MainWindow
{
    // Применяет изменённый масштаб/режим движения к уже открытым окнам, не создавая новых
    // экземпляров и не меняя состояние воспроизведения.
    public void ApplyAccessibilityPreferences()
    {
        AccessibilityPreferences.ApplyToWindow(this, _settings);
        ProgressMaterial.IsAnimationEnabled = !AccessibilityPreferences.ShouldReduceMotion(_settings, AnimationAreas.Progress);
        SliderSmoothing.IsAnimationEnabled = !AccessibilityPreferences.ShouldReduceMotion(_settings, AnimationAreas.Sliders);
        _settingsWindow?.ApplyAccessibilityPreferences();
        _miniPlayerWindow?.ApplyAccessibilityPreferences();
        _nowPlayingWindow?.ApplyAccessibilityPreferences();
    }

    private void ApplyFullscreenLayout(bool fullscreen)
    {
        UpdateContentMaxWidth();
        ApplyContentScale(fullscreen || _viewMode == PlayerViewMode.Square);
    }

    // Крупный стиль общий для полноэкранного режима (весь монитор) и квадратного вида (увеличенное окно), см. SetPlayerViewMode.
    private void ApplyContentScale(bool big)
    {
        bool modern = IsModernLayout;
        string layout = LayoutName;
        double artSize = !modern ? (big ? 260.0 : 150.0)
            : layout == "Compact" ? (big ? 190.0 : 132.0)
            : layout == "Glass" ? (big ? 300.0 : 196.0)
            : (big ? 300.0 : 184.0);
        // «Постер»: обложка-баннер во всю ширину окна, но не выше ~290 px (400 в крупном виде), иначе плейлисту не хватит места;
        // у остальных видов обложка квадратная.
        double artWidth = artSize;
        double artHeight = artSize;
        if (layout == "Poster")
        {
            double available = ContentHost.MaxWidth;
            artWidth = double.IsInfinity(available) || available < 280 ? 440 : Math.Min(available, 900);
            artHeight = big ? Math.Min(artWidth * 0.62, 400) : Math.Min(artWidth * 0.66, 290);
            artSize = artHeight;
        }

        double artRadius = layout switch
        {
            "Vinyl" => artSize / 2,
            "Glass" => 28,
            "Compact" => 18,
            "Modern" => 24,
            "Poster" => 0,
            _ => 16,
        };
        AlbumArtContainer.Width = artWidth;
        AlbumArtContainer.Height = artHeight;
        AlbumArtBorder.Width = artWidth;
        AlbumArtBorder.Height = artHeight;
        // Image.Clip в XAML — под 150×150 по умолчанию; без пересчёта здесь в крупном виде
        // (260×260) было бы видно только 150×150 в углу растянутой картинки, а не всю обложку.
        var artClip = new RectangleGeometry(new Rect(0, 0, artWidth, artHeight), artRadius, artRadius);
        var artGhostClip = new RectangleGeometry(new Rect(0, 0, artWidth, artHeight), artRadius, artRadius);
        if (artClip.CanFreeze) artClip.Freeze();
        if (artGhostClip.CanFreeze) artGhostClip.Freeze();
        AlbumArtImage.Clip = artClip;
        AlbumArtGhostImage.Clip = artGhostClip;
        AlbumArtBorder.CornerRadius = AlbumArtGhostBorder.CornerRadius = new CornerRadius(artRadius);
        AlbumArtCarouselHost.Width = modern ? artWidth : 150;
        AlbumArtCarouselHost.Height = modern ? artHeight : 150;
        AlbumArtIcon.Size = modern ? (big ? 72 : 48) : (big ? 64 : 36);
        AlbumArtPanel.Margin = layout == "Poster" ? new Thickness(0, 0, 0, 6)
            : modern
                ? (big ? new Thickness(0, 28, 0, 14) : new Thickness(0, 12, 0, 4))
                : (big ? new Thickness(0, 32, 0, 20) : new Thickness(0, 8, 0, 8));

        TrackTitleText.FontSize = layout == "Poster" ? (big ? 32 : 26) : modern ? (big ? 26 : 20) : (big ? 24 : 17);
        TrackTitleText.MaxWidth = layout == "Poster" ? Math.Max(200, artWidth - 44) : big ? 560 : 360;
        TrackArtistText.FontSize = layout == "Poster" ? (big ? 17 : 15) : modern ? (big ? 16 : 13) : (big ? 15 : 12);

        var controlsScale = big ? 1.25 : 1.0;
        double sideButton = modern ? 38 : 40;
        double stepButton = modern ? 46 : 44;
        double playButton = modern ? 62 : 54;
        ShuffleButton.Width = ShuffleButton.Height = sideButton * controlsScale;
        RepeatButton.Width = RepeatButton.Height = sideButton * controlsScale;
        PrevButton.Width = PrevButton.Height = stepButton * controlsScale;
        NextButton.Width = NextButton.Height = stepButton * controlsScale;
        StopButton.Width = StopButton.Height = 40 * controlsScale;
        MiniModeButton.Width = MiniModeButton.Height = 40 * controlsScale;
        PlayPauseButton.Width = PlayPauseButton.Height = playButton * controlsScale;

        ControlsCapsule.Margin = modern
            ? (big ? new Thickness(0, 20, 0, 12) : new Thickness(0, 12, 0, 8))
            : (big ? new Thickness(0, 22, 0, 10) : new Thickness(0, 14, 0, 6));
        // Кнопки управления без внутренних отступов: у Wpf.Ui.Button они (11 px по бокам) оставляют значку внутри кнопки меньше места,
        // чем он занимает, и значок обрезался по краям.
        foreach (Wpf.Ui.Controls.Button button in new[] { ShuffleButton, RepeatButton, PrevButton, PlayPauseButton, NextButton, StopButton, MiniModeButton })
            button.Padding = new Thickness(0);
        ApplyControlsLayout(modern, sideButton * controlsScale);
        ApplyLayoutVariant(artSize);
    }

    // Ряд кнопок управления: «Назад / Пуск / Вперёд» в центре и до двух кнопок по сторонам (слоты в настройках).
    // Результат зависит только от настроек, поэтому метод можно вызывать повторно.
    private void ApplyControlsLayout(bool modern, double sideSize)
    {
        CaptureSlotButtonHomes();

        // Слоты слева направо: дальний левый, ближний левый | Назад, Пуск, Вперёд | ближний правый, дальний правый.
        List<string> slots = NormalizeModernSlots(_settings.ModernControlSlots);
        bool sides = _settings.ModernShowSideButtons;
        var chosen = new List<Wpf.Ui.Controls.Button?>();
        for (int i = 0; i < ModernSlotCount; i++) chosen.Add(sides ? ModernSlotButton(slots[i]) : null);

        var order = new List<UIElement>();
        if (chosen[0] is { } farLeft) order.Add(farLeft);
        if (chosen[1] is { } nearLeft) order.Add(nearLeft);
        order.AddRange([PrevButton, PlayPauseButton, NextButton]);
        if (chosen[2] is { } nearRight) order.Add(nearRight);
        if (chosen[3] is { } farRight) order.Add(farRight);

        // Выбранные кнопки из других мест окна сначала отцепляются от прежних родителей: у элемента не может быть двух.
        foreach (UIElement element in order)
        {
            if (element is FrameworkElement { Parent: System.Windows.Controls.Panel parent } && !ReferenceEquals(parent, ControlsPanel))
                parent.Children.Remove(element);
        }

        ControlsPanel.Children.Clear();

        // Не выбранные кнопки из других мест возвращаются на прежние места в прежнем порядке.
        foreach (var home in _slotButtonHomes.OrderBy(pair => pair.Value.Index))
        {
            if (ModernSlotButton(home.Key) is not { } button || button.Parent is not null || order.Contains(button)) continue;

            RestoreSlotButtonStyle(button, home.Value);
            home.Value.Parent.Children.Insert(Math.Min(home.Value.Index, home.Value.Parent.Children.Count), button);
        }

        foreach (UIElement element in order)
        {
            if (element is Wpf.Ui.Controls.Button { } button && _slotButtonHomes.ContainsKey(SlotActionOf(button)))
            {
                // Чужие кнопки в ряду оформляются как соседние: те же размер и вторичный вид.
                button.Width = button.Height = sideSize;
                button.Margin = new Thickness(4, 0, 4, 0);
                button.Padding = new Thickness(0);
                button.Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary;
                // Кнопки из меню «Ещё» там с подписью; в ряду управления, как и остальные боковые, они только со значком.
                button.Content = null;
            }
            else
            {
                element.Visibility = Visibility.Visible;
            }

            ControlsPanel.Children.Add(element);
        }

        // Если обе кнопки ушли из меню «Ещё» в ряд управления, открывать там нечего: кнопка «Ещё» скрывается.
        MoreActionsButton.Visibility = MoreActionsItemsPanel.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        // Запись в журнал только при изменении набора кнопок: метод вызывается и при каждом изменении размеров окна.
        string summary = $"слоты [{string.Join(", ", slots.Select(slot => slot.Length == 0 ? "-" : slot))}], по бокам {(sides ? "включены" : "выключены")}";
        if (summary != _lastControlsLayoutSummary)
        {
            _lastControlsLayoutSummary = summary;
            Logger.Info($"Ряд кнопок управления: {summary}.");
        }
    }

    private string? _lastControlsLayoutSummary;

    private string SlotActionOf(Wpf.Ui.Controls.Button button) =>
        ModernSlotActions.FirstOrDefault(action => ReferenceEquals(ModernSlotButton(action), button)) ?? string.Empty;

    private static void RestoreSlotButtonStyle(Wpf.Ui.Controls.Button button, SlotButtonHome home)
    {
        button.Width = home.Width;
        button.Height = home.Height;
        button.Margin = home.Margin;
        button.Padding = home.Padding;
        button.Appearance = home.Appearance;
        button.Content = home.Content;
    }

    // ContentHost.MaxWidth считается от реальной ширины окна: шире на широком мониторе, без раздувания на ноутбучном;
    // SquareContentMaxWidth — только нижняя граница, если окно квадратного вида окажется уже.
    private void UpdateContentMaxWidth()
    {
        ContentHost.MaxWidth = _isFullscreenLayout
            ? Math.Clamp(ActualWidth * 0.55, NormalContentMaxWidth, 760)
            : _viewMode == PlayerViewMode.Square
                ? Math.Clamp(Width - 40, SquareContentMaxWidth, 900)
                : NormalContentMaxWidth;
    }

    // Применяет акцент (AccentColorMode/AccentColorHex) на старте и при смене акцента или темы: Apply() учитывает тему,
    // выбирая светлые/тёмные варианты (SystemAccentColorLight1 и т.п.), поэтому пересчёт нужен и при смене темы.

    // Не полагаемся на ControlAppearance.Primary WPF-UI: баг библиотеки (lepoco/wpfui #965/#981) — не подхватывает смену акцента
    // вживую; красим Background вручную.
    private void SetAccentButtonActive(Wpf.Ui.Controls.Button button, bool active)
    {
        button.Appearance = ControlAppearance.Secondary;

        if (active)
            button.Background = new SolidColorBrush(GetResolvedAccentColor());
        else
            button.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
    }

    // Реально применённый сейчас акцент: свой (AccentColorHex) либо системный, если он повреждён или выбран "Системный";
    // публичный, чтобы мини-плеер красил кнопки той же логикой (MiniPlayerWindow.SetAccentButtonActive).
    public Color GetResolvedAccentColor()
    {
        if (_settings.AccentColorMode == "Manual")
        {
            try { return (Color)ColorConverter.ConvertFromString(_settings.AccentColorHex); }
            catch { /* некорректный hex — откатываемся на системный акцент ниже */ }
        }

        if (_settings.AccentColorMode == "Cover" && _coverAccentColor is Color coverColor)
            return coverColor;

        return Application.Current.Resources["SystemAccentColor"] is Color color
            ? color
            : Color.FromRgb(0x00, 0x78, 0xD4);
    }

    private void RefreshCoverThemeColor()
    {
        _coverAccentColor = _currentAlbumArt is null
            ? null
            : ExtractCoverAccentColor(_currentAlbumArt);
    }

    private void ApplySelectableControlAccentResources(Color accent)
    {
        // Явные стили SettingsWindow используют эти DynamicResource: меняем значения в Application и открытых окнах, но Template
        // не переустанавливаем — это давало артефакты у Thumb Slider при смене обложки.
        var accentBrush = new SolidColorBrush(accent);
        accentBrush.Freeze();
        var contrastBrush = new SolidColorBrush(GetAccentContrastColor(accent));
        contrastBrush.Freeze();

        void ApplyResources(ResourceDictionary resources)
        {
            resources["AccentFillColorDefaultBrush"] = accentBrush;
            resources["AccentFillColorSecondaryBrush"] = accentBrush;
            resources["AccentTextFillColorPrimaryBrush"] = accentBrush;
            resources["TextOnAccentFillColorPrimaryBrush"] = contrastBrush;
        }

        ApplyResources(Application.Current.Resources);
        foreach (Window window in Application.Current.Windows.OfType<Window>())
            ApplyResources(window.Resources);
    }

    // Цвет полосы воспроизведения окна по его профилю (ProgressBarAppearance): акцент, цвет обложки текущего трека или свой.
    public Color GetProgressBarColor(ProgressSurface surface)
    {
        ProgressBarAppearance appearance = ProgressAppearanceApplier.For(_settings, surface);
        Color? cover = appearance.ColorMode == "Cover" && _currentAlbumArt is not null
            ? ExtractCoverAccentColor(_currentAlbumArt)
            : null;
        return ProgressAppearanceApplier.ResolveColor(appearance, GetResolvedAccentColor(), cover);
    }

    // Переносит профиль вида полосы на полосу главного окна и сразу на Now Playing и мини-плеер, если они открыты.
    public void ApplyProgressAppearance()
    {
        ProgressBarAppearance appearance = ProgressAppearanceApplier.For(_settings, ProgressSurface.Main);
        Color color = GetProgressBarColor(ProgressSurface.Main);
        SolidColorBrush brush = ProgressAppearanceApplier.ToBrush(color);

        ProgressAppearanceApplier.Apply(ProgressMaterial, appearance);
        ProgressAppearanceApplier.Apply(ProgressWaveform, appearance);
        ProgressAppearanceApplier.Apply(ProgressSlider, appearance, color);
        ProgressMaterial.PlayedBrush = brush;
        ProgressWaveform.PlayedBrush = brush;

        _nowPlayingWindow?.ApplyProgressAppearance();

        // Мини-плеер перекрашивает и контур вокруг обложки, а он зовёт ApplyProgressAppearance полосы сам.
        _miniPlayerWindow?.ApplyArtworkProgressColor();
        _settingsWindow?.RefreshProgressPreview();
    }

    // Форма звука текущего трека для предпросмотра в настройках; null, пока она не посчитана или выбрана не Waveform.
    public float[]? GetCurrentWaveformPeaks() => ProgressWaveform.Peaks;

    public void ApplyAccentColor()
    {
        RefreshCoverThemeColor();
        Color appliedAccent;

        if (_settings.AccentColorMode == "Cover" && _coverAccentColor is Color coverColor)
        {
            // Не вызываем ApplicationAccentColorManager.Apply на каждый трек: он заменяет глобальные DynamicResource WPF-UI и мог
            // попасть в TreeWalkHelper во время анимации обложки (fatal CLR error); ресурсы Lumisense обновляем без замены словаря.
            appliedAccent = coverColor;
        }
        else if (_settings.AccentColorMode == "Manual")
        {
            try
            {
                appliedAccent = (Color)ColorConverter.ConvertFromString(_settings.AccentColorHex);
                ApplicationAccentColorManager.Apply(appliedAccent,
                    _settings.IsLightThemeResolved() ? ApplicationTheme.Light : ApplicationTheme.Dark);
            }
            catch
            {
                ApplicationAccentColorManager.ApplySystemAccent();
                appliedAccent = GetResolvedAccentColor();
            }
        }
        else
        {
            ApplicationAccentColorManager.ApplySystemAccent();
            appliedAccent = GetResolvedAccentColor();
        }

        IconResources.AccentContrastBrush = new SolidColorBrush(GetAccentContrastColor(appliedAccent));
        ApplySelectableControlAccentResources(appliedAccent);
        RefreshAccentDependentIcons();
        _miniPlayerWindow?.ApplyArtworkProgressColor();
        _nowPlayingWindow?.ApplyAccentColor();
        ApplyProgressAppearance();
        ApplyCoverBaseBackground();
    }

    // Вызывается из SettingsWindow отдельно от ApplyAccentColor: окраска основы больше не
    // зависит от того, выбран ли акцент от обложки.
    public void ApplyCoverBaseTheme() => ApplyCoverBaseBackground();

    // Добавляет к системному Mica/Acrylic очень прозрачный слой текущей обложки. Эта настройка
    // независима от AccentColorMode: акцент и основа окна могут использовать разные источники.
    private void ApplyCoverBaseBackground()
    {
        if (!_settings.CoverBaseFromCover || _currentAlbumArt is null)
        {
            RootGrid.Background = Brushes.Transparent;
            return;
        }

        RefreshCoverThemeColor();
        if (_coverAccentColor is not Color cover)
        {
            RootGrid.Background = Brushes.Transparent;
            return;
        }

        byte r = (byte)Math.Clamp((int)Math.Round(cover.R * 0.52), 0, 255);
        byte g = (byte)Math.Clamp((int)Math.Round(cover.G * 0.52), 0, 255);
        byte b = (byte)Math.Clamp((int)Math.Round(cover.B * 0.52), 0, 255);
        RootGrid.Background = new SolidColorBrush(Color.FromArgb(0x4A, r, g, b));
    }

    private static Color? ExtractCoverAccentColor(BitmapSource source)
    {
        try
        {
            const int size = 32;
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
                context.DrawImage(source, new Rect(0, 0, size, size));

            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var pixels = new byte[size * size * 4];
            bitmap.CopyPixels(pixels, size * 4, 0);

            double red = 0, green = 0, blue = 0, weightSum = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                double blueValue = pixels[i] / 255.0;
                double greenValue = pixels[i + 1] / 255.0;
                double redValue = pixels[i + 2] / 255.0;
                double brightness = Math.Max(redValue, Math.Max(greenValue, blueValue));
                double minimum = Math.Min(redValue, Math.Min(greenValue, blueValue));
                double saturation = brightness <= 0 ? 0 : (brightness - minimum) / brightness;
                if (brightness < 0.08 || saturation < 0.12) continue;

                double weight = 0.25 + saturation * 0.75;
                red += redValue * weight;
                green += greenValue * weight;
                blue += blueValue * weight;
                weightSum += weight;
            }

            if (weightSum <= 0) return null;
            return Color.FromRgb(
                (byte)Math.Clamp(red / weightSum * 255, 0, 255),
                (byte)Math.Clamp(green / weightSum * 255, 0, 255),
                (byte)Math.Clamp(blue / weightSum * 255, 0, 255));
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось извлечь цвет из обложки: {ex.Message}");
            return null;
        }
    }

    // Выбираем по реальному WCAG contrast ratio: яркость BT.601 с порогом расходится с фактическим контрастом
    // на насыщенных жёлтых/оранжевых акцентах.
    private static Color GetAccentContrastColor(Color accent)
    {
        double luminance = RelativeLuminance(accent);
        double contrastWithWhite = 1.05 / (luminance + 0.05);
        double contrastWithBlack = (luminance + 0.05) / 0.05;
        return contrastWithBlack >= contrastWithWhite ? Colors.Black : Colors.White;
    }

    // WCAG 2.x relative luminance: https://www.w3.org/TR/WCAG21/#dfn-relative-luminance
    private static double RelativeLuminance(Color color)
    {
        double r = ToLinearChannel(color.R / 255.0);
        double g = ToLinearChannel(color.G / 255.0);
        double b = ToLinearChannel(color.B / 255.0);
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    private static double ToLinearChannel(double sRgbChannel) =>
        sRgbChannel <= 0.03928 ? sRgbChannel / 12.92 : Math.Pow((sRgbChannel + 0.055) / 1.055, 2.4);

    // IconResources.AccentContrastBrush задаёт цвет лишь для новых иконок (IconResources.SetOnAccent): уже показанные на
    // акцентных кнопках иконки (Пуск/Пауза, включённые Шаффл/Повтор) переприсваиваем явно после пересчёта кисти.
    private void RefreshAccentDependentIcons()
    {
        PlayPauseButton.Icon = IconResources.MakeOnAccent(_isPlaying ? "IconPause" : "IconPlay", 15);
        PlayPauseButton.Background = new SolidColorBrush(GetResolvedAccentColor()); // всегда акцентная, не переключается
        ProgressWaveform.PlayedBrush = new SolidColorBrush(GetResolvedAccentColor());
        ProgressMaterial.PlayedBrush = new SolidColorBrush(GetResolvedAccentColor());

        SetAccentButtonActive(ShuffleButton, _shuffleSession.IsEnabled);
        IconResources.SetOnAccent(ShuffleIcon, _shuffleSession.IsEnabled);

        RepeatButton.Icon = _repeatMode switch
        {
            RepeatMode.All => IconResources.MakeOnAccent("IconRepeatAll"),
            RepeatMode.One => IconResources.MakeOnAccent("IconRepeatOne"),
            _ => RepeatButton.Icon
        };
        SetAccentButtonActive(RepeatButton, _repeatMode != RepeatMode.Off);
        // Кнопка текста лежит в Popup «Ещё»: SetAccentButtonActive не годится — он переводит Button в Secondary и делает
        // одну строку меню визуально тяжелее другой.
        LyricsPanelButton.Opacity = _isLyricsPanelActive ? 1.0 : 0.86;

        if (_isFavoritesView)
        {
            IconResources.SetOnAccent(FavoritesButtonIcon, true);
            SetAccentButtonActive(FavoritesButton, true);
        }

        _miniPlayerWindow?.UpdateSecondaryButton();
        _miniPlayerWindow?.RefreshAccentButtons();
        _islandWindow?.RefreshAccent();
    }

    // Подложка главного окна (AppSettings.WindowBackdropType): на старте и из окна настроек при смене настройки
    // (SettingsWindow.WindowBackdropRadio_Changed) на открытом окне.
    public void ApplyWindowBackdrop(bool forceReapply = false)
    {
        var desiredBackdrop = WindowBackdropOptions.ToWpfUi(_settings.WindowBackdropType);
        bool blur = WindowBackdropOptions.IsBlur(_settings.WindowBackdropType);

        // После «Размытия» основу WPF-UI нужно наложить заново: наше размытие уже сняло системную основу окна.
        if (!blur && DwmBlurBackdrop.IsApplied(this))
        {
            DwmBlurBackdrop.Clear(this);
            forceReapply = true;
        }

        // DependencyProperty не вызывает callback при присваивании того же значения, а после ApplicationThemeManager.Apply WPF-UI 4
        // может вернуть Mica при выбранном Acrylic; переход через None вызывает OnBackdropTypeChanged и накладывает нужный backdrop.
        if (forceReapply && WindowBackdropType == desiredBackdrop)
            WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;

        WindowBackdropType = desiredBackdrop;
        DwmBlurBackdrop.Sync(this, blur);
        ApplyCoverBaseBackground();
    }
}
