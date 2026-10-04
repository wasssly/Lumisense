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

// Источники плейлиста: drag & drop, добавление файлов и папок, наблюдение за папками, нормализация имён файлов.
// Вынесено из MainWindow.xaml.cs только ради навигации, логика не менялась.
public partial class MainWindow
{
    // Drag & Drop файлов/папок из Проводника (как "Добавить"): папки — отдельные группы (AddFolderPath), файлы — общая группа
    // "Отдельные файлы" (AddLooseFiles). Тот же обработчик у DragOver (XAML): WPF не помнит e.Effects, иначе курсор покажет "нельзя".
    private void MainWindow_DragEnter(object sender, System.Windows.DragEventArgs e)
    {
        bool hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        DragDropOverlay.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    // DragLeave срабатывает и при уходе с окна, и при переходе между дочерними элементами; AllowDrop только на корневом
    // FluentWindow, поэтому это означает уход с окна — оверлей прячем в обоих случаях.
    private void MainWindow_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        DragDropOverlay.Visibility = Visibility.Collapsed;
    }

    private void MainWindow_Drop(object sender, System.Windows.DragEventArgs e)
        => BackgroundTask.FireAndForget(MainWindow_DropAsync(sender, e), nameof(MainWindow_Drop));

    private async Task MainWindow_DropAsync(object sender, System.Windows.DragEventArgs e)
    {
        DragDropOverlay.Visibility = Visibility.Collapsed;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        var newFiles = new List<string>();
        bool foundAnyFolder = false;
        bool foundAnything = false;

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                foundAnyFolder = true;
                foundAnything = await AddFolderPathAsync(path) || foundAnything;
            }
            else if (File.Exists(path) && SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
            {
                newFiles.Add(path);
                foundAnything = true;
            }
            // Прочие файлы молча пропускаем (мог задеть лишнее при перетаскивании); сообщение "ничего не найдено" ниже — только
            // если в итоге не добавилось ничего.
        }

        if (_isExiting) return;
        if (newFiles.Count > 0)
            AddLooseFiles(newFiles);

        if (!foundAnything)
        {
            string message = foundAnyFolder
                ? "В перетащенных папках не найдено поддерживаемых аудиофайлов."
                : "Среди перетащенного не найдено ни поддерживаемых аудиофайлов, ни папок.";
            LocalizedMessageBox.Show(this, message,
                "Ничего не найдено", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
    }

    // Клик по объединённой кнопке "Добавить" открывает её собственное контекстное меню
    // (выбор "Файлы…" / "Папку…") прямо под кнопкой, как обычное выпадающее меню.
    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } button) return;

        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private void AddFilesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Аудиофайлы (*.mp3;*.wav;*.wma;*.flac;*.m4a;*.aac;*.ogg)|*.mp3;*.wav;*.wma;*.flac;*.m4a;*.aac;*.ogg|Все файлы (*.*)|*.*",
            Multiselect = true,
            Title = "Выберите аудиофайлы"
        };

        if (dialog.ShowDialog() != true) return;

        AddLooseFiles(dialog.FileNames);
    }

    // Пустая "временная" папка без привязки к диску: наполняется кнопкой в заголовке (AddFilesToFolderButton_Click), удобно для
    // разового плейлиста из файлов из разных мест.
    private void CreateFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TextInputDialog("Новая папка", "Название папки:", settings: _settings) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var folder = new PlaylistFolder
        {
            SourcePath = null,
            DisplayName = dialog.ResultText,
            IsLooseFilesBucket = false
        };

        _folders.Add(folder);
        RefreshPlaylistView();
    }

    // Кнопка "Добавить файлы" в заголовке группы (только "Отдельные файлы" и ручные папки, PlaylistFolder.CanAddFilesDirectly)
    // добавляет именно в эту группу, в отличие от общей кнопки в шапке.
    private void AddFilesToFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PlaylistFolder folder }) return;

        var dialog = new OpenFileDialog
        {
            Filter = "Аудиофайлы (*.mp3;*.wav;*.wma;*.flac;*.m4a;*.aac;*.ogg)|*.mp3;*.wav;*.wma;*.flac;*.m4a;*.aac;*.ogg|Все файлы (*.*)|*.*",
            Multiselect = true,
            Title = $"Добавить файлы в «{folder.DisplayName}»"
        };

        if (dialog.ShowDialog() != true) return;

        bool wasEmptyBeforeAdd = FlattenAll().Count == 0;

        var allExisting = FlattenAll();
        var actuallyNew = dialog.FileNames.Where(f => !allExisting.Contains(f)).ToList();
        if (actuallyNew.Count == 0) return;

        folder.Tracks.AddRange(actuallyNew);
        RefreshPlaylistView();

        if (wasEmptyBeforeAdd)
            LoadAndPlay(actuallyNew[0]);
    }

    private void StartFolderWatchers()
    {
        StopFolderWatchers();
        if (_isExiting || !_settings.AutoRefreshPlaylistFolders) return;

        foreach (string folderPath in _folders
                     .Select(folder => folder.SourcePath)
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Cast<string>()
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(folderPath)) continue;

                var watcher = new FileSystemWatcher(folderPath, "*.*")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                };
                watcher.Created += FolderWatcher_FileChanged;
                watcher.Renamed += FolderWatcher_FileChanged;
                watcher.Deleted += FolderWatcher_FileChanged;
                watcher.Error += FolderWatcher_Error;
                watcher.EnableRaisingEvents = true;
                _folderWatchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Одна недоступная сетевая/удалённая папка не должна ломать слежение за остальными.
                Logger.Warn($"Не удалось включить автообновление папки {folderPath}: {ex.Message}");
            }
        }
    }

    private void StopFolderWatchers()
    {
        _folderRefreshDebounceTimer.Stop();
        _pendingFolderRefreshPaths.Clear();

        foreach (FileSystemWatcher watcher in _folderWatchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= FolderWatcher_FileChanged;
            watcher.Renamed -= FolderWatcher_FileChanged;
            watcher.Deleted -= FolderWatcher_FileChanged;
            watcher.Error -= FolderWatcher_Error;
            watcher.Dispose();
        }
        _folderWatchers.Clear();
    }

    private void FolderWatcher_FileChanged(object sender, FileSystemEventArgs e)
    {
        // При копировании большого файла событие приходит несколько раз, при переносе каталога — только для него: повторный скан
        // корня после debounce найдёт все готовые файлы. Тот же обработчик у Deleted; удаление подпапки refresh не запускает.
        bool isDirectory = Directory.Exists(e.FullPath);
        bool isSupportedAudio = SupportedExtensions.Contains(Path.GetExtension(e.FullPath), StringComparer.OrdinalIgnoreCase);
        if (!isDirectory && !isSupportedAudio) return;
        if (sender is not FileSystemWatcher watcher) return;

        QueueFolderRefresh(watcher.Path);
    }

    private void FolderWatcher_Error(object sender, ErrorEventArgs e)
    {
        if (sender is not FileSystemWatcher watcher) return;
        Logger.Warn($"Буфер отслеживания папки {watcher.Path} переполнен или недоступен: {e.GetException().Message}");
        QueueFolderRefresh(watcher.Path);
    }

    private void QueueAllFolderRefreshes()
    {
        if (!_settings.AutoRefreshPlaylistFolders) return;

        foreach (string folderPath in _folders
                     .Select(folder => folder.SourcePath)
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Cast<string>()
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // FileSystemWatcher не знает о событиях до запуска: один отложенный скан после восстановления закрывает этот случай,
            // с той же дедупликацией AddFolderPathAsync, что и уведомления сеанса.
            QueueFolderRefresh(folderPath);
        }
    }

    private void QueueFolderRefresh(string folderPath)
    {
        if (_isExiting || !_settings.AutoRefreshPlaylistFolders || !_playlistRestoreCompleted || Dispatcher.HasShutdownStarted)
            return;

        try
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (_isExiting || !_settings.AutoRefreshPlaylistFolders || !_playlistRestoreCompleted) return;
                _pendingFolderRefreshPaths.Add(folderPath);
                _folderRefreshDebounceTimer.Stop();
                _folderRefreshDebounceTimer.Start();
            }));
        }
        catch (InvalidOperationException)
        {
            // Dispatcher уже завершает работу приложения; очищать watcher будет OnClosed.
        }
    }

    private void FolderRefreshDebounceTimer_Tick(object? sender, EventArgs e)
        => BackgroundTask.FireAndForget(FolderRefreshDebounceTimer_TickAsync(sender, e), nameof(FolderRefreshDebounceTimer_Tick));

    private async Task FolderRefreshDebounceTimer_TickAsync(object? sender, EventArgs e)
    {
        _folderRefreshDebounceTimer.Stop();
        if (_isExiting || !_settings.AutoRefreshPlaylistFolders || _pendingFolderRefreshPaths.Count == 0)
            return;

        // Новые события, пришедшие пока Directory.EnumerateFiles работает в фоне, будут
        // обработаны следующим debounce-циклом, а не потеряны.
        if (_isFolderRefreshInProgress)
        {
            _folderRefreshDebounceTimer.Start();
            return;
        }

        string[] pathsToRefresh = _pendingFolderRefreshPaths.ToArray();
        _pendingFolderRefreshPaths.Clear();
        _isFolderRefreshInProgress = true;
        try
        {
            foreach (string folderPath in pathsToRefresh)
            {
                if (_isExiting || _lifetimeCts.IsCancellationRequested) break;
                if (!_folders.Any(folder => string.Equals(folder.SourcePath, folderPath, StringComparison.OrdinalIgnoreCase)))
                    continue;

                await AddFolderPathAsync(folderPath);
            }
        }
        catch (OperationCanceledException)
        {
            // Нормальный путь при выходе из приложения.
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось автоматически обновить папку плейлиста", ex);
        }
        finally
        {
            _isFolderRefreshInProgress = false;
            if (!_isExiting && _settings.AutoRefreshPlaylistFolders && _pendingFolderRefreshPaths.Count > 0)
                _folderRefreshDebounceTimer.Start();
        }
    }

    private void AddFolderMenuItem_Click(object sender, RoutedEventArgs e)
        => BackgroundTask.FireAndForget(AddFolderMenuItem_ClickAsync(sender, e), nameof(AddFolderMenuItem_Click));

    private async Task AddFolderMenuItem_ClickAsync(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Выберите папку с музыкой",
            Multiselect = true
        };

        if (dialog.ShowDialog() != true) return;

        // Каждая выбранная папка становится отдельной группой плейлиста, которую
        // потом можно независимо включать/выключать
        bool foundAnything = false;
        foreach (var folderPath in dialog.FolderNames)
            foundAnything = await AddFolderPathAsync(folderPath) || foundAnything;

        if (_isExiting) return;
        if (!foundAnything)
        {
            LocalizedMessageBox.Show(this, "В выбранной папке не найдено поддерживаемых аудиофайлов.",
                "Ничего не найдено", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
    }

    // Сканирует папку рекурсивно и добавляет как группу; false, если аудиофайлов не нашлось (нет доступа или пусто) —
    // по нему решается, показывать ли предупреждение "ничего не найдено".
    private async Task<bool> AddFolderPathAsync(string folderPath)
    {
        try
        {
            var filesInFolder = await Task.Run(() => Directory.EnumerateFiles(folderPath, "*.*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    ReturnSpecialDirectories = false
                })
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList(), _lifetimeCts.Token);

            if (filesInFolder.Count == 0) return false;
            AddFolderGroup(folderPath, filesInFolder);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (PathTooLongException ex)
        {
            Logger.Warn($"Слишком длинный путь при сканировании {folderPath}: {ex.Message}");
            return false;
        }
        catch (IOException ex)
        {
            Logger.Warn($"Не удалось просканировать папку {folderPath}: {ex.Message}");
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            Logger.Warn($"Неподдерживаемый путь папки {folderPath}: {ex.Message}");
            return false;
        }
    }

    // Добавляет группу без дубликата; новые файлы подхватываются автоматически, исчезнувшие остаются как «Файл недоступен»:
    // заменить путь или убрать запись решает пользователь.
    private void AddFolderGroup(string folderPath, List<string> filesInFolder)
    {
        bool wasEmptyBeforeAdd = FlattenAll().Count == 0;

        var existingFolder = _folders.FirstOrDefault(f =>
            f.SourcePath != null && string.Equals(f.SourcePath, folderPath, StringComparison.OrdinalIgnoreCase));

        string? firstNewTrack = null;
        bool createdFolder = false;

        if (existingFolder != null)
        {
            var newOnes = filesInFolder.Where(f => !existingFolder.Tracks.Contains(f)).ToList();

            // Пересобираем UI даже без новых треков: так после удаления/перемещения появляется
            // карточка недоступного файла, а после возврата файла по прежнему пути она исчезает.
            firstNewTrack = newOnes.Count > 0 ? newOnes[0] : null;
            existingFolder.Tracks.AddRange(newOnes);
        }
        else
        {
            string displayName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(displayName)) displayName = folderPath;

            var folder = new PlaylistFolder
            {
                SourcePath = folderPath,
                DisplayName = displayName
            };

            folder.Tracks.AddRange(filesInFolder);
            firstNewTrack = filesInFolder[0];
            _folders.Add(folder);
            createdFolder = true;
        }

        RefreshPlaylistView();
        if (createdFolder)
            StartFolderWatchers();

        // Если до этого ничего не играло — сразу запускаем первый добавленный трек
        if (wasEmptyBeforeAdd && firstNewTrack != null)
        {
            LoadAndPlay(firstNewTrack);
        }
    }

    // Отдельно выбранные файлы (не через папку) собираются в одну общую группу "Отдельные файлы"
    private void AddLooseFiles(IEnumerable<string> filePaths)
    {
        var newTracks = filePaths.ToList();
        if (newTracks.Count == 0) return;

        bool wasEmptyBeforeAdd = FlattenAll().Count == 0;

        var allExisting = FlattenAll();
        var actuallyNew = newTracks.Where(f => !allExisting.Contains(f)).ToList();
        if (actuallyNew.Count == 0)
        {
            // Всё уже в плейлисте (например, файл лежит в добавленной папке): дубликат не создаём, но показываем, где трек,
            // и не заводим пустую группу «Отдельные файлы».
            RevealExistingTrack(newTracks[0]);
            return;
        }

        var looseFolder = _folders.FirstOrDefault(f => f.IsLooseFilesBucket);
        if (looseFolder == null)
        {
            looseFolder = new PlaylistFolder
            {
                SourcePath = null,
                DisplayName = LocalizationService.Get(LocalizationKey.PlaylistLooseFiles),
                IsLooseFilesBucket = true
            };
            _folders.Add(looseFolder);
        }

        looseFolder.Tracks.AddRange(actuallyNew);
        RefreshPlaylistView();

        if (wasEmptyBeforeAdd)
        {
            LoadAndPlay(actuallyNew[0]);
        }
    }

    // Раскрывает папку с уже имеющимся треком и подсвечивает его: так видно, что перетащенный/выбранный файл уже в плейлисте.
    private void RevealExistingTrack(string filePath)
    {
        var folder = _folders.FirstOrDefault(f => f.Tracks.Contains(filePath));
        if (folder == null) return;

        if (_isFavoritesView)
            SetFavoritesViewActive(false);
        if (!folder.IsExpanded)
            folder.IsExpanded = true;
        RefreshPlaylistView();
        Dispatcher.BeginInvoke(new Action(() => HighlightAndScrollToTrack(folder, filePath)),
            DispatcherPriority.Loaded);
    }

    // Нормализация имён — только вручную (настройки для всех файлов или меню для одного трека): сначала предпросмотр по тегам,
    // затем явное подтверждение. Текущий трек исключён: AudioFileReader держит его дескриптор, и Windows не даст переместить файл.
    public Task<FileNameNormalizer.RenameResult?> NormalizePlaylistFileNamesAsync(System.Windows.Window dialogOwner) =>
        NormalizeTrackFileNamesAsync(_folders.SelectMany(folder => folder.Tracks), dialogOwner);

    private async Task<FileNameNormalizer.RenameResult?> NormalizeTrackFileNamesAsync(
        IEnumerable<string> requestedPaths, System.Windows.Window dialogOwner)
    {
        if (_isExiting) return null;

        var sourcePaths = requestedPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (sourcePaths.Count == 0)
        {
            LocalizedMessageBox.Show(dialogOwner, "Нет доступных файлов для нормализации.",
                "Нормализация имён", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return null;
        }

        string? currentPath = _currentTrackPath;
        IReadOnlyList<FileNameNormalizer.RenamePreview> preview;
        try
        {
            preview = await Task.Run(() => FileNameNormalizer.BuildPreview(
                sourcePaths,
                _settings.FileNameNormalizationTemplate,
                string.IsNullOrWhiteSpace(currentPath) ? null : new[] { currentPath }), _lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        if (_isExiting) return null;

        var candidates = preview.Where(item => item.CanRename).ToList();
        if (candidates.Count == 0)
        {
            string reason = preview.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.SkipReason))?.SkipReason
                            ?? "нет файлов, подходящих для переименования";
            string message = sourcePaths.Count == 1 && reason == "уже соответствует шаблону"
                ? "Имя файла уже соответствует выбранному шаблону. Переименование не требуется."
                : $"Ни один файл не будет переименован: {reason}.";
            LocalizedMessageBox.Show(dialogOwner, message,
                "Нормализация имён", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);

            // Выбранный текущий трек мог не требовать File.Move, но всё равно нуждается в
            // обновлённом fallback исполнителя/названия из имени файла.
            if (_currentTrackPath is not null && sourcePaths.Any(path =>
                    string.Equals(path, _currentTrackPath, StringComparison.OrdinalIgnoreCase)))
            {
                RefreshCurrentTrackMetadataFromFileName();
            }

            return new FileNameNormalizer.RenameResult(0, preview.Count, 0,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), Array.Empty<string>());
        }

        string examples = string.Join(Environment.NewLine, candidates.Take(5).Select(item =>
            $"• {item.SourceFileName} → {item.TargetFileName}"));
        int skipped = preview.Count - candidates.Count;
        string skippedText = skipped > 0 ? $"\n\nПропущено: {skipped} (уже соответствует шаблону, конфликтует или играет сейчас)." : string.Empty;

        var confirmation = LocalizedMessageBox.Show(dialogOwner,
            $"Переименовать файлов: {candidates.Count}.\n\n{examples}{skippedText}\n\n" +
            "Файлы останутся в исходных папках; изменятся только имена. Продолжить?",
            "Нормализация имён", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (confirmation != System.Windows.MessageBoxResult.Yes) return null;

        FileNameNormalizer.RenameResult result;
        try
        {
            result = await Task.Run(() => FileNameNormalizer.Execute(preview), _lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        if (_isExiting) return null;

        if (result.RenamedCount > 0)
        {
            ApplyNormalizedTrackPaths(result.RenamedPaths);
            PersistPlaybackAndPlaylistState();
        }

        // Текущий трек исключён из File.Move, но его подписи не должны хранить устаревший fallback вида «имя папки»:
        // если путь был в запросе, пересчитываем UI из тегов и имени файла, даже при результате «уже соответствует шаблону».
        if (_currentTrackPath is not null && sourcePaths.Any(path =>
                string.Equals(path, _currentTrackPath, StringComparison.OrdinalIgnoreCase)))
        {
            RefreshCurrentTrackMetadataFromFileName();
        }

        return result;
    }

    private void RefreshCurrentTrackMetadataFromFileName()
    {
        if (string.IsNullOrWhiteSpace(_currentTrackPath)) return;

        var metadata = FileNameNormalizer.ResolveArtistAndTitle(
            _currentTrackPath, _currentTrackTaggedArtist, _currentTrackTaggedTitle, "—");
        SetTrackInfoText(metadata.Title, metadata.Artist);
        _integrations.NowPlaying?.UpdateTrackInfo(metadata.Title, metadata.Artist);
        RaiseTrackInfoChanged(metadata.Title, metadata.Artist, CurrentArtBrush);
    }

    private void ApplyNormalizedTrackPaths(IReadOnlyDictionary<string, string> renamedPaths)
    {
        if (renamedPaths.Count == 0) return;

        string Remap(string path) => renamedPaths.TryGetValue(path, out string? renamed) ? renamed : path;

        foreach (var folder in _folders)
        {
            for (int index = 0; index < folder.Tracks.Count; index++)
                folder.Tracks[index] = Remap(folder.Tracks[index]);
        }

        // В нормальном сценарии текущий трек исключён из плана, но обновление оставляем как
        // защиту от будущих способов запуска нормализации или от момента между переключениями.
        if (_currentTrackPath != null)
            _currentTrackPath = Remap(_currentTrackPath);
        if (_settings.LastTrackPath != null)
            _settings.LastTrackPath = Remap(_settings.LastTrackPath);

        var favoriteOrder = FavoritesManager.GetOrder().Select(Remap).ToList();
        var pinnedFavorites = FavoritesManager.GetPinnedPaths().Select(Remap).ToList();
        FavoritesManager.Initialize(favoriteOrder, pinnedFavorites);
        FavoritesChangeNotifier.Instance.Bump();

        var remappedCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, count) in PlayCountManager.GetAll())
        {
            string remapped = Remap(path);
            remappedCounts[remapped] = remappedCounts.TryGetValue(remapped, out int existing)
                ? existing + count
                : count;
        }
        PlayCountManager.Initialize(remappedCounts);
        PlayCountChangeNotifier.Instance.Bump();

        // Очередь и история шаффла содержат абсолютные пути. Перезапускаем их вместо частичного
        // исправления, чтобы кнопки «следующий» и «предыдущий» никогда не ссылались на старое имя.
        ResetShuffleState();
        RefreshPlaylistView();
        if (_isFavoritesView) RefreshFavoritesTrackList();
    }
}
