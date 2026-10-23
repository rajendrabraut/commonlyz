using Microsoft.EntityFrameworkCore;

namespace Commonlyz.Modules.TimeTrack.Data;

public sealed class Project
{
    public const int MaxNameLength = 100;

    public int Id { get; set; }
    public required string Name { get; set; }
    public string ColorHex { get; set; } = "#3b82f6";
    public DateTime? ArchivedUtc { get; set; }
}

public sealed class TimeEntry
{
    public const int MaxDescriptionLength = 300;

    public long Id { get; set; }
    public required string UserId { get; set; }
    public int? ProjectId { get; set; }
    public Project? Project { get; set; }
    public string? Description { get; set; }
    public DateTime StartUtc { get; set; }

    /// <summary><c>null</c> while running. At most one running entry per user (filtered unique index).</summary>
    public DateTime? EndUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}

public sealed class UserSettings
{
    public required string UserId { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
}

public sealed class TimeTrackDb(DbContextOptions<TimeTrackDb> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<TimeEntry> Entries => Set<TimeEntry>();
    public DbSet<UserSettings> Settings => Set<UserSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(TimeTrackModule.Key);

        modelBuilder.Entity<Project>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(Project.MaxNameLength);
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.ColorHex).HasMaxLength(7).IsUnicode(false).IsFixedLength();
            e.Property(x => x.ArchivedUtc).HasPrecision(0);
        });

        modelBuilder.Entity<TimeEntry>(e =>
        {
            e.ToTable("TimeEntry", t => t.HasCheckConstraint("CK_TimeEntry_Range", "[EndUtc] IS NULL OR [EndUtc] > [StartUtc]"));
            e.Property(x => x.UserId).HasMaxLength(450);
            e.Property(x => x.Description).HasMaxLength(TimeEntry.MaxDescriptionLength);
            e.Property(x => x.StartUtc).HasPrecision(0);
            e.Property(x => x.EndUtc).HasPrecision(0);
            e.Property(x => x.UpdatedUtc).HasPrecision(0);
            e.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.UserId).IsUnique().HasFilter("[EndUtc] IS NULL").HasDatabaseName("UX_TimeEntry_OneRunningPerUser");
            e.HasIndex(x => new { x.UserId, x.StartUtc }).IncludeProperties(x => new { x.EndUtc, x.ProjectId });
        });

        modelBuilder.Entity<UserSettings>(e =>
        {
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).HasMaxLength(450);
            e.Property(x => x.TimeZoneId).HasMaxLength(64).IsUnicode(false);
        });
    }
}
