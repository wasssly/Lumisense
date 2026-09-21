using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace Lumisense;

// Окно редактирования тегов (ID3/Vorbis/MP4 — через ATL.NET) — название, исполнитель,
// альбом, год, номер трека, жанр, комментарий
public partial class TrackTagsWindow : FluentWindow
{
    private readonly string _filePath;
    private readonly MainWindow? _owner;

    // Новая обложка, выбранная в этом окне (в файл ещё не записана — только по "Сохранить").
    // null — обложка не менялась, если только _coverArtChanged не true — тогда null значит "удалили"
    private byte[]? _pendingCoverBytes;
    private bool _coverArtChanged;

    // true после успешного сохранения — по этому флагу MainWindow решает, обновлять ли
    // название/исполнителя в плеере, если редактировался именно текущий трек
    public bool Saved { get; private set; }

    public TrackTagsWindow(string filePath, MainWindow? owner = null)
    {
        InitializeComponent();

        _filePath = filePath;
        _owner = owner;
        if (_owner != null)
            AccessibilityPreferences.ApplyToWindow(this, _owner.Settings);
        FileNameHeader.Text = Path.GetFileName(filePath);

        try
        {
            var tagFile = new ATL.Track(filePath);

            TitleBox.Text = tagFile.Title ?? "";
            ArtistBox.Text = tagFile.Artist ?? "";
            AlbumBox.Text = tagFile.Album ?? "";
            GenreBox.Text = tagFile.Genre ?? "";
            CommentBox.Text = tagFile.Comment ?? "";
            YearBox.Text = tagFile.Year > 0 ? tagFile.Year.ToString() : "";
            TrackNumberBox.Text = tagFile.TrackNumber > 0 ? tagFile.TrackNumber.ToString() : "";

            if (tagFile.EmbeddedPictures.Count > 0)
            {
                try
                {
                    var bitmap = BitmapFromBytes(tagFile.EmbeddedPictures[0].PictureData);
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
            // Обычно файл играет сейчас и занят другим процессом. Поля остаются пустыми, но окно не блокируем:
            // можно остановить воспроизведение и открыть заново или вписать теги руками.
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

    private void CoverArtBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => PickNewCover();

    private void ChangeCoverButton_Click(object sender, RoutedEventArgs e) => PickNewCover();

    // Ищет обложку по текущим значениям исполнителя и названия из формы; найденная применяется как выбранная
    // с диска — запись в файл только по кнопке "Сохранить".
    private void FindCoverOnlineButton_Click(object sender, RoutedEventArgs e)
    {
        var searchWindow = new CoverArtSearchWindow(ArtistBox.Text, TitleBox.Text, _owner?.Settings) { Owner = this };
        if (searchWindow.ShowDialog() != true) return;
        if (searchWindow.SelectedImageBytes is not { } bytes) return;

        try
        {
            var bitmap = BitmapFromBytes(bytes);

            _pendingCoverBytes = bytes;
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

        // Играющий файл держит NAudio, и ATL может не суметь его переписать (блок обложки мог вырасти). Хендл плеера
        // освобождается на время записи, воспроизведение возобновляется с той же позиции при любом исходе.
        var resumeInfo = _owner?.ReleaseFileForExternalWrite(_filePath);

        try
        {
            var tagFile = new ATL.Track(_filePath);

            tagFile.Title = string.IsNullOrWhiteSpace(TitleBox.Text) ? "" : TitleBox.Text.Trim();
            tagFile.Artist = string.IsNullOrWhiteSpace(ArtistBox.Text) ? "" : ArtistBox.Text.Trim();
            tagFile.Album = string.IsNullOrWhiteSpace(AlbumBox.Text) ? "" : AlbumBox.Text.Trim();
            tagFile.Genre = string.IsNullOrWhiteSpace(GenreBox.Text) ? "" : GenreBox.Text.Trim();
            tagFile.Comment = string.IsNullOrWhiteSpace(CommentBox.Text) ? "" : CommentBox.Text.Trim();
            tagFile.Year = int.TryParse(YearBox.Text, out var year) ? year : 0;
            tagFile.TrackNumber = int.TryParse(TrackNumberBox.Text, out var trackNumber) ? trackNumber : 0;

            if (_coverArtChanged)
            {
                tagFile.EmbeddedPictures.Clear();
                if (_pendingCoverBytes is { } coverBytes)
                {
                    var picture = ATL.PictureInfo.fromBinaryData(coverBytes, ATL.PictureInfo.PIC_TYPE.Front);
                    tagFile.EmbeddedPictures.Add(picture);
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
        finally
        {
            if (resumeInfo != null) _owner!.ResumeAfterExternalWrite(_filePath, resumeInfo.Value);
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
