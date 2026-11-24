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
    // Список изменений и настройки не должны быть открыты одновременно: MainWindow.ShowChangelogWindow (симметрично
    // ShowSettingsWindow) централизует переключение окон и закроет это окно.
    private void ChangelogButton_Click(object sender, RoutedEventArgs e) => _owner.ShowChangelogWindow();

    // Process.Start с UseShellExecute=true: в .NET Core без этого флага URL напрямую не открывается (как у MoreButton_Click
    // в UpdateAvailableWindow); try/catch на случай отсутствия браузера по умолчанию.
    private void DeveloperGitHubButton_Click(object sender, RoutedEventArgs e) => OpenUrl("https://github.com/wasssly");

    private void OpenRepositoryButton_Click(object sender, RoutedEventArgs e) => OpenUrl(DiscordRichPresenceDefaults.GitHubRepositoryUrl);

    private void DeveloperTelegramButton_Click(object sender, RoutedEventArgs e) => OpenUrl("https://t.me/dontwritetoblame");

    private void OpenTelegramChannelButton_Click(object sender, RoutedEventArgs e) => OpenUrl(UpdateCheckHealth.TelegramChannelUrl);

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e) => Logger.OpenLogsFolder();

    // Загружается при первом показе страницы «О плеере» и кэшируется локально, чтобы последующие
    // открытия Settings не зависели от сети и не создавали новый HTTP-запрос каждый раз.
    private void LoadDeveloperAvatar()
    {
        // Одна попытка на окно: повторные переключения вкладок не должны повторять неудавшийся запрос.
        if (_developerAvatarRequested) return;

        _developerAvatarRequested = true;
        BackgroundTask.FireAndForget(LoadDeveloperAvatarAsync(), nameof(LoadDeveloperAvatar));
    }

    private async Task LoadDeveloperAvatarAsync()
    {
        const string avatarUrl = "https://github.com/wasssly.png?size=96";
        string cacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumisense", "Cache");
        string cachePath = Path.Combine(cacheDirectory, "developer-avatar-96.png");
        string temporaryPath = Path.Combine(cacheDirectory, $"developer-avatar-{Guid.NewGuid():N}.part");

        try
        {
            byte[] bytes;
            bool cacheIsFresh = File.Exists(cachePath) &&
                                DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < TimeSpan.FromDays(30);
            if (cacheIsFresh)
            {
                bytes = await File.ReadAllBytesAsync(cachePath);
            }
            else
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd(LyricsNetworkIdentity.UserAgentValue);
                bytes = await client.GetByteArrayAsync(avatarUrl);
                if (bytes.Length == 0 || bytes.Length > 2 * 1024 * 1024) return;

                Directory.CreateDirectory(cacheDirectory);
                await File.WriteAllBytesAsync(temporaryPath, bytes);
                File.Move(temporaryPath, cachePath, true);
            }

            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            _developerAvatarBitmap = bitmap;
            DeveloperAvatarBrush.ImageSource = bitmap;
        }
        catch
        {
            // При недоступной сети или повреждённом кэше остаётся XAML placeholder.
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch
            {
                // Временный файл аватара остаётся до очистки временной папки: на работу окна это не влияет.
            }
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // Нет браузера по умолчанию и т.п. — не критично, но без записи в журнал нажатие выглядело бы поломкой.
            Logger.Warn($"Не удалось открыть ссылку {url}: {ex.Message}");
        }
    }

    // Запись хоткея: клик по кнопке включает режим записи, следующее нажатие (с Ctrl/Alt/Shift) сохраняется как
    // глобальная комбинация и сразу перерегистрируется в GlobalMediaHotKeys без перезапуска.
}
