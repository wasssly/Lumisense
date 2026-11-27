using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using static Lumisense.BackgroundTask;

namespace Lumisense;

// Создаётся при первом показе страницы (см. SettingsWindow.EnsurePageBuilt).
public sealed partial class WindowSettingsPage : System.Windows.Controls.UserControl, ILazySettingsPage
{
    private readonly AppSettings _settings;
    private readonly ISettingsHost _owner;

    // Глушит Changed-обработчики, пока значения читаются из настроек.
    private bool _isInitializing = true;

    internal WindowSettingsPage(Window window, AppSettings settings, ISettingsHost owner)
    {
        _settings = settings;
        _owner = owner;

        // StaticResource разбирается до того, как элементы попадут в дерево окна, поэтому ресурсы окна подключаются заранее.
        Resources.MergedDictionaries.Add(window.Resources);
        InitializeComponent();

        AlwaysOnTopCheckBox.IsChecked = _settings.AlwaysOnTop;
        MinimizeToTrayCheckBox.IsChecked = _settings.MinimizeToTrayOnClose;
        LaunchOnStartupCheckBox.IsChecked = StartupManager.IsEnabled();
        StartHiddenInTrayCheckBox.IsChecked = _settings.StartHiddenInTray;
        RefreshViewModeRadios();
        MainWindowLayoutModernRadio.IsChecked = _settings.MainWindowLayout == "Modern";
        MainWindowLayoutGlassRadio.IsChecked = _settings.MainWindowLayout == "Glass";
        MainWindowLayoutVinylRadio.IsChecked = _settings.MainWindowLayout == "Vinyl";
        MainWindowLayoutCompactRadio.IsChecked = _settings.MainWindowLayout == "Compact";
        MainWindowLayoutPosterRadio.IsChecked = _settings.MainWindowLayout == "Poster";
        ModernShowSideButtonsCheckBox.IsChecked = _settings.ModernShowSideButtons;
        GlassControlsBackgroundCheckBox.IsChecked = _settings.GlassControlsBackground;
        ControlButtonsBackgroundCheckBox.IsChecked = _settings.ControlButtonsBackground;
        PlayButtonAccentRadio.IsChecked = _settings.PlayButtonAccentBackground;
        PlayButtonPlainRadio.IsChecked = !_settings.PlayButtonAccentBackground;
        PlayButtonSquareRadio.IsChecked = !_settings.PlayButtonRound;
        PlayButtonRoundRadio.IsChecked = _settings.PlayButtonRound;
        MoreMenuSpringRadio.IsChecked = _settings.MoreMenuSpring;
        MoreMenuClassicRadio.IsChecked = !_settings.MoreMenuSpring;
        SettingsWindow.SelectComboByTag(CompactCoverAlignmentCombo, _settings.CompactCoverAlignment);
        ModernSlotsGrid.IsEnabled = _settings.ModernShowSideButtons;
        SelectModernSlotCombos(MainWindow.NormalizeModernSlots(_settings.ModernControlSlots));

        // Выпадающие списки радиогрупп читают отмеченные радиокнопки, поэтому связываются после загрузки настроек.
        RadioComboLinks.Initialize(this, FindName);
        _isInitializing = false;
    }

    public void MoveContentTo(System.Windows.Controls.Panel host) => LazySettingsPage.MoveChildren(PageWindow, host);

    public FrameworkElement? FindElement(string name) => FindName(name) as FrameworkElement;

    private System.Windows.Controls.ComboBox[] ModernSlotCombos() =>
        [ModernSlot0Combo, ModernSlot1Combo, ModernSlot2Combo, ModernSlot3Combo];

    private void SelectModernSlotCombos(IReadOnlyList<string> slots)
    {
        var combos = ModernSlotCombos();
        for (int i = 0; i < combos.Length; i++) SettingsWindow.SelectComboByTag(combos[i], slots[i]);
    }

    // Кнопки по бокам от «Назад / Пуск / Вперёд» в новом виде: включение и выбор четырёх мест.
    private void ModernLayoutOptions_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        // Список без выбранного пункта значит «ещё не заполнен» (окно в процессе загрузки), а не «Нет»: иначе слоты затираются пустыми.
        if (ModernSlotCombos().Any(combo => combo.SelectedItem is null)) return;

        _settings.ModernShowSideButtons = ModernShowSideButtonsCheckBox.IsChecked == true;
        ModernSlotsGrid.IsEnabled = _settings.ModernShowSideButtons;
        List<string> slots = MainWindow.NormalizeModernSlots(ModernSlotCombos().Select(combo => SettingsWindow.SelectedTag(combo, string.Empty)));
        _settings.ModernControlSlots = slots;

        // Повторно выбранная кнопка сбрасывается в «Нет»: показываем это в списках сразу.
        bool wasInitializing = _isInitializing;
        _isInitializing = true;
        try
        {
            SelectModernSlotCombos(slots);
        }
        finally
        {
            _isInitializing = wasInitializing;
        }

        _owner.ApplyMainWindowLayoutLive();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void GlassControlsBackground_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.GlassControlsBackground = GlassControlsBackgroundCheckBox.IsChecked == true;
        _owner.ApplyMainWindowLayoutLive();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void ControlButtonsBackground_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.ControlButtonsBackground = ControlButtonsBackgroundCheckBox.IsChecked == true;
        _owner.ApplyMainWindowLayoutLive();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void PlayButtonStyle_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.PlayButtonAccentBackground = PlayButtonAccentRadio.IsChecked == true;
        _owner.ApplyMainWindowLayoutLive();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void MoreMenuStyle_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.MoreMenuSpring = MoreMenuSpringRadio.IsChecked == true;
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void PlayButtonShape_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.PlayButtonRound = PlayButtonRoundRadio.IsChecked == true;
        _owner.ApplyMainWindowLayoutLive();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void CompactCoverAlignment_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.CompactCoverAlignment = SettingsWindow.SelectedTag(CompactCoverAlignmentCombo, "Left");
        _owner.ApplyMainWindowLayoutLive();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void MainWindowLayoutRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || sender is not System.Windows.Controls.RadioButton { Tag: string layout }) return;

        _settings.MainWindowLayout = layout;
        _owner.ApplyMainWindowLayoutLive();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void PlayerViewModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        if (sender is not System.Windows.Controls.RadioButton { Tag: string modeName }) return;
        _owner.SetPlayerViewModeByName(modeName);
    }

    private void AlwaysOnTopCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.AlwaysOnTop = AlwaysOnTopCheckBox.IsChecked == true;

        // Пока активен мини-плеер, поверх окон управляет отдельная мини-настройка —
        // обычную применяем только когда плеер в обычном виде
        if (!_owner.IsMiniMode)
            _owner.Topmost = _settings.AlwaysOnTop;
    }

    private void MinimizeToTrayCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.MinimizeToTrayOnClose = MinimizeToTrayCheckBox.IsChecked == true;
    }

    private void LaunchOnStartupCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        if (StartupManager.SetEnabled(LaunchOnStartupCheckBox.IsChecked == true)) return;

        // Реестр не изменился — возвращаем флажок к фактическому состоянию.
        bool wasInitializing7 = _isInitializing;
        _isInitializing = true;
        try
        {
            LaunchOnStartupCheckBox.IsChecked = StartupManager.IsEnabled();
        }
        finally
        {
            _isInitializing = wasInitializing7;
        }
    }

    private void StartHiddenInTrayCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.StartHiddenInTray = StartHiddenInTrayCheckBox.IsChecked == true;
    }

    // Отмечает миниатюру текущего вида плеера — при открытии окна и извне (MainWindow), когда вид сменили меню заголовка
    // или кнопкой мини-плеера, чтобы страница не отставала от состояния.
    public void RefreshViewModeRadios()
    {
        bool wasInitializing2 = _isInitializing;
        _isInitializing = true;
        try
        {
            switch (_owner.CurrentViewModeName)
            {
                case "Square": ViewModeSquareRadio.IsChecked = true; break;
                case "Rectangular": ViewModeRectangularRadio.IsChecked = true; break;
                case "Mini": ViewModeMiniRadio.IsChecked = true; break;
            }
        }
        finally
        {
            _isInitializing = wasInitializing2;
        }
    }
}
