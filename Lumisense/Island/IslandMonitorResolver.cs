namespace Lumisense;

internal static class IslandMonitorResolver
{
    // Сохранённый монитор, если он подключён; иначе основной, а без него — первый из списка.
    // Обобщённая сигнатура нужна, чтобы выбор проверялся тестами без реальных Screen.
    public static T Pick<T>(IReadOnlyList<T> screens, string? savedDeviceName, Func<T, string> deviceName, Func<T, bool> isPrimary)
    {
        if (!string.IsNullOrEmpty(savedDeviceName))
        {
            foreach (var screen in screens)
                if (deviceName(screen) == savedDeviceName) return screen;
        }

        foreach (var screen in screens)
            if (isPrimary(screen)) return screen;

        return screens[0];
    }
}
