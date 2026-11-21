using System.IO.Compression;
using System.Text.Json;
using Commonlyz.Files.Api.Contracts;
using Commonlyz.Files.Application;
using Commonlyz.Files.Application.Abstractions;
using Commonlyz.Files.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Commonlyz.Files.Api.Endpoints;

/// <summary>HTTP surface. Endpoints only translate HTTP to use-case calls; all rules live in Application/Domain.</summary>
public static class FileEndpoints
{
    public const string ProcessingPolicy = "processing";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapFileEndpoints(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/v1");

        v1.MapGet("/capabilities", (ICapabilityProvider capabilities, ConvertFilesUseCase convert) => Results.Ok(new
        {
            capabilities.Current.ImageEditing,
            capabilities.Current.AiUpscale,
            Conversions = convert.Options().Select(o => new { From = o.Source.ToString().ToLowerInvariant(), To = o.Target.ToString().ToLowerInvariant(), o.Available }),
        }));

        var processing = v1.MapGroup("").RequireRateLimiting(ProcessingPolicy).DisableAntiforgery();

        processing.MapPost("/images/edit", async (HttpRequest request, EditImagesUseCase useCase, CancellationToken ct) =>
        {
            var (files, options) = await ReadAsync<ImageEditDto>(request, ct);
            return Results.Ok(await useCase.ExecuteAsync(files, options.ToDomain(), ct));
        });

        processing.MapPost("/images/upscale", async (HttpRequest request, UpscaleImagesUseCase useCase, CancellationToken ct) =>
        {
            var (files, options) = await ReadAsync<UpscaleDto>(request, ct);
            return Results.Ok(await useCase.ExecuteAsync(files, options.ToDomain(), ct));
        });

        processing.MapPost("/convert", async (HttpRequest request, ConvertFilesUseCase useCase, CancellationToken ct) =>
        {
            var (files, options) = await ReadAsync<ConversionDto>(request, ct);
            return Results.Ok(await useCase.ExecuteAsync(files, options.ToDomain(), ct));
        });

        v1.MapGet("/files/{id}", (string id, bool? inline, DownloadsQuery downloads, HttpResponse response) =>
        {
            var file = downloads.File(id);
            if (file is null)
                return Results.NotFound();
            response.Headers.CacheControl = "private, no-store";
            // Only images may render inline; everything else is always a download.
            var asInline = inline == true && file.ContentType.StartsWith("image/", StringComparison.Ordinal);
            return Results.File(file.Path, file.ContentType, asInline ? null : file.Name, enableRangeProcessing: true);
        });

        v1.MapGet("/jobs/{id}/zip", async (string id, DownloadsQuery downloads, HttpResponse response, CancellationToken ct) =>
        {
            var files = downloads.Job(id);
            if (files.Count == 0)
                return Results.NotFound();
            response.Headers.CacheControl = "private, no-store";
            response.ContentType = "application/zip";
            response.Headers.ContentDisposition = "attachment; filename=\"commonlyz-files.zip\"";
            await using var zip = new ZipArchive(response.BodyWriter.AsStream(), ZipArchiveMode.Create, leaveOpen: true);
            foreach (var file in files)
            {
                // Results are already compressed formats (JPEG, PDF, DOCX...): store them as-is for speed.
                var entry = zip.CreateEntry(file.Name, CompressionLevel.NoCompression);
                await using var target = await entry.OpenAsync(ct);
                await using var source = File.OpenRead(file.Path);
                await source.CopyToAsync(target, ct);
            }
            return Results.Empty;
        });
    }

    /// <summary>Reads the multipart form: files under "files", JSON options under "options".</summary>
    private static async Task<(IReadOnlyList<IncomingFile> Files, T Options)> ReadAsync<T>(HttpRequest request, CancellationToken ct)
    {
        InvalidInputException.ThrowIf(!request.HasFormContentType, "Send the files as multipart/form-data.");
        var form = await request.ReadFormAsync(ct);
        var files = form.Files.GetFiles("files").Select(f => new IncomingFile(f.FileName, f.Length, f.OpenReadStream)).ToList();

        T options;
        try
        {
            var raw = form.TryGetValue("options", out var value) && !string.IsNullOrWhiteSpace(value) ? value.ToString() : "{}";
            options = JsonSerializer.Deserialize<T>(raw, Json) ?? throw new InvalidInputException("The options are missing.");
        }
        catch (JsonException ex)
        {
            throw new InvalidInputException($"The options are not valid JSON: {ex.Message}");
        }
        return (files, options);
    }
}
