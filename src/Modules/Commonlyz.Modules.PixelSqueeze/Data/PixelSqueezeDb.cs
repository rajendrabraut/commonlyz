using Microsoft.EntityFrameworkCore;

namespace Commonlyz.Modules.PixelSqueeze.Data;

/// <summary>Statistics only; image bytes are never persisted.</summary>
public sealed class CompressionJob
{
    public long Id { get; set; }
    public required string FileName { get; set; }
    public required string InputFormat { get; set; }
    public required string OutputFormat { get; set; }
    public long OriginalBytes { get; set; }
    public long CompressedBytes { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class PixelSqueezeDb(DbContextOptions<PixelSqueezeDb> options) : DbContext(options)
{
    public DbSet<CompressionJob> Jobs => Set<CompressionJob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(PixelSqueezeModule.Key);
        modelBuilder.Entity<CompressionJob>(e =>
        {
            e.Property(x => x.FileName).HasMaxLength(260);
            e.Property(x => x.InputFormat).HasMaxLength(10).IsUnicode(false);
            e.Property(x => x.OutputFormat).HasMaxLength(10).IsUnicode(false);
            e.Property(x => x.CreatedUtc).HasPrecision(0);
        });
    }
}
