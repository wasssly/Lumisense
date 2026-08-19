using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AudioPlayer;

// Нормализует ИМЕНА уже добавленных аудиофайлов, не меняя папку, расширение или содержимое
// файла. Процесс намеренно двухэтапный: сначала BuildPreview строит полностью проверяемый план,
// затем Execute переименовывает только одобренные и всё ещё валидные элементы плана.
public static class FileNameNormalizer
{
    public const string DefaultTemplate = "{Artist} - {Title}{Extension}";

    private static readonly string[] SupportedTokens = { "Artist", "Title", "Album", "Track", "Extension" };
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public sealed record RenamePreview(string SourcePath, string? TargetPath, string? SkipReason)
    {
        public bool CanRename => TargetPath != null && SkipReason == null;
        public string SourceFileName => Path.GetFileName(SourcePath);
        public string TargetFileName => TargetPath == null ? string.Empty : Path.GetFileName(TargetPath);
    }

    public sealed record RenameResult(
        int RenamedCount,
        int SkippedCount,
        int FailedCount,
        IReadOnlyDictionary<string, string> RenamedPaths,
        IReadOnlyList<string> Errors);

    public static IReadOnlyList<RenamePreview> BuildPreview(
        IEnumerable<string> sourcePaths,
        string? template,
        IEnumerable<string>? protectedPaths = null)
    {
        string normalizedTemplate = NormalizeTemplate(template);
        var protectedLookup = new HashSet<string>(protectedPaths ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var previews = new List<RenamePreview>();

        foreach (string sourcePath in sourcePaths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (protectedLookup.Contains(sourcePath))
            {
                previews.Add(new RenamePreview(sourcePath, null, "сейчас воспроизводится"));
                continue;
            }

            if (!File.Exists(sourcePath))
            {
                previews.Add(new RenamePreview(sourcePath, null, "файл не найден"));
                continue;
            }

            try
            {
                string targetPath = BuildTargetPath(sourcePath, normalizedTemplate);
                if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
                    previews.Add(new RenamePreview(sourcePath, null, "уже соответствует шаблону"));
                else if (File.Exists(targetPath))
                    previews.Add(new RenamePreview(sourcePath, null, "целевое имя уже занято"));
                else
                    previews.Add(new RenamePreview(sourcePath, targetPath, null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                previews.Add(new RenamePreview(sourcePath, null, ShortReason(ex.Message)));
            }
        }

        // Два разных исходника могут дать одинаковый результат по одному шаблону. Такие пары
        // не пытаемся переименовать в произвольном порядке: оба остаются без изменений.
        foreach (var duplicate in previews
                     .Where(item => item.CanRename)
                     .GroupBy(item => item.TargetPath!, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            foreach (var item in duplicate)
            {
                int index = previews.IndexOf(item);
                previews[index] = item with { TargetPath = null, SkipReason = "дубликат целевого имени в плане" };
            }
        }

        return previews;
    }

    public static RenameResult Execute(IEnumerable<RenamePreview> preview)
    {
        var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        int skipped = 0;
        int failed = 0;

        foreach (var item in preview)
        {
            if (!item.CanRename)
            {
                skipped++;
                continue;
            }

            string targetPath = item.TargetPath!;
            try
            {
                // Состояние могло измениться между предпросмотром и подтверждением пользователя.
                if (!File.Exists(item.SourcePath) || File.Exists(targetPath))
                {
                    failed++;
                    errors.Add($"{item.SourceFileName}: файл или целевое имя изменились до переименования.");
                    continue;
                }

                File.Move(item.SourcePath, targetPath, overwrite: false);
                renamed[item.SourcePath] = targetPath;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                failed++;
                errors.Add($"{item.SourceFileName}: {ShortReason(ex.Message)}");
            }
        }

        return new RenameResult(renamed.Count, skipped, failed, renamed, errors);
    }

    public static string NormalizeTemplate(string? template)
    {
        string value = string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template.Trim();
        return value.Length > 180 ? value[..180] : value;
    }

    private static string BuildTargetPath(string sourcePath, string template)
    {
        var values = ReadTagValues(sourcePath);
        string rendered = RenderTemplate(template, values);
        string extension = Path.GetExtension(sourcePath);

        if (!rendered.Contains("{Extension}", StringComparison.OrdinalIgnoreCase) &&
            !rendered.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            rendered += extension;
        }

        string fileName = SanitizeFileName(rendered);
        if (string.IsNullOrWhiteSpace(fileName) || fileName == ".")
            throw new ArgumentException("шаблон дал пустое имя");
        if (fileName.Length > 240)
            throw new PathTooLongException("имя после нормализации длиннее 240 символов");

        string stem = Path.GetFileNameWithoutExtension(fileName);
        if (ReservedWindowsNames.Contains(stem))
            throw new ArgumentException("получилось служебное имя Windows");

        string? directory = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("не удалось определить папку файла");

        return Path.Combine(directory, fileName);
    }

    private static Dictionary<string, string> ReadTagValues(string sourcePath)
    {
        string artist = string.Empty;
        string title = string.Empty;
        string album = string.Empty;
        uint track = 0;

        try
        {
            using var tagFile = TagLib.File.Create(sourcePath);
            artist = FirstNonEmpty(tagFile.Tag.FirstPerformer, tagFile.Tag.FirstAlbumArtist);
            title = tagFile.Tag.Title ?? string.Empty;
            album = tagFile.Tag.Album ?? string.Empty;
            track = tagFile.Tag.Track;
        }
        catch (Exception)
        {
            // Не останавливаем весь план из-за одного повреждённого или неподдерживаемого
            // набора тегов: в этом случае используем безопасный fallback из имени файла.
        }

        var metadata = ResolveArtistAndTitle(sourcePath, artist, title, "Неизвестный исполнитель");
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Artist"] = metadata.Artist,
            ["Title"] = metadata.Title,
            ["Album"] = string.IsNullOrWhiteSpace(album) ? "Неизвестный альбом" : album.Trim(),
            ["Track"] = track > 0 ? track.ToString("D2") : string.Empty,
            ["Extension"] = Path.GetExtension(sourcePath)
        };
    }

    // Единое правило для нормализации и экрана плеера: корректные теги имеют приоритет, но
    // пустой Artist или склеенный Title вида «Исполнитель - Название» дополняются из имени.
    // Пустая строка unknownArtistFallback нужна MainWindow: он сохранит привычное «—» только
    // для файлов, где в имени действительно нечего разобрать, а не подставит имя папки.
    public static (string Artist, string Title) ResolveArtistAndTitle(
        string filePath, string? taggedArtist, string? taggedTitle, string unknownArtistFallback)
    {
        string fallbackFileName = Path.GetFileNameWithoutExtension(filePath);
        (string? artistFromFileName, string? titleFromFileName) = SplitArtistAndTitleFromFileName(fallbackFileName);
        (string? artistFromTitleTag, string? titleFromTitleTag) = SplitArtistAndTitleFromFileName(taggedTitle ?? string.Empty);

        if (string.IsNullOrWhiteSpace(taggedArtist) && artistFromTitleTag is not null)
            return (artistFromTitleTag, titleFromTitleTag!);

        string artist = !string.IsNullOrWhiteSpace(taggedArtist)
            ? taggedArtist.Trim()
            : artistFromFileName ?? unknownArtistFallback;
        string title = !string.IsNullOrWhiteSpace(taggedTitle)
            ? taggedTitle.Trim()
            : titleFromFileName ?? fallbackFileName;
        return (artist, title);
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static (string? Artist, string? Title) SplitArtistAndTitleFromFileName(string fileName)
    {
        string candidate = Regex.Replace(fileName ?? string.Empty, @"^\s*\d{1,3}\s*[._-]\s*", string.Empty).Trim();
        foreach (string separator in new[] { " - ", " – ", " — " })
        {
            int separatorIndex = candidate.IndexOf(separator, StringComparison.Ordinal);
            if (separatorIndex <= 0 || separatorIndex + separator.Length >= candidate.Length) continue;

            string artist = candidate[..separatorIndex].Trim();
            string title = candidate[(separatorIndex + separator.Length)..].Trim();
            if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
                return (artist, title);
        }

        return (null, null);
    }

    private static string RenderTemplate(string template, IReadOnlyDictionary<string, string> values)
    {
        string result = template;
        foreach (string token in SupportedTokens)
            result = Regex.Replace(result, $"\\{{{token}\\}}", values[token], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Неподдерживаемые токены не должны превращаться в буквальную часть имени: это обычно
        // опечатка, которая иначе даст неприятный результат для всей медиатеки.
        if (Regex.IsMatch(result, "\\{[^{}]+\\}"))
            throw new ArgumentException("шаблон содержит неизвестный токен");

        return result;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);

        foreach (char character in value)
            builder.Append(invalid.Contains(character) ? '_' : character);

        string result = Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
        return result.TrimEnd('.', ' ');
    }

    private static string ShortReason(string message)
    {
        string value = Regex.Replace(message ?? string.Empty, @"\s+", " ").Trim();
        return string.IsNullOrEmpty(value) ? "не удалось обработать файл" : value;
    }
}
