using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Xml.Linq;
using SharpVectors.Converters;
using Wpf.Ui.Controls;

namespace Lumisense;

// Векторная SVG-иконка: рисует .svg из Icons/svg/{Pack} через SharpVectors вместо геометрии в
// XAML. Заливка всегда берётся из Foreground — fill в самом .svg не важен. Размер по умолчанию —
// из data-default-size на корневом <svg>, если Size не задан явно.
//
// Активный пак — IconPacks.Current, читается через IconPackContext (INotifyPropertyChanged),
// поэтому смена пака (IconPacks.SetCurrent) применяется сразу на всех открытых окнах. Явно
// заданный Pack на конкретной иконке (только в галерее предпросмотра) имеет приоритет над Current.
public sealed class SvgPathIcon : IconElement
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(string), typeof(SvgPathIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(SvgPathIcon),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure));

    // Необязательное переопределение пака только для этой иконки — используется в галерее
    // предпросмотра паков на странице настроек, чтобы показать превью пака, который сейчас
    // не является активным (IconPacks.Current). В остальных местах приложения не задаётся.
    public static readonly DependencyProperty PackProperty = DependencyProperty.Register(
        nameof(Pack), typeof(string), typeof(SvgPathIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

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

    public string? Pack
    {
        get => (string?)GetValue(PackProperty);
        set => SetValue(PackProperty, value);
    }

    protected override UIElement InitializeChildren()
    {
        // SvgIcon (SharpVectors) — специализация Image для монохромных SVG: заливка результата
        // привязывается к его свойству Fill целиком, независимо от того, что указано в самом файле.
        var icon = new SvgIcon { Stretch = Stretch.Uniform };

        icon.SetBinding(SvgIcon.FillProperty, new Binding(nameof(Foreground)) { Source = this });

        var uriBinding = new MultiBinding { Converter = IconKeyToUriConverter.Instance };
        uriBinding.Bindings.Add(new Binding(nameof(Icon)) { Source = this });
        uriBinding.Bindings.Add(new Binding(nameof(Pack)) { Source = this });
        uriBinding.Bindings.Add(new Binding(nameof(IconPackContext.CurrentPack)) { Source = IconPackContext.Instance });
        icon.SetBinding(SvgIcon.UriSourceProperty, uriBinding);

        var sizeBinding = new MultiBinding { Converter = IconSizeConverter.Instance };
        sizeBinding.Bindings.Add(new Binding(nameof(Icon)) { Source = this });
        sizeBinding.Bindings.Add(new Binding(nameof(Size)) { Source = this });
        sizeBinding.Bindings.Add(new Binding(nameof(Pack)) { Source = this });
        sizeBinding.Bindings.Add(new Binding(nameof(IconPackContext.CurrentPack)) { Source = IconPackContext.Instance });
        icon.SetBinding(WidthProperty, sizeBinding);
        icon.SetBinding(HeightProperty, sizeBinding);

        return icon;
    }

    // Явный Pack игнорирует живой Current; третий параметр присутствует только чтобы
    // MultiBinding пересчитывался при смене пака.
    private sealed class IconKeyToUriConverter : IMultiValueConverter
    {
        public static readonly IconKeyToUriConverter Instance = new();

        public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values[0] is not string key) return null;
            var pack = values[1] as string ?? (values[2] as string) ?? IconPacks.Duotone;
            return new Uri($"pack://application:,,,/Icons/svg/{pack}/{key}.svg");
        }

        public object[] ConvertBack(object? value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    // Если Size не задан (NaN) — читает data-default-size из .svg-файла, кэширует результат
    // (кэш ключуется вместе с паком: у разных паков один и тот же IconXxx может иметь другой
    // data-default-size, хотя на практике мы стараемся держать его одинаковым везде).
    private sealed class IconSizeConverter : IMultiValueConverter
    {
        public static readonly IconSizeConverter Instance = new();

        private const double Fallback = 20.0;
        private static readonly Dictionary<string, double> Cache = new();

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var key = values[0] as string;
            var explicitSize = values[1] is double d ? d : double.NaN;
            var pack = values[2] as string ?? (values[3] as string) ?? IconPacks.Duotone;

            if (!double.IsNaN(explicitSize))
                return explicitSize;

            return key is not null ? GetDefaultSize(key, pack) : Fallback;
        }

        private static double GetDefaultSize(string key, string pack)
        {
            var cacheKey = $"{pack}/{key}";
            if (Cache.TryGetValue(cacheKey, out var cached))
                return cached;

            var size = Fallback;
            try
            {
                var streamInfo = Application.GetResourceStream(new Uri($"/Icons/svg/{pack}/{key}.svg", UriKind.Relative));
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

            Cache[cacheKey] = size;
            return size;
        }

        public object[] ConvertBack(object? value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}

// Единственный источник PropertyChanged для активного пака — вынесен из IconPacks в отдельный
// объект, потому что WPF Binding умеет реагировать на смену значения, только когда её источник
// реализует INotifyPropertyChanged; статическое свойство само по себе для этого не годится.
public sealed class IconPackContext : INotifyPropertyChanged
{
    public static readonly IconPackContext Instance = new();

    private string _currentPack = IconPacks.Duotone;

    public string CurrentPack
    {
        get => _currentPack;
        set
        {
            if (_currentPack == value) return;
            _currentPack = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentPack)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

// Реестр доступных паков иконок и текущего активного. IconPacks.Current выставляется при
// старте из AppSettings.IconPack, а дальше меняется вживую через IconPacks.SetCurrent —
// см. IconPackContext выше и SettingsWindow.IconPackCard_MouseLeftButtonDown.
public static class IconPacks
{
    public const string Duotone = "Duotone";
    public const string Outline = "Outline";
    public const string Bold = "Bold";
    public const string Fill = "Fill";
    public const string Thin = "Thin";

    public static readonly string[] All = { Duotone, Outline, Bold, Fill, Thin };

    // Полный список имён иконок, одинаковый для всех паков (см. Icons/svg/{Pack}/) —
    // используется окном полного превью пака (см. IconPackPreviewWindow), которое
    // открывается по ПКМ на карточке пака в настройках.
    public static readonly string[] IconNames =
    {
        "IconAdd", "IconAddFile", "IconAddFolder", "IconBell", "IconChangelog", "IconCheckmark",
        "IconChevronDown", "IconChevronRight", "IconCopy", "IconDelete", "IconDismiss", "IconEdit",
        "IconExpand", "IconGitHub", "IconHeart", "IconHeartFilled", "IconInfo", "IconKeyboard",
        "IconLyrics", "IconMinimizePlayer", "IconMoreHorizontal", "IconMusicNote", "IconNext", "IconPause",
        "IconPictureInPicture", "IconPin", "IconPlay", "IconPrevious", "IconRefresh", "IconRepeat",
        "IconRepeatAll", "IconRepeatOne", "IconSearch", "IconSettings", "IconShuffle", "IconSpeaker",
        "IconSpeaker2", "IconSpeakerMute", "IconSpeed", "IconStop", "IconSun", "IconSync",
        "IconTelegram", "IconWindow", "IconWrench",
    };

    public static string Current => IconPackContext.Instance.CurrentPack;

    // Вызывается один раз при старте (до построения первого окна) — задаёт начальный пак без
    // необходимости в PropertyChanged, слушателей ещё нет.
    public static void Initialize(AppSettings settings) =>
        IconPackContext.Instance.CurrentPack = IsKnown(settings.IconPack) ? settings.IconPack : Duotone;

    // Меняет активный пак немедленно на всех открытых окнах; сохранение в settings.json —
    // отдельный шаг на стороне вызывающего кода (см. SettingsWindow).
    public static void SetCurrent(string pack)
    {
        if (IsKnown(pack))
            IconPackContext.Instance.CurrentPack = pack;
    }

    public static bool IsKnown(string? pack) => pack is not null && Array.IndexOf(All, pack) >= 0;
}
