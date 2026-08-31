using System;
using System.IO;
using System.Windows;

namespace Lumisense;

internal static class PlaybackErrorExperience
{
    public static void Show(Window owner, string? filePath, Exception exception)
    {
        string message = Classify(filePath, exception);
        Logger.Error("Ошибка воспроизведения", exception);
        LocalizedMessageBox.Show(owner, LocalizationService.Translate(message),
            LocalizationService.Translate("Ошибка воспроизведения"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static string Classify(string? filePath, Exception exception)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return "Файл трека больше недоступен. Проверьте подключение диска или удалите его из плейлиста.";
        if (exception is UnauthorizedAccessException)
            return "Нет доступа к файлу трека. Проверьте права доступа к папке.";
        if (exception is IOException)
            return "Не удалось прочитать файл трека. Проверьте, что диск доступен и файл не занят другой программой.";
        return "Не удалось воспроизвести этот файл. Попробуйте выбрать другой трек или изменить устройство вывода в настройках.";
    }
}
