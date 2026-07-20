using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace AudioPlayer;

/// <summary>
/// Регистрирует системные мультимедийные клавиши клавиатуры (Play/Pause, Next, Previous, Stop)
/// как глобальные хоткеи через WinAPI RegisterHotKey — плеер реагирует на них, даже когда
/// окно свёрнуто или не в фокусе (как и положено медиаклавишам). Плюс к этому поддерживает
/// настраиваемые пользователем комбинации (например, Ctrl+Alt+P) — они задаются в настройках
/// и применяются через <see cref="ApplyCustomHotkeys"/>, без необходимости перезапускать приложение.
/// </summary>
public sealed class GlobalMediaHotKeys : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    private const uint VK_MEDIA_NEXT_TRACK = 0xB0;
    private const uint VK_MEDIA_PREV_TRACK = 0xB1;
    private const uint VK_MEDIA_STOP = 0xB2;
    private const uint VK_MEDIA_PLAY_PAUSE = 0xB3;

    // Физические мультимедийные клавиши — фиксированные ID, всегда активны
    private const int IdNext = 0xA001;
    private const int IdPrev = 0xA002;
    private const int IdStop = 0xA003;
    private const int IdPlayPause = 0xA004;

    // Настраиваемые пользователем комбинации — отдельный диапазон ID
    private const int IdCustomPlayPause = 0xA011;
    private const int IdCustomNext = 0xA012;
    private const int IdCustomPrevious = 0xA013;
    private const int IdCustomStop = 0xA014;
    private const int IdCustomVolumeUp = 0xA015;
    private const int IdCustomVolumeDown = 0xA016;
    private const int IdCustomMute = 0xA017;
    private const int IdCustomShuffle = 0xA018;
    private const int IdCustomRepeat = 0xA019;
    private const int IdCustomDeleteTrack = 0xA01A;

    private readonly IntPtr _handle;
    private readonly HwndSource _source;

    private bool _customPlayPauseRegistered;
    private bool _customNextRegistered;
    private bool _customPreviousRegistered;
    private bool _customStopRegistered;
    private bool _customVolumeUpRegistered;
    private bool _customVolumeDownRegistered;
    private bool _customMuteRegistered;
    private bool _customShuffleRegistered;
    private bool _customRepeatRegistered;
    private bool _customDeleteTrackRegistered;

    public event Action? NextPressed;
    public event Action? PreviousPressed;
    public event Action? StopPressed;
    public event Action? PlayPausePressed;
    public event Action? VolumeUpPressed;
    public event Action? VolumeDownPressed;
    public event Action? MutePressed;
    public event Action? ShufflePressed;
    public event Action? RepeatPressed;
    public event Action? DeleteTrackPressed;

    public GlobalMediaHotKeys(Window window)
    {
        _handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_handle)
                  ?? throw new InvalidOperationException("Окно ещё не инициализировано (нет Hwnd).");

        _source.AddHook(WndProc);

        // Регистрация может не удаться, если клавишу уже перехватило другое приложение —
        // это не критично, остальные хоткеи всё равно продолжат работать
        // MOD_NOREPEAT здесь намеренно НЕ используется для Next/Prev: без него Windows сама
        // шлёт повторные WM_HOTKEY, пока клавиша зажата (с той же частотой, что и обычный
        // повтор клавиатуры из настроек Windows) — то есть переключение треков само повторяется
        // при удержании клавиши, без какого-либо отдельного таймера в самом приложении.
        // Для Stop/PlayPause повтор не нужен и был бы вреден (зажатие "пауза" не должно
        // судорожно дёргать play/pause туда-обратно), поэтому там MOD_NOREPEAT остаётся.
        RegisterHotKey(_handle, IdNext, 0, VK_MEDIA_NEXT_TRACK);
        RegisterHotKey(_handle, IdPrev, 0, VK_MEDIA_PREV_TRACK);
        RegisterHotKey(_handle, IdStop, MOD_NOREPEAT, VK_MEDIA_STOP);
        RegisterHotKey(_handle, IdPlayPause, MOD_NOREPEAT, VK_MEDIA_PLAY_PAUSE);
    }

    // Перерегистрирует настраиваемые хоткеи под текущие настройки. Можно вызывать повторно
    // в любой момент (например, сразу после того как пользователь записал новую комбинацию
    // в окне настроек) — старые комбинации корректно снимаются перед регистрацией новых.
    public void ApplyCustomHotkeys(AppSettings settings)
    {
        if (_customPlayPauseRegistered) UnregisterHotKey(_handle, IdCustomPlayPause);
        if (_customNextRegistered) UnregisterHotKey(_handle, IdCustomNext);
        if (_customPreviousRegistered) UnregisterHotKey(_handle, IdCustomPrevious);
        if (_customStopRegistered) UnregisterHotKey(_handle, IdCustomStop);
        if (_customVolumeUpRegistered) UnregisterHotKey(_handle, IdCustomVolumeUp);
        if (_customVolumeDownRegistered) UnregisterHotKey(_handle, IdCustomVolumeDown);
        if (_customMuteRegistered) UnregisterHotKey(_handle, IdCustomMute);
        if (_customShuffleRegistered) UnregisterHotKey(_handle, IdCustomShuffle);
        if (_customRepeatRegistered) UnregisterHotKey(_handle, IdCustomRepeat);
        if (_customDeleteTrackRegistered) UnregisterHotKey(_handle, IdCustomDeleteTrack);

        _customPlayPauseRegistered = TryRegister(IdCustomPlayPause, settings.HotkeyPlayPause);
        _customNextRegistered = TryRegister(IdCustomNext, settings.HotkeyNext, allowRepeat: true);
        _customPreviousRegistered = TryRegister(IdCustomPrevious, settings.HotkeyPrevious, allowRepeat: true);
        _customStopRegistered = TryRegister(IdCustomStop, settings.HotkeyStop);
        _customVolumeUpRegistered = TryRegister(IdCustomVolumeUp, settings.HotkeyVolumeUp, allowRepeat: true);
        _customVolumeDownRegistered = TryRegister(IdCustomVolumeDown, settings.HotkeyVolumeDown, allowRepeat: true);
        _customMuteRegistered = TryRegister(IdCustomMute, settings.HotkeyMute);
        _customShuffleRegistered = TryRegister(IdCustomShuffle, settings.HotkeyShuffle);
        _customRepeatRegistered = TryRegister(IdCustomRepeat, settings.HotkeyRepeat);
        // Удаление с диска — намеренно БЕЗ allowRepeat: держать клавишу зажатой не должно
        // пытаться удалить несколько треков подряд одно за другим.
        _customDeleteTrackRegistered = TryRegister(IdCustomDeleteTrack, settings.HotkeyDeleteTrack);
    }

    // allowRepeat=true снимает флаг MOD_NOREPEAT: Windows будет сама слать повторные
    // WM_HOTKEY, пока комбинация зажата (переключение треков и громкость — см. вызовы выше).
    // Для остальных действий (play/pause, стоп, mute, shuffle, repeat) повтор при удержании
    // не нужен, поэтому по умолчанию (allowRepeat=false) поведение прежнее — одно нажатие.
    private bool TryRegister(int id, HotkeyBinding binding, bool allowRepeat = false)
    {
        if (binding.IsEmpty) return false;
        if (!Enum.TryParse<Key>(binding.Key, out var key)) return false;

        uint modifiers = allowRepeat ? 0 : MOD_NOREPEAT;
        if (binding.Ctrl) modifiers |= MOD_CONTROL;
        if (binding.Alt) modifiers |= MOD_ALT;
        if (binding.Shift) modifiers |= MOD_SHIFT;
        if (binding.Win) modifiers |= MOD_WIN;

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return RegisterHotKey(_handle, id, modifiers, vk);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            switch (wParam.ToInt32())
            {
                case IdNext:
                case IdCustomNext:
                    NextPressed?.Invoke();
                    handled = true;
                    break;
                case IdPrev:
                case IdCustomPrevious:
                    PreviousPressed?.Invoke();
                    handled = true;
                    break;
                case IdStop:
                case IdCustomStop:
                    StopPressed?.Invoke();
                    handled = true;
                    break;
                case IdPlayPause:
                case IdCustomPlayPause:
                    PlayPausePressed?.Invoke();
                    handled = true;
                    break;
                case IdCustomVolumeUp:
                    VolumeUpPressed?.Invoke();
                    handled = true;
                    break;
                case IdCustomVolumeDown:
                    VolumeDownPressed?.Invoke();
                    handled = true;
                    break;
                case IdCustomMute:
                    MutePressed?.Invoke();
                    handled = true;
                    break;
                case IdCustomShuffle:
                    ShufflePressed?.Invoke();
                    handled = true;
                    break;
                case IdCustomRepeat:
                    RepeatPressed?.Invoke();
                    handled = true;
                    break;
                case IdCustomDeleteTrack:
                    DeleteTrackPressed?.Invoke();
                    handled = true;
                    break;
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterHotKey(_handle, IdNext);
        UnregisterHotKey(_handle, IdPrev);
        UnregisterHotKey(_handle, IdStop);
        UnregisterHotKey(_handle, IdPlayPause);

        if (_customPlayPauseRegistered) UnregisterHotKey(_handle, IdCustomPlayPause);
        if (_customNextRegistered) UnregisterHotKey(_handle, IdCustomNext);
        if (_customPreviousRegistered) UnregisterHotKey(_handle, IdCustomPrevious);
        if (_customStopRegistered) UnregisterHotKey(_handle, IdCustomStop);
        if (_customVolumeUpRegistered) UnregisterHotKey(_handle, IdCustomVolumeUp);
        if (_customVolumeDownRegistered) UnregisterHotKey(_handle, IdCustomVolumeDown);
        if (_customMuteRegistered) UnregisterHotKey(_handle, IdCustomMute);
        if (_customShuffleRegistered) UnregisterHotKey(_handle, IdCustomShuffle);
        if (_customRepeatRegistered) UnregisterHotKey(_handle, IdCustomRepeat);
        if (_customDeleteTrackRegistered) UnregisterHotKey(_handle, IdCustomDeleteTrack);

        _source.RemoveHook(WndProc);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
