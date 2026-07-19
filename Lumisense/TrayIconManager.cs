using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Windows.Forms;

namespace AudioPlayer;

/// <summary>
/// Обёртка над System.Windows.Forms.NotifyIcon — показывает значок в системном трее,
/// когда окно свёрнуто, с контекстным меню (воспроизведение и открытие/выход).
/// Вынесено в отдельный класс, чтобы не тащить using System.Windows.Forms в MainWindow
/// (там уже есть свои Button/MessageBox из WPF, а из WinForms — тёзки с теми же именами).
///
/// Меню оформлено вручную под фирменный стиль Lumisense (см. остальной WPF-UI/Fluent
/// интерфейс приложения): скруглённые углы самого выпадающего меню и подсветки пунктов
/// (тот же радиус 6-8px, что и в плейлисте/карточках), акцентный цвет приложения
/// (#605CFF) на подсветке выбранного пункта и на "иконке-логотипе" в шапке, минималистичные
/// векторные иконки пунктов (нарисованы вручную GDI+, в духе Segoe Fluent icons, которыми
/// пользуется остальной плеер), и фирменная шапка с названием приложения вместо сухого
/// системного пункта "Открыть Lumisense".
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    // Тот же акцент, что и в остальном приложении (см. AccentFillColorDefaultBrush/App.xaml)
    private static readonly Color Accent = Color.FromArgb(96, 92, 255);
    private const int CornerRadius = 8;
    private const int ItemCornerRadius = 6;

    private readonly NotifyIcon _notifyIcon;
    private readonly RoundedContextMenuStrip _menu;
    private readonly ToolStripMenuItem _headerItem;
    private readonly ToolStripMenuItem _nowPlayingItem;
    private readonly ToolStripMenuItem _playPauseItem;
    private readonly ToolStripMenuItem _openItem;
    private readonly ToolStripMenuItem _nextItem;
    private readonly ToolStripMenuItem _previousItem;
    private readonly ToolStripMenuItem _exitItem;

    private bool _isLight;

    public event Action? OpenRequested;
    public event Action? ExitRequested;
    public event Action? PlayPauseRequested;
    public event Action? NextRequested;
    public event Action? PreviousRequested;

    public TrayIconManager()
    {
        _headerItem = new ToolStripMenuItem("Lumisense")
        {
            Enabled = false,
            Font = new Font("Segoe UI Semibold", 9.5f),
            Image = TrayIcons.Logo(Accent),
            ImageScaling = ToolStripItemImageScaling.None
        };

        _nowPlayingItem = new ToolStripMenuItem("Ничего не играет")
        {
            Enabled = false,
            Font = new Font("Segoe UI", 8.25f),
            AutoToolTip = false
        };

        _openItem = new ToolStripMenuItem("Открыть Lumisense", null, (_, _) => OpenRequested?.Invoke());
        _playPauseItem = new ToolStripMenuItem("Пауза", null, (_, _) => PlayPauseRequested?.Invoke());
        _nextItem = new ToolStripMenuItem("Следующий трек", null, (_, _) => NextRequested?.Invoke());
        _previousItem = new ToolStripMenuItem("Предыдущий трек", null, (_, _) => PreviousRequested?.Invoke());
        _exitItem = new ToolStripMenuItem("Выход", null, (_, _) => ExitRequested?.Invoke());

        _menu = new RoundedContextMenuStrip(CornerRadius)
        {
            ShowImageMargin = true,
            Font = new Font("Segoe UI", 9f),
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

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "Lumisense",
            Visible = false,
            ContextMenuStrip = _menu
        };
        _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke();

        ApplyTheme(isLight: false); // тема применяется поверх при старте — см. MainWindow.OnSourceInitialized
    }

    // Иконка самого плеера (та же, что и у .exe/окон), а не общая системная — берём прямо
    // из запущенного исполняемого файла, поэтому не зависим от того, лежит ли .ico-файл
    // рядом при разных вариантах публикации (single-file и т.п.). SystemIcons.Application —
    // запасной вариант на случай, если извлечь иконку почему-то не удалось.
    private static Icon LoadAppIcon()
    {
        try
        {
            var path = Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(path))
                path = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";

            var extracted = Icon.ExtractAssociatedIcon(path);
            if (extracted != null) return extracted;
        }
        catch
        {
            // ignore — используем запасную иконку ниже
        }

        return SystemIcons.Application;
    }

    /// <summary>Подпись текущего трека под кнопкой "Пауза"/"Продолжить" в меню — вызывается
    /// из MainWindow при каждом PlaybackStateChanged, чтобы пункт всегда отражал реальное
    /// состояние воспроизведения, а не просто оставался статичной надписью "Пауза".</summary>
    public void SetPlayingState(bool isPlaying)
    {
        _playPauseItem.Text = isPlaying ? "Пауза" : "Продолжить";
        _playPauseItem.Image = TrayIcons.PlayPause(isPlaying, ForegroundColor);
    }

    /// <summary>Название текущего трека прямо в меню трея — обновляется из MainWindow при
    /// каждой смене трека (см. TrackInfoChanged), чтобы не приходилось открывать окно плеера,
    /// чтобы узнать, что сейчас играет.</summary>
    public void SetNowPlayingText(string title, string artist)
    {
        var text = string.IsNullOrWhiteSpace(title) ? "Ничего не играет" : $"{title} — {artist}";
        _nowPlayingItem.Text = Truncate(text, 60);
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
        _openItem.Image = TrayIcons.OpenApp(ForegroundColor);
        _playPauseItem.Image = TrayIcons.PlayPause(_playPauseItem.Text == "Пауза", ForegroundColor);
        _nextItem.Image = TrayIcons.Next(ForegroundColor);
        _previousItem.Image = TrayIcons.Previous(ForegroundColor);
        _exitItem.Image = TrayIcons.Exit(ForegroundColor);

        _menu.RefreshRoundedRegion();
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
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    /// <summary>Палитра для ToolStripProfessionalRenderer — тёмный вариант в стиле Fluent/Mica
    /// остального приложения (нейтральные тёмно-серые фоны, акцентная подсветка выбранного
    /// пункта), светлый — простой нейтральный светлый набор для тех, кто выбрал светлую тему
    /// в настройках плеера.</summary>
    private sealed class TrayColorTable : ProfessionalColorTable
    {
        private readonly bool _isLight;
        public TrayColorTable(bool isLight) => _isLight = isLight;

        private Color Background => _isLight ? Color.FromArgb(252, 252, 252) : Color.FromArgb(32, 32, 32);

        // Подсветка наведённого/выбранного пункта — полупрозрачный акцент поверх фона (а не
        // нейтральный серый, как в стандартном системном меню), чтобы выделение сразу читалось
        // как часть фирменного стиля Lumisense, а не как обычный Windows-контрол.
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

    /// <summary>Рендерер поверх ToolStripProfessionalRenderer: рисует подсветку наведённого
    /// пункта и разделители со скруглёнными углами (тот же приём скругления, что используется
    /// по всему остальному интерфейсу плеера — карточки плейлиста, кнопки и т.п.), вместо
    /// стандартных прямоугольных полос WinForms-меню.</summary>
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

    /// <summary>ContextMenuStrip со скруглёнными углами самого выпадающего окна — фирменная
    /// деталь оформления, которой в стандартном WinForms-меню нет из коробки. Форма окна
    /// задаётся через Region (GDI-приём: у окна нет CornerRadius, но можно вручную обрезать
    /// его по скруглённому пути), пересчитывается при каждом изменении размера меню.</summary>
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

            Region = new Region(path);
        }
    }

    /// <summary>Маленькие (16x16) векторные иконки пунктов меню, нарисованные вручную через
    /// GDI+ в духе минималистичных Segoe Fluent icons, которыми пользуется остальной плеер
    /// (см. Icons/svg) — не растровые ассеты, а геометрия, поэтому всегда идеально ровные
    /// на любом DPI и перекрашиваются под тему одной заменой цвета.</summary>
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

/// <summary>Небольшой хелпер поверх GDI+ Graphics — DrawRoundedRectangle отсутствует в System.Drawing "из коробки".</summary>
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
