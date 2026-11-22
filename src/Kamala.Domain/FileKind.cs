namespace Kamala.Domain;

/// <summary>The real type of a file, as determined from its bytes.</summary>
public enum FileKind
{
    Unknown,
    Jpeg,
    Png,
    Webp,
    Gif,
    Bmp,
    Tiff,
    Pdf,
    Docx,
    Doc,
    Odt,
    Rtf,
}

public enum FileCategory
{
    Unknown,
    Image,
    Pdf,
    Word,
}

public static class FileKinds
{
    public static FileCategory Category(this FileKind kind) => kind switch
    {
        FileKind.Jpeg or FileKind.Png or FileKind.Webp or FileKind.Gif or FileKind.Bmp or FileKind.Tiff => FileCategory.Image,
        FileKind.Pdf => FileCategory.Pdf,
        FileKind.Docx or FileKind.Doc or FileKind.Odt or FileKind.Rtf => FileCategory.Word,
        _ => FileCategory.Unknown,
    };

    public static string Extension(this FileKind kind) => kind switch
    {
        FileKind.Jpeg => "jpg",
        FileKind.Png => "png",
        FileKind.Webp => "webp",
        FileKind.Gif => "gif",
        FileKind.Bmp => "bmp",
        FileKind.Tiff => "tiff",
        FileKind.Pdf => "pdf",
        FileKind.Docx => "docx",
        FileKind.Doc => "doc",
        FileKind.Odt => "odt",
        FileKind.Rtf => "rtf",
        _ => "bin",
    };

    public static string ContentType(this FileKind kind) => kind switch
    {
        FileKind.Jpeg => "image/jpeg",
        FileKind.Png => "image/png",
        FileKind.Webp => "image/webp",
        FileKind.Gif => "image/gif",
        FileKind.Bmp => "image/bmp",
        FileKind.Tiff => "image/tiff",
        FileKind.Pdf => "application/pdf",
        FileKind.Docx => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        FileKind.Doc => "application/msword",
        FileKind.Odt => "application/vnd.oasis.opendocument.text",
        FileKind.Rtf => "application/rtf",
        _ => "application/octet-stream",
    };
}
