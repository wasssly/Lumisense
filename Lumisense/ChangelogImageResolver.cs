using System.IO;

namespace AudioPlayer;

/// <summary>Общая логика разрешения поля "image" (и на уровне версии, и на уровне отдельного
/// пункта изменений) в реальный путь для Image.Source. Поддерживает три варианта:
///  - прямая ссылка ("https://.../screenshot.png") — используется как есть, BitmapImage
///    сам скачает картинку;
///  - уже полный локальный путь (например, скопированный из проводника) — тоже используется
///    как есть, без подстановки папки Changelog;
///  - имя файла или относительный путь ("release-1.2.png", "screenshots\dark-theme.png") —
///    ищется внутри папки Changelog, рядом с changelog.json.
///
/// ВАЖНО: начиная с того, как changelog.json стал EmbeddedResource (см. ChangelogLoader), папка
/// Changelog/ рядом с .exe в установленной программе больше не существует — третий вариант
/// (имя файла без полного пути) работать не будет, если реально не положить туда файлы вручную.
/// На практике сейчас везде используются полные ссылки на изображения (репозиторий
/// github.com/wasssly/LumisenseImg) — так и стоит делать дальше.</summary>
public static class ChangelogImageResolver
{
    public static string? Resolve(string? image)
    {
        if (string.IsNullOrWhiteSpace(image)) return null;

        if (Uri.TryCreate(image, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return image;

        if (Path.IsPathRooted(image))
            return image;

        return Path.Combine(AppContext.BaseDirectory, "Changelog", image);
    }
}
