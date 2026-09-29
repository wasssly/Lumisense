using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls.Primitives;
using WpfSlider = System.Windows.Controls.Slider;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Lumisense;

// Контекстное меню трека плейлиста: воспроизведение, очередь, relink, экспорт, теги, удаление. Вынесено из
// MainWindow.xaml.cs только ради навигации, логика не менялась.
public partial class MainWindow
{
    // DataContext пунктов меню унаследован от ContextMenu.PlacementTarget (WPF пробрасывает его и через Popup) — это сам
    // PlaylistTrackRow (row.FilePath и row.Folder), без Tag/CommandParameter.

    private void PlayTrackMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { DataContext: PlaylistTrackRow row }) return;
        LoadAndPlay(row.FilePath);
    }

    // Помощник для пунктов, применяемых ко всем выделенным строкам (как PlaylistTrackList_PreviewKeyDown/DeleteTracksFromPlaylist);
    // если правый клик вне выделения, действие — только для этой строки.
    private List<PlaylistTrackRow> GetSelectedRowsForBulkAction(PlaylistTrackRow clickedRow)
    {
        var listView = clickedRow.Folder.IsFavoritesGroup ? FavoritesTrackListView : PlaylistFoldersControl;
        var selected = listView.SelectedItems.OfType<PlaylistTrackRow>().ToList();
        return selected.Contains(clickedRow) ? selected : new List<PlaylistTrackRow> { clickedRow };
    }

    private void PlayNextMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { DataContext: PlaylistTrackRow row }) return;
        _playbackQueue.PlayNext(GetSelectedRowsForBulkAction(row).Select(r => r.FilePath));
    }

    private void AddToQueueMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { DataContext: PlaylistTrackRow row }) return;
        _playbackQueue.AddToEnd(GetSelectedRowsForBulkAction(row).Select(r => r.FilePath));
    }

    private void RelinkTrackMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PlaylistTrackRow row }) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = LocalizationService.Translate("Выберите замену для недоступного трека"),
            Filter = $"{LocalizationService.Translate("Файлы аудио")}|*.mp3;*.wav;*.flac;*.m4a;*.aac;*.ogg;*.wma|{LocalizationService.Translate("Все файлы")}|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        string oldPath = row.FilePath;
        string replacementPath = dialog.FileName;
        if (string.Equals(oldPath, replacementPath, StringComparison.OrdinalIgnoreCase)) return;

        bool wasFavorite = FavoritesManager.IsFavorite(oldPath);
        bool wasPinned = FavoritesManager.IsPinned(oldPath);
        if (row.Folder.IsFavoritesGroup)
        {
            FavoritesManager.SetFavorite(oldPath, false);
            FavoritesManager.SetFavorite(replacementPath, true);
            if (wasPinned) FavoritesManager.TogglePin(replacementPath);
        }
        else
        {
            int index = row.Folder.Tracks.IndexOf(oldPath);
            if (index < 0) return;

            // Автoобновление могло уже добавить найденный файл как новую запись. В таком
            // случае не создаём дубликат: убираем только старый недоступный путь.
            if (row.Folder.Tracks.Any(path => string.Equals(path, replacementPath, StringComparison.OrdinalIgnoreCase)))
                row.Folder.Tracks.RemoveAt(index);
            else
                row.Folder.Tracks[index] = replacementPath;
            if (wasFavorite)
            {
                FavoritesManager.SetFavorite(oldPath, false);
                FavoritesManager.SetFavorite(replacementPath, true);
                if (wasPinned) FavoritesManager.TogglePin(replacementPath);
            }
        }

        _playbackQueue.Remove(oldPath);
        RefreshPlaylistView();
        if (_isFavoritesView) RefreshFavoritesTrackList();
    }

    private void ShowInExplorerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { DataContext: PlaylistTrackRow row }) return;
        if (!File.Exists(row.FilePath)) return;

        // /select, выделяет сам файл в открывшемся окне проводника, а не просто открывает папку
        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{row.FilePath}\"");
    }

    private void CopyTrackNameMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { DataContext: PlaylistTrackRow row }) return;

        // Копируем то, что видно в плейлисте: имя файла без расширения и пути (FileNameConverter), а не теги.
        System.Windows.Clipboard.SetText(Path.GetFileNameWithoutExtension(row.FilePath));
    }

    private async void ExportProcessedCopyMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_trackExportInProgress || sender is not System.Windows.Controls.MenuItem { DataContext: PlaylistTrackRow row }) return;
        if (!File.Exists(row.FilePath)) return;

        string stem = Path.GetFileNameWithoutExtension(row.FilePath);
        string suffix = $" - speed {_runtimePlaybackRate:0.##}x pitch {_settings.PlaybackPitchSemitones:+0.##;-0.##;0}st";
        var dialog = new SaveFileDialog
        {
            Title = LocalizationService.Translate("Сохранить обработанную копию"),
            Filter = LocalizationService.Translate("MP3-файл (*.mp3)|*.mp3"),
            DefaultExt = ".mp3",
            AddExtension = true,
            OverwritePrompt = false,
            InitialDirectory = Path.GetDirectoryName(row.FilePath),
            FileName = stem + suffix
        };
        if (dialog.ShowDialog() != true) return;
        if (string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(row.FilePath), StringComparison.OrdinalIgnoreCase))
        {
            LocalizedMessageBox.Show(this, "Копия должна сохраняться в отдельный MP3-файл.", "Сохранение копии",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }
        if (File.Exists(dialog.FileName))
        {
            LocalizedMessageBox.Show(this, "Файл с таким именем уже существует. Выберите другое имя, чтобы не перезаписывать его.",
                                "Файл уже существует", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }
        _trackExportInProgress = true;
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try
        {
            await _trackExportService.ExportMp3Async(
                row.FilePath,
                dialog.FileName,
                new TrackExportOptions(_runtimePlaybackRate, _settings.PlaybackPitchSemitones));
            LocalizedMessageBox.Show(this, $"Копия сохранена:\n{dialog.FileName}", "Сохранение завершено",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось сохранить обработанную MP3-копию '{dialog.FileName}': {ex.Message}");
            LocalizedMessageBox.Show(this, $"Не удалось сохранить MP3-копию:\n{ex.Message}", "Ошибка сохранения",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            System.Windows.Input.Mouse.OverrideCursor = null;
            _trackExportInProgress = false;
        }
    }

    private void CopyPathMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { DataContext: PlaylistTrackRow row }) return;
        System.Windows.Clipboard.SetText(row.FilePath);
    }

    private void CopyFileMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { DataContext: PlaylistTrackRow row }) return;
        if (!File.Exists(row.FilePath)) return;

        // Кладём в буфер обмена сам файл (а не просто его путь текстом), чтобы можно было
        // вставить (Ctrl+V) прямо в проводник или другую папку — как при обычном Ctrl+C по файлу.
        var files = new System.Collections.Specialized.StringCollection();
        files.Add(row.FilePath);
        System.Windows.Clipboard.SetFileDropList(files);
    }

    // Системный shell-диалог "Свойства" для многих аудиотипов молча не срабатывал (нет обработчика verb): вместо него
    // своё окно TrackPropertiesWindow, независимое от реестра пользователя.
    private void TrackPropertiesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { DataContext: PlaylistTrackRow row }) return;
        if (!File.Exists(row.FilePath)) return;

        new TrackPropertiesWindow(row.FilePath, _settings) { Owner = this }.ShowDialog();
    }

    // Окно редактирования тегов пишет прямо в файл через ATL.NET; если файл — играющий трек, название/исполнитель/обложка
    // в плеере обновляются сразу, не дожидаясь переключения.
    private void EditTagsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { DataContext: PlaylistTrackRow row }) return;
        string filePath = row.FilePath;
        if (!File.Exists(filePath)) return;

        var tagsWindow = new TrackTagsWindow(filePath, this) { Owner = this };
        tagsWindow.ShowDialog();

        if (tagsWindow.Saved && PathEquals(filePath, _currentTrackPath))
        {
            LoadAlbumArt(filePath);
            _integrations.NowPlaying?.UpdateTrackInfo(TrackTitleText.Text, TrackArtistText.Text);
            RaiseTrackInfoChanged(TrackTitleText.Text, TrackArtistText.Text, CurrentArtBrush);
        }
    }

    private async void NormalizeTrackFileNameMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { DataContext: PlaylistTrackRow row }) return;

        try
        {
            FileNameNormalizer.RenameResult? result = await NormalizeTrackFileNamesAsync(new[] { row.FilePath }, this);
            if (result is null || result.RenamedCount == 0) return;

            string errors = result.Errors.Count > 0
                ? $"\n\nОшибок: {result.Errors.Count}. {string.Join(" ", result.Errors.Take(2))}"
                : string.Empty;
            LocalizedMessageBox.Show(this,
                $"Имя файла нормализовано. Переименовано: {result.RenamedCount}; пропущено: {result.SkippedCount}.{errors}",
                "Нормализация имён", System.Windows.MessageBoxButton.OK,
                result.Errors.Count == 0 ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            LocalizedMessageBox.Show(this, $"Не удалось нормализовать имя файла:\n{ex.Message}",
                "Нормализация имён", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void RemoveTrackMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { DataContext: PlaylistTrackRow row }) return;

        // В виртуальном "Избранном" нет своего списка (пересобирается из FavoritesManager, RefreshFavoritesTrackList): "убрать" здесь
        // означает "снять сердечко", иначе трек вернулся бы при следующем обновлении.
        if (row.Folder.IsFavoritesGroup)
        {
            FavoritesManager.SetFavorite(row.FilePath, false);
            if (_isFavoritesView) RefreshFavoritesTrackList();
            return;
        }

        // Если убираемый трек сейчас играет — не прерываем воспроизведение (он уже
        // загружен в память и от списка не зависит), просто убираем строку из плейлиста.
        row.Folder.Tracks.Remove(row.FilePath);
        RefreshPlaylistView();
    }

    // Удаляет файл с диска (в отличие от "Убрать из плейлиста"): сначала подтверждение, затем корзина (Microsoft.VisualBasic.FileIO)
    // вместо File.Delete, чтобы файл можно было восстановить.
    private void DeleteTrackFromDiskMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { DataContext: PlaylistTrackRow row }) return;

        DeleteTrackFromDisk(row.FilePath);
    }

    // Хоткей удаления (AppSettings.HotkeyDeleteTrack) по умолчанию не назначен; удаляет текущий играющий трек тем же путём,
    // что пункт меню (DeleteTrackFromDiskMenuItem_Click): с подтверждением и через корзину.
    void IIntegrationHost.DeleteCurrentTrackFromDiskHotkey() => DeleteCurrentTrackFromDiskHotkey();

    private void DeleteCurrentTrackFromDiskHotkey()
    {
        if (_currentTrackPath == null) return;
        DeleteTrackFromDisk(_currentTrackPath);
    }

    private void DeleteTrackFromDisk(string filePath)
    {
        var trackName = Path.GetFileName(filePath);

        // Владелец диалога — реально видимое окно: в мини-режиме MainWindow скрыто (Hide в ShowMiniPlayer), и диалог
        // с невидимым владельцем не выходит на передний план, поэтому берём мини-плеер.
        Window ownerWindow = _isMiniMode && _miniPlayerWindow != null ? _miniPlayerWindow : this;

        // Хоткей глобальный: плеер почти наверняка не в фокусе, и MessageBox оказался бы под чужим окном (Windows блокирует
        // кражу фокуса); Topmost-моргание (ForceForeground) чинит это, для контекстного меню — безвредный no-op.
        ForceForeground(ownerWindow);

        var confirm = LocalizedMessageBox.Show(
            ownerWindow,
            $"Удалить файл «{trackName}» с диска?\n\nФайл будет перемещён в корзину, а трек — убран из всех плейлистов.",
            "Удаление трека с диска",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.Yes);

        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        bool isCurrentlyLoaded = PathEquals(filePath, _currentTrackPath) && _audioFile != null;
        string? nextPath = null;
        bool wasPlaying = false;
        TimeSpan previousPosition = TimeSpan.Zero;

        if (isCurrentlyLoaded)
        {
            previousPosition = _audioFile!.CurrentTime;
            wasPlaying = _isPlaying;

            // "Следующий трек" считаем до удаления текущего из плейлиста: иначе ComputeNextTrackPath отсчитал бы позицию без него;
            // если следующий — тот же файл (он один в очереди), играть больше нечего.
            nextPath = ResolveNextTrackPathRespectingQueue(_currentTrackPath);
            if (PathEquals(nextPath, filePath)) nextPath = null;

            // Файл играющего трека открыт NAudio-потоком, и без остановки воспроизведения и освобождения хендла удаление упадёт ("файл занят").
            StopPlayback();
        }

        try
        {
            if (File.Exists(filePath))
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    filePath,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
        }
        catch (Exception ex)
        {
            // StopPlayback освобождал хендл, но при ошибке удаления трек существует: возвращаем позицию, чтобы временная ошибка
            // корзины/прав не превращалась в потерю воспроизведения.
            if (isCurrentlyLoaded && File.Exists(filePath))
                LoadAndPlay(filePath, autoPlay: wasPlaying, startPosition: previousPosition,
                    changeOrigin: TrackChangeOrigin.ExternalEdit);

            LocalizedMessageBox.Show(ownerWindow, $"Не удалось удалить файл:\n{filePath}\n\n{ex.Message}",
                "Ошибка удаления", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return;
        }

        // Файла больше нет: убираем его из ВСЕХ плейлистов и избранного, иначе в других группах остались бы битые ссылки.
        foreach (var folder in _folders)
            folder.Tracks.RemoveAll(t => PathEquals(t, filePath));
        FavoritesManager.SetFavorite(filePath, false);

        // Действие редкое (подтверждённое удаление файла): полный пересбор обоих списков не проблема, а пропуск одного был бы багом.
        RefreshPlaylistView();
        if (_isFavoritesView) RefreshFavoritesTrackList();

        // Если играл удалённый трек и в очереди есть следующий — переключаемся, сохраняя состояние играло/на паузе.
        if (nextPath != null)
            LoadAndPlay(nextPath, autoPlay: wasPlaying, changeOrigin: TrackChangeOrigin.Automatic);
    }
}
