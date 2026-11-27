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

public partial class SettingsWindow : FluentWindow
{
    private readonly AppSettings _settings;
    private readonly ISettingsHost _owner;
    private bool _isInitializing = true;
    private bool _isWindowClosed;

    // Индекс поиска — ручной перечень опций: подпись, страница, ссылка на элемент (для прокрутки и подсветки)
    // и ключевые слова; разметка не читается.
    private sealed record SettingsSearchEntry(string Label, string PageTitle, string PageKey, string Keywords, FrameworkElement? Target, string? TargetName = null)
    {
        // Результаты строятся при вводе запроса, уже после прохода локализации, поэтому подписи переводятся здесь.
        public string DisplayLabel => LocalizationService.Translate(Label);
        public string DisplayPageTitle => LocalizationService.Translate(PageTitle);
    }

    private readonly List<SettingsSearchEntry> _searchIndex = new();
    private readonly ObservableCollection<SettingsSearchEntry> _searchResults = new();

    // Переключает страницу по ключу: при первом открытии и при повторном открытии уже висящего окна
    // (например, "Настройки" из меню мини-плеера ведут на "Мини-плеер", см. MainWindow.ShowSettingsWindow).
    public void NavigateToPage(string? pageKey)
    {
        (pageKey switch
        {
            "About" => NavAbout,
            "Updates" => NavUpdates,
            "Window" => NavWindow,
            "Playback" => NavPlayback,
            "Progress" => NavProgress,
            "Library" => NavLibrary,
            "Cover" => NavCover,
            "Lyrics" => NavLyrics,
            "Integrations" => NavIntegrations,
            "Notifications" => NavNotifications,
            "Equalizer" => NavEqualizer,
            "MiniPlayer" => NavMiniPlayer,
            "Island" => NavIsland,
            "Hotkeys" => NavHotkeys,
            "Profile" => NavProfile,
            _ => NavAppearance
        }).IsChecked = true;
    }

    // Своя копия MainWindow.ApplyWindowBackdrop: применяется к собственному HWND этого окна.
    private void ApplyWindowBackdrop(AppSettings settings, bool forceReapply = false)
    {
        var desiredBackdrop = WindowBackdropOptions.ToWpfUi(settings.WindowBackdropType);
        bool blur = WindowBackdropOptions.IsBlur(settings.WindowBackdropType);

        if (!blur && DwmBlurBackdrop.IsApplied(this))
        {
            DwmBlurBackdrop.Clear(this);
            forceReapply = true;
        }

        // См. MainWindow.ApplyWindowBackdrop: при смене темы WPF-UI может оставить Acrylic в свойстве, но наложить Mica;
        // None → desired заставляет FluentWindow заново применить нужный backdrop.
        if (forceReapply && WindowBackdropType == desiredBackdrop)
            WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;

        WindowBackdropType = desiredBackdrop;
        DwmBlurBackdrop.Sync(this, blur);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyWindowBackdrop(_settings);
    }

    // Замер открытия окна: в журнал одной строкой попадает, сколько длились разбор XAML, заполнение страниц и первая отрисовка.
    private readonly System.Diagnostics.Stopwatch _openClock = System.Diagnostics.Stopwatch.StartNew();
    private readonly List<string> _openMarks = new();
    private long _openLastMark;

    private void MarkOpen(string name)
    {
        long now = _openClock.ElapsedMilliseconds;
        _openMarks.Add($"{name}={now - _openLastMark}ms");
        _openLastMark = now;
    }

    internal SettingsWindow(AppSettings settings, ISettingsHost owner, string? initialPage = null)
    {
        InitializeComponent();
        MarkOpen("initialize-component");
        InitializeRadioCombos();
        InitializeNavScroll();
        ComboPopupFit.Register();
        WindowSizing.FitToWorkArea(this);
        // Class handlers получают событие даже если дочерний контрол пометил MouseDown
        // обработанным. Это важно для клика по тексту, Slider и другим элементам страницы.
        AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(SettingsWindow_PreviewMouseDown), true);
        AddHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(SettingsWindow_PreviewMouseMove), true);
        AddHandler(UIElement.LostMouseCaptureEvent, new MouseEventHandler(SettingsRoot_LostMouseCapture), true);

        ApplyWindowBackdrop(settings);

        // Стартовую страницу выбираем здесь, а не IsChecked="True" в XAML: все страницы уже созданы, и NavItem_Checked
        // не падает с NullReferenceException.
        _settings = settings;
        _owner = owner;
        NavigateToPage(initialPage);

        AccessibilityPreferences.ApplyToWindow(this, _settings);

        // Owner намеренно не выставляется: Windows не позволяет owner оказаться выше owned-окна, и клик по главному окну
        // не поднимал бы его над настройками. Позиция — RestoreOrCenterPosition, закрытие — MainWindow.OnClosed.
        ShowInTaskbar = true;
        TaskbarWindowIdentity.AssignWhenSourceReady(this, TaskbarWindowIdentity.Settings);
        RestoreOrCenterPosition(owner);


        // Источник истины для автозапуска — сам реестр (см. StartupManager), а не settings.json —
        // чекбокс всегда показывает то, что реально настроено, а не могло устареть.


        SearchResultsList.ItemsSource = _searchResults;
        BuildSearchIndex();


        ApplyNavigationStyle(_settings.SettingsNavigationStyle);
        SizeChanged += (_, _) => UpdateFloatingBarMaxWidth();
        Loaded += (_, _) => StartPageWarmUp();

        _isInitializing = false;
        MarkOpen("fill-controls-and-search");
        ContentRendered += (_, _) =>
        {
            MarkOpen("first-render");
            Logger.Info($"TRACE settings-open: {string.Join(", ", _openMarks)}; всего {_openClock.ElapsedMilliseconds}ms");
        };
        LocalizationService.LanguageChanged += LocalizationService_LanguageChanged;
        Loaded += async (_, _) =>
        {
            // Страница «Обновления» может быть открыта до Loaded (сохранённая вкладка); иначе план читается при первом показе.
            if (_lazyPages.TryGetValue("Updates", out ILazySettingsPage? updatesPage))
                await ((UpdatesSettingsPage)updatesPage).LoadBasePackagePlanAsync();
        };
        Closed += (_, _) =>
        {
            _isWindowClosed = true;
            StopAutoScrolling();
            if (_lazyPages.TryGetValue("Updates", out ILazySettingsPage? updatesPage))
                ((UpdatesSettingsPage)updatesPage).CancelBackgroundWork();
            if (_lazyPages.TryGetValue("Lyrics", out ILazySettingsPage? lyricsPage))
                ((LyricsSettingsPage)lyricsPage).MarkWindowClosed();
            if (_lazyPages.TryGetValue("Playback", out ILazySettingsPage? playbackPage))
                ((PlaybackSettingsPage)playbackPage).MarkWindowClosed();
            LocalizationService.LanguageChanged -= LocalizationService_LanguageChanged;
        };
        LocalizationService.Apply(this);
    }

    // CenterOwner не подходит (Owner не выставляется), центрируем вручную; двигавшееся окно открываем на прежнем месте
    // (SettingsWindowLeft/Top). Вместе с ShowInTaskbar это чинит окно, унесённое отключённым монитором за экран.
    private void RestoreOrCenterPosition(ISettingsHost owner)
    {
        if (_settings.SettingsWindowLeft is double savedLeft && _settings.SettingsWindowTop is double savedTop
            && IsPositionOnAnyScreen(savedLeft, savedTop))
        {
            SetPositionProgrammatically(savedLeft, savedTop);
            return;
        }

        double ownerWidth = owner.ActualWidth > 0 ? owner.ActualWidth : owner.Width;
        double ownerHeight = owner.ActualHeight > 0 ? owner.ActualHeight : owner.Height;

        double left = owner.Left + (ownerWidth - Width) / 2;
        double top = owner.Top + (ownerHeight - Height) / 2;

        var ownerBounds = new System.Drawing.Rectangle(
            (int)owner.Left, (int)owner.Top,
            (int)Math.Max(ownerWidth, 1), (int)Math.Max(ownerHeight, 1));
        var workArea = System.Windows.Forms.Screen.FromRectangle(ownerBounds).WorkingArea;

        left = Math.Clamp(left, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
        top = Math.Clamp(top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));

        SetPositionProgrammatically(left, top);
    }

    // Сохранённая позиция может не попадать ни на один подключённый монитор — проверяем пересечение с рабочей областью
    // любого из них, а не ближайший Screen.FromRectangle.
    private bool IsPositionOnAnyScreen(double left, double top)
    {
        var bounds = new System.Drawing.Rectangle((int)left, (int)top,
            (int)Math.Max(Width, 1), (int)Math.Max(Height, 1));
        return System.Windows.Forms.Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(bounds));
    }

    // Left/Top правятся кодом, а не пользователем: в настройки (OnLocationChanged) пишем только реальное перетаскивание.
    private bool _isApplyingProgrammaticPosition;

    private void SetPositionProgrammatically(double left, double top)
    {
        _isApplyingProgrammaticPosition = true;
        try
        {
            Left = left;
            Top = top;
        }
        finally
        {
            _isApplyingProgrammaticPosition = false;
        }
    }

    // Позиция запоминается при перемещении пользователем (как MiniPlayerLeft/Top) только в памяти; на диск уйдёт со
    // следующим SettingsManager.Save (гарантированно при закрытии, MainWindow.PersistPlaybackAndPlaylistState).
    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (_isApplyingProgrammaticPosition) return;
        if (_isInitializing) return;

        _settings.SettingsWindowLeft = Left;
        _settings.SettingsWindowTop = Top;
    }

    // Страница «Воспроизведение» создаётся при первом показе; до этого читает состояние при создании.
    public void RefreshOutputDeviceSelection()
    {
        if (_lazyPages.TryGetValue("Playback", out ILazySettingsPage? page)) ((PlaybackSettingsPage)page).RefreshOutputDeviceSelection();
    }

    public void RefreshOutputDeviceRuntimeStatus()
    {
        if (_lazyPages.TryGetValue("Playback", out ILazySettingsPage? page)) ((PlaybackSettingsPage)page).RefreshOutputDeviceRuntimeStatus();
    }

    public void RefreshWasapiModeSelection()
    {
        if (_lazyPages.TryGetValue("Playback", out ILazySettingsPage? page)) ((PlaybackSettingsPage)page).RefreshWasapiModeSelection();
    }

    // Страница «Окно» отмечает миниатюру сама; пока она не создана, вид читается при её создании.
    public void RefreshViewModeRadios()
    {
        if (_lazyPages.TryGetValue("Window", out ILazySettingsPage? windowPage)) ((WindowSettingsPage)windowPage).RefreshViewModeRadios();
    }
}
