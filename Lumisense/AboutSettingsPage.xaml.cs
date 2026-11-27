using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Lumisense;

// Создаётся при первом показе страницы (см. SettingsWindow.EnsurePageBuilt).
public sealed partial class AboutSettingsPage : System.Windows.Controls.UserControl, ILazySettingsPage
{
    private readonly ISettingsHost _owner;
    private BitmapImage? _developerAvatarBitmap;
    private bool _developerAvatarRequested;
    private DiagnosticsWindow? _diagnosticsWindow;

    internal AboutSettingsPage(Window window, AppSettings settings, ISettingsHost owner)
    {
        _settings = settings;
        _owner = owner;

        // StaticResource разбирается до того, как элементы попадут в дерево окна, поэтому ресурсы окна подключаются заранее.
        Resources.MergedDictionaries.Add(window.Resources);
        InitializeComponent();

        RefreshAppVersionText();
    }

    private readonly AppSettings _settings;

    public void MoveContentTo(System.Windows.Controls.Panel host) => LazySettingsPage.MoveChildren(PageAbout, host);

    public FrameworkElement? FindElement(string name) => FindName(name) as FrameworkElement;

    // Версия в карточке «О плеере» — из assembly metadata (как у UpdateChecker и в release workflow), а не из changelog.
    public void RefreshAppVersionText()
    {
        AppVersionText.Text = LocalizationService.FormatKey(
            LocalizationKey.ApplicationVersion, UpdateChecker.GetCurrentVersion());
    }

    // Список изменений и настройки не должны быть открыты одновременно: MainWindow.ShowChangelogWindow (симметрично
    // ShowSettingsWindow) централизует переключение окон и закроет окно настроек.
    private void ChangelogButton_Click(object sender, RoutedEventArgs e) => _owner.ShowChangelogWindow();

    // Process.Start с UseShellExecute=true: в .NET Core без этого флага URL напрямую не открывается (как у MoreButton_Click
    // в UpdateAvailableWindow); try/catch на случай отсутствия браузера по умолчанию.
    private void DeveloperGitHubButton_Click(object sender, RoutedEventArgs e) => OpenUrl("https://github.com/wasssly");

    private void OpenRepositoryButton_Click(object sender, RoutedEventArgs e) => OpenUrl(DiscordRichPresenceDefaults.GitHubRepositoryUrl);

    private void DeveloperTelegramButton_Click(object sender, RoutedEventArgs e) => OpenUrl("https://t.me/dontwritetoblame");

    private void OpenTelegramChannelButton_Click(object sender, RoutedEventArgs e) => OpenUrl(UpdateCheckHealth.TelegramChannelUrl);

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e) => Logger.OpenLogsFolder();

    private void DiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_diagnosticsWindow is { IsLoaded: true })
        {
            _diagnosticsWindow.Activate();
            return;
        }

        // Без владельца: долгая проверка не должна прерываться, если закрыть окно настроек.
        _diagnosticsWindow = new DiagnosticsWindow(_settings, _owner.GetDiagnosticsHost())
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        _diagnosticsWindow.Closed += (_, _) => _diagnosticsWindow = null;
        _diagnosticsWindow.Show();
    }

    // Загружается при первом показе страницы «О плеере» и кэшируется локально, чтобы последующие
    // открытия Settings не зависели от сети и не создавали новый HTTP-запрос каждый раз.
    public void LoadDeveloperAvatar()
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

    private static void OpenUrl(string url) => SettingsWindow.OpenUrl(url);
}
