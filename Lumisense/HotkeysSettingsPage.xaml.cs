using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace Lumisense;

// Создаётся при первом показе страницы: разметка в SettingsWindow не разбирается заранее. Элементы переносятся в хост окна,
// а этот объект остаётся владельцем их имён и обработчиков.
public sealed partial class HotkeysSettingsPage : System.Windows.Controls.UserControl, ILazySettingsPage
{
    private enum HotkeyTarget { None, PlayPause, Next, Previous, Stop, VolumeUp, VolumeDown, Mute, Shuffle, Repeat, ToggleFavorite, ToggleLyrics, ToggleMiniPlayer, DeleteTrack, SeekForward, SeekBackward }

    private readonly Window _window;
    private readonly AppSettings _settings;
    private readonly ISettingsHost _owner;

    // Пока не None — страница "слушает" следующее нажатие клавиш и запишет его как новую комбинацию
    private HotkeyTarget _recordingTarget = HotkeyTarget.None;

    internal HotkeysSettingsPage(Window window, AppSettings settings, ISettingsHost owner)
    {
        _window = window;
        _settings = settings;
        _owner = owner;

        // StaticResource разбирается до того, как элементы попадут в дерево окна, поэтому ресурсы окна подключаются заранее.
        Resources.MergedDictionaries.Add(window.Resources);
        InitializeComponent();
        foreach (HotkeyTarget target in Enum.GetValues<HotkeyTarget>())
        {
            if (target != HotkeyTarget.None) RefreshHotkeyButtonText(target);
        }
    }

    public void MoveContentTo(System.Windows.Controls.Panel host) => LazySettingsPage.MoveChildren(PageHotkeys, host);

    public FrameworkElement? FindElement(string name) => FindName(name) as FrameworkElement;

    private void HotkeyPlayPauseButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.PlayPause);
    private void HotkeyNextButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.Next);
    private void HotkeyPreviousButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.Previous);
    private void HotkeyStopButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.Stop);
    private void HotkeyVolumeUpButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.VolumeUp);
    private void HotkeyVolumeDownButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.VolumeDown);
    private void HotkeyMuteButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.Mute);
    private void HotkeyShuffleButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.Shuffle);
    private void HotkeyRepeatButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.Repeat);
    private void HotkeyToggleFavoriteButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.ToggleFavorite);
    private void HotkeyToggleLyricsButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.ToggleLyrics);
    private void HotkeyToggleMiniPlayerButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.ToggleMiniPlayer);
    private void HotkeyDeleteTrackButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.DeleteTrack);
    private void HotkeySeekForwardButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.SeekForward);
    private void HotkeySeekBackwardButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.SeekBackward);

    private void HotkeyPlayPauseClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.PlayPause);
    private void HotkeyNextClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.Next);
    private void HotkeyPreviousClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.Previous);
    private void HotkeyStopClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.Stop);
    private void HotkeyVolumeUpClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.VolumeUp);
    private void HotkeyVolumeDownClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.VolumeDown);
    private void HotkeyMuteClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.Mute);
    private void HotkeyShuffleClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.Shuffle);
    private void HotkeyRepeatClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.Repeat);
    private void HotkeyToggleFavoriteClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.ToggleFavorite);
    private void HotkeyToggleLyricsClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.ToggleLyrics);
    private void HotkeyToggleMiniPlayerClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.ToggleMiniPlayer);
    private void HotkeyDeleteTrackClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.DeleteTrack);
    private void HotkeySeekForwardClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.SeekForward);
    private void HotkeySeekBackwardClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.SeekBackward);

    private void BeginRecording(HotkeyTarget target)
    {
        // Если уже что-то записывали, но не закончили — просто отменяем ту запись
        CancelRecording();

        _recordingTarget = target;
        GetHotkeyButton(target).Content = LocalizationService.Translate("Нажмите комбинацию…");
    }

    private void CancelRecording()
    {
        if (_recordingTarget == HotkeyTarget.None) return;

        var target = _recordingTarget;
        _recordingTarget = HotkeyTarget.None;
        RefreshHotkeyButtonText(target);
    }

    public void HandlePreviewKeyDown(KeyEventArgs e)
    {
        if (_recordingTarget == HotkeyTarget.None) return;

        e.Handled = true;

        // Alt-комбинации в WPF приходят как Key.System, реальная клавиша — в SystemKey
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            CancelRecording();
            return;
        }

        // Одни только модификаторы не считаем нажатием — ждём клавишу вместе с ними
        if (IsModifierKey(key)) return;

        // Не используем Keyboard.Modifiers: в PreviewKeyDown он ненадёжно определяет правые Ctrl/Alt/Shift
        // (на некоторых клавиатурах/раскладках видит только левые), поэтому опрашиваем Keyboard.IsKeyDown для обеих сторон.
        var isCtrl = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
        var isAlt = Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt);
        var isShift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
        var isWinDown = Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin);

        if (!isCtrl && !isAlt && !isShift && !isWinDown)
        {
            // Глобальная комбинация без модификатора будет перехватывать обычный ввод
            // во всех остальных окнах и приложениях — не даём её записать
            GetHotkeyButton(_recordingTarget).Content = LocalizationService.Translate("Нужен Ctrl/Alt/Shift/Win…");
            return;
        }

        var binding = new HotkeyBinding
        {
            Ctrl = isCtrl,
            Alt = isAlt,
            Shift = isShift,
            Win = isWinDown,
            Key = key.ToString()
        };

        // Две функции на одной комбинации: сработала бы только первая, вторая молча нет. Остаёмся в режиме записи,
        // как и для комбинации без модификатора, чтобы пользователь сразу нажал другую.
        if (FindConflictingTarget(_recordingTarget, binding) != HotkeyTarget.None)
        {
            GetHotkeyButton(_recordingTarget).Content = LocalizationService.Translate("Уже занято другим действием…");
            return;
        }

        var target = _recordingTarget;
        _recordingTarget = HotkeyTarget.None;

        SetHotkeyBinding(target, binding);
        RefreshHotkeyButtonText(target);

        // Windows не регистрирует комбинацию, которую уже занял другой процесс; без сообщения кнопка показывала бы «назначено».
        var failed = _owner.ReapplyHotkeys();
        if (failed.Any(candidate => candidate.SameCombinationAs(binding)))
        {
            LocalizedMessageBox.Show(_window,
                string.Format(LocalizationService.Translate("Комбинация {0} не зарегистрирована: её уже заняло другое приложение или система. Действие не сработает, пока вы не выберете другую комбинацию."), FormatBinding(binding)),
                LocalizationService.Translate("Горячая клавиша недоступна"),
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private HotkeyTarget FindConflictingTarget(HotkeyTarget target, HotkeyBinding binding)
    {
        foreach (HotkeyTarget other in Enum.GetValues<HotkeyTarget>())
        {
            if (other == HotkeyTarget.None || other == target) continue;
            if (GetHotkeyBinding(other).SameCombinationAs(binding)) return other;
        }

        return HotkeyTarget.None;
    }

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;

    private void ClearHotkey(HotkeyTarget target)
    {
        CancelRecording();

        SetHotkeyBinding(target, new HotkeyBinding());
        RefreshHotkeyButtonText(target);
        _owner.ReapplyHotkeys();
    }

    private void SetHotkeyBinding(HotkeyTarget target, HotkeyBinding binding)
    {
        switch (target)
        {
            case HotkeyTarget.PlayPause: _settings.HotkeyPlayPause = binding; break;
            case HotkeyTarget.Next: _settings.HotkeyNext = binding; break;
            case HotkeyTarget.Previous: _settings.HotkeyPrevious = binding; break;
            case HotkeyTarget.Stop: _settings.HotkeyStop = binding; break;
            case HotkeyTarget.VolumeUp: _settings.HotkeyVolumeUp = binding; break;
            case HotkeyTarget.VolumeDown: _settings.HotkeyVolumeDown = binding; break;
            case HotkeyTarget.Mute: _settings.HotkeyMute = binding; break;
            case HotkeyTarget.Shuffle: _settings.HotkeyShuffle = binding; break;
            case HotkeyTarget.Repeat: _settings.HotkeyRepeat = binding; break;
            case HotkeyTarget.ToggleFavorite: _settings.HotkeyToggleFavorite = binding; break;
            case HotkeyTarget.ToggleLyrics: _settings.HotkeyToggleLyrics = binding; break;
            case HotkeyTarget.ToggleMiniPlayer: _settings.HotkeyToggleMiniPlayer = binding; break;
            case HotkeyTarget.DeleteTrack: _settings.HotkeyDeleteTrack = binding; break;
            case HotkeyTarget.SeekForward: _settings.HotkeySeekForward = binding; break;
            case HotkeyTarget.SeekBackward: _settings.HotkeySeekBackward = binding; break;
        }
    }

    private HotkeyBinding GetHotkeyBinding(HotkeyTarget target) => target switch
    {
        HotkeyTarget.PlayPause => _settings.HotkeyPlayPause,
        HotkeyTarget.Next => _settings.HotkeyNext,
        HotkeyTarget.Previous => _settings.HotkeyPrevious,
        HotkeyTarget.Stop => _settings.HotkeyStop,
        HotkeyTarget.VolumeUp => _settings.HotkeyVolumeUp,
        HotkeyTarget.VolumeDown => _settings.HotkeyVolumeDown,
        HotkeyTarget.Mute => _settings.HotkeyMute,
        HotkeyTarget.Shuffle => _settings.HotkeyShuffle,
        HotkeyTarget.Repeat => _settings.HotkeyRepeat,
        HotkeyTarget.ToggleFavorite => _settings.HotkeyToggleFavorite,
        HotkeyTarget.ToggleLyrics => _settings.HotkeyToggleLyrics,
        HotkeyTarget.ToggleMiniPlayer => _settings.HotkeyToggleMiniPlayer,
        HotkeyTarget.DeleteTrack => _settings.HotkeyDeleteTrack,
        HotkeyTarget.SeekForward => _settings.HotkeySeekForward,
        HotkeyTarget.SeekBackward => _settings.HotkeySeekBackward,
        _ => new HotkeyBinding()
    };

    private Button GetHotkeyButton(HotkeyTarget target) => target switch
    {
        HotkeyTarget.PlayPause => HotkeyPlayPauseButton,
        HotkeyTarget.Next => HotkeyNextButton,
        HotkeyTarget.Previous => HotkeyPreviousButton,
        HotkeyTarget.Stop => HotkeyStopButton,
        HotkeyTarget.VolumeUp => HotkeyVolumeUpButton,
        HotkeyTarget.VolumeDown => HotkeyVolumeDownButton,
        HotkeyTarget.Mute => HotkeyMuteButton,
        HotkeyTarget.Shuffle => HotkeyShuffleButton,
        HotkeyTarget.Repeat => HotkeyRepeatButton,
        HotkeyTarget.ToggleFavorite => HotkeyToggleFavoriteButton,
        HotkeyTarget.ToggleLyrics => HotkeyToggleLyricsButton,
        HotkeyTarget.ToggleMiniPlayer => HotkeyToggleMiniPlayerButton,
        HotkeyTarget.DeleteTrack => HotkeyDeleteTrackButton,
        HotkeyTarget.SeekForward => HotkeySeekForwardButton,
        HotkeyTarget.SeekBackward => HotkeySeekBackwardButton,
        _ => throw new ArgumentOutOfRangeException(nameof(target))
    };

    private void RefreshHotkeyButtonText(HotkeyTarget target)
    {
        GetHotkeyButton(target).Content = FormatBinding(GetHotkeyBinding(target));
    }

    private static string FormatBinding(HotkeyBinding binding)
    {
        if (binding.IsEmpty) return "Не задано";

        var parts = new List<string>();
        if (binding.Ctrl) parts.Add("Ctrl");
        if (binding.Alt) parts.Add("Alt");
        if (binding.Shift) parts.Add("Shift");
        if (binding.Win) parts.Add("Win");
        parts.Add(DisplayKeyName(binding.Key));

        return string.Join(" + ", parts);
    }

    // Немного облагораживаем отображение некоторых клавиш, чьи имена в System.Windows.Input.Key
    // не совсем очевидны пользователю (например, Key.Next — это на самом деле PageDown)
    private static string DisplayKeyName(string keyName) => keyName switch
    {
        "Left" => "←",
        "Right" => "→",
        "Up" => "↑",
        "Down" => "↓",
        "Next" => "PageDown",
        "Prior" => "PageUp",
        "OemPlus" => "+",
        "OemMinus" => "-",
        "OemComma" => ",",
        "OemPeriod" => ".",
        "Escape" => "Esc",
        _ => keyName
    };

    // Глобальные хоткеи по умолчанию не заданы, поэтому сброс всех — это очистка всех привязок (как кнопка «Очистить» у каждой).
    private void HotkeysResetAllButton_Click(object sender, RoutedEventArgs e)
    {
        CancelRecording();

        foreach (HotkeyTarget target in System.Enum.GetValues<HotkeyTarget>())
        {
            if (target == HotkeyTarget.None) continue;

            SetHotkeyBinding(target, new HotkeyBinding());
            RefreshHotkeyButtonText(target);
        }

        _owner.ReapplyHotkeys();
    }
}
