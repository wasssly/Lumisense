using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace AudioPlayer;

/// <summary>
/// Окно редактирования тегов (ID3/Vorbis/MP4 — какой формат поддерживает сам файл, через
/// TagLib#) — название, исполнитель, альбом, год, номер трека, жанр, комментарий.
/// </summary>
public partial class TrackTagsWindow : FluentWindow
{
    private readonly string _filePath;

    // Новая обложка, выбранная пользователем в этом окне (ещё не записана в файл — это
    // происходит только при нажатии "Сохранить"). null означает "обложка не менялась",
    // если только _coverArtChanged не true — тогда null означает "обложку удалили".
    private byte[]? _pendingCoverBytes;
    private string? _pendingCoverMimeType;
    private bool _coverArtChanged;

    /// <summary>true, если пользователь нажал "Сохранить" и запись в файл прошла успешно —
    /// по этому флагу вызывающая сторона (MainWindow) решает, нужно ли обновить название/
    /// исполнителя в самом плеере, если редактировался именно сейчас играющий трек.</summary>
    public bool Saved { get; private set; }

    public TrackTagsWindow(string filePath)
    {
        InitializeComponent();

        _filePath = filePath;
        FileNameHeader.Text = Path.GetFileName(filePath);

        try
        {
            using var tagFile = TagLib.File.Create(filePath);
            var tag = tagFile.Tag;

            TitleBox.Text = tag.Title ?? "";
            ArtistBox.Text = tag.FirstPerformer ?? "";
            AlbumBox.Text = tag.Album ?? "";
            GenreBox.Text = tag.FirstGenre ?? "";
            CommentBox.Text = tag.Comment ?? "";
            YearBox.Text = tag.Year > 0 ? tag.Year.ToString() : "";
            TrackNumberBox.Text = tag.Track > 0 ? tag.Track.ToString() : "";

            if (tag.Pictures.Length > 0)
            {
                try
                {
                    var bitmap = BitmapFromBytes(tag.Pictures[0].Data.Data);
                    ApplyCoverPreview(bitmap);
                }
                catch
                {
                    // Битые встроенные данные обложки — оставляем плейсхолдер, остальные
                    // теги при этом уже прочитаны и доступны для редактирования как обычно
                }
            }
        }
        catch (IOException)
        {
            // Самая частая причина — сейчас именно этот файл играет в плеере и занят другим
            // процессом на чтение/запись. Поля остаются пустыми (реальные значения тегов
            // прочитать не удалось), но не блокируем окно полностью — можно остановить
            // воспроизведение и открыть заново, либо всё равно вписать теги руками и сохранить.
            ShowError("Не удалось прочитать теги — возможно, файл сейчас воспроизводится. " +
                       "Остановите воспроизведение этого трека и откройте окно заново, чтобы увидеть текущие значения.");
        }
        catch (Exception ex)
        {
            ShowError($"Не удалось прочитать теги: {ex.Message}");
        }
    }

    // Год/номер трека — только цифры, чтобы не пришлось разбирать и отклонять произвольный
    // текст уже при сохранении
    private void NumericBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = !Regex.IsMatch(e.Text, "^[0-9]+$");

    // ---------- Обложка ----------

    private void CoverArtBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => PickNewCover();

    private void ChangeCoverButton_Click(object sender, RoutedEventArgs e) => PickNewCover();

    // Открывает окно поиска обложки в интернете (по исполнителю и названию — оба поля берутся
    // прямо из текущих значений формы, включая то, что пользователь мог уже успеть поправить
    // здесь же). Найденная обложка применяется точно так же, как если бы её выбрали с диска —
    // запись в файл всё ещё происходит только по кнопке "Сохранить".
    private void FindCoverOnlineButton_Click(object sender, RoutedEventArgs e)
    {
        var searchWindow = new CoverArtSearchWindow(ArtistBox.Text, TitleBox.Text) { Owner = this };
        if (searchWindow.ShowDialog() != true) return;
        if (searchWindow.SelectedImageBytes is not { } bytes) return;

        try
        {
            var bitmap = BitmapFromBytes(bytes);

            _pendingCoverBytes = bytes;
            _pendingCoverMimeType = searchWindow.SelectedImageMimeType ?? "image/jpeg";
            _coverArtChanged = true;

            ApplyCoverPreview(bitmap);
        }
        catch (Exception ex)
        {
            ShowError($"Не удалось применить найденную обложку: {ex.Message}");
        }
    }

    private void PickNewCover()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите обложку",
            Filter = "Изображения (*.jpg;*.jpeg;*.png;*.bmp;*.gif)|*.jpg;*.jpeg;*.png;*.bmp;*.gif|Все файлы (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var bytes = File.ReadAllBytes(dialog.FileName);
            var bitmap = BitmapFromBytes(bytes);

            _pendingCoverBytes = bytes;
            _pendingCoverMimeType = GetMimeType(dialog.FileName);
            _coverArtChanged = true;

            ApplyCoverPreview(bitmap);
        }
        catch (Exception ex)
        {
            ShowError($"Не удалось загрузить изображение: {ex.Message}");
        }
    }

    private void RemoveCoverButton_Click(object sender, RoutedEventArgs e)
    {
        _pendingCoverBytes = null;
        _pendingCoverMimeType = null;
        _coverArtChanged = true;
        ResetCoverPreview();
    }

    private static BitmapImage BitmapFromBytes(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = ms;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static string GetMimeType(string filePath) => Path.GetExtension(filePath).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".bmp" => "image/bmp",
        ".gif" => "image/gif",
        _ => "image/jpeg",
    };

    private void ApplyCoverPreview(BitmapImage bitmap)
    {
        CoverArtBorder.Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
        CoverArtPlaceholderIcon.Visibility = Visibility.Collapsed;
        RemoveCoverButton.Visibility = Visibility.Visible;
    }

    private void ResetCoverPreview()
    {
        CoverArtBorder.Background = (Brush)FindResource("ControlFillColorSecondaryBrush");
        CoverArtPlaceholderIcon.Visibility = Visibility.Visible;
        RemoveCoverButton.Visibility = Visibility.Collapsed;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;

        try
        {
            using var tagFile = TagLib.File.Create(_filePath);
            var tag = tagFile.Tag;

            tag.Title = string.IsNullOrWhiteSpace(TitleBox.Text) ? null : TitleBox.Text.Trim();
            tag.Performers = string.IsNullOrWhiteSpace(ArtistBox.Text) ? [] : [ArtistBox.Text.Trim()];
            tag.Album = string.IsNullOrWhiteSpace(AlbumBox.Text) ? null : AlbumBox.Text.Trim();
            tag.Genres = string.IsNullOrWhiteSpace(GenreBox.Text) ? [] : [GenreBox.Text.Trim()];
            tag.Comment = string.IsNullOrWhiteSpace(CommentBox.Text) ? null : CommentBox.Text.Trim();
            tag.Year = uint.TryParse(YearBox.Text, out var year) ? year : 0;
            tag.Track = uint.TryParse(TrackNumberBox.Text, out var trackNumber) ? trackNumber : 0;

            if (_coverArtChanged)
            {
                if (_pendingCoverBytes is { } coverBytes)
                {
                    var picture = new TagLib.Picture(coverBytes)
                    {
                        Type = TagLib.PictureType.FrontCover,
                        MimeType = _pendingCoverMimeType ?? "image/jpeg",
                        Description = "Cover"
                    };
                    tag.Pictures = new TagLib.IPicture[] { picture };
                }
                else
                {
                    tag.Pictures = Array.Empty<TagLib.IPicture>();
                }
            }

            tagFile.Save();
            Saved = true;
            Close();
        }
        catch (Exception ex)
        {
            // Файл может быть занят, доступен только для чтения, без поддержки записи тегов
            // для этого формата и т.п. — сообщаем прямо в окне, а не роняем весь плеер
            ShowError($"Не удалось сохранить: {ex.Message}");
        }
    }

    // Копирование названия трека (имени файла) из шапки окна. Иконка на кнопке на секунду
    // сменяется на галочку — простой способ подтвердить копирование без Toast/MessageBox.
    private void CopyFileNameButton_Click(object sender, RoutedEventArgs e)
    {
        // В буфер копируем название без расширения (.mp3 и т.п.) — в отличие от заголовка
        // выше, где расширение оставлено для наглядности, какой именно файл открыт.
        System.Windows.Clipboard.SetText(Path.GetFileNameWithoutExtension(_filePath));

        CopyFileNameIcon.Icon = "IconCheckmark";
        var revertTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.1) };
        revertTimer.Tick += (_, _) =>
        {
            CopyFileNameIcon.Icon = "IconCopy";
            revertTimer.Stop();
        };
        revertTimer.Start();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
