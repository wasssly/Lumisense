using System.Windows;
using System.Windows.Threading;
using static Lumisense.BackgroundTask;

namespace Lumisense;

// Мини-плеер и авто-определение оверлеев игр: вынесено из MainWindow.xaml.cs
// только ради навигации, логика не менялась.
public partial class MainWindow
{
    private void MiniModeButton_Click(object sender, RoutedEventArgs e) => SetPlayerViewMode(PlayerViewMode.Mini);

    // Переключает в мини-плеер. Вызывается из SetPlayerViewMode — как по кнопке/пункту
    // меню, так и при восстановлении сохранённого состояния на старте.
    void IIntegrationHost.ToggleMiniPlayerHotkey() => ToggleMiniPlayerHotkey();

    private void ToggleMiniPlayerHotkey()
    {
        SetPlayerViewMode(_isMiniMode ? _preMiniViewMode : PlayerViewMode.Mini);
    }

    private void EnterMiniMode()
    {
        // После фактического возврата в мини-плеер отложенный маркер больше не нужен.
        _returnToMiniOnNextTaskbarMinimize = false;

        // _viewMode ещё хранит вид ДО мини-режима (SetPlayerViewMode присваивает новый после вызова): запоминаем его,
        // чтобы "развернуть" в ExitMiniMode вернулся туда, откуда ушли.
        _preMiniViewMode = _viewMode;

        // У мини-плеера ShowInTaskbar="False" (MiniPlayerWindow.xaml): иконку в трее показываем здесь, а не только в OnClosing, иначе
        // до него не добраться; делаем до показа окна, чтобы значок не отставал от видимого мини-плеера (ForceForeground небыстр).
        Logger.Info($"EnterMiniMode: вызываю _integrations.Tray.Show() (стартовый вызов={_isApplyingStartupSettings}).");
        _integrations.Tray?.Show("Lumisense");

        _miniPlayerWindow = new MiniPlayerWindow(this)
        {
            Topmost = _settings.MiniPlayerAlwaysOnTop
        };
        _miniPlayerWindow.ApplyOverlayCompatibilityLive(EffectiveGameOverlayCompatibilityEnabled);

        // Возвращаем мини-плеер на прежнее место; если позиция не задавалась — в правый нижний угол рабочей области.
        if (_settings.MiniPlayerLeft.HasValue && _settings.MiniPlayerTop.HasValue)
        {
            _miniPlayerWindow.Left = _settings.MiniPlayerLeft.Value;
            _miniPlayerWindow.Top = _settings.MiniPlayerTop.Value;
        }
        else
        {
            var workArea = SystemParameters.WorkArea;
            _miniPlayerWindow.Left = workArea.Right - _miniPlayerWindow.Width - 24;
            _miniPlayerWindow.Top = workArea.Bottom - _miniPlayerWindow.Height - 24;
        }

        _miniPlayerWindow.Closed += (_, _) => _miniPlayerWindow = null;
        _miniPlayerWindow.Show();
        ForceForeground(_miniPlayerWindow);

        _isMiniMode = true;
        Hide();
    }

    // Вызывается из MiniPlayerWindow по кнопке "развернуть"; внешняя активация ярлыка передаёт true, чтобы следующий клик
    // по кнопке панели задач снова вернул мини-плеер (обычное разворачивание маркер не ставит).
    public void ExitMiniMode(bool returnToMiniOnNextTaskbarMinimize = false)
    {
        _isMiniMode = false;
        _returnToMiniOnNextTaskbarMinimize = returnToMiniOnNextTaskbarMinimize;

        _miniPlayerWindow?.Close();
        _miniPlayerWindow = null;

        Show();
        WindowState = WindowState.Normal;
        CenterOnFirstShowIfNeeded();
        SyncPlaylistToCurrentTrackAfterShow();
        ForceForeground(this);
        _integrations.Tray?.Hide();

        // Ширина/высота не менялись в мини-режиме и уже соответствуют прежнему виду: возвращаем лишь флаг вида (галочка меню и
        // настроек) без повторного SetPlayerViewMode и пересчёта размеров.
        _viewMode = _preMiniViewMode;
        _settings.PlayerViewMode = _viewMode.ToString();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
        UpdateViewModeMenuChecks();
    }

    // Вызывается при перемещении мини-плеера пользователем: запоминаем положение, чтобы следующее сворачивание (и после
    // перезапуска) появилось на том же месте.
    public void SaveMiniPlayerPosition(double left, double top)
    {
        _settings.MiniPlayerLeft = left;
        _settings.MiniPlayerTop = top;
    }

    // Позволяет окну настроек мгновенно применить изменения прозрачности/поверх окон,
    // если мини-плеер сейчас открыт
    public void ApplyMiniPlayerOpacityLive(double opacity)
    {
        // MiniPlayerOpacity уже обновлён вызывающей стороной (MiniOpacitySlider_ValueChanged): ApplyOpacityLive лишь пересчитывает альфу фона.
        if (_miniPlayerWindow != null) _miniPlayerWindow.ApplyOpacityLive();
    }

    public void ApplyMiniPlayerTopmostLive(bool topmost)
    {
        if (_miniPlayerWindow != null) _miniPlayerWindow.Topmost = topmost;
    }

    // Ручной режим совместимости с играми/Steam Overlay: снижает количество эффектов и
    // анимаций сразу в обоих плавающих окнах, не меняя сохранённые обычные настройки.
    public void ApplyMiniPlayerOverlayCompatibilityLive(bool enabled)
    {
        _miniPlayerWindow?.ApplyOverlayCompatibilityLive(enabled);
        _trackChangeToastController.ApplyOverlayCompatibilityLive(enabled);
    }

    // Включено вручную либо автоопределением игры/оверлея: они независимы — выключение одного не мешает другому.
    public bool EffectiveGameOverlayCompatibilityEnabled =>
        _settings.GameOverlayCompatibilityMode ||
        (_settings.GameOverlayCompatibilityAutoDetect && _autoDetectedGameOverlayActive);

    // Работает всё время работы приложения; тик почти ничего не делает, если автоопределение
    // выключено — дешевле, чем гонять Start/Stop при каждом открытии/закрытии мини-плеера.
    private void StartGameOverlayDetectionTimer()
    {
        _gameOverlayDetectionTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _gameOverlayDetectionTimer.Tick += GameOverlayDetectionTimer_Tick;
        _gameOverlayDetectionTimer.Start();
    }

    // DispatcherTimer держится диспетчером, а не полем: без Stop тик мог сработать уже после закрытия окна.
    private void StopGameOverlayDetectionTimer()
    {
        if (_gameOverlayDetectionTimer is null) return;

        _gameOverlayDetectionTimer.Stop();
        _gameOverlayDetectionTimer.Tick -= GameOverlayDetectionTimer_Tick;
        _gameOverlayDetectionTimer = null;
    }

    private void GameOverlayDetectionTimer_Tick(object? sender, EventArgs e)
    {
        if (!_settings.GameOverlayCompatibilityAutoDetect)
        {
            // Настройку выключили, пока эвристика держала состояние включённым — сбрасываем
            // флаг, иначе следующее включение "вспомнило" бы устаревшее обнаружение.
            if (_autoDetectedGameOverlayActive)
            {
                _autoDetectedGameOverlayActive = false;
                ApplyMiniPlayerOverlayCompatibilityLive(EffectiveGameOverlayCompatibilityEnabled);
            }
            return;
        }

        bool detected = GameOverlayDetectionService.IsGameOrOverlayLikelyActive();
        if (detected == _autoDetectedGameOverlayActive) return;

        _autoDetectedGameOverlayActive = detected;
        ApplyMiniPlayerOverlayCompatibilityLive(EffectiveGameOverlayCompatibilityEnabled);
    }

    // Вызывается из SettingsWindow сразу при переключении галочки автоопределения — без этого
    // пользователь увидел бы эффект только на следующем тике таймера (до 5 секунд).
    public void ApplyGameOverlayAutoDetectSettingLive()
    {
        if (!_settings.GameOverlayCompatibilityAutoDetect)
            _autoDetectedGameOverlayActive = false;
        ApplyMiniPlayerOverlayCompatibilityLive(EffectiveGameOverlayCompatibilityEnabled);
    }

    // Мгновенно применяет смену темы к открытому мини-плееру, иначе он узнал бы о ней только при пересоздании окна.
    public void ApplyMiniPlayerThemeLive()
    {
        if (_miniPlayerWindow != null) _miniPlayerWindow.ApplyThemeLive();
    }

    // Мгновенно переключает функцию второй кнопки мини-плеера (AppSettings.MiniPlayerSecondaryButton) на открытом окне.
    public void ApplyMiniPlayerSecondaryButtonLive()
    {
        _miniPlayerWindow?.UpdateSecondaryButton();
    }

    // Единая точка изменения режима второй кнопки для страницы настроек и контекстного меню
    // мини-плеера. Открытое SettingsWindow синхронизируется без повторного вызова обработчика.
    public void SetMiniPlayerSecondaryButtonMode(string? mode)
    {
        _settings.MiniPlayerSecondaryButton = mode switch
        {
            "Shuffle" => "Shuffle",
            "Favorite" => "Favorite",
            _ => "Repeat"
        };
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");

        ApplyMiniPlayerSecondaryButtonLive();
        _settingsWindow?.RefreshMiniPlayerToggles();
    }

    // Аналог ApplyMiniPlayerSecondaryButtonLive для настройки "расположение кнопок" (снизу /
    // на месте обложки, см. AppSettings.MiniPlayerButtonsLayout).
    public void ApplyMiniPlayerButtonsLayoutLive()
    {
        _miniPlayerWindow?.ApplyButtonsLayoutMode();
    }

    public void ApplyMiniPlayerSizePresetLive()
    {
        _miniPlayerWindow?.ApplySizePreset();
    }

    public void ApplyNowPlayingBackgroundLive()
    {
        _nowPlayingWindow?.ApplyBackgroundMode();
    }

    // Аналог ApplyMiniPlayerSecondaryButtonLive для настройки "показывать полосу прогресса"
    // (см. AppSettings.MiniPlayerShowProgress).
    public void ApplyMiniPlayerProgressBarVisibilityLive()
    {
        _miniPlayerWindow?.ApplyProgressBarVisibility();
    }

    // Аналог ApplyMiniPlayerProgressBarVisibilityLive для настройки "прогресс вокруг
    // обложки" (см. AppSettings.MiniPlayerShowArtworkProgress).
    public void ApplyMiniPlayerArtworkProgressVisibilityLive()
    {
        _miniPlayerWindow?.ApplyArtworkProgressVisibility();
    }

    // Применяет толщину трека и линии progress вокруг обложки немедленно, без закрытия мини-плеера.
    public void ApplyMiniPlayerArtworkProgressThicknessLive()
    {
        _miniPlayerWindow?.ApplyArtworkProgressThickness();
    }

    // Применяет выбранный вид обложки (обычный / винил) немедленно, без закрытия мини-плеера.
    public void ApplyMiniPlayerArtworkStyleLive()
    {
        _miniPlayerWindow?.ApplyArtworkStyle();
    }

    // Применяет выбранный источник цвета (акцент оформления или фиксированный цвет) к уже
    // открытому мини-плееру без необходимости переоткрывать его.
    public void ApplyMiniPlayerArtworkProgressColorLive()
    {
        _miniPlayerWindow?.ApplyArtworkProgressColor();
    }

    // Аналог ApplyMiniPlayerSecondaryButtonLive для настройки "что показывать во второй
    // строке" (исполнитель / ничего / оставшееся время, см. AppSettings.MiniPlayerInfoMode).
    public void ApplyMiniPlayerInfoModeLive()
    {
        _miniPlayerWindow?.ApplyInfoModeLive();
    }

    // "Закрепить"/"Поверх окон" из контекстного меню мини-плеера: те же настройки, что чекбоксы в окне настроек —
    // если оно открыто, подтягиваем в нём состояние, чтобы два места управления не расходились.
    public void SetMiniPlayerPinned(bool pinned)
    {
        _settings.MiniPlayerPinned = pinned;
        _settingsWindow?.RefreshMiniPlayerToggles();
    }

    public void SetMiniPlayerTopmost(bool topmost)
    {
        _settings.MiniPlayerAlwaysOnTop = topmost;
        ApplyMiniPlayerTopmostLive(topmost);
        _settingsWindow?.RefreshMiniPlayerToggles();
    }

    // Прозрачность из контекстного меню мини-плеера (как SetMiniPlayerPinned/SetMiniPlayerTopmost): сохраняет, применяет вживую
    // и подтягивает значение в открытое окно настроек, чтобы два места редактирования не расходились.
    public void SetMiniPlayerOpacity(double opacity)
    {
        _settings.MiniPlayerOpacity = opacity;
        _miniPlayerWindow?.ApplyOpacityLive();
        _settingsWindow?.RefreshMiniPlayerToggles();
    }
}
