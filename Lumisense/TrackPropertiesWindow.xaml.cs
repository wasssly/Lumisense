using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Controls;

namespace AudioPlayer;

/// <summary>
/// Своё окно "Свойства" для трека — показывает те же данные, что и системный диалог
/// Windows (имя, папка, размер, даты), плюс аудио-параметры (длительность, битрейт,
/// частота дискретизации, каналы) и обложку/тег, но оформлено как часть самого плеера
/// (Fluent/Mica, тот же стиль, что и у остальных окон), а не системный shell-диалог.
/// </summary>
public partial class TrackPropertiesWindow : FluentWindow
{
    public TrackPropertiesWindow(string filePath)
    {
        InitializeComponent();
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
            using var tagFile = TagLib.File.Create(filePath);

            if (!string.IsNullOrWhiteSpace(tagFile.Tag.Title)) title = tagFile.Tag.Title;
            artist = tagFile.Tag.FirstPerformer ?? "";

            var duration = tagFile.Properties.Duration;
            durationText = duration.TotalHours >= 1
                ? duration.ToString(@"h\:mm\:ss")
                : duration.ToString(@"m\:ss");

            if (tagFile.Properties.AudioBitrate > 0)
                bitrateText = $"{tagFile.Properties.AudioBitrate} кбит/с";

            if (tagFile.Properties.AudioSampleRate > 0)
                sampleRateText = $"{tagFile.Properties.AudioSampleRate} Гц";

            if (tagFile.Properties.AudioChannels > 0)
                channelsText = tagFile.Properties.AudioChannels switch
                {
                    1 => "Моно",
                    2 => "Стерео",
                    var n => $"{n}"
                };

            if (!string.IsNullOrWhiteSpace(tagFile.Properties.Description))
                formatText = tagFile.Properties.Description;

            var pictures = tagFile.Tag.Pictures;
            if (pictures.Length > 0)
            {
                using var ms = new MemoryStream(pictures[0].Data.Data);
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
