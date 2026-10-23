using System.IO.Compression;
using Commonlyz.Core;
using Commonlyz.Core.Data;
using Commonlyz.Core.Modules;
using Commonlyz.Modules.PixelSqueeze.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Commonlyz.Modules.PixelSqueeze;

public sealed class PixelSqueezeModule : IAppModule
{
    public const string Key = "pixelsqueeze";

    public ModuleInfo Info { get; } = new(
        Key, "PixelSqueeze", "TinyPNG", "Compress, resize and convert images in the browser. Metadata stripped.", "/pixelsqueeze", "image", ModuleGroup.PersonalTools);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<PixelSqueezeDb>(Key);
        services.AddSingleton<PixelSqueezeService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/pixelsqueeze/files").RequireAuthorization();

        group.MapGet("/{id:guid}", async (Guid id, bool? inline, PixelSqueezeService service, ICurrentUser user) =>
        {
            var file = service.GetFile(id, (await user.GetAsync()).Id);
            if (file is null)
                return Results.NotFound();
            return inline == true
                ? Results.File(file.Bytes, file.ContentType)
                : Results.File(file.Bytes, file.ContentType, file.FileName);
        });

        group.MapGet("/zip", async (string ids, PixelSqueezeService service, ICurrentUser user) =>
        {
            var owner = (await user.GetAsync()).Id;
            using var zip = new MemoryStream();
            using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var part in ids.Split(',', StringSplitOptions.RemoveEmptyEntries).Take(100))
                {
                    if (!Guid.TryParse(part, out var id) || service.GetFile(id, owner) is not { } file)
                        continue;
                    var name = file.FileName;
                    for (var n = 2; !names.Add(name); n++)
                        name = $"{Path.GetFileNameWithoutExtension(file.FileName)}-{n}{Path.GetExtension(file.FileName)}";
                    await using var entry = archive.CreateEntry(name, CompressionLevel.NoCompression).Open();
                    await entry.WriteAsync(file.Bytes);
                }
            }
            return Results.File(zip.ToArray(), "application/zip", "pixelsqueeze.zip");
        });
    }

    public async Task<IReadOnlyList<ModuleStat>> GetStatsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var db = await services.GetRequiredService<IDbContextFactory<PixelSqueezeDb>>().CreateDbContextAsync(cancellationToken);
        var images = await db.Jobs.CountAsync(cancellationToken);
        var saved = await db.Jobs.SumAsync(j => (long?)(j.OriginalBytes - j.CompressedBytes), cancellationToken) ?? 0;
        return [new("Images", Format.Number(images)), new("Saved", Format.Bytes(Math.Max(0, saved)))];
    }
}
