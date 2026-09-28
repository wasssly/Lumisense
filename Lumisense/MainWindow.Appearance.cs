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
        double artSize = big ? 260.0 : 150.0;
        AlbumArtContainer.Width = artSize;
        AlbumArtContainer.Height = artSize;
        AlbumArtBorder.Width = artSize;
        AlbumArtBorder.Height = artSize;
        // Image.Clip в XAML — под 150×150 по умолчанию; без пересчёта здесь в крупном виде
        // (260×260) было бы видно только 150×150 в углу растянутой картинки, а не всю обложку.
        var artClip = new RectangleGeometry(new Rect(0, 0, artSize, artSize), 16, 16);
        var artGhostClip = new RectangleGeometry(new Rect(0, 0, artSize, artSize), 16, 16);
        if (artClip.CanFreeze) artClip.Freeze();
        if (artGhostClip.CanFreeze) artGhostClip.Freeze();
        AlbumArtImage.Clip = artClip;
        AlbumArtGhostImage.Clip = artGhostClip;
        AlbumArtIcon.Size = big ? 64 : 36;
        AlbumArtPanel.Margin = big ? new Thickness(0, 32, 0, 20) : new Thickness(0, 8, 0, 8);

        TrackTitleText.FontSize = big ? 24 : 17;
        TrackTitleText.MaxWidth = big ? 560 : 360;
        TrackArtistText.FontSize = big ? 15 : 12;

        var controlsScale = big ? 1.25 : 1.0;
        ShuffleButton.Width = ShuffleButton.Height = 40 * controlsScale;
        RepeatButton.Width = RepeatButton.Height = 40 * controlsScale;
        PrevButton.Width = PrevButton.Height = 44 * controlsScale;
        NextButton.Width = NextButton.Height = 44 * controlsScale;
        StopButton.Width = StopButton.Height = 40 * controlsScale;
        MiniModeButton.Width = MiniModeButton.Height = 40 * controlsScale;
        PlayPauseButton.Width = PlayPauseButton.Height = 54 * controlsScale;

        ControlsPanel.Margin = big ? new Thickness(0, 22, 0, 10) : new Thickness(0, 14, 0, 6);
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
    }

    // Подложка главного окна (AppSettings.WindowBackdropType): на старте и из окна настроек при смене настройки
    // (SettingsWindow.WindowBackdropRadio_Changed) на открытом окне.
    public void ApplyWindowBackdrop(bool forceReapply = false)
    {
        var desiredBackdrop = _settings.WindowBackdropType == "Acrylic"
            ? Wpf.Ui.Controls.WindowBackdropType.Acrylic
            : Wpf.Ui.Controls.WindowBackdropType.Mica;

        // DependencyProperty не вызывает callback при присваивании того же значения, а после ApplicationThemeManager.Apply WPF-UI 4
        // может вернуть Mica при выбранном Acrylic; переход через None вызывает OnBackdropTypeChanged и накладывает нужный backdrop.
        if (forceReapply && WindowBackdropType == desiredBackdrop)
            WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;

        WindowBackdropType = desiredBackdrop;
        ApplyCoverBaseBackground();
    }
}
