using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Kamala.Tests.Integration;

/// <summary>End-to-end: HTTP in, real Rust/Python/LibreOffice engines, files out.</summary>
[Collection(ApiCollection.Name)]
public sealed class ApiTests(ApiFactory api)
{
    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement;
    }

    private static async Task<string> Problem(HttpResponseMessage response, HttpStatusCode status)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(status, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("detail").GetString()!;
    }

    private static JsonElement Single(JsonElement job) => job.GetProperty("files").EnumerateArray().Single();

    [Fact]
    public async Task Health_and_capabilities_respond()
    {
        using var client = api.CreateClient();
        (await client.GetAsync("/health")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var caps = await api.CapabilitiesAsync();
        caps.GetProperty("conversions").GetArrayLength().ShouldBe(14);
    }

    [Fact]
    public async Task Security_headers_are_set()
    {
        using var client = api.CreateClient();
        var response = await client.GetAsync("/v1/capabilities");
        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        response.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("default-src 'none'");
    }

    [Fact]
    public async Task Compress_resize_and_crop_ratio()
    {
        if (!await api.HasAsync("image")) return;
        var job = await Ok(await api.PostAsync("/v1/images/edit",
            new { format = "jpeg", quality = 70, operations = new object[] { new { type = "crop_ratio", ratioWidth = 1, ratioHeight = 1 }, new { type = "resize", width = 200 } } },
            ("holiday.png", TestFiles.Png(800, 400))));
        var file = Single(job);
        file.GetProperty("name").GetString().ShouldBe("holiday.jpg");
        file.GetProperty("width").GetInt32().ShouldBe(200);
        file.GetProperty("height").GetInt32().ShouldBe(200);
        var bytes = await api.DownloadAsync(file.GetProperty("id").GetString()!);
        TestFiles.Dimensions(bytes).ShouldBe((200, 200));
        file.GetProperty("sourceSize").GetInt64().ShouldBeGreaterThan(bytes.Length);
    }

    [Fact]
    public async Task Interactive_crop_then_enhance_and_degrade()
    {
        if (!await api.HasAsync("image")) return;
        var crop = Single(await Ok(await api.PostAsync("/v1/images/edit",
            new { format = "png", operations = new object[] { new { type = "crop", x = 10, y = 20, width = 120, height = 60 }, new { type = "auto_enhance" }, new { type = "sharpen", amount = 1.5 } } },
            ("a.png", TestFiles.Png(300, 200)))));
        TestFiles.Dimensions(await api.DownloadAsync(crop.GetProperty("id").GetString()!)).ShouldBe((120, 60));

        var degraded = Single(await Ok(await api.PostAsync("/v1/images/edit",
            new { format = "jpeg", quality = 5, operations = new object[] { new { type = "scale", percent = 25 }, new { type = "blur", sigma = 2 }, new { type = "grayscale" } } },
            ("a.png", TestFiles.Png(400, 200)))));
        degraded.GetProperty("width").GetInt32().ShouldBe(100);
    }

    [Fact]
    public async Task Target_file_size_is_met()
    {
        if (!await api.HasAsync("image")) return;
        var file = Single(await Ok(await api.PostAsync("/v1/images/edit", new { format = "jpeg", targetKb = 20 }, ("big.png", TestFiles.Png(1200, 900)))));
        file.GetProperty("size").GetInt64().ShouldBeLessThanOrEqualTo(20 * 1024);
    }

    [Fact]
    public async Task Pad_to_ratio_and_rotate()
    {
        if (!await api.HasAsync("image")) return;
        var file = Single(await Ok(await api.PostAsync("/v1/images/edit",
            new { format = "png", operations = new object[] { new { type = "pad_ratio", ratioWidth = 16, ratioHeight = 9, color = "#000000" }, new { type = "rotate", degrees = 90 } } },
            ("a.png", TestFiles.Png(160, 160)))));
        // 160x160 padded to 16:9 = 284x160, then rotated = 160x284.
        (file.GetProperty("width").GetInt32(), file.GetProperty("height").GetInt32()).ShouldBe((160, 284));
    }

    [Fact]
    public async Task Bad_edit_options_are_rejected()
    {
        (await Problem(await api.PostAsync("/v1/images/edit", new { operations = new object[] { new { type = "rotate", degrees = 45 } } }, ("a.png", TestFiles.Png(10, 10))),
            HttpStatusCode.BadRequest)).ShouldContain("multiple of 90");
        (await Problem(await api.PostAsync("/v1/images/edit", new { operations = new object[] { new { type = "explode" } } }, ("a.png", TestFiles.Png(10, 10))),
            HttpStatusCode.BadRequest)).ShouldContain("Unknown edit step");
    }

    [Fact]
    public async Task Uploads_are_checked_by_content()
    {
        (await Problem(await api.PostAsync("/v1/convert", new { to = "pdf" }, ("innocent.png", "MZ\0\0this is an exe"u8.ToArray())), HttpStatusCode.BadRequest))
            .ShouldContain("not a supported file");
        (await Problem(await api.PostAsync("/v1/convert", new { to = "pdf" }, ("big.png", new byte[6 * 1024 * 1024])), HttpStatusCode.BadRequest))
            .ShouldContain("larger than");
        (await Problem(await api.PostAsync("/v1/convert", new { to = "exe" }, ("a.png", TestFiles.Png(4, 4))), HttpStatusCode.BadRequest))
            .ShouldContain("Choose what to convert to");
    }

    [Fact]
    public async Task Unknown_or_malformed_download_ids_are_404()
    {
        using var client = api.CreateClient();
        (await client.GetAsync("/v1/files/0123456789abcdef0123456789abcdef")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync("/v1/files/..%2F..%2Fappsettings.json")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync("/v1/jobs/nope/zip")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Images_to_pdf_and_back_to_images()
    {
        if (!await api.HasAsync("documents")) return;
        var pdf = Single(await Ok(await api.PostAsync("/v1/convert", new { to = "pdf", pageSize = "a4", marginPoints = 36 },
            ("one.png", TestFiles.Png(300, 200)), ("two.png", TestFiles.Png(200, 300)))));
        pdf.GetProperty("name").GetString().ShouldBe("one.pdf");
        var pdfBytes = await api.DownloadAsync(pdf.GetProperty("id").GetString()!);
        Encoding.ASCII.GetString(pdfBytes, 0, 5).ShouldBe("%PDF-");

        var pages = await Ok(await api.PostAsync("/v1/convert", new { to = "jpeg", dpi = 72 }, ("one.pdf", pdfBytes)));
        var names = pages.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("name").GetString()).ToList();
        names.ShouldBe(["one-001.jpg", "one-002.jpg"]);
    }

    [Fact]
    public async Task Pdf_page_range_selects_pages()
    {
        if (!await api.HasAsync("documents")) return;
        var job = await Ok(await api.PostAsync("/v1/convert", new { to = "png", pages = "2-3" }, ("deck.pdf", TestFiles.TextPdf("One", "Two", "Three", "Four"))));
        job.GetProperty("files").GetArrayLength().ShouldBe(2);
        (await Problem(await api.PostAsync("/v1/convert", new { to = "png", pages = "9" }, ("deck.pdf", TestFiles.TextPdf("One"))), HttpStatusCode.BadRequest))
            .ShouldContain("outside");
    }

    [Fact]
    public async Task Pdf_to_word_keeps_text()
    {
        if (!await api.HasAsync("documents")) return;
        var docx = Single(await Ok(await api.PostAsync("/v1/convert", new { to = "docx" }, ("minutes.pdf", TestFiles.TextPdf("Board meeting minutes", "Action items follow")))));
        docx.GetProperty("name").GetString().ShouldBe("minutes.docx");
        var xml = DocumentXml(await api.DownloadAsync(docx.GetProperty("id").GetString()!));
        xml.ShouldContain("Board meeting minutes");
        xml.ShouldContain("Action items follow");
    }

    [Fact]
    public async Task Images_to_word_embeds_pictures()
    {
        if (!await api.HasAsync("documents")) return;
        var docx = Single(await Ok(await api.PostAsync("/v1/convert", new { to = "docx" }, ("a.png", TestFiles.Png(100, 80)), ("b.png", TestFiles.Png(80, 100)))));
        using var zip = new ZipArchive(new MemoryStream(await api.DownloadAsync(docx.GetProperty("id").GetString()!)));
        zip.Entries.Count(e => e.FullName.StartsWith("word/media/", StringComparison.Ordinal)).ShouldBe(2);
    }

    [Fact]
    public async Task Ai_upscale_doubles_resolution()
    {
        if (!await api.HasAsync("ai")) return;
        var file = Single(await Ok(await api.PostAsync("/v1/images/upscale", new { scale = 2, format = "png" }, ("small.png", TestFiles.Png(64, 48)))));
        file.GetProperty("name").GetString().ShouldBe("small-2x.png");
        TestFiles.Dimensions(await api.DownloadAsync(file.GetProperty("id").GetString()!)).ShouldBe((128, 96));
    }

    [Fact]
    public async Task Word_to_pdf_and_images()
    {
        if (!await api.HasAsync("office")) return;
        var pdf = Single(await Ok(await api.PostAsync("/v1/convert", new { to = "pdf" }, ("letter.docx", TestFiles.Docx("Dear customer, thank you.")))));
        pdf.GetProperty("name").GetString().ShouldBe("letter.pdf");
        Encoding.ASCII.GetString(await api.DownloadAsync(pdf.GetProperty("id").GetString()!), 0, 5).ShouldBe("%PDF-");

        var images = await Ok(await api.PostAsync("/v1/convert", new { to = "jpeg", dpi = 72 }, ("letter.docx", TestFiles.Docx("Page one"))));
        images.GetProperty("files").EnumerateArray().First().GetProperty("name").GetString().ShouldBe("letter-001.jpg");
    }

    [Fact]
    public async Task Zip_download_contains_every_result()
    {
        if (!await api.HasAsync("image")) return;
        var job = await Ok(await api.PostAsync("/v1/images/edit", new { format = "png" }, ("a.png", TestFiles.Png(20, 20)), ("b.png", TestFiles.Png(20, 20))));
        using var client = api.CreateClient();
        var zip = await client.GetAsync($"/v1/jobs/{job.GetProperty("jobId").GetString()}/zip");
        zip.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var archive = new ZipArchive(await zip.Content.ReadAsStreamAsync());
        archive.Entries.Select(e => e.Name).Order().ShouldBe(["a.png", "b.png"]);
    }

    [Fact]
    public async Task Downloads_force_attachment_except_inline_images()
    {
        if (!await api.HasAsync("image")) return;
        var file = Single(await Ok(await api.PostAsync("/v1/images/edit", new { format = "png" }, ("a.png", TestFiles.Png(20, 20)))));
        using var client = api.CreateClient();
        var id = file.GetProperty("id").GetString();
        (await client.GetAsync($"/v1/files/{id}")).Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        (await client.GetAsync($"/v1/files/{id}?inline=true")).Content.Headers.ContentDisposition.ShouldBeNull();
    }

    private static string DocumentXml(byte[] docx)
    {
        using var zip = new ZipArchive(new MemoryStream(docx));
        using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        return reader.ReadToEnd();
    }
}
