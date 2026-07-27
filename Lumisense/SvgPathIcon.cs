using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Xml.Linq;
using SharpVectors.Converters;
using Wpf.Ui.Controls;

namespace AudioPlayer;

// Векторная SVG-иконка: рисует .svg из Icons/svg через SharpVectors, а не геометрию,
// зашитую в код/XAML. Чтобы поменять иконку — заменить файл и пересобрать. Цвет заливки
// всегда берётся из Foreground, исходный fill в самом .svg не важен, SharpVectors его подменяет.
// Размер по умолчанию — из атрибута data-default-size на корневом <svg> (Icons/svg/README.md),
// но Size можно задать и явно.
public sealed class SvgPathIcon : IconElement
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(string), typeof(SvgPathIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(SvgPathIcon),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public string? Icon
    {
        get => (string?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    protected override UIElement InitializeChildren()
    {
        // SvgIcon (SharpVectors) — специализация Image для монохромных SVG: заливка результата
        // привязывается к его свойству Fill целиком, независимо от того, что указано в самом файле.
        var icon = new SvgIcon { Stretch = Stretch.Uniform };

        icon.SetBinding(SvgIcon.FillProperty, new Binding(nameof(Foreground)) { Source = this });
        icon.SetBinding(SvgIcon.UriSourceProperty,
            new Binding(nameof(Icon)) { Source = this, Converter = IconKeyToUriConverter.Instance });

        var sizeBinding = new MultiBinding { Converter = IconSizeConverter.Instance };
        sizeBinding.Bindings.Add(new Binding(nameof(Icon)) { Source = this });
        sizeBinding.Bindings.Add(new Binding(nameof(Size)) { Source = this });
        icon.SetBinding(WidthProperty, sizeBinding);
        icon.SetBinding(HeightProperty, sizeBinding);

        return icon;
    }

    // "IconPlay" → pack-URI файла Icons/svg/IconPlay.svg
    private sealed class IconKeyToUriConverter : IValueConverter
    {
        public static readonly IconKeyToUriConverter Instance = new();

        public object? Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
            value is string key ? new Uri($"pack://application:,,,/Icons/svg/{key}.svg") : null;

        public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    // Если Size не задан (NaN) — читает data-default-size из .svg-файла, кэширует результат
    private sealed class IconSizeConverter : IMultiValueConverter
    {
        public static readonly IconSizeConverter Instance = new();

        private const double Fallback = 20.0;
        private static readonly Dictionary<string, double> Cache = new();

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var key = values[0] as string;
            var explicitSize = values[1] is double d ? d : double.NaN;

            if (!double.IsNaN(explicitSize))
                return explicitSize;

            return key is not null ? GetDefaultSize(key) : Fallback;
        }

        private static double GetDefaultSize(string key)
        {
            if (Cache.TryGetValue(key, out var cached))
                return cached;

            var size = Fallback;
            try
            {
                var streamInfo = Application.GetResourceStream(new Uri($"/Icons/svg/{key}.svg", UriKind.Relative));
                if (streamInfo is not null)
                {
                    using var stream = streamInfo.Stream;
                    var root = XDocument.Load(stream).Root;
                    var attr = root?.Attribute("data-default-size");
                    if (attr is not null && double.TryParse(attr.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                        size = parsed;
                }
            }
            catch (IOException) { /* используем Fallback */ }
            catch (System.Xml.XmlException) { /* используем Fallback */ }

            Cache[key] = size;
            return size;
        }

        public object[] ConvertBack(object? value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
