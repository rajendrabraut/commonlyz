using Commonlyz.Core;
using Commonlyz.Modules.PixelSqueeze.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Commonlyz.Modules.PixelSqueeze;

public sealed record StoredFile(string FileName, string ContentType, byte[] Bytes, string OwnerId);

public sealed record CompressResult(
    Guid OriginalId, Guid CompressedId, string FileName, long OriginalBytes, long CompressedBytes, int Width, int Height, bool KeptOriginal)
{
    public double SavedPercent => OriginalBytes == 0 ? 0 : 100.0 * (OriginalBytes - CompressedBytes) / OriginalBytes;
}

/// <summary>
/// Compresses uploads in memory. Results live in the memory cache for 30 minutes and are only downloadable
/// by the user who created them; nothing is written to disk. Only statistics are stored in the database.
/// </summary>
public sealed class PixelSqueezeService(IDbContextFactory<PixelSqueezeDb> dbFactory, TimeProvider clock) : IDisposable
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(30);

    // Own cache with a hard memory cap, so image bytes can't crowd out other modules' cached data.
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 512L * 1024 * 1024 });

    public async Task<CompressResult> CompressAsync(
        Stream input, string fileName, string ownerId, int quality, OutputFormat format, int? maxDimension, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer, ct);
        var original = buffer.ToArray();

        // CPU-bound: keep it off the Blazor circuit's synchronization context.
        var encoded = await Task.Run(() => ImageCompressor.Compress(original, quality, format, maxDimension), ct);

        var baseName = Path.GetFileNameWithoutExtension(Path.GetFileName(fileName));
        var originalType = encoded.InputFormat is "jpeg" ? "image/jpeg" : "image/" + encoded.InputFormat;

        // Never hand back something bigger than what came in (unless the user changed format or size).
        var keepOriginal = encoded.Bytes.Length >= original.Length && format == OutputFormat.Same && maxDimension is null;
        var output = keepOriginal ? original : encoded.Bytes;
        var outputName = keepOriginal ? Path.GetFileName(fileName) : $"{baseName}.min.{encoded.Extension}";

        var originalId = Store(new StoredFile(Path.GetFileName(fileName), originalType, original, ownerId));
        var compressedId = Store(new StoredFile(outputName, keepOriginal ? originalType : encoded.ContentType, output, ownerId));

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            db.Jobs.Add(new CompressionJob
            {
                FileName = outputName.Length > 260 ? outputName[..260] : outputName,
                InputFormat = encoded.InputFormat,
                OutputFormat = keepOriginal ? encoded.InputFormat : encoded.Extension,
                OriginalBytes = original.Length,
                CompressedBytes = output.Length,
                CreatedUtc = clock.UtcNowSeconds(),
            });
            await db.SaveChangesAsync(ct);
        }

        return new CompressResult(originalId, compressedId, outputName, original.Length, output.Length, encoded.Width, encoded.Height, keepOriginal);
    }

    public StoredFile? GetFile(Guid id, string ownerId) =>
        _cache.TryGetValue(CacheKey(id), out StoredFile? file) && file!.OwnerId == ownerId ? file : null;

    private Guid Store(StoredFile file)
    {
        var id = Guid.NewGuid();
        _cache.Set(CacheKey(id), file, new MemoryCacheEntryOptions { SlidingExpiration = Retention, Size = file.Bytes.Length });
        return id;
    }

    public void Dispose() => _cache.Dispose();

    private static string CacheKey(Guid id) => "pixelsqueeze:" + id.ToString("N");
}
