using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Lumisense;

// Мультимедийные клавиши как глобальные хоткеи через RegisterHotKey — работают без фокуса.
// Плюс пользовательские комбинации (ApplyCustomHotkeys), применяются без перезапуска.
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
    private const int IdCustomSeekForward = 0xA01B;
    private const int IdCustomSeekBackward = 0xA01C;
    private const int IdCustomToggleFavorite = 0xA01D;
    private const int IdCustomToggleLyrics = 0xA01E;
    private const int IdCustomToggleMiniPlayer = 0xA01F;

    private readonly IntPtr _handle;
    private readonly HwndSource _source;

    private int _disposed;
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
    private bool _customSeekForwardRegistered;
    private bool _customSeekBackwardRegistered;
    private bool _customToggleFavoriteRegistered;
    private bool _customToggleLyricsRegistered;
    private bool _customToggleMiniPlayerRegistered;

    // Next/Previous передают код фактически нажатой клавиши, чтобы MainWindow сам определял удержание
    // хоткея, не завися от системной частоты повторных WM_HOTKEY.
    public event Action<int>? NextPressed;
    public event Action<int>? PreviousPressed;
    public event Action? StopPressed;
    public event Action? PlayPausePressed;
    public event Action? VolumeUpPressed;
    public event Action? VolumeDownPressed;
    public event Action? MutePressed;
    public event Action? ShufflePressed;
    public event Action? RepeatPressed;
    public event Action? DeleteTrackPressed;
    public event Action? SeekForwardPressed;
    public event Action? SeekBackwardPressed;
    public event Action? ToggleFavoritePressed;
    public event Action? ToggleLyricsPressed;
    public event Action? ToggleMiniPlayerPressed;

    public GlobalMediaHotKeys(Window window)
    {
        _handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_handle)
                  ?? throw new InvalidOperationException("Окно ещё не инициализировано (нет Hwnd).");

        _source.AddHook(WndProc);

        // Сбой регистрации (клавишу занято другое приложение) не критичен для остальных хоткеев. Next/Prev без
        // MOD_NOREPEAT: Windows сама повторяет WM_HOTKEY при удержании; для Stop/PlayPause повтор вреден.
        RegisterHotKey(_handle, IdNext, 0, VK_MEDIA_NEXT_TRACK);
        RegisterHotKey(_handle, IdPrev, 0, VK_MEDIA_PREV_TRACK);
        RegisterHotKey(_handle, IdStop, MOD_NOREPEAT, VK_MEDIA_STOP);
        RegisterHotKey(_handle, IdPlayPause, MOD_NOREPEAT, VK_MEDIA_PLAY_PAUSE);
    }

    // Перерегистрирует хоткеи под текущие настройки; безопасно вызывать повторно — старые комбинации снимаются.
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
        if (_customSeekForwardRegistered) UnregisterHotKey(_handle, IdCustomSeekForward);
        if (_customSeekBackwardRegistered) UnregisterHotKey(_handle, IdCustomSeekBackward);
        if (_customToggleFavoriteRegistered) UnregisterHotKey(_handle, IdCustomToggleFavorite);
        if (_customToggleLyricsRegistered) UnregisterHotKey(_handle, IdCustomToggleLyrics);
        if (_customToggleMiniPlayerRegistered) UnregisterHotKey(_handle, IdCustomToggleMiniPlayer);

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
        // Перемотка — allowRepeat: true, как и у громкости: держать клавишу зажатой значит
        // мотать дальше, а не один раз дёрнуть на фиксированный шаг.
        _customSeekForwardRegistered = TryRegister(IdCustomSeekForward, settings.HotkeySeekForward, allowRepeat: true);
        _customSeekBackwardRegistered = TryRegister(IdCustomSeekBackward, settings.HotkeySeekBackward, allowRepeat: true);
        _customToggleFavoriteRegistered = TryRegister(IdCustomToggleFavorite, settings.HotkeyToggleFavorite);
        _customToggleLyricsRegistered = TryRegister(IdCustomToggleLyrics, settings.HotkeyToggleLyrics);
        _customToggleMiniPlayerRegistered = TryRegister(IdCustomToggleMiniPlayer, settings.HotkeyToggleMiniPlayer);
    }

    // allowRepeat=true снимает MOD_NOREPEAT, и Windows повторяет WM_HOTKEY при удержании (треки, громкость);
    // для остальных действий по умолчанию одно нажатие.
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
                    NextPressed?.Invoke(GetVirtualKey(lParam));
                    handled = true;
                    break;
                case IdPrev:
                case IdCustomPrevious:
                    PreviousPressed?.Invoke(GetVirtualKey(lParam));
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
                case IdCustomSeekForward:
                    SeekForwardPressed?.Invoke();
                    handled = true;
                    break;
                case IdCustomSeekBackward:
                    SeekBackwardPressed?.Invoke();
                    handled = true;
                    break;
                case IdCustomToggleFavorite:
                    ToggleFavoritePressed?.Invoke();
                    handled = true;
                    break;
                case IdCustomToggleLyrics:
                    ToggleLyricsPressed?.Invoke();
                    handled = true;
                    break;
                case IdCustomToggleMiniPlayer:
                    ToggleMiniPlayerPressed?.Invoke();
                    handled = true;
                    break;
            }
        }

        return IntPtr.Zero;
    }

    // Физическое состояние клавиши надёжнее ожидания следующего WM_HOTKEY: у системного повтора есть
    // начальная пауза, а частота задаётся пользователем и может быть слишком медленной или быстрой.
    public static bool IsVirtualKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static int GetVirtualKey(IntPtr lParam) => (int)((lParam.ToInt64() >> 16) & 0xFFFF);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
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
        if (_customSeekForwardRegistered) UnregisterHotKey(_handle, IdCustomSeekForward);
        if (_customSeekBackwardRegistered) UnregisterHotKey(_handle, IdCustomSeekBackward);
        if (_customToggleFavoriteRegistered) UnregisterHotKey(_handle, IdCustomToggleFavorite);
        if (_customToggleLyricsRegistered) UnregisterHotKey(_handle, IdCustomToggleLyrics);
        if (_customToggleMiniPlayerRegistered) UnregisterHotKey(_handle, IdCustomToggleMiniPlayer);

        _source.RemoveHook(WndProc);
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
