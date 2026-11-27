namespace Lumisense;

// Не привязан к конкретной библиотеке тегов (раньше протекал TagLib.PictureType, теперь
// ATL.PictureInfo.PIC_TYPE) — см. AlbumArtPictureKindExtensions.FromAtl.
public enum AlbumArtPictureKind
{
    Other,
    FrontCover,
    BackCover,
    Artist,
    Media,
    Illustration
}

public static class AlbumArtPictureKindExtensions
{
    public static AlbumArtPictureKind FromAtl(ATL.PictureInfo.PIC_TYPE type) => type switch
    {
        ATL.PictureInfo.PIC_TYPE.Front => AlbumArtPictureKind.FrontCover,
        ATL.PictureInfo.PIC_TYPE.Back => AlbumArtPictureKind.BackCover,
        ATL.PictureInfo.PIC_TYPE.Artist => AlbumArtPictureKind.Artist,
        ATL.PictureInfo.PIC_TYPE.CD => AlbumArtPictureKind.Media,
        ATL.PictureInfo.PIC_TYPE.Illustration => AlbumArtPictureKind.Illustration,
        _ => AlbumArtPictureKind.Other
    };

    // ATL.NET не отдаёт MIME-тип отдельным полем — определяем по первым байтам картинки.
    public static string DetectAlbumArtMimeType(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
            return "image/gif";
        if (bytes.Length >= 2 && bytes[0] == 0x42 && bytes[1] == 0x4D)
            return "image/bmp";
        return "image/jpeg";
    }
}
