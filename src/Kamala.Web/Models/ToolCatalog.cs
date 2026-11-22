namespace Kamala.Web.Models;

public enum ToolGroup
{
    Convert,
    Image,
}

/// <summary>
/// One tool page. <see cref="Panel"/> selects the options partial and the matching options builder in tool.js;
/// <see cref="Preset"/> is merged into the options sent to the API.
/// </summary>
public sealed record Tool(
    string Slug,
    string Title,
    string Summary,
    ToolGroup Group,
    string Endpoint,
    string Accept,
    string Panel,
    string Badge,
    string Preset = "{}",
    bool Multiple = true);

/// <summary>Every tool the site offers. Adding a tool is one entry here (plus a panel if it needs new options).</summary>
public static class ToolCatalog
{
    private const string Images = "image/jpeg,image/png,image/webp,image/gif,image/bmp,image/tiff";
    private const string Pdf = "application/pdf,.pdf";
    private const string Word = ".doc,.docx,.odt,.rtf,application/msword,application/vnd.openxmlformats-officedocument.wordprocessingml.document,application/vnd.oasis.opendocument.text,application/rtf";

    public static IReadOnlyList<Tool> All { get; } =
    [
        new("pdf-to-jpg", "PDF to JPG", "Turn every PDF page into a JPG image.", ToolGroup.Convert, "convert", Pdf, "pdf-to-image", "PDF", """{"to":"jpeg"}"""),
        new("pdf-to-png", "PDF to PNG", "Lossless PNG images of each page.", ToolGroup.Convert, "convert", Pdf, "pdf-to-image", "PDF", """{"to":"png"}"""),
        new("jpg-to-pdf", "Image to PDF", "Combine JPG, PNG or WebP images into one PDF.", ToolGroup.Convert, "convert", Images, "image-to-pdf", "IMG", """{"to":"pdf"}"""),
        new("pdf-to-word", "PDF to Word", "Editable DOCX with text, fonts and pictures kept.", ToolGroup.Convert, "convert", Pdf, "none", "PDF", """{"to":"docx"}"""),
        new("word-to-pdf", "Word to PDF", "DOCX, DOC, ODT or RTF to PDF.", ToolGroup.Convert, "convert", Word, "none", "DOC", """{"to":"pdf"}"""),
        new("word-to-jpg", "Word to JPG", "Each page of a Word document as an image.", ToolGroup.Convert, "convert", Word, "pdf-to-image", "DOC", """{"to":"jpeg"}"""),
        new("image-to-word", "Image to Word", "Put images into a Word document, one per page.", ToolGroup.Convert, "convert", Images, "merge", "IMG", """{"to":"docx"}"""),
        new("doc-to-docx", "DOC to DOCX", "Upgrade DOC, ODT or RTF files to DOCX.", ToolGroup.Convert, "convert", ".doc,.odt,.rtf", "none", "DOC", """{"to":"docx"}"""),
        new("convert-image", "Convert image", "JPG, PNG and WebP to each other.", ToolGroup.Convert, "convert", Images, "image-format", "IMG"),

        new("compress-image", "Compress image", "Smaller files with the best quality for the size.", ToolGroup.Image, "images/edit", Images, "compress", "KB"),
        new("resize-image", "Resize image", "Set exact pixels or a percentage.", ToolGroup.Image, "images/edit", Images, "resize", "PX"),
        new("crop-image", "Crop image", "Drag to select the area to keep.", ToolGroup.Image, "images/edit", Images, "crop", "CROP", Multiple: false),
        new("aspect-ratio", "Change aspect ratio", "Crop or pad to 1:1, 4:5, 16:9 and more.", ToolGroup.Image, "images/edit", Images, "ratio", "16:9"),
        new("rotate-image", "Rotate & flip", "Turn or mirror images.", ToolGroup.Image, "images/edit", Images, "rotate", "90°"),
        new("enhance-image", "Enhance image", "Auto-fix, sharpen, denoise, brightness and colour.", ToolGroup.Image, "images/edit", Images, "enhance", "HD"),
        new("upscale-image", "AI upscale", "Enlarge 2-4x and restore detail with Real-ESRGAN.", ToolGroup.Image, "images/upscale", Images, "upscale", "AI"),
        new("reduce-quality", "Reduce quality", "Lower resolution, blur or grayscale on purpose.", ToolGroup.Image, "images/edit", Images, "degrade", "LO"),
    ];

    public static Tool? Find(string slug) => All.FirstOrDefault(t => t.Slug == slug);
}
