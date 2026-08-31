using System.IO;

namespace Lumisense;

// image: абсолютный URL / полный путь — как есть, иначе относительный путь внутри папки Changelog.
// Папка Changelog в установленной версии не существует (changelog.json теперь EmbeddedResource),
// так что на практике везде используются полные ссылки — см. github.com/wasssly/LumisenseImg
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
