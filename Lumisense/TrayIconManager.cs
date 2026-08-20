using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace AudioPlayer;

// Обёртка над System.Windows.Forms.NotifyIcon — значок в трее при свёрнутом окне,
// с контекстным меню (воспроизведение, открытие/выход). Отдельный класс, чтобы не тащить
// using System.Windows.Forms в MainWindow — там уже есть WPF-тёзки вроде Button/MessageBox.
//
// Меню оформлено вручную под фирменный стиль: скруглённые углы, акцент #605CFF на подсветке
// и на иконке-логотипе в шапке, свои векторные иконки пунктов (GDI+, в духе Segoe Fluent),
// шапка с названием приложения вместо системного "Открыть Lumisense".
public sealed class TrayIconManager : IDisposable
{
    // Тот же акцент, что и в остальном приложении (см. AccentFillColorDefaultBrush/App.xaml)
    private static readonly Color Accent = Color.FromArgb(96, 92, 255);
    private const int CornerRadius = 8;
    private const int ItemCornerRadius = 6;
    private const int ArtThumbnailSize = 28;
    private const int ArtCornerRadius = 8;

    private readonly NotifyIcon _notifyIcon;
    private readonly RoundedContextMenuStrip _menu;
    private readonly ToolStripMenuItem _headerItem;
    private readonly Font _headerFont;
    private readonly Font _nowPlayingFont;
    private readonly Font _menuFont;
    private readonly Icon? _ownedAppIcon;
    private bool _disposed;
    private readonly ToolStripMenuItem _nowPlayingItem;
    private readonly ToolStripMenuItem _playPauseItem;
    private readonly ToolStripMenuItem _openItem;
    private readonly ToolStripMenuItem _nextItem;
    private readonly ToolStripMenuItem _previousItem;
    private readonly ToolStripMenuItem _exitItem;

    private bool _isLight;
    private bool _isPlaying;
    private string _nowPlayingTitle = "";
    private string _nowPlayingArtist = "";

    // Миниатюра обложки, показанная сейчас в пункте "сейчас играет" (см. SetNowPlaying) —
    // хранится отдельно, чтобы её можно было корректно освободить (Bitmap — обёртка над GDI-
    // хендлом, а не управляемая память) перед тем, как заменить на следующую при смене трека.
    private Bitmap? _currentArtThumbnail;

    public event Action? OpenRequested;
    public event Action? ExitRequested;
    public event Action? PlayPauseRequested;
    public event Action? NextRequested;
    public event Action? PreviousRequested;

    public TrayIconManager()
    {
        _headerFont = new Font("Segoe UI Semibold", 9.5f);
        _nowPlayingFont = new Font("Segoe UI", 8.25f);
        _menuFont = new Font("Segoe UI", 9f);

        _headerItem = new ToolStripMenuItem("Lumisense")
        {
            Enabled = false,
            Font = _headerFont,
            Image = TrayIcons.Logo(Accent),
            ImageScaling = ToolStripItemImageScaling.None
        };

        _nowPlayingItem = new ToolStripMenuItem(LocalizationService.Translate("Ничего не играет"))
        {
            Enabled = false,
            Font = _nowPlayingFont,
            AutoToolTip = false
        };

        _openItem = new ToolStripMenuItem(LocalizationService.Translate("Открыть Lumisense"), null, (_, _) => OpenRequested?.Invoke());
        _playPauseItem = new ToolStripMenuItem(LocalizationService.Translate("Пауза"), null, (_, _) => PlayPauseRequested?.Invoke());
        _nextItem = new ToolStripMenuItem(LocalizationService.Translate("Следующий трек"), null, (_, _) => NextRequested?.Invoke());
        _previousItem = new ToolStripMenuItem(LocalizationService.Translate("Предыдущий трек"), null, (_, _) => PreviousRequested?.Invoke());
        _exitItem = new ToolStripMenuItem(LocalizationService.Translate("Выход"), null, (_, _) => ExitRequested?.Invoke());

        _menu = new RoundedContextMenuStrip(CornerRadius)
        {
            ShowImageMargin = true,
            Font = _menuFont,
            Padding = new Padding(4, 6, 4, 6)
        };
        _menu.Items.Add(_headerItem);
        _menu.Items.Add(_nowPlayingItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_openItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_playPauseItem);
        _menu.Items.Add(_nextItem);
        _menu.Items.Add(_previousItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_exitItem);

        var appIcon = LoadAppIcon(out var ownsAppIcon);
        _ownedAppIcon = ownsAppIcon ? appIcon : null;
        _notifyIcon = new NotifyIcon
        {
            Icon = appIcon,
            Text = "Lumisense",
            Visible = false,
            ContextMenuStrip = _menu
        };
        _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke();

        ApplyTheme(isLight: false); // тема применяется поверх при старте — см. MainWindow.OnSourceInitialized
        LocalizationService.LanguageChanged += LocalizationService_LanguageChanged;
    }

    private void LocalizationService_LanguageChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;

        _openItem.Text = LocalizationService.Translate("Открыть Lumisense");
        _nextItem.Text = LocalizationService.Translate("Следующий трек");
        _previousItem.Text = LocalizationService.Translate("Предыдущий трек");
        _exitItem.Text = LocalizationService.Translate("Выход");
        SetPlayingState(_isPlaying);
        UpdateNowPlayingText();
    }

    // Иконка самого плеера (та же, что и у .exe/окон), а не общая системная — берём прямо
    // из запущенного исполняемого файла, поэтому не зависим от того, лежит ли .ico-файл
    // рядом при разных вариантах публикации (single-file и т.п.). SystemIcons.Application —
    // запасной вариант на случай, если извлечь иконку почему-то не удалось.
    private static Icon LoadAppIcon(out bool ownsIcon)
    {
        ownsIcon = false;
        try
        {
            var path = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ??
                       Path.Combine(AppContext.BaseDirectory, "Lumisense.exe");

            var extracted = Icon.ExtractAssociatedIcon(path);
            if (extracted != null)
            {
                ownsIcon = true;
                return extracted;
            }
        }
        catch
        {
            // ignore — используем запасную иконку ниже
        }

        return SystemIcons.Application;
    }

    // Вызывается из MainWindow на каждый PlaybackStateChanged, чтобы пункт меню всегда
    // отражал реальное состояние, а не оставался статичной надписью "Пауза"
    public void SetPlayingState(bool isPlaying)
    {
        _isPlaying = isPlaying;
        _playPauseItem.Text = LocalizationService.Translate(isPlaying ? "Пауза" : "Продолжить");
        ReplaceMenuImage(_playPauseItem, TrayIcons.PlayPause(isPlaying, ForegroundColor));
    }

    // Название/исполнитель + миниатюра обложки прямо в меню трея, как в мини-плеере.
    // Вызывается из MainWindow на каждый TrackInfoChanged.
    public void SetNowPlaying(string title, string artist, byte[]? artBytes)
    {
        _nowPlayingTitle = title;
        _nowPlayingArtist = artist;
        UpdateNowPlayingText();

        var previousThumbnail = _currentArtThumbnail;
        _currentArtThumbnail = BuildRoundedThumbnail(artBytes);
        _nowPlayingItem.Image = _currentArtThumbnail;
        _nowPlayingItem.ImageScaling = ToolStripItemImageScaling.None;

        // Освобождаем СТАРУЮ миниатюру уже после того, как новая (или null) назначена пункту
        // меню — если освободить раньше, а перерисовка пункта меню случится ровно в этот
        // промежуток, WinForms попытается нарисовать уже освобождённый Bitmap.
        previousThumbnail?.Dispose();
    }

    private void UpdateNowPlayingText()
    {
        var text = string.IsNullOrWhiteSpace(_nowPlayingTitle)
            ? LocalizationService.Translate("Ничего не играет")
            : $"{_nowPlayingTitle} — {_nowPlayingArtist}";
        _nowPlayingItem.Text = Truncate(text, 60);
    }

    // Декодирование могло бы упасть на битых/незнакомых по формату тегах — сам плеер в этом
    // случае и так показывает плейсхолдер вместо обложки в своём окне (см.
    // MainWindow.ResetAlbumArtPlaceholder), поэтому здесь просто не показываем миниатюру
    // вовсе, а не роняем меню трея из-за одного плохого файла с обложкой.
    private static Bitmap? BuildRoundedThumbnail(byte[]? artBytes)
    {
        if (artBytes is null || artBytes.Length == 0) return null;

        Bitmap? thumbnail = null;
        try
        {
            using var source = new Bitmap(new MemoryStream(artBytes));
            thumbnail = new Bitmap(ArtThumbnailSize, ArtThumbnailSize);

            using (var g = Graphics.FromImage(thumbnail))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                // Скруглённые углы миниатюры — тот же приём (обрезка по GraphicsPath), что и у
                // обложки в самом плеере (ArtBorder CornerRadius=8 в MiniPlayerWindow.xaml) и у
                // самого выпадающего меню трея (см. RoundedContextMenuStrip ниже в этом файле).
                var bounds = new Rectangle(0, 0, ArtThumbnailSize, ArtThumbnailSize);
                using var path = RoundedPath(bounds, ArtCornerRadius);
                g.SetClip(path);

                g.DrawImage(source, bounds);
            }

            return thumbnail;
        }
        catch
        {
            thumbnail?.Dispose();
            return null;
        }
    }

    private static GraphicsPath RoundedPath(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private Color ForegroundColor => _isLight ? Color.Black : Color.White;

    // WinForms-меню не подхватывает Fluent-тему WPF-UI автоматически (это два разных UI-стека),
    // поэтому без этого трей всегда показывал бы стандартное светлое системное меню, даже
    // когда весь остальной плеер — в тёмной теме. ToolStripProfessionalRenderer с подменённой
    // ProfessionalColorTable — стандартный приём для тонирования WinForms-меню под конкретную
    // палитру. Вызывается один раз при старте (см. конструктор) и повторно — при переключении
    // темы в настройках (см. MainWindow.ApplyTrayTheme).
    public void ApplyTheme(bool isLight)
    {
        _isLight = isLight;

        var colors = new TrayColorTable(isLight);
        _menu.Renderer = new RoundedMenuRenderer(colors, ItemCornerRadius);
        _menu.BackColor = colors.ToolStripDropDownBackground;
        _menu.ForeColor = ForegroundColor;

        foreach (ToolStripItem item in _menu.Items)
            item.ForeColor = _menu.ForeColor;

        _headerItem.ForeColor = Accent;
        _nowPlayingItem.ForeColor = isLight ? Color.FromArgb(110, 110, 110) : Color.FromArgb(170, 170, 170);

        // Иконки пунктов перерисовываем под новый цвет текста темы, чтобы они не выглядели
        // тёмными штрихами на тёмном фоне (или наоборот)
        ReplaceMenuImage(_openItem, TrayIcons.OpenApp(ForegroundColor));
        ReplaceMenuImage(_playPauseItem, TrayIcons.PlayPause(_isPlaying, ForegroundColor));
        ReplaceMenuImage(_nextItem, TrayIcons.Next(ForegroundColor));
        ReplaceMenuImage(_previousItem, TrayIcons.Previous(ForegroundColor));
        ReplaceMenuImage(_exitItem, TrayIcons.Exit(ForegroundColor));

        _menu.RefreshRoundedRegion();
    }

    private static void ReplaceMenuImage(ToolStripItem item, Image replacement)
    {
        var previous = item.Image;
        item.Image = replacement;
        if (!ReferenceEquals(previous, replacement))
            previous?.Dispose();
    }

    private void DisposeMenuImages()
    {
        var currentThumbnail = _currentArtThumbnail;
        _currentArtThumbnail = null;

        foreach (ToolStripItem item in _menu.Items)
        {
            var image = item.Image;
            item.Image = null;
            if (image is not null && !ReferenceEquals(image, currentThumbnail))
                image.Dispose();
        }

        currentThumbnail?.Dispose();
    }

    public void Show(string? tooltipText = null)
    {
        if (tooltipText != null)
            _notifyIcon.Text = Truncate(tooltipText, 63); // у NotifyIcon.Text лимит в 63 символа

        _notifyIcon.Visible = true;
    }

    public void UpdateTooltip(string text) => _notifyIcon.Text = Truncate(text, 63);

    public void Hide() => _notifyIcon.Visible = false;

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        LocalizationService.LanguageChanged -= LocalizationService_LanguageChanged;

        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip = null;
        _notifyIcon.Icon = null;
        _notifyIcon.Dispose();

        DisposeMenuImages();
        _headerItem.Font = null;
        _nowPlayingItem.Font = null;
        _menu.Font = null;
        _menu.Dispose();

        _headerFont.Dispose();
        _nowPlayingFont.Dispose();
        _menuFont.Dispose();
        _ownedAppIcon?.Dispose();
    }

    // Тёмный вариант в стиле Fluent/Mica (тёмно-серые фоны, акцентная подсветка),
    // светлый — нейтральный набор для тех, кто выбрал светлую тему
    private sealed class TrayColorTable : ProfessionalColorTable
    {
        private readonly bool _isLight;
        public TrayColorTable(bool isLight) => _isLight = isLight;

        private Color Background => _isLight ? Color.FromArgb(252, 252, 252) : Color.FromArgb(32, 32, 32);

        // полупрозрачный акцент поверх фона, а не нейтральный серый — чтобы выделение читалось
        // как часть фирменного стиля, а не как стандартный Windows-контрол
        public Color Hover => Blend(Background, Accent, _isLight ? 0.16 : 0.24);
        private Color Border => _isLight ? Color.FromArgb(218, 218, 218) : Color.FromArgb(58, 58, 61);

        public override Color ToolStripDropDownBackground => Background;
        public override Color ImageMarginGradientBegin => Background;
        public override Color ImageMarginGradientMiddle => Background;
        public override Color ImageMarginGradientEnd => Background;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Accent;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;

        private static Color Blend(Color background, Color accent, double amount) => Color.FromArgb(
            (int)(background.R + (accent.R - background.R) * amount),
            (int)(background.G + (accent.G - background.G) * amount),
            (int)(background.B + (accent.B - background.B) * amount));
    }

    // Подсветка наведённого пункта и разделители со скруглёнными углами — тот же приём,
    // что и по всему остальному интерфейсу плеера, вместо прямоугольных полос WinForms-меню
    private sealed class RoundedMenuRenderer : ToolStripProfessionalRenderer
    {
        private readonly int _radius;
        public RoundedMenuRenderer(ProfessionalColorTable table, int radius) : base(table)
        {
            _radius = radius;
            RoundedEdges = false;
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected && !(e.Item is ToolStripMenuItem { Pressed: true }))
            {
                base.OnRenderMenuItemBackground(e);
                return;
            }

            var bounds = new Rectangle(Point.Empty, e.Item.Size);
            bounds.Inflate(-2, -1);
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            using var path = RoundedPath(bounds, _radius);
            using var brush = new SolidBrush(((TrayColorTable)ColorTable).Hover);

            var g = e.Graphics;
            var oldMode = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillPath(brush, path);
            g.SmoothingMode = oldMode;
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var bounds = new Rectangle(6, e.Item.Height / 2 - 1, e.Item.Width - 12, 1);
            using var pen = new Pen(ColorTable.SeparatorDark);
            e.Graphics.DrawLine(pen, bounds.Left, bounds.Y, bounds.Right, bounds.Y);
        }

        private static GraphicsPath RoundedPath(Rectangle bounds, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    // Скруглённые углы выпадающего окна меню — форма задаётся через Region (GDI-приём: у
    // окна нет CornerRadius, обрезаем по скруглённому пути), пересчитывается при ресайзе
    private sealed class RoundedContextMenuStrip : ContextMenuStrip
    {
        private readonly int _radius;
        public RoundedContextMenuStrip(int radius) => _radius = radius;

        public void RefreshRoundedRegion() => ApplyRoundedRegion();

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            ApplyRoundedRegion();
        }

        private void ApplyRoundedRegion()
        {
            if (Width <= 0 || Height <= 0) return;

            int d = _radius * 2;
            var bounds = new Rectangle(0, 0, Width, Height);
            using var path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();

            var previousRegion = Region;
            Region = new Region(path);
            previousRegion?.Dispose();
        }
    }

    // Маленькие (16x16) векторные иконки пунктов меню, нарисованные вручную через GDI+
    // в духе Segoe Fluent icons — геометрия, а не растровые ассеты, поэтому ровные на любом
    // DPI и перекрашиваются под тему одной заменой цвета
    private static class TrayIcons
    {
        private const int Size = 16;

        public static Bitmap Logo(Color color) => Draw(g =>
        {
            // Стилизованная нота — тот же силуэт, что и IconMusicNote в остальном интерфейсе
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 2, 9, 5, 5);
            g.FillEllipse(brush, 9, 7, 5, 5);
            g.FillRectangle(brush, 6.5f, 2, 1.6f, 10.5f);
            g.FillRectangle(brush, 13.5f, 1, 1.6f, 8.5f);
            using var pen = new Pen(color, 1.6f);
            g.DrawLine(pen, 7, 3, 14, 1.6f);
        });

        public static Bitmap OpenApp(Color color) => Draw(g =>
        {
            using var pen = new Pen(color, 1.4f);
            var body = new RectangleF(2.5f, 3.5f, 11, 9);
            g.DrawRoundedRectangle(pen, body, 2f);
            g.DrawLine(pen, 2.5f, 6.5f, 13.5f, 6.5f);
            using var dot = new SolidBrush(color);
            g.FillEllipse(dot, 4f, 4.6f, 1.3f, 1.3f);
        });

        public static Bitmap PlayPause(bool isPlaying, Color color) => isPlaying
            ? Draw(g =>
            {
                using var brush = new SolidBrush(color);
                g.FillRectangle(brush, 4.5f, 3, 2.6f, 10);
                g.FillRectangle(brush, 9, 3, 2.6f, 10);
            })
            : Draw(g =>
            {
                using var brush = new SolidBrush(color);
                g.FillPolygon(brush, new PointF[] { new(4.5f, 2.5f), new(4.5f, 13.5f), new(13, 8) });
            });

        public static Bitmap Next(Color color) => Draw(g =>
        {
            using var brush = new SolidBrush(color);
            g.FillPolygon(brush, new PointF[] { new(3.5f, 3), new(3.5f, 13), new(10.5f, 8) });
            g.FillRectangle(brush, 11.5f, 3, 1.8f, 10);
        });

        public static Bitmap Previous(Color color) => Draw(g =>
        {
            using var brush = new SolidBrush(color);
            g.FillPolygon(brush, new PointF[] { new(12.5f, 3), new(12.5f, 13), new(5.5f, 8) });
            g.FillRectangle(brush, 2.7f, 3, 1.8f, 10);
        });

        public static Bitmap Exit(Color color) => Draw(g =>
        {
            using var pen = new Pen(color, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, 4, 4, 12, 12);
            g.DrawLine(pen, 12, 4, 4, 12);
        });

        private static Bitmap Draw(Action<Graphics> paint)
        {
            var bitmap = new Bitmap(Size, Size);
            using var g = Graphics.FromImage(bitmap);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            paint(g);
            return bitmap;
        }
    }
}

// Хелпер поверх GDI+ Graphics — DrawRoundedRectangle отсутствует в System.Drawing из коробки
internal static class GraphicsExtensions
{
    public static void DrawRoundedRectangle(this Graphics g, Pen pen, RectangleF bounds, float radius)
    {
        float d = radius * 2;
        using var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.DrawPath(pen, path);
    }
}
