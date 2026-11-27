using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using static Lumisense.BackgroundTask;

namespace Lumisense;

public partial class SettingsWindow
{
    // Навигация: каждый пункт слева — RadioButton с Tag = ключ страницы; Checked прячет все страницы и показывает выбранную
    // ("О плеере" — тоже просто страница, а не отдельное окно).
    private void NavItem_Checked(object sender, RoutedEventArgs e)
    {
        // Полное имя типа, а не using System.Windows.Controls.Primitives; чтобы не столкнуть RadioButton
        // с Wpf.Ui.Controls.Button, который в этом файле используется как просто "Button".
        if (sender is not System.Windows.Controls.RadioButton { Tag: string key }) return;

        // Защита от раннего вызова до присвоения полей страниц в InitializeComponent (например, из-за IsChecked в XAML).
        if (PageAppearance is null) return;

        EnsurePageBuilt(key);

        PageAppearance.Visibility = key == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
        PageWindow.Visibility = key == "Window" ? Visibility.Visible : Visibility.Collapsed;
        PagePlayback.Visibility = key == "Playback" ? Visibility.Visible : Visibility.Collapsed;
        PageProgress.Visibility = key == "Progress" ? Visibility.Visible : Visibility.Collapsed;
        PageLibrary.Visibility = key == "Library" ? Visibility.Visible : Visibility.Collapsed;
        PageCover.Visibility = key == "Cover" ? Visibility.Visible : Visibility.Collapsed;
        PageLyrics.Visibility = key == "Lyrics" ? Visibility.Visible : Visibility.Collapsed;
        PageIntegrations.Visibility = key == "Integrations" ? Visibility.Visible : Visibility.Collapsed;
        PageNotifications.Visibility = key == "Notifications" ? Visibility.Visible : Visibility.Collapsed;
        PageEqualizer.Visibility = key == "Equalizer" ? Visibility.Visible : Visibility.Collapsed;
        PageMiniPlayer.Visibility = key == "MiniPlayer" ? Visibility.Visible : Visibility.Collapsed;
        PageIsland.Visibility = key == "Island" ? Visibility.Visible : Visibility.Collapsed;
        PageHotkeys.Visibility = key == "Hotkeys" ? Visibility.Visible : Visibility.Collapsed;
        PageProfile.Visibility = key == "Profile" ? Visibility.Visible : Visibility.Collapsed;
        PageUpdates.Visibility = key == "Updates" ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = key == "About" ? Visibility.Visible : Visibility.Collapsed;
        if (key == "Updates" && _lazyPages.TryGetValue(key, out ILazySettingsPage? updatesPage))
            ((UpdatesSettingsPage)updatesPage).OnShown();
        if (key == "About" && _lazyPages.TryGetValue(key, out ILazySettingsPage? aboutPage))
            ((AboutSettingsPage)aboutPage).LoadDeveloperAvatar();

        // Один ScrollViewer на все страницы (см. SettingsWindow.xaml) без сброса помнил бы прокрутку прошлой вкладки;
        // SearchResultItem_Click позже отложенно прокрутит к найденному элементу и просто переопределит позицию.
        ResetNavScrollMemory();
        PART_ContentScroll.ScrollToTop();

        FrameworkElement? activePage = key switch
        {
            "Appearance" => PageAppearance,
            "Window" => PageWindow,
            "Playback" => PagePlayback,
            "Progress" => PageProgress,
            "Library" => PageLibrary,
            "Cover" => PageCover,
            "Lyrics" => PageLyrics,
            "Integrations" => PageIntegrations,
            "Notifications" => PageNotifications,
            "Equalizer" => PageEqualizer,
            "MiniPlayer" => PageMiniPlayer,
            "Island" => PageIsland,
            "Hotkeys" => PageHotkeys,
            "Profile" => PageProfile,
            "Updates" => PageUpdates,
            "About" => PageAbout,
            _ => null
        };
        var checkedButton = (System.Windows.Controls.RadioButton)sender;
        List<System.Windows.Controls.RadioButton> navButtons =
            _sidebarNavChildren?.OfType<System.Windows.Controls.RadioButton>().ToList() ?? new List<System.Windows.Controls.RadioButton>();
        int index = navButtons.IndexOf(checkedButton);

        // В горизонтальных видах страница въезжает со стороны, куда сдвинулся выбор (вправо по списку — справа, влево — слева).
        int direction = 0;
        if (_navigationStyle != "Sidebar" && IsLoaded && index >= 0 && _lastNavIndex >= 0 && index != _lastNavIndex
            && !AccessibilityPreferences.ShouldReduceMotion(_settings, AnimationAreas.SettingsWindow))
        {
            direction = index > _lastNavIndex ? 1 : -1;
        }

        if (index >= 0) _lastNavIndex = index;

        if (activePage is not null)
        {
            // Элементы страницы проявляются по очереди во всех видах навигации; в горизонтальных видах сама страница
            // дополнительно въезжает на той же пружине, что и панель.
            AnimateSettingsPage(activePage);
            if (direction != 0) StartPageSpring(activePage, direction);
        }

        if (_navigationStyle != "Sidebar")
        {
            bool animate = IsLoaded && !AccessibilityPreferences.ShouldReduceMotion(_settings, AnimationAreas.SettingsWindow);
            RefreshNavLabels();
            UpdatePillHighlight(animate);
            if (_navigationStyle == "FloatingBar") SelectFloatingIndex(index, animate);

            // Прокручиваем к выбранному пункту, только если полоса действительно шире окна. Из-за округления ширин содержимое бывает
            // шире области просмотра на доли пикселя; BringIntoView тогда сдвигал всю панель на эту долю на каждом переключении —
            // иконки и текст чуть «дёргались», хотя прокручивать было нечего.
            if (_navigationStyle == "Pills" && TopNavScroll.ScrollableWidth > 1) checkedButton.BringIntoView();
            else if (FloatingLabelsStacked && FloatingNavScroll.ScrollableWidth > 1) checkedButton.BringIntoView();
        }
    }

    // Страницы, вынесенные в отдельные UserControl, разбираются при первом показе или в прогреве, а не при создании окна.
    private readonly Dictionary<string, ILazySettingsPage> _lazyPages = [];

    private void EnsurePageBuilt(string key)
    {
        if (_lazyPages.ContainsKey(key)) return;

        System.Windows.Controls.Panel? host = key switch
        {
            "Hotkeys" => PageHotkeys,
            "Profile" => PageProfile,
            "Notifications" => PageNotifications,
            "Equalizer" => PageEqualizer,
            "Integrations" => PageIntegrations,
            "About" => PageAbout,
            "Updates" => PageUpdates,
            "Window" => PageWindow,
            "Library" => PageLibrary,
            "Cover" => PageCover,
            "Lyrics" => PageLyrics,
            "Playback" => PagePlayback,
            "Appearance" => PageAppearance,
            "Progress" => PageProgress,
            "MiniPlayer" => PageMiniPlayer,
            _ => null
        };
        if (host is null) return;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        ILazySettingsPage page = key switch
        {
            "Hotkeys" => new HotkeysSettingsPage(this, _settings, _owner),
            "Profile" => new ProfileSettingsPage(this, _settings, _owner),
            "Equalizer" => new EqualizerSettingsPage(this, _settings, _owner),
            "Integrations" => new IntegrationsSettingsPage(this, _settings, _owner),
            "About" => new AboutSettingsPage(this, _settings, _owner),
            "Updates" => new UpdatesSettingsPage(this, _settings),
            "Window" => new WindowSettingsPage(this, _settings, _owner),
            "Library" => new LibrarySettingsPage(this, _settings, _owner),
            "Cover" => new CoverSettingsPage(this, _settings, _owner),
            "Lyrics" => new LyricsSettingsPage(this, _settings, _owner),
            "Playback" => new PlaybackSettingsPage(this, _settings, _owner),
            "Appearance" => new AppearanceSettingsPage(this, _settings, _owner),
            "Progress" => new ProgressSettingsPage(this, _settings, _owner),
            "MiniPlayer" => new MiniPlayerSettingsPage(this, _settings, _owner),
            _ => new NotificationsSettingsPage(this, _settings, _owner)
        };
        page.MoveContentTo(host);
        LocalizationService.Apply(host);
        _lazyPages[key] = page;
        Logger.Info($"TRACE settings-page-build: {key}={clock.ElapsedMilliseconds}ms");
    }

    private string? LazyPageKeyOf(FrameworkElement host) =>
        ReferenceEquals(host, PageHotkeys) ? "Hotkeys"
        : ReferenceEquals(host, PageProfile) ? "Profile"
        : ReferenceEquals(host, PageNotifications) ? "Notifications"
        : ReferenceEquals(host, PageEqualizer) ? "Equalizer"
        : ReferenceEquals(host, PageIntegrations) ? "Integrations"
        : ReferenceEquals(host, PageAbout) ? "About"
        : ReferenceEquals(host, PageUpdates) ? "Updates"
        : ReferenceEquals(host, PageWindow) ? "Window"
        : ReferenceEquals(host, PageLibrary) ? "Library"
        : ReferenceEquals(host, PageCover) ? "Cover"
        : ReferenceEquals(host, PageLyrics) ? "Lyrics"
        : ReferenceEquals(host, PagePlayback) ? "Playback"
        : ReferenceEquals(host, PageAppearance) ? "Appearance"
        : ReferenceEquals(host, PageProgress) ? "Progress"
        : ReferenceEquals(host, PageMiniPlayer) ? "MiniPlayer"
        : null;

    private FrameworkElement? ResolveLazyTarget(SettingsSearchEntry entry) =>
        entry.TargetName is { } name && _lazyPages.TryGetValue(entry.PageKey, out ILazySettingsPage? page) ? page.FindElement(name) : null;

    // Виды навигации. Пункты — одни и те же RadioButton, что и в боковой панели: ApplyNavigationStyle лишь переносит их между
    // контейнерами и меняет стиль, поэтому NavigateToPage, поиск и NavItem_Checked работают одинаково в любом виде.
    // Строка поиска и её результаты при виде без боковой панели переезжают в верхнюю полосу и во всплывающую карточку.
    private string _navigationStyle = "Sidebar";
    private List<UIElement>? _sidebarNavChildren;
    private int _lastNavIndex = -1;

    private static string NormalizeNavigationStyle(string? style) =>
        style is "Pills" or "FloatingBar" ? style : "Sidebar";

    // Панель с названиями может быть шире окна: ограничиваем её ширину окном, остальное листается внутри.
    private void UpdateFloatingBarMaxWidth()
    {
        FloatingNavBar.MaxWidth = Math.Max(240, ActualWidth - 60);
        UpdateFloatingScrollMode();
    }

    // Область прокрутки обрезается капсулой с радиусом в половину высоты: при прокрутке пункты не упираются в прямые края,
    // а у неподвижной панели обрезка совпадает со скруглением подсветки крайних пунктов.
    private void FloatingNavScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double width = FloatingNavScroll.ActualWidth, height = FloatingNavScroll.ActualHeight;
        if (width <= 0 || height <= 0) return;

        FloatingNavScroll.Clip = new System.Windows.Media.RectangleGeometry(new Rect(0, 0, width, height), height / 2, height / 2);
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private static void DetachFromParent(FrameworkElement element)
    {
        switch (element.Parent)
        {
            case System.Windows.Controls.Panel panel:
                panel.Children.Remove(element);
                break;
            case System.Windows.Controls.Decorator decorator:
                decorator.Child = null;
                break;
        }
    }

    private void ApplyNavigationStyle(string? requestedStyle)
    {
        string style = NormalizeNavigationStyle(requestedStyle);
        _navigationStyle = style;

        _sidebarNavChildren ??= NavCategoriesPanel.Children.Cast<UIElement>().ToList();
        var buttons = _sidebarNavChildren.OfType<System.Windows.Controls.RadioButton>().ToList();

        // Сначала снимаем пункты со всех контейнеров: у элемента может быть только один родитель.
        NavCategoriesPanel.Children.Clear();
        TopNavPanel.Children.Clear();
        FloatingNavPanel.Children.Clear();

        bool sidebar = style == "Sidebar";
        SidebarColumn.Width = sidebar ? new GridLength(236) : new GridLength(0);
        SidebarBorder.Visibility = sidebar ? Visibility.Visible : Visibility.Collapsed;
        TopNavBar.Visibility = sidebar ? Visibility.Collapsed : Visibility.Visible;
        TopNavStrip.Visibility = style == "Pills" ? Visibility.Visible : Visibility.Collapsed;
        FloatingNavBar.Visibility = style == "FloatingBar" ? Visibility.Visible : Visibility.Collapsed;

        DetachFromParent(SettingsSearchBox);
        DetachFromParent(SearchResultsHost);
        if (sidebar)
        {
            SidebarHeaderPanel.Children.Add(SettingsSearchBox);
            SidebarNavHost.Children.Add(SearchResultsHost);
        }
        else
        {
            TopSearchHost.Child = SettingsSearchBox;
            SearchOverlayHost.Child = SearchResultsHost;
        }

        switch (style)
        {
            case "Pills":
                SetNavButtons(buttons, "NavPillStyle", TopNavPanel);
                break;
            case "FloatingBar":
                SetNavButtons(buttons,
                    FloatingLabelsStacked ? "NavFloatingLabelStyle" : FloatingLabelsInline ? "NavFloatingInlineStyle" : "NavFloatingStyle",
                    FloatingNavPanel);
                break;
            default:
                foreach (UIElement child in _sidebarNavChildren)
                {
                    if (child is System.Windows.Controls.RadioButton radio)
                        radio.Style = (Style)FindResource("NavItemStyle");
                    NavCategoriesPanel.Children.Add(child);
                }
                break;
        }

        // Страница в плавающем виде получает нижний запас, чтобы последняя карточка не пряталась под панелью.
        ContentBorder.Margin = sidebar ? new Thickness(18, 14, 18, 16) : new Thickness(18, 0, 18, 16);
        ContentPagesGrid.Margin = style == "FloatingBar" ? new Thickness(46, 40, 46, 128) : new Thickness(46, 40, 46, 44);

        // Сбрасываем следы прошлого вида: перекраску иконок и прозрачность подписей, которые ставила плавающая панель.
        foreach (System.Windows.Controls.RadioButton radio in buttons)
        {
            radio.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
            if (GetNavLabel(radio) is { } navLabel) navLabel.ClearValue(UIElement.OpacityProperty);
        }

        _floatingTinted.Clear();

        // Параметры плавающей панели; радиус панели = радиус пункта (половина высоты) + отступ 7: края — ровные капсулы.
        WireFloatingPanel();
        bool stacked = FloatingLabelsStacked;
        FloatingNavPanel.ItemHeight = stacked ? 56 : 40;
        FloatingNavPanel.MinItemWidth = stacked ? 56 : 40;
        FloatingNavPanel.ExpandSelected = FloatingLabelsInline;
        FloatingNavPanel.InvalidateProximity();
        FloatingNavBar.CornerRadius = new CornerRadius(FloatingNavPanel.ItemHeight / 2 + 7);
        UpdateFloatingBarMaxWidth();

        _lastNavIndex = buttons.FindIndex(radio => radio.IsChecked == true);
        RefreshNavLabels();
        UpdatePillHighlight(animate: false);
        if (style == "FloatingBar") SelectFloatingIndex(_lastNavIndex, animate: false);
        UpdateTopNavArrows();
        UpdateSearchOverlayVisibility();
    }

    private void SetNavButtons(List<System.Windows.Controls.RadioButton> buttons, string styleKey, System.Windows.Controls.Panel target)
    {
        var style = (Style)FindResource(styleKey);
        foreach (System.Windows.Controls.RadioButton radio in buttons)
        {
            radio.Style = style;
            target.Children.Add(radio);
        }
    }

    private static System.Windows.Controls.TextBlock? GetNavLabel(System.Windows.Controls.RadioButton radio) =>
        (radio.Content as System.Windows.Controls.StackPanel)?.Children.OfType<System.Windows.Controls.TextBlock>().FirstOrDefault();

    // Режимы названий в плавающей панели (SettingsNavigationShowLabels + SettingsNavigationLabelMode):
    // выключено — только иконки (название в подсказке); "Below" — иконка над подписью у всех пунктов; "Selected" — подпись рядом
    // с иконкой только у выбранного пункта.
    private bool FloatingLabelsStacked =>
        _navigationStyle == "FloatingBar" && _settings.SettingsNavigationShowLabels && _settings.SettingsNavigationLabelMode != "Selected";

    private bool FloatingLabelsInline =>
        _navigationStyle == "FloatingBar" && _settings.SettingsNavigationShowLabels && _settings.SettingsNavigationLabelMode == "Selected";

    private void RefreshNavLabels()
    {
        if (_sidebarNavChildren is null) return;

        bool floating = _navigationStyle == "FloatingBar";
        bool stacked = FloatingLabelsStacked;
        bool inline = FloatingLabelsInline;
        foreach (System.Windows.Controls.RadioButton radio in _sidebarNavChildren.OfType<System.Windows.Controls.RadioButton>())
        {
            if (GetNavLabel(radio) is not { } label) continue;

            // Режим «рядом с иконкой»: подписи есть у всех пунктов, но свёрнутый пункт их обрезает, а прозрачность задаёт панель.
            bool labelVisible = !floating || stacked || inline;
            label.Visibility = labelVisible ? Visibility.Visible : Visibility.Collapsed;
            radio.ToolTip = floating && !stacked ? LocalizationService.Translate(label.Text) : null;

            if (radio.Content is System.Windows.Controls.StackPanel content)
            {
                content.Orientation = stacked ? System.Windows.Controls.Orientation.Vertical : System.Windows.Controls.Orientation.Horizontal;

                // В вертикальной раскладке иконка стоит по центру над подписью.
                if (content.Children.Count > 0 && content.Children[0] is FrameworkElement icon)
                {
                    if (stacked) icon.HorizontalAlignment = HorizontalAlignment.Center;
                    else icon.ClearValue(FrameworkElement.HorizontalAlignmentProperty);
                }
            }

            if (stacked)
            {
                label.Margin = new Thickness(0, 3, 0, 0);
                label.FontSize = 11;
                label.HorizontalAlignment = HorizontalAlignment.Center;
            }
            else
            {
                label.Margin = new Thickness(10, 0, 0, 0);
                label.ClearValue(System.Windows.Controls.TextBlock.FontSizeProperty);
                label.ClearValue(FrameworkElement.HorizontalAlignmentProperty);
            }
        }
    }

    // ----- Плавающая панель (ExpressiveNavPanel) -----
    // Пружина двигает одно «положение выбора», а панель каждый кадр выводит из него ширины пунктов, их положения и подсветку.
    // Окно лишь перекрашивает иконки и проявляет подписи по «близости» пункта к выбору (FloatingNavPanel_ProximityChanged).
    private bool _floatingWired;

    private void WireFloatingPanel()
    {
        if (_floatingWired) return;

        _floatingWired = true;
        FloatingNavPanel.ProximityChanged = FloatingNavPanel_ProximityChanged;
        FloatingNavPanel.AnimatingChanged += FloatingNavPanel_AnimatingChanged;
    }

    // Округление раскладки и привязку к пикселям на время движения НЕ отключаем: переключение этих свойств в начале и в конце
    // каждого перехода сдвигало на полпикселя все иконки и весь текст панели, в том числе неподвижные, — это и было «подёргиванием»
    // при переключении страниц. Плавность дробных сдвигов обеспечивает сама панель (остаток отдаётся преобразованию).
    private bool _floatingCentering;
    private double _fbExactStart, _fbActualStart, _fbExactEnd, _fbActualEnd;

    private void FloatingNavPanel_AnimatingChanged(bool animating)
    {
        if (animating)
        {
            // В режиме «рядом с иконкой» ширина панели меняется, а сама она центрируется: её левый край WPF округляет до пикселя на
            // каждом кадре, и содержимое шагало бы вместе с ним. Ведём содержимое плавно от начального положения к итоговому
            // (оба заранее известны и лежат на целых пикселях), поэтому в конце движения нет «доводки» и рывка в сторону.
            if (FloatingLabelsInline && !_floatingCentering && FloatingNavBar.Parent is FrameworkElement parent
                && TryGetFloatingBarFrame(out double parentWidth, out double frame))
            {
                double startWidth = FloatingNavPanel.ExactWidth + frame;
                double endWidth = FloatingNavPanel.ComputeTargetWidth() + frame;
                if (Math.Max(startWidth, endWidth) < FloatingNavBar.MaxWidth)
                {
                    double scale = FloatingNavBar.UseLayoutRounding ? VisualTreeHelper.GetDpi(this).DpiScaleX : 0;
                    _fbExactStart = (parentWidth - startWidth) / 2;
                    _fbActualStart = FloatingNavBar.TranslatePoint(new Point(0, 0), parent).X;
                    _fbExactEnd = (parentWidth - endWidth) / 2;
                    // Так же округляет раскладка WPF (Math.Round до физического пикселя).
                    _fbActualEnd = scale > 0 ? Math.Round(_fbExactEnd * scale) / scale : _fbExactEnd;

                    _floatingCentering = true;
                    FloatingNavPanel.LayoutUpdated += FloatingNavPanel_LayoutUpdated;
                }
            }

            return;
        }

        if (_floatingCentering)
        {
            FloatingNavPanel.LayoutUpdated -= FloatingNavPanel_LayoutUpdated;
            _floatingCentering = false;
        }

        FloatingNavShift.X = 0;
        ClearFloatingForegrounds();
    }

    private bool TryGetFloatingBarFrame(out double parentWidth, out double frame)
    {
        parentWidth = frame = 0;
        if (FloatingNavBar.Parent is not FrameworkElement parent || FloatingNavBar.ActualWidth <= 0) return false;

        parentWidth = parent.ActualWidth;
        frame = FloatingNavBar.Padding.Left + FloatingNavBar.Padding.Right
                + FloatingNavBar.BorderThickness.Left + FloatingNavBar.BorderThickness.Right;
        return true;
    }

    // После каждой раскладки: содержимое ставится туда, где оно должно быть при плавном движении панели от начального левого
    // края к итоговому (линейное соответствие точному положению по текущей ширине), а не туда, куда его округлила раскладка.
    // В начале и в конце движения это совпадает с раскладкой, поэтому сдвиг там нулевой.
    private void FloatingNavPanel_LayoutUpdated(object? sender, EventArgs e)
    {
        if (FloatingNavBar.Parent is not FrameworkElement parent || !TryGetFloatingBarFrame(out double parentWidth, out double frame))
        {
            FloatingNavShift.X = 0;
            return;
        }

        double exact = (parentWidth - (FloatingNavPanel.ExactWidth + frame)) / 2;
        double span = _fbExactEnd - _fbExactStart;
        double desired = Math.Abs(span) < 0.01
            ? _fbActualStart
            : _fbActualStart + (exact - _fbExactStart) * ((_fbActualEnd - _fbActualStart) / span);
        double actual = FloatingNavBar.TranslatePoint(new Point(0, 0), parent).X;
        FloatingNavShift.X = Math.Clamp(desired - actual, -2, 2);
    }

    // Полоса прокрутки внутри панели нужна, только когда панель шире окна. Пока она включена (Hidden), ширина содержимого на доли
    // пикселя больше области просмотра, и любой запрос «показать элемент» (в том числе при фокусе нажатого пункта, у которого
    // размер больше его ячейки) сдвигал содержимое на эту долю; при возвращении ширин в покой сдвиг снимался — иконки чуть
    // «уезжали» в сторону после анимации. Без переполнения прокрутка выключена совсем (Disabled): смещаться нечему.
    private void FloatingNavPanel_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateFloatingScrollMode();

    private void UpdateFloatingScrollMode()
    {
        double frame = FloatingNavBar.Padding.Left + FloatingNavBar.Padding.Right
                       + FloatingNavBar.BorderThickness.Left + FloatingNavBar.BorderThickness.Right;
        bool overflow = FloatingNavPanel.ExactWidth + frame > FloatingNavBar.MaxWidth + 0.5;
        System.Windows.Controls.ScrollBarVisibility mode = overflow
            ? System.Windows.Controls.ScrollBarVisibility.Hidden
            : System.Windows.Controls.ScrollBarVisibility.Disabled;

        if (FloatingNavScroll.HorizontalScrollBarVisibility != mode)
        {
            if (mode == System.Windows.Controls.ScrollBarVisibility.Disabled) FloatingNavScroll.ScrollToHorizontalOffset(0);
            FloatingNavScroll.HorizontalScrollBarVisibility = mode;
        }
    }

    private void FloatingNavPanel_ProximityChanged(UIElement item, double closeness)
    {
        if (item is not System.Windows.Controls.RadioButton radio) return;

        // Подпись проявляется по мере раскрытия пункта (только в режиме «рядом с иконкой»).
        if (FloatingLabelsInline && GetNavLabel(radio) is { } label)
            label.Opacity = Math.Clamp((closeness - 0.25) / 0.75, 0, 1);

        // Цвет иконки и текста плавно переходит вместе с подсветкой, а не меняется мгновенно при выборе.
        if (TryFindResource("TextFillColorSecondaryBrush") is SolidColorBrush idle
            && TryFindResource("TextOnAccentFillColorPrimaryBrush") is SolidColorBrush active)
        {
            radio.Foreground = closeness <= 0 ? idle
                : closeness >= 1 ? active
                : new SolidColorBrush(BlendColors(idle.Color, active.Color, closeness));
            _floatingTinted.Add(radio);
        }
    }

    private static Color BlendColors(Color start, Color end, double amount) => Color.FromArgb(
        (byte)Math.Round(start.A + (end.A - start.A) * amount),
        (byte)Math.Round(start.R + (end.R - start.R) * amount),
        (byte)Math.Round(start.G + (end.G - start.G) * amount),
        (byte)Math.Round(start.B + (end.B - start.B) * amount));

    // Пункты, которым панель задавала цвет: снимаем только их, а не все одиннадцать (каждое снятие перерисовывает иконку).
    private readonly HashSet<System.Windows.Controls.RadioButton> _floatingTinted = new();

    private void ClearFloatingForegrounds()
    {
        foreach (System.Windows.Controls.RadioButton radio in _floatingTinted)
            radio.ClearValue(System.Windows.Controls.Control.ForegroundProperty);

        _floatingTinted.Clear();
    }

    private void SelectFloatingIndex(int index, bool animate)
    {
        if (index < 0) return;

        FloatingNavPanel.SelectIndex(index, animate);
        if (!FloatingNavPanel.IsAnimating) ClearFloatingForegrounds();
    }

    private System.Windows.Controls.RadioButton? CheckedNavButton() =>
        _sidebarNavChildren?.OfType<System.Windows.Controls.RadioButton>().FirstOrDefault(radio => radio.IsChecked == true);

    // Скользящая акцентная подсветка выбранной таблетки: переезжает на новый пункт, меняя сдвиг и ширину.
    private void SlideNavHighlight(
        System.Windows.Controls.RadioButton? selected,
        System.Windows.Controls.Panel panel,
        System.Windows.Controls.Border highlight,
        System.Windows.Media.TranslateTransform translate,
        bool animate)
    {
        // До первой раскладки ActualWidth равна нулю: подсветка поставится по SizeChanged панели.
        if (selected is null || selected.ActualWidth <= 0 || !selected.IsDescendantOf(panel))
        {
            highlight.Visibility = Visibility.Collapsed;
            return;
        }

        double x = selected.TranslatePoint(new Point(0, 0), panel).X;
        double width = selected.ActualWidth;
        bool firstPlacement = highlight.Visibility != Visibility.Visible || highlight.Width <= 0;
        highlight.Visibility = Visibility.Visible;

        if (!animate || firstPlacement || AccessibilityPreferences.ShouldReduceMotion(_settings, AnimationAreas.SettingsWindow))
        {
            translate.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            highlight.BeginAnimation(FrameworkElement.WidthProperty, null);
            translate.X = x;
            highlight.Width = width;
            return;
        }

        var easing = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        translate.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
            new System.Windows.Media.Animation.DoubleAnimation(x, TimeSpan.FromMilliseconds(300)) { EasingFunction = easing });
        highlight.BeginAnimation(FrameworkElement.WidthProperty,
            new System.Windows.Media.Animation.DoubleAnimation(width, TimeSpan.FromMilliseconds(300)) { EasingFunction = easing });
    }

    private void UpdatePillHighlight(bool animate) =>
        SlideNavHighlight(_navigationStyle == "Pills" ? CheckedNavButton() : null, TopNavPanel, PillHighlight, PillHighlightTranslate, animate);

    // Размер панели меняется при первой раскладке, смене языка или масштаба: подсветку ставим заново без анимации.
    private void TopNavPanel_SizeChanged(object sender, SizeChangedEventArgs e) => UpdatePillHighlight(animate: false);


    internal static readonly System.Windows.Media.Animation.CubicEase NavEasing =
        new() { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };

    // ----- Стрелки по краям полосы таблеток -----
    private static readonly DependencyProperty NavScrollOffsetProperty = DependencyProperty.RegisterAttached(
        "NavScrollOffset", typeof(double), typeof(SettingsWindow),
        new PropertyMetadata(0.0, (d, e) =>
        {
            if (d is System.Windows.Controls.ScrollViewer viewer) viewer.ScrollToHorizontalOffset((double)e.NewValue);
        }));

    private double _navScrollTarget;

    private void TopNavArrow_Click(object sender, RoutedEventArgs e)
    {
        int direction = (sender as FrameworkElement)?.Tag is "-1" ? -1 : 1;

        // Если предыдущая прокрутка ещё идёт (удержание кнопки), продолжаем от её цели, иначе от текущего положения.
        double start = Math.Abs(_navScrollTarget - TopNavScroll.HorizontalOffset) > 0.5 ? _navScrollTarget : TopNavScroll.HorizontalOffset;
        double target = Math.Clamp(start + direction * 160, 0, TopNavScroll.ScrollableWidth);
        _navScrollTarget = target;

        if (AccessibilityPreferences.ShouldReduceMotion(_settings, AnimationAreas.SettingsWindow))
        {
            TopNavScroll.ScrollToHorizontalOffset(target);
            return;
        }

        TopNavScroll.BeginAnimation(NavScrollOffsetProperty,
            new System.Windows.Media.Animation.DoubleAnimation(TopNavScroll.HorizontalOffset, target, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = NavEasing
            });
    }

    private void TopNavScroll_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e) => UpdateTopNavArrows();

    // Стрелки показываются, только когда пункты не помещаются. Место, занятое самими стрелками, учитывается в проверке,
    // иначе появление стрелок сужало бы область и скрывало их обратно.
    private void UpdateTopNavArrows()
    {
        double reserved = (TopNavLeftArrow.Visibility == Visibility.Visible ? TopNavLeftArrow.ActualWidth : 0)
                          + (TopNavRightArrow.Visibility == Visibility.Visible ? TopNavRightArrow.ActualWidth : 0);
        bool overflow = TopNavScroll.ExtentWidth > TopNavScroll.ViewportWidth + reserved + 0.5;
        Visibility visibility = overflow ? Visibility.Visible : Visibility.Collapsed;

        if (TopNavLeftArrow.Visibility != visibility) TopNavLeftArrow.Visibility = visibility;
        if (TopNavRightArrow.Visibility != visibility) TopNavRightArrow.Visibility = visibility;

        TopNavLeftArrow.IsEnabled = TopNavScroll.HorizontalOffset > 0.5;
        TopNavRightArrow.IsEnabled = TopNavScroll.HorizontalOffset < TopNavScroll.ScrollableWidth - 0.5;
    }

    private void UpdateSearchOverlayVisibility() =>
        SearchOverlayHost.Visibility = _navigationStyle != "Sidebar" && SearchResultsHost.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;

    // Колесо над полосой навигации (таблетки или плавающая панель) листает её по горизонтали, если пункты не помещаются.
    private void NavScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not System.Windows.Controls.ScrollViewer viewer || viewer.ScrollableWidth <= 0) return;

        viewer.ScrollToHorizontalOffset(Math.Clamp(viewer.HorizontalOffset - e.Delta, 0, viewer.ScrollableWidth));
        e.Handled = true;
    }

    // Въезд страницы снизу (боковая панель).
    private const int StaggerMaxItems = 10;
    private const int StaggerStepMs = 50;

    // Элементы страницы проявляются по очереди; прежняя целостная анимация страницы остаётся для сдвига пружиной в горизонтальных видах.
    private void AnimateSettingsPage(FrameworkElement page)
    {
        FinishPageSpring();

        if (page is not System.Windows.Controls.Panel panel || AccessibilityPreferences.ShouldReduceMotion(_settings, AnimationAreas.SettingsWindow) || !IsLoaded) return;

        var easing = new System.Windows.Media.Animation.CubicEase
        {
            EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
        };

        int index = 0;
        foreach (UIElement child in panel.Children)
        {
            if (child.Visibility != Visibility.Visible) continue;
            if (index >= StaggerMaxItems) break;

            // Свой RenderTransform у элемента (например, из XAML) не затираем: для него остаётся только проявление.
            System.Windows.Media.TranslateTransform? translate = null;
            if (child.RenderTransform is null || child.RenderTransform == System.Windows.Media.Transform.Identity)
            {
                translate = new System.Windows.Media.TranslateTransform(0, 18);
                child.RenderTransform = translate;
            }

            var begin = TimeSpan.FromMilliseconds(index * StaggerStepMs);
            child.Opacity = 0;
            var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260))
            {
                BeginTime = begin,
                EasingFunction = easing
            };
            // После конца снимаем анимацию, иначе она удерживала бы Opacity и позже мешала бы локальным изменениям.
            fade.Completed += (_, _) =>
            {
                child.BeginAnimation(UIElement.OpacityProperty, null);
                child.Opacity = 1;
            };
            child.BeginAnimation(UIElement.OpacityProperty, fade);

            translate?.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                new System.Windows.Media.Animation.DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(360))
                {
                    BeginTime = begin,
                    EasingFunction = easing
                });
            index++;
        }
    }

    // ----- Прогрев страниц -----
    // Страница, которую ещё ни разу не показывали, при первом показе раскладывается целиком: применяются шаблоны, считается
    // раскладка, и интерфейс на десятки-сотни миллисекунд замирает ровно в тот момент, когда должна идти анимация панели
    // навигации (это заметно на переходе с первой страницы на дальнюю). Поэтому после открытия окна, в простое, каждая скрытая
    // страница по одной раскладывается заранее: на время раскладки она Hidden (а не Visible), то есть измеряется, но не
    // рисуется и не вызывает событий видимости (IsVisible у Hidden и Collapsed одинаков), и тут же возвращается в Collapsed.
    private Queue<FrameworkElement>? _pagesToWarm;

    private void StartPageWarmUp()
    {
        _pagesToWarm = new Queue<FrameworkElement>(new FrameworkElement[]
        {
            PageWindow, PagePlayback, PageProgress, PageLibrary, PageCover, PageLyrics, PageIntegrations, PageNotifications,
            PageEqualizer, PageMiniPlayer, PageIsland, PageHotkeys, PageProfile, PageUpdates, PageAbout
        });
        // Раскладка страниц блокирует UI-поток на десятки миллисекунд каждая; во время появления окна и первых движений мыши
        // это давало рывки, поэтому прогрев начинается, когда окно уже показано и успокоилось.
        var startTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        startTimer.Tick += (_, _) =>
        {
            startTimer.Stop();
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(WarmUpNextPage));
        };
        startTimer.Start();
    }

    private void WarmUpNextPage()
    {
        if (_isWindowClosed || _pagesToWarm is null || _pagesToWarm.Count == 0)
        {
            _pagesToWarm = null;
            return;
        }

        FrameworkElement page = _pagesToWarm.Dequeue();
        try
        {
            if (LazyPageKeyOf(page) is { } lazyKey) EnsurePageBuilt(lazyKey);

            // Страницу, которую пользователь уже открывал, трогать не нужно.
            if (IsLoaded && page.Visibility == Visibility.Collapsed)
            {
                page.Visibility = Visibility.Hidden;
                page.UpdateLayout();
                page.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Прогрев страницы настроек не удался: {ex.Message}");
            page.Visibility = Visibility.Collapsed;
            _pagesToWarm = null;
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(WarmUpNextPage));
    }

    // ----- Въезд страницы на пружине (таблетки и плавающая панель) -----
    // Та же пружина со скоростью, что и у панели навигации (SpringValue): страница въезжает со стороны выбора с лёгким «пружинным»
    // доводом, а на время движения рисуется один раз в текстуру с масштабом экрана. Прозрачность и сдвиг считаются из одного
    // значения, поэтому не расходятся. (Раньше здесь была BeginTime = null — такая анимация не стартует вовсе, и страница
    // при первом открытии окна оставалась невидимой.)
    private const double PageSlideDistance = 36;

    private sealed class PageSpringState
    {
        public required FrameworkElement Page { get; init; }
        public required System.Windows.Media.TranslateTransform Translate { get; init; }
        public required int Direction { get; init; }
        public SpringValue Spring { get; } = new() { Stiffness = 300, DampingRatio = 0.82, Target = 1 };
    }

    private PageSpringState? _pageSpring;
    private TimeSpan _pageSpringLastFrame = TimeSpan.MinValue;

    private void StartPageSpring(FrameworkElement page, int direction)
    {
        FinishPageSpring();

        // Прежние анимации страницы (боковая панель) могли оставить удерживаемое значение прозрачности — снимаем его.
        page.BeginAnimation(UIElement.OpacityProperty, null);
        page.Opacity = 0;
        var translate = new System.Windows.Media.TranslateTransform(PageSlideDistance * direction, 0);
        page.RenderTransform = translate;
        page.RenderTransformOrigin = new Point(0.5, 0);

        // Кэш страницы в текстуру (BitmapCache) убран: по журналу каждый переход начинался с провала в ~30 мс (кадр №2), то есть
        // растеризация всей страницы в текстуру тормозила как раз первые кадры панели. Без кэша страница лишь сдвигается и
        // проявляется, как в боковой панели, где такого провала нет.
        _pageSpring = new PageSpringState { Page = page, Translate = translate, Direction = direction };
        _pageSpringLastFrame = TimeSpan.MinValue;
        CompositionTarget.Rendering += PageSpring_Frame;
    }

    private void PageSpring_Frame(object? sender, EventArgs e)
    {
        if (_pageSpring is not { } state || e is not RenderingEventArgs args || args.RenderingTime == _pageSpringLastFrame) return;

        double delta = _pageSpringLastFrame == TimeSpan.MinValue ? 1.0 / 60 : (args.RenderingTime - _pageSpringLastFrame).TotalSeconds;
        _pageSpringLastFrame = args.RenderingTime;

        bool moving = state.Spring.Step(delta);
        double progress = state.Spring.Value;
        state.Page.Opacity = Math.Clamp(progress * 1.3, 0, 1);
        state.Translate.X = (1 - progress) * PageSlideDistance * state.Direction;
        if (!moving) FinishPageSpring();
    }

    private void FinishPageSpring()
    {
        if (_pageSpring is not { } state) return;

        CompositionTarget.Rendering -= PageSpring_Frame;
        _pageSpring = null;
        state.Page.Opacity = 1;
        state.Page.ClearValue(UIElement.RenderTransformProperty);
        state.Page.CacheMode = null;
    }
}
