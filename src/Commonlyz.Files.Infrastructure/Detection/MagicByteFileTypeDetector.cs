using System.IO.Compression;
using System.Text;
using Commonlyz.Files.Application.Abstractions;
using Commonlyz.Files.Domain;

namespace Commonlyz.Files.Infrastructure.Detection;

/// <summary>
/// Identifies files from their leading bytes; names and Content-Type headers come from the client and are never
/// trusted. ZIP-based documents (DOCX, ODT) are also checked for zip bombs before any engine opens them.
/// </summary>
public sealed class MagicByteFileTypeDetector : IFileTypeDetector
{
    private const long MaxUncompressedBytes = 300L * 1024 * 1024;
    private const int MaxEntries = 5000;
    private const int MaxRatio = 200;

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] Ole2 = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04];

    public FileKind Detect(string path)
    {
        Span<byte> buffer = stackalloc byte[16];
        int read;
        using (var stream = File.OpenRead(path))
            read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        ReadOnlySpan<byte> head = buffer[..read];

        if (head.StartsWith(Jpeg)) return FileKind.Jpeg;
        if (head.StartsWith(Png)) return FileKind.Png;
        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8)) return FileKind.Webp;
        if (head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8)) return FileKind.Gif;
        if (head.StartsWith("BM"u8) && head.Length >= 14) return FileKind.Bmp;
        if (head.StartsWith("II*\0"u8) || head.StartsWith("MM\0*"u8)) return FileKind.Tiff;
        if (head.StartsWith("%PDF-"u8)) return FileKind.Pdf;
        if (head.StartsWith("{\\rtf"u8)) return FileKind.Rtf;
        if (head.StartsWith(Ole2)) return FileKind.Doc;
        if (head.StartsWith(Zip)) return DetectZip(path);
        return FileKind.Unknown;
    }

    private static FileKind DetectZip(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            InvalidInputException.ThrowIf(zip.Entries.Count > MaxEntries, "The document contains too many parts.");

            long total = 0;
            var kind = FileKind.Unknown;
            foreach (var entry in zip.Entries)
            {
                total += entry.Length;
                var suspicious = entry.Length > 10 * 1024 * 1024 && entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > MaxRatio;
                InvalidInputException.ThrowIf(total > MaxUncompressedBytes || suspicious, "The document expands to an unsafe size (possible zip bomb).");

                if (entry.FullName == "word/document.xml")
                {
                    kind = FileKind.Docx;
                }
                else if (entry.FullName == "mimetype" && entry.Length < 100 && kind == FileKind.Unknown)
                {
                    using var reader = new StreamReader(entry.Open(), Encoding.ASCII);
                    if (reader.ReadToEnd().Trim() == "application/vnd.oasis.opendocument.text")
                        kind = FileKind.Odt;
                }
            }
            return kind;
        }
        catch (InvalidDataException)
        {
            return FileKind.Unknown;
        }
    }
}
