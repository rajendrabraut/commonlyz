using Microsoft.EntityFrameworkCore;

namespace Commonlyz.Modules.LinkHub.Data;

public sealed class Profile
{
    public const int MaxHandleLength = 40;

    public int Id { get; set; }
    public required string Handle { get; set; }
    public required string DisplayName { get; set; }
    public string? Bio { get; set; }
    public string? AvatarUrl { get; set; }
    public string AccentColor { get; set; } = "#2563eb";
    public bool IsPublished { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public List<ProfileLink> Links { get; set; } = [];
}

public sealed class ProfileLink
{
    public int Id { get; set; }
    public int ProfileId { get; set; }
    public required string Title { get; set; }
    public required string Url { get; set; }
    public int SortOrder { get; set; }
    public bool IsDraft { get; set; }
    public long ClickCount { get; set; }
}

public sealed class LinkHubDb(DbContextOptions<LinkHubDb> options) : DbContext(options)
{
    public DbSet<Profile> Profiles => Set<Profile>();
    public DbSet<ProfileLink> Links => Set<ProfileLink>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(LinkHubModule.Key);

        modelBuilder.Entity<Profile>(e =>
        {
            e.Property(x => x.Handle).HasMaxLength(Profile.MaxHandleLength).IsUnicode(false);
            e.HasIndex(x => x.Handle).IsUnique();
            e.Property(x => x.DisplayName).HasMaxLength(100);
            e.Property(x => x.Bio).HasMaxLength(280);
            e.Property(x => x.AvatarUrl).HasMaxLength(500);
            e.Property(x => x.AccentColor).HasMaxLength(7).IsUnicode(false);
            e.Property(x => x.UpdatedUtc).HasPrecision(0);
            e.HasMany(x => x.Links).WithOne().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProfileLink>(e =>
        {
            e.ToTable("Link", t => t.HasCheckConstraint("CK_Link_Url", "[Url] LIKE 'https://%' OR [Url] LIKE 'http://%'"));
            e.Property(x => x.Title).HasMaxLength(100);
            e.Property(x => x.Url).HasMaxLength(2048);
            e.HasIndex(x => new { x.ProfileId, x.SortOrder });
        });
    }
}
