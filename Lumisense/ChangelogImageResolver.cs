using System.IO;

namespace Lumisense;

// image: абсолютный URL — как есть, иначе относительный путь внутри папки Changelog. В
// установленной версии этой папки нет (EmbeddedResource), поэтому на практике везде ссылки.
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
