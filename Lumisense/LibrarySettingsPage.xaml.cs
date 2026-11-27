using System;
using System.Linq;
using System.Windows;
using static Lumisense.BackgroundTask;

namespace Lumisense;

// Создаётся при первом показе страницы (см. SettingsWindow.EnsurePageBuilt).
public sealed partial class LibrarySettingsPage : System.Windows.Controls.UserControl, ILazySettingsPage
{
    private readonly Window _window;
    private readonly AppSettings _settings;
    private readonly ISettingsHost _owner;

    // Глушит Changed-обработчики, пока значения читаются из настроек.
    private bool _isInitializing = true;

    internal LibrarySettingsPage(Window window, AppSettings settings, ISettingsHost owner)
    {
        _window = window;
        _settings = settings;
        _owner = owner;

        // StaticResource разбирается до того, как элементы попадут в дерево окна, поэтому ресурсы окна подключаются заранее.
        Resources.MergedDictionaries.Add(window.Resources);
        InitializeComponent();

        FileNameNormalizationTemplateTextBox.Text = string.IsNullOrWhiteSpace(_settings.FileNameNormalizationTemplate)
            ? FileNameNormalizer.DefaultTemplate
            : _settings.FileNameNormalizationTemplate;
        FileNameNormalizationResultText.Visibility = Visibility.Collapsed;
        InitializeTrackContextMenuActionCheckBoxes();
        ContextMenuOrderLink.Attach(TrackContextMenuActionsPanel, ContextMenuOrdering.Track, _settings, () => _isInitializing);
        _isInitializing = false;
    }

    public void MoveContentTo(System.Windows.Controls.Panel host) => LazySettingsPage.MoveChildren(PageLibrary, host);

    public FrameworkElement? FindElement(string name) => FindName(name) as FrameworkElement;

    private void ContextMenuOrderReset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string menu } && menu == ContextMenuOrdering.Track)
            ContextMenuOrderLink.Reset(TrackContextMenuActionsPanel, menu, _settings);
    }

    private void ClearArtworkCacheButton_Click(object sender, RoutedEventArgs e)
        => BackgroundTask.FireAndForget(ClearArtworkCacheButton_ClickAsync(sender, e), nameof(ClearArtworkCacheButton_Click));

    private async Task ClearArtworkCacheButton_ClickAsync(object sender, RoutedEventArgs e)
    {
        var confirmation = LocalizedMessageBox.Show(_window,
            "Удалить локально сохранённые интернет-обложки?\n\nПри следующем поиске нужные изображения будут скачаны заново.",
            "Очистить кэш обложек?", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (confirmation != System.Windows.MessageBoxResult.Yes) return;

        ClearArtworkCacheButton.IsEnabled = false;
        ArtworkCacheClearResultText.Visibility = Visibility.Collapsed;
        try
        {
            var result = await System.Threading.Tasks.Task.Run(CoverArtSearchWindow.ClearArtworkCache);
            ArtworkCacheClearResultText.Text = result.DeletedFiles == 0
                ? "Кэш обложек уже пуст."
                : $"Удалено файлов: {result.DeletedFiles}; освобождено: {FormatArtworkCacheSize(result.FreedBytes)}."
                  + (result.FailedFiles > 0 ? $" Не удалось удалить файлов: {result.FailedFiles}." : string.Empty);
            ArtworkCacheClearResultText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ArtworkCacheClearResultText.Text = LocalizationService.Translate($"Не удалось очистить кэш: {ex.Message}");
            ArtworkCacheClearResultText.Visibility = Visibility.Visible;
        }
        finally
        {
            ClearArtworkCacheButton.IsEnabled = true;
        }
    }

    private static string FormatArtworkCacheSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} Б";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} КБ";
        return $"{bytes / (1024.0 * 1024.0):0.#} МБ";
    }

    private void TrackContextMenuActionCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        if (sender is not System.Windows.Controls.CheckBox { Tag: string actionId } checkBox) return;

        _owner.SetTrackContextMenuActionDisabled(actionId, checkBox.IsChecked != true);
    }

    private void FileNameNormalizationTemplateTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_isInitializing) return;

        // Пустой шаблон безопасно возвращается к дефолтному в момент запуска операции. Не
        // переписываем TextBox во время набора, чтобы не ломать редактирование пользователю.
        _settings.FileNameNormalizationTemplate = string.IsNullOrWhiteSpace(FileNameNormalizationTemplateTextBox.Text)
            ? FileNameNormalizer.DefaultTemplate
            : FileNameNormalizationTemplateTextBox.Text.Trim();
        FileNameNormalizationResultText.Visibility = Visibility.Collapsed;
    }

    private void NormalizePlaylistFileNamesButton_Click(object sender, RoutedEventArgs e)
        => BackgroundTask.FireAndForget(NormalizePlaylistFileNamesButton_ClickAsync(sender, e), nameof(NormalizePlaylistFileNamesButton_Click));

    private async Task NormalizePlaylistFileNamesButton_ClickAsync(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.FileNameNormalizationTemplate = string.IsNullOrWhiteSpace(FileNameNormalizationTemplateTextBox.Text)
            ? FileNameNormalizer.DefaultTemplate
            : FileNameNormalizationTemplateTextBox.Text.Trim();

        NormalizePlaylistFileNamesButton.IsEnabled = false;
        FileNameNormalizationResultText.Text = LocalizationService.Translate("Подготавливается предпросмотр файлов…");
        FileNameNormalizationResultText.Visibility = Visibility.Visible;
        FileNameNormalizationLoadingIndicator.IsAnimationEnabled = !AccessibilityPreferences.ShouldReduceMotion(_settings, AnimationAreas.Loading);
        FileNameNormalizationLoadingIndicator.Visibility = Visibility.Visible;

        try
        {
            FileNameNormalizer.RenameResult? result = await _owner.NormalizePlaylistFileNamesAsync(_window);
            if (result is null)
            {
                FileNameNormalizationResultText.Text = LocalizationService.Translate("Нормализация отменена.");
                return;
            }

            string errors = result.Errors.Count > 0 ? $" Ошибок: {result.Errors.Count}." : string.Empty;
            FileNameNormalizationResultText.Text =
                LocalizationService.Translate($"Готово. Переименовано: {result.RenamedCount}; пропущено: {result.SkippedCount}.{errors}");
        }
        catch (Exception ex)
        {
            FileNameNormalizationResultText.Text = LocalizationService.Translate($"Не удалось нормализовать имена: {ex.Message}");
        }
        finally
        {
            FileNameNormalizationLoadingIndicator.Visibility = Visibility.Collapsed;
            NormalizePlaylistFileNamesButton.IsEnabled = true;
        }
    }

    private void InitializeTrackContextMenuActionCheckBoxes()
    {
        var checkBoxes = new[]
        {
            TrackContextPlayNextCheckBox,
            TrackContextAddToQueueCheckBox,
            TrackContextFindFileCheckBox,
            TrackContextFavoriteCheckBox,
            TrackContextShowInExplorerCheckBox,
            TrackContextCopyNameCheckBox,
            TrackContextCopyPathCheckBox,
            TrackContextCopyFileCheckBox,
            TrackContextExportProcessedCopyCheckBox,
            TrackContextPropertiesCheckBox,
            TrackContextEditTagsCheckBox,
            TrackContextNormalizeFileNameCheckBox,
            TrackContextRemoveFromPlaylistCheckBox,
            TrackContextDeleteFromDiskCheckBox
        };

        foreach (System.Windows.Controls.CheckBox checkBox in checkBoxes)
        {
            if (checkBox.Tag is string actionId)
                checkBox.IsChecked = !_owner.IsTrackContextMenuActionDisabled(actionId);
        }
    }
}
