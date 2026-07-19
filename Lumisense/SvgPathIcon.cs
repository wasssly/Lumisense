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

/// <summary>
/// Векторная SVG-иконка: рисует настоящий .svg-файл из папки Icons/svg (через SharpVectors),
/// а не геометрию, "зашитую" в код или в XAML. Чтобы поменять иконку — просто замени файл
/// Icons/svg/{Icon}.svg на другой (экспортированный из Figma/Illustrator/Inkscape и т.п.) и
/// пересобери проект. Цвет заливки всегда берётся из Foreground этой же иконки (в т.ч. когда
/// шаблон кнопки меняет его при наведении/нажатии) — исходный fill/color внутри самого .svg
/// для этого не важен, SharpVectors подменяет его.
///
/// У SvgPathIcon только одно обязательное свойство — <see cref="Icon"/> (имя файла в Icons/svg
/// без расширения, например "IconPlay"):
///     &lt;local:SvgPathIcon Icon="IconPlay" /&gt;
/// Размер по умолчанию берётся из атрибута data-default-size на корневом &lt;svg&gt; в самом файле
/// иконки (см. Icons/svg/README.md). Явно задать Size в конкретном месте использования по-прежнему
/// можно — это исключение, а не правило.
/// </summary>
public sealed class SvgPathIcon : IconElement
{
    /// <summary>Имя файла иконки в папке Icons/svg/ без расширения (например "IconPlay").
    /// Можно задать как обычной строкой, так и через привязку (см. ExpandChevronConverter — он
    /// в зависимости от состояния возвращает то один ключ, то другой, и иконка сама перерисуется).</summary>
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(string), typeof(SvgPathIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Размер стороны квадрата, в который вписывается иконка. Если не задан (NaN, значение
    /// по умолчанию) — берётся из атрибута data-default-size корневого &lt;svg&gt; файла иконки.</summary>
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

    /// <summary>"IconPlay" → pack-URI файла Icons/svg/IconPlay.svg.</summary>
    private sealed class IconKeyToUriConverter : IValueConverter
    {
        public static readonly IconKeyToUriConverter Instance = new();

        public object? Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
            value is string key ? new Uri($"pack://application:,,,/Icons/svg/{key}.svg") : null;

        public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>Если Size не задан явно (NaN) — читает атрибут data-default-size из самого .svg-файла
    /// иконки (кэшируя результат: файл на диске за время работы программы не меняется).</summary>
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
