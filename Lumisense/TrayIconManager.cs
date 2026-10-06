using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Tray;

namespace Lumisense;

// Значок в трее поверх Wpf.Ui.Tray.NotifyIconService. Меню открывается собственным окном (TrayMenuWindow) по правому клику.
public sealed class TrayIconManager : NotifyIconService, IDisposable
{
    private const int ArtThumbnailSize = 20;
    private const double ArtCornerRadius = 6;

    private bool _disposed;
    private bool _isPlaying;
    private string _nowPlayingTitle = "";
    private string _nowPlayingArtist = "";
    private ImageSource? _nowPlayingThumbnail;
    private TrayMenuWindow? _menuWindow;

    public event Action? OpenRequested;
    public event Action? ExitRequested;
    public event Action? PlayPauseRequested;
    public event Action? NextRequested;
    public event Action? PreviousRequested;
    public event Action? SettingsRequested;

    // Выставляется владельцем: настройка «Меньше анимации» живёт в AppSettings, до которой трею напрямую не добраться.
    public Func<bool>? ReduceMotionProvider { get; set; }

    public TrayIconManager(Window owner)
    {
        SetParentWindow(owner);
        TooltipText = "Lumisense";
        Icon = AppIcons.LoadTrayIcon(AppIcons.Current);
        AppIconContext.Instance.PropertyChanged += AppIconContext_PropertyChanged;

        LocalizationService.LanguageChanged += LocalizationService_LanguageChanged;
    }

    // Меню строится заново на каждое открытие: окно закрывается при потере фокуса и не должно держать приложение живым.
    protected override void OnRightClick()
    {
        if (_disposed) return;

        _menuWindow?.Close();
        bool animate = ReduceMotionProvider?.Invoke() != true;
        var window = new TrayMenuWindow(BuildEntries(), animate);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_menuWindow, window)) _menuWindow = null;
        };
        _menuWindow = window;
        window.ShowAtCursor();
    }

    private List<TrayMenuEntry> BuildEntries() =>
    [
        new(TrayMenuEntryKind.Header, "Lumisense", "IconMusicNote"),
        new(TrayMenuEntryKind.Label, BuildNowPlayingText(), Image: _nowPlayingThumbnail),
        new(TrayMenuEntryKind.Separator),
        new(TrayMenuEntryKind.Item, LocalizationService.Translate("Настройки"), "IconSettings", Click: () => SettingsRequested?.Invoke()),
        new(TrayMenuEntryKind.Item, LocalizationService.Translate("Открыть Lumisense"), "IconWindow", Click: () => OpenRequested?.Invoke()),
        new(TrayMenuEntryKind.Separator),
        new(TrayMenuEntryKind.Item, LocalizationService.Translate(_isPlaying ? "Пауза" : "Продолжить"),
            _isPlaying ? "IconPause" : "IconPlay", Click: () => PlayPauseRequested?.Invoke()),
        new(TrayMenuEntryKind.Item, LocalizationService.Translate("Следующий трек"), "IconNext", Click: () => NextRequested?.Invoke()),
        new(TrayMenuEntryKind.Item, LocalizationService.Translate("Предыдущий трек"), "IconPrevious", Click: () => PreviousRequested?.Invoke()),
        new(TrayMenuEntryKind.Separator),
        new(TrayMenuEntryKind.Item, LocalizationService.Translate("Выход"), "IconDismiss", Click: () => ExitRequested?.Invoke()),
    ];

    // Базовый NotifyIconService сам вызывает MainWindow.Show() на одном клике (FocusOnLeftClick,
    // недоступен для переопределения) — дублируем OpenRequested, чтобы мини-плеер закрылся следом.
    protected override void OnLeftClick() => OpenRequested?.Invoke();

    private void LocalizationService_LanguageChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _menuWindow?.Close();
    }

    // Живая смена значка через настройки (см. AppIcons.SetCurrent) — тот же источник, на
    // который биндится Icon у обычных окон в XAML.
    private void AppIconContext_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        Icon = AppIcons.LoadTrayIcon(AppIcons.Current);
    }

    // Вызывается из MainWindow на каждый PlaybackStateChanged; меню читает состояние при открытии.
    public void SetPlayingState(bool isPlaying) => _isPlaying = isPlaying;

    // Название/исполнитель + миниатюра обложки в меню трея, как в мини-плеере.
    // Вызывается из MainWindow на каждый TrackInfoChanged.
    public void SetNowPlaying(string title, string artist, byte[]? artBytes)
    {
        _nowPlayingTitle = title;
        _nowPlayingArtist = artist;
        _nowPlayingThumbnail = BuildRoundedThumbnail(artBytes);
    }

    private string BuildNowPlayingText() =>
        string.IsNullOrWhiteSpace(_nowPlayingTitle)
            ? LocalizationService.Translate("Ничего не играет")
            : $"{_nowPlayingTitle} — {_nowPlayingArtist}";

    // Битые/незнакомые теги — просто не показываем миниатюру, а не роняем всё меню.
    private static ImageSource? BuildRoundedThumbnail(byte[]? artBytes)
    {
        if (artBytes is null || artBytes.Length == 0) return null;

        try
        {
            using var stream = new MemoryStream(artBytes);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

            var rect = new Rect(0, 0, ArtThumbnailSize, ArtThumbnailSize);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.PushClip(new RectangleGeometry(rect, ArtCornerRadius, ArtCornerRadius));
                dc.DrawImage(frame, rect);
            }

            var rendered = new RenderTargetBitmap(ArtThumbnailSize, ArtThumbnailSize, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(visual);
            rendered.Freeze();
            return rendered;
        }
        catch
        {
            return null;
        }
    }

    // Тонируется автоматически через DynamicResource — метод-заглушка ради совместимости вызовов.
    public void ApplyTheme(bool isLight) { }

    // Подсказка статичная ("Lumisense"): NotifyIconService.TooltipText — просто {get;set;}, без
    // Shell_NotifyIcon(NIM_MODIFY), так что живое обновление на уже показанной иконке потребовало
    // бы пере-Register() с миганием значка. Название трека — через SetNowPlaying в меню трея.
    public void Show(string? tooltipText = null)
    {
        if (tooltipText != null)
            TooltipText = Truncate(tooltipText, 63); // тот же лимит, что был у Win32 NOTIFYICONDATA.szTip

        if (IsRegistered)
        {
            Logger.Info("TrayIconManager.Show: уже зарегистрирован, Register() не вызывается.");
            return;
        }

        bool ok = Register();
        Logger.Info($"TrayIconManager.Show: Register() вернул {ok}, IsRegistered={IsRegistered}, " +
                    $"ParentWindow.IsLoaded={ParentWindow?.IsLoaded}, " +
                    $"Handle={(ParentWindow is null ? "null" : new System.Windows.Interop.WindowInteropHelper(ParentWindow).Handle)}.");
    }

    public void Hide()
    {
        if (!IsRegistered)
        {
            Logger.Info("TrayIconManager.Hide: уже не зарегистрирован, Unregister() не вызывается.");
            return;
        }

        bool ok = Unregister();
        Logger.Info($"TrayIconManager.Hide: Unregister() вернул {ok}, IsRegistered={IsRegistered}.");
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _menuWindow?.Close();
        LocalizationService.LanguageChanged -= LocalizationService_LanguageChanged;
        AppIconContext.Instance.PropertyChanged -= AppIconContext_PropertyChanged;
        Unregister();
    }
}
