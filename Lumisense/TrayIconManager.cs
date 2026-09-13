using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Tray;

namespace Lumisense;

// Значок в трее при свёрнутом окне поверх Wpf.Ui.Tray.NotifyIconService. Контекстное меню —
// обычный WPF ContextMenu: App.xaml уже глобально переопределяет ContextMenu/MenuItem/Separator
// под Fluent-тему (ui:ControlsDictionary), поэтому меню трея выглядит так же, как остальные меню
// плеера, автоматически и без единой строчки собственной отрисовки — раньше здесь было ~300
// строк ручного GDI+ поверх System.Windows.Forms.NotifyIcon (скруглённый Region, свой
// ToolStripProfessionalRenderer, нарисованные вручную иконки под каждую тему).
public sealed class TrayIconManager : NotifyIconService, IDisposable
{
    private const int ArtThumbnailSize = 20;
    private const double ArtCornerRadius = 6;
    private const int NowPlayingTextLimit = 34;

    private readonly MenuItem _nowPlayingItem;
    private readonly MenuItem _playPauseItem;
    private readonly MenuItem _openItem;
    private readonly MenuItem _nextItem;
    private readonly MenuItem _previousItem;
    private readonly MenuItem _exitItem;
    private readonly MenuItem _settingsItem;

    private bool _disposed;
    private bool _isPlaying;
    private string _nowPlayingTitle = "";
    private string _nowPlayingArtist = "";

    public event Action? OpenRequested;
    public event Action? ExitRequested;
    public event Action? PlayPauseRequested;
    public event Action? NextRequested;
    public event Action? PreviousRequested;
    public event Action? SettingsRequested;

    public TrayIconManager(Window owner)
    {
        SetParentWindow(owner);
        TooltipText = "Lumisense";
        Icon = LoadAppIcon();

        var headerItem = new MenuItem
        {
            Header = "Lumisense",
            IsEnabled = false,
            FontWeight = FontWeights.SemiBold,
            Icon = new SvgPathIcon { Size = 14, Icon = "IconMusicNote" }
        };
        headerItem.SetResourceReference(Control.ForegroundProperty, "AccentFillColorDefaultBrush");

        _nowPlayingItem = new MenuItem
        {
            Header = LocalizationService.Translate("Ничего не играет"),
            IsEnabled = false,
            FontSize = 12
        };

        _settingsItem = BuildItem(LocalizationService.Translate("Настройки"), "IconSettings", () => SettingsRequested?.Invoke());
        _openItem = BuildItem(LocalizationService.Translate("Открыть Lumisense"), "IconWindow", () => OpenRequested?.Invoke());
        _playPauseItem = BuildItem(LocalizationService.Translate("Пауза"), "IconPause", () => PlayPauseRequested?.Invoke());
        _nextItem = BuildItem(LocalizationService.Translate("Следующий трек"), "IconNext", () => NextRequested?.Invoke());
        _previousItem = BuildItem(LocalizationService.Translate("Предыдущий трек"), "IconPrevious", () => PreviousRequested?.Invoke());
        _exitItem = BuildItem(LocalizationService.Translate("Выход"), "IconDismiss", () => ExitRequested?.Invoke());

        var menu = new ContextMenu();
        menu.Items.Add(headerItem);
        menu.Items.Add(_nowPlayingItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_settingsItem);
        menu.Items.Add(_openItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_playPauseItem);
        menu.Items.Add(_nextItem);
        menu.Items.Add(_previousItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_exitItem);
        ContextMenu = menu;

        LocalizationService.LanguageChanged += LocalizationService_LanguageChanged;
    }

    private static MenuItem BuildItem(string header, string iconKey, Action onClick)
    {
        var item = new MenuItem { Header = header, Icon = new SvgPathIcon { Size = 14, Icon = iconKey } };
        item.Click += (_, _) => onClick();
        return item;
    }

    protected override void OnLeftDoubleClick() => OpenRequested?.Invoke();

    private void LocalizationService_LanguageChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;

        _openItem.Header = LocalizationService.Translate("Открыть Lumisense");
        _nextItem.Header = LocalizationService.Translate("Следующий трек");
        _previousItem.Header = LocalizationService.Translate("Предыдущий трек");
        _exitItem.Header = LocalizationService.Translate("Выход");
        SetPlayingState(_isPlaying);
        UpdateNowPlayingText();
    }

    // Иконка самого плеера — та же, что и у .exe/окон, встроенная как Resource (см.
    // Lumisense.csproj), поэтому не зависит от способа публикации (single-file и т.п.),
    // в отличие от прежнего Icon.ExtractAssociatedIcon из запущенного процесса.
    private static ImageSource? LoadAppIcon()
    {
        try
        {
            // BitmapCacheOption.OnLoad — принудительно синхронное декодирование. Icon
            // выставляется один раз в конструкторе и больше никогда не переустанавливается,
            // поэтому если бы пиксели догружались лениво (поведение по умолчанию для
            // BitmapFrame.Create), а Register() внутри NotifyIconService конвертировал бы
            // ImageSource в нативный HICON сразу же — на самом первом запуске (особенно если
            // приложение стартует сразу в мини-режиме, то есть очень рано, до того как
            // Dispatcher начал прокачивать сообщения) значок мог остаться пустым до следующей
            // полной перерегистрации (Hide+Show), которая как раз и происходит при переключении
            // между окном и мини-плеером — этим и объясняется, что после такого переключения
            // всё сразу начинает работать правильно.
            return BitmapFrame.Create(
                new Uri("pack://application:,,,/Icons/app/lumisense.ico", UriKind.Absolute),
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }
        catch
        {
            return null;
        }
    }

    // Вызывается из MainWindow на каждый PlaybackStateChanged, чтобы пункт меню всегда
    // отражал реальное состояние, а не оставался статичной надписью "Пауза".
    public void SetPlayingState(bool isPlaying)
    {
        _isPlaying = isPlaying;
        _playPauseItem.Header = LocalizationService.Translate(isPlaying ? "Пауза" : "Продолжить");
        _playPauseItem.Icon = new SvgPathIcon { Size = 14, Icon = isPlaying ? "IconPause" : "IconPlay" };
    }

    // Название/исполнитель + миниатюра обложки прямо в меню трея, как в мини-плеере.
    // Вызывается из MainWindow на каждый TrackInfoChanged.
    public void SetNowPlaying(string title, string artist, byte[]? artBytes)
    {
        _nowPlayingTitle = title;
        _nowPlayingArtist = artist;
        UpdateNowPlayingText();

        var thumbnail = BuildRoundedThumbnail(artBytes);
        _nowPlayingItem.Icon = thumbnail is null
            ? null
            : new Image { Source = thumbnail, Width = ArtThumbnailSize, Height = ArtThumbnailSize };
    }

    private void UpdateNowPlayingText()
    {
        var text = string.IsNullOrWhiteSpace(_nowPlayingTitle)
            ? LocalizationService.Translate("Ничего не играет")
            : $"{_nowPlayingTitle} — {_nowPlayingArtist}";
        _nowPlayingItem.Header = TruncateWithEllipsis(text, NowPlayingTextLimit);
    }

    // Декодирование могло бы упасть на битых/незнакомых по формату тегах — сам плеер в этом
    // случае и так показывает плейсхолдер вместо обложки в своём окне, поэтому здесь просто
    // не показываем миниатюру вовсе, а не роняем меню трея из-за одного плохого файла обложки.
    // WPF-изображения управляются GC, отдельного Dispose (в отличие от GDI+ Bitmap) не нужно.
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

    // WPF ContextMenu/MenuItem уже сами тонируются под текущую Fluent-тему через DynamicResource
    // (см. ui:ControlsDictionary в App.xaml) — в отличие от прежнего WinForms-меню, здесь больше
    // нечего перекрашивать вручную. Метод оставлен пустым, чтобы не трогать вызовы в MainWindow.
    public void ApplyTheme(bool isLight) { }

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

    public void UpdateTooltip(string text) => TooltipText = Truncate(text, 63);

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

    // У пункта меню нет безопасного MaxWidth, одинаково работающего при любом масштабировании
    // Windows. Ограничиваем сами данные: длинное название не растянет выпадающее меню,
    // а многоточие честно показывает, что строка сокращена.
    private static string TruncateWithEllipsis(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..Math.Max(1, maxLength - 1)] + "…";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        LocalizationService.LanguageChanged -= LocalizationService_LanguageChanged;
        Unregister();
    }
}
