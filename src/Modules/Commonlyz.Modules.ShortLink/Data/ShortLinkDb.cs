using Microsoft.EntityFrameworkCore;

namespace Commonlyz.Modules.ShortLink.Data;

public sealed class Link
{
    public int Id { get; set; }
    public required string Slug { get; set; }
    public required string DestinationUrl { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? ExpiresUtc { get; set; }

    /// <summary>Denormalised total, incremented in batches by <see cref="ClickWriter"/>.</summary>
    public long ClickCount { get; set; }

    public DateTime CreatedUtc { get; set; }
}

public sealed class Click
{
    public const int MaxReferrerLength = 500;

    public long Id { get; set; }
    public int LinkId { get; set; }
    public DateTime ClickedUtc { get; set; }
    public string? Referrer { get; set; }
}

public sealed class ShortLinkDb(DbContextOptions<ShortLinkDb> options) : DbContext(options)
{
    public const int MaxSlugLength = 64;

    public DbSet<Link> Links => Set<Link>();
    public DbSet<Click> Clicks => Set<Click>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(ShortLinkModule.Key);

        modelBuilder.Entity<Link>(e =>
        {
            e.ToTable("Link", t => t.HasCheckConstraint("CK_Link_Url", "[DestinationUrl] LIKE 'https://%' OR [DestinationUrl] LIKE 'http://%'"));
            e.Property(x => x.Slug).HasMaxLength(MaxSlugLength).IsUnicode(false);
            // The unique index decides who owns a slug — no check-then-insert race.
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.DestinationUrl).HasMaxLength(2048);
            e.Property(x => x.CreatedUtc).HasPrecision(0);
            e.Property(x => x.ExpiresUtc).HasPrecision(0);
        });

        modelBuilder.Entity<Click>(e =>
        {
            e.Property(x => x.ClickedUtc).HasPrecision(0);
            e.Property(x => x.Referrer).HasMaxLength(Click.MaxReferrerLength);
            e.HasIndex(x => new { x.LinkId, x.ClickedUtc });
            e.HasOne<Link>().WithMany().HasForeignKey(x => x.LinkId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
