using Microsoft.EntityFrameworkCore;

namespace Commonlyz.Modules.TeamWiki.Data;

public sealed class WikiPage
{
    public const int MaxTitleLength = 200;
    public const int MaxBodyLength = 200_000;

    public int Id { get; set; }
    public int? ParentId { get; set; }
    public required string Title { get; set; }
    public string Body { get; set; } = "";
    public int Position { get; set; }
    public required string UpdatedBy { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime CreatedUtc { get; set; }

    /// <summary>Optimistic concurrency: a save based on a stale copy is rejected instead of silently overwriting.</summary>
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Every save writes one; history and restore come for free.</summary>
public sealed class PageRevision
{
    public long Id { get; set; }
    public int PageId { get; set; }
    public required string Title { get; set; }
    public required string Body { get; set; }
    public required string SavedBy { get; set; }
    public DateTime SavedUtc { get; set; }
}

public sealed class TeamWikiDb(DbContextOptions<TeamWikiDb> options) : DbContext(options)
{
    public DbSet<WikiPage> Pages => Set<WikiPage>();
    public DbSet<PageRevision> Revisions => Set<PageRevision>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(TeamWikiModule.Key);

        modelBuilder.Entity<WikiPage>(e =>
        {
            e.ToTable("Page");
            e.Property(x => x.Title).HasMaxLength(WikiPage.MaxTitleLength);
            e.Property(x => x.UpdatedBy).HasMaxLength(100);
            e.Property(x => x.UpdatedUtc).HasPrecision(0);
            e.Property(x => x.CreatedUtc).HasPrecision(0);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasIndex(x => new { x.ParentId, x.Position });
            e.HasOne<WikiPage>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<PageRevision>(e =>
        {
            e.ToTable("Revision");
            e.Property(x => x.Title).HasMaxLength(WikiPage.MaxTitleLength);
            e.Property(x => x.SavedBy).HasMaxLength(100);
            e.Property(x => x.SavedUtc).HasPrecision(0);
            e.HasIndex(x => new { x.PageId, x.SavedUtc });
            e.HasOne<WikiPage>().WithMany().HasForeignKey(x => x.PageId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
