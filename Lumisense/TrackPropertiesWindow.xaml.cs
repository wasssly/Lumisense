using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Controls;

namespace Lumisense;

// Своё окно "Свойства" для трека вместо системного shell-диалога — те же данные
// (имя, папка, размер, даты) плюс аудио-параметры и обложка, в стиле остальных окон плеера
public partial class TrackPropertiesWindow : FluentWindow
{
    public TrackPropertiesWindow(string filePath, AppSettings settings)
    {
        InitializeComponent();
        AccessibilityPreferences.ApplyToWindow(this, settings);
        Load(filePath);
    }

    private void Load(string filePath)
    {
        var fileInfo = new FileInfo(filePath);

        string title = fileInfo.Name;
        string artist = "";
        string durationText = "—";
        string bitrateText = "—";
        string sampleRateText = "—";
        string channelsText = "—";
        string formatText = fileInfo.Extension.TrimStart('.').ToUpperInvariant();

        try
        {
            var tagFile = new ATL.Track(filePath);

            if (!string.IsNullOrWhiteSpace(tagFile.Title)) title = tagFile.Title;
            artist = tagFile.Artist ?? "";

            var duration = TimeSpan.FromSeconds(tagFile.Duration);
            durationText = duration.TotalHours >= 1
                ? duration.ToString(@"h\:mm\:ss")
                : duration.ToString(@"m\:ss");

            if (tagFile.Bitrate > 0)
                bitrateText = $"{tagFile.Bitrate} кбит/с";

            if (tagFile.SampleRate > 0)
                sampleRateText = $"{tagFile.SampleRate} Гц";

            if (tagFile.ChannelsArrangement?.NbChannels > 0)
                channelsText = tagFile.ChannelsArrangement.NbChannels switch
                {
                    1 => "Моно",
                    2 => "Стерео",
                    var n => $"{n}"
                };

            var pictures = tagFile.EmbeddedPictures;
            if (pictures.Count > 0)
            {
                using var ms = new MemoryStream(pictures[0].PictureData);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
                bitmap.Freeze();

                ArtBorder.Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
                ArtPlaceholder.Visibility = Visibility.Collapsed;
            }
        }
        catch
        {
            // Файл повреждён, формат не распознан библиотекой и т.п. — просто показываем
            // то немногое, что уже знаем из System.IO, без падения всего окна
        }

        TitleText.Text = title;
        ArtistText.Text = artist;
        ArtistText.Visibility = string.IsNullOrWhiteSpace(artist) ? Visibility.Collapsed : Visibility.Visible;

        FileNameValue.Text = fileInfo.Name;
        FolderValue.Text = fileInfo.DirectoryName ?? "";
        SizeValue.Text = FormatFileSize(fileInfo.Length);
        FormatValue.Text = formatText;

        int playCount = PlayCountManager.GetCount(filePath);
        PlayCountValue.Text = playCount == 0 ? "ещё не проигрывался" : playCount.ToString();

        DurationValue.Text = durationText;
        BitrateValue.Text = bitrateText;
        SampleRateValue.Text = sampleRateText;
        ChannelsValue.Text = channelsText;

        CreatedValue.Text = fileInfo.CreationTime.ToString("d MMMM yyyy, HH:mm");
        ModifiedValue.Text = fileInfo.LastWriteTime.ToString("d MMMM yyyy, HH:mm");
    }

    private static string FormatFileSize(long bytes)
    {
        const double mb = 1024 * 1024;
        const double kb = 1024;

        if (bytes >= mb) return $"{bytes / mb:0.0} МБ";
        if (bytes >= kb) return $"{bytes / kb:0.0} КБ";
        return $"{bytes} байт";
    }
}
