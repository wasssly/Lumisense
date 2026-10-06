using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
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

    public static string GetFileName(string icon) => FileNames[icon];

    public static Uri GetUri(string icon) =>
        new($"pack://application:,,,/Icons/app/{FileNames[icon]}.ico", UriKind.Absolute);

    // OnLoad — синхронное декодирование: ленивая загрузка могла на миг оставить пустые пиксели
    // при живой смене (см. аналогичный комментарий в прежнем TrayIconManager.LoadAppIcon).
    private static ImageSource Load(string icon) =>
        BitmapFrame.Create(GetUri(icon), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

    private const int SM_CXSMICON = 49;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    // WPF-UI делает HICON трея из одного кадра, а первый кадр .ico — 256 px: Windows грубо уменьшала его до
    // 16–32 px. Берём кадр размера трея или, если такого нет, уменьшаем ближайший больший с HighQuality.
    public static ImageSource LoadTrayIcon(string icon)
    {
        int target = Math.Max(16, GetSystemMetrics(SM_CXSMICON));
        BitmapDecoder decoder = BitmapDecoder.Create(GetUri(icon), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        BitmapFrame? exact = decoder.Frames.FirstOrDefault(frame => frame.PixelWidth == target);
        if (exact is not null) return exact;

        BitmapFrame source = decoder.Frames.Where(frame => frame.PixelWidth > target).OrderBy(frame => frame.PixelWidth).FirstOrDefault()
            ?? decoder.Frames.OrderByDescending(frame => frame.PixelWidth).First();

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (DrawingContext context = visual.RenderOpen())
            context.DrawImage(source, new Rect(0, 0, target, target));

        var scaled = new RenderTargetBitmap(target, target, 96, 96, PixelFormats.Pbgra32);
        scaled.Render(visual);
        scaled.Freeze();
        return BitmapFrame.Create(scaled);
    }
}
