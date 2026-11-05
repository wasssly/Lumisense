using System.Text.RegularExpressions;

namespace Lumisense;

// Общая очистка текстов песен из онлайн-источников: метки времени LRC в одном месте для всех источников и отсев служебных строк
// (авторы, композитор, продюсер), которыми NetEase, QQ Music и другие начинают и заканчивают текст.
internal static class LyricsTextHygiene
{
    // [mm:ss], [mm:ss.xx], а также метка со служебным числом, которое дописывают NetEase и др.: [00:00.00-1], [00:12.34,500].
    public static readonly Regex Timestamp = new(
        @"\[(?<minutes>\d{1,3}):(?<seconds>\d{2})(?:[\.:](?<fraction>\d{1,3}))?(?:[-,]-?\d+)?\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // «Автор : Имя» и «作曲 : Имя»: ключ из списка, затем двоеточие. Строки без двоеточия (обычные слова песни) не затрагиваются.
    private static readonly Regex CreditLine = new(
        @"^\s*(?:作词|作詞|作曲|词|詞|曲|编曲|編曲|制作人|製作人|制作|製作|监制|監製|出品|混音|母带|母帶|录音|錄音|和声|和聲|演唱|歌手|原唱|翻唱|吉他|贝斯|鼓|弦乐|发行|OP|SP"
        + @"|lyrics?(?:\s+by)?|words?(?:\s+by)?|music(?:\s+by)?|composer|composed\s+by|lyricist|written\s+by|arrange(?:r|d)?(?:\s+by)?"
        + @"|producer|produced\s+by|mix(?:ed)?\s+by|master(?:ed)?(?:\s+by)?|recorded\s+by|engineer)\s*[:：]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // У NetEase бывают служебные строки в виде JSON: {"t":0,"c":[{"tx":"作曲: "}...]}.
    private static readonly Regex JsonCreditLine = new(@"^\s*\{\s*""t""\s*:", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsCreditLine(string? text) =>
        !string.IsNullOrWhiteSpace(text) && (CreditLine.IsMatch(text) || JsonCreditLine.IsMatch(text));

    // Обычный текст без меток времени и служебных строк. Пустые строки внутри (разделители куплетов) сохраняются.
    public static string CleanPlain(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        IEnumerable<string> lines = text.Replace("\r\n", "\n").Split('\n')
            .Select(line => Timestamp.Replace(line, string.Empty).Trim())
            .Where(line => !IsCreditLine(line));
        return string.Join(Environment.NewLine, lines).Trim();
    }

    // Есть ли в тексте хоть одна настоящая строка песни. Результат из одних служебных строк (на NetEase так бывает у треков
    // без текста) настоящим текстом не считается и не должен попадать ни в список результатов, ни на экран.
    public static bool HasRealLyrics(string? synced, string? plain)
    {
        if (!string.IsNullOrWhiteSpace(synced))
        {
            foreach (string rawLine in synced.Replace("\r\n", "\n").Split('\n'))
            {
                string line = Timestamp.Replace(rawLine, string.Empty).Trim();
                // Служебные теги вида [ar:Имя] и [offset:0] строками песни не являются.
                if (line.Length > 0 && !IsCreditLine(line) && !line.StartsWith('[')) return true;
            }

            return false;
        }

        return CleanPlain(plain).Length > 0;
    }
}
