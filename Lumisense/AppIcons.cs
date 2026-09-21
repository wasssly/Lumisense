using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lumisense;

// PropertyChanged для активной иконки: WPF Binding реагирует на смену значения только у
// INotifyPropertyChanged-источника, статика не подходит (та же причина, что у IconPackContext).
public sealed class AppIconContext : INotifyPropertyChanged
{
    public static readonly AppIconContext Instance = new();

    private ImageSource? _current;

    public ImageSource? Current
    {
        get => _current;
        set
        {
            if (ReferenceEquals(_current, value)) return;
            _current = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Current)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

// Реестр значков окон и трея: Current выставляется при старте из AppSettings.AppIcon и меняется
// вживую через SetCurrent; окна биндятся на AppIconContext.Instance.Current, трей слушает PropertyChanged.
public static class AppIcons
{
    public const string Classic = "Classic";
    public const string Aurora = "Aurora";
    public const string Lavender = "Lavender";

    public static readonly string[] All = { Classic, Aurora, Lavender };

    private static readonly Dictionary<string, string> FileNames = new()
    {
        [Classic] = "lumisense",
        [Aurora] = "lumisense-aurora",
        [Lavender] = "lumisense-lavender",
    };

    public static string Current { get; private set; } = Aurora;

    // Вызывается один раз при старте (до построения первого окна) — задаёт начальное значение
    // без необходимости в PropertyChanged, слушателей ещё нет.
    public static void Initialize(AppSettings settings)
    {
        Current = IsKnown(settings.AppIcon) ? settings.AppIcon : Aurora;
        AppIconContext.Instance.Current = Load(Current);
    }

    // Меняет активную иконку немедленно на всех открытых окнах и в трее — сохранение в
    // settings.json — отдельный шаг на стороне вызывающего кода (см. SettingsWindow).
    public static void SetCurrent(string icon)
    {
        if (!IsKnown(icon)) return;
        Current = icon;
        AppIconContext.Instance.Current = Load(icon);
    }

    public static bool IsKnown(string? icon) => icon is not null && Array.IndexOf(All, icon) >= 0;

    public static Uri GetUri(string icon) =>
        new($"pack://application:,,,/Icons/app/{FileNames[icon]}.ico", UriKind.Absolute);

    // OnLoad — синхронное декодирование: ленивая загрузка могла на миг оставить пустые пиксели
    // при живой смене (см. аналогичный комментарий в прежнем TrayIconManager.LoadAppIcon).
    private static ImageSource Load(string icon) =>
        BitmapFrame.Create(GetUri(icon), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
}
