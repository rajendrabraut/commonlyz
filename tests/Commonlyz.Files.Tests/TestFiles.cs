using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Commonlyz.Files.Tests;

/// <summary>Builds small, valid test files in memory (no binary fixtures in the repo).</summary>
public static class TestFiles
{
    /// <summary>An RGB gradient PNG.</summary>
    public static byte[] Png(int width, int height)
    {
        var raw = new byte[height * (1 + width * 3)];
        for (var y = 0; y < height; y++)
        {
            var row = y * (1 + width * 3);
            for (var x = 0; x < width; x++)
            {
                raw[row + 1 + x * 3] = (byte)(x * 255 / Math.Max(1, width - 1));
                raw[row + 2 + x * 3] = (byte)(y * 255 / Math.Max(1, height - 1));
                raw[row + 3 + x * 3] = (byte)((x ^ y) & 0xFF);
            }
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 2; // RGB
        Chunk(png, "IHDR", header);
        using (var compressed = new MemoryStream())
        {
            using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
                z.Write(raw);
            Chunk(png, "IDAT", compressed.ToArray());
        }
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>A PDF with a real text layer, one text line per page.</summary>
    public static byte[] TextPdf(params string[] pages)
    {
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>" };
        var kids = new List<string>();
        foreach (var text in pages)
        {
            var content = $"BT /F1 18 Tf 72 720 Td ({text}) Tj ET";
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
            var streamId = objects.Count;
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {streamId} 0 R /Resources << /Font << /F1 3 0 R >> >> >>");
            kids.Add($"{objects.Count} 0 R");
        }
        objects[1] = $"<< /Type /Pages /Kids [{string.Join(' ', kids)}] /Count {kids.Count} >>";

        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(builder.Length);
            builder.Append(FormattableString.Invariant($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"));
        }
        var xref = builder.Length;
        builder.Append(FormattableString.Invariant($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n"));
        foreach (var offset in offsets)
            builder.Append(FormattableString.Invariant($"{offset:D10} 00000 n \n"));
        builder.Append(FormattableString.Invariant($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"));
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    /// <summary>A minimal DOCX with one paragraph.</summary>
    public static byte[] Docx(string text)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
                </Types>
                """);
            Add(zip, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
                </Relationships>
                """);
            Add(zip, "word/document.xml", $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                  <w:body><w:p><w:r><w:t>{text}</w:t></w:r></w:p></w:body>
                </w:document>
                """);
        }
        return stream.ToArray();
    }

    /// <summary>Width and height from a PNG or JPEG header.</summary>
    public static (int Width, int Height) Dimensions(byte[] image)
    {
        if (image[0] == 0x89)
            return (BinaryPrimitives.ReadInt32BigEndian(image.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(image.AsSpan(20)));
        for (var i = 2; i + 9 < image.Length;)
        {
            var marker = image[i + 1];
            var length = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(i + 2));
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                return (BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(i + 7)), BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(i + 5)));
            i += 2 + length;
        }
        throw new InvalidDataException("No image header found.");
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(content);
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        png.Write(buffer);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        png.Write(typeBytes);
        png.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(buffer, Crc32([.. typeBytes, .. data]));
        png.Write(buffer);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
}
