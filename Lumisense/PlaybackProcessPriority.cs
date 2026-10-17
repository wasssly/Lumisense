using System.ComponentModel;
using System.Diagnostics;

namespace Lumisense;

// Повышенный приоритет процесса плеера (AppSettings.RaisePlaybackPriority). Звуковой поток уже работает под MMCSS «Pro Audio», но
// остальные потоки процесса (декодирование, сборщик мусора, интерфейс) при загрузке ЦП под 100% голодают, а блокирующая сборка
// мусора ждёт их все: поток вывода стоит, и звук щёлкает. AboveNormal даёт им ЦП раньше фоновых программ.
internal static class PlaybackProcessPriority
{
    public static void Apply(bool raise)
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            if (raise)
            {
                if (process.PriorityClass == ProcessPriorityClass.Normal)
                    process.PriorityClass = ProcessPriorityClass.AboveNormal;
            }
            else if (process.PriorityClass == ProcessPriorityClass.AboveNormal)
            {
                // Возвращаем Normal, только если приоритет повысили мы (запуск пользователем с другим классом не трогаем).
                process.PriorityClass = ProcessPriorityClass.Normal;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            Logger.Warn($"Не удалось изменить приоритет процесса: {ex.Message}");
        }
    }
}
