using Microsoft.EntityFrameworkCore;

namespace Commonlyz.Modules.StatusBoard.Data;

/// <summary>Ordered from best to worst so "overall status" is simply the maximum.</summary>
public enum ComponentStatus
{
    Operational,
    Maintenance,
    Degraded,
    PartialOutage,
    MajorOutage,
}

public enum IncidentStatus
{
    Investigating,
    Identified,
    Monitoring,
    Resolved,
}

public enum IncidentImpact
{
    None,
    Minor,
    Major,
    Critical,
}

public sealed class StatusComponent
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public int Position { get; set; }
    public ComponentStatus Status { get; set; }
}

public sealed class Incident
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public IncidentStatus Status { get; set; }
    public IncidentImpact Impact { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? ResolvedUtc { get; set; }
    public List<IncidentUpdate> Updates { get; set; } = [];
    public List<IncidentComponent> Components { get; set; } = [];
}

public sealed class IncidentUpdate
{
    public int Id { get; set; }
    public int IncidentId { get; set; }
    public IncidentStatus Status { get; set; }
    public required string Body { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class IncidentComponent
{
    public int IncidentId { get; set; }
    public int ComponentId { get; set; }
}

public sealed class StatusBoardDb(DbContextOptions<StatusBoardDb> options) : DbContext(options)
{
    public DbSet<StatusComponent> Components => Set<StatusComponent>();
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<IncidentUpdate> Updates => Set<IncidentUpdate>();
    public DbSet<IncidentComponent> IncidentComponents => Set<IncidentComponent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(StatusBoardModule.Key);

        modelBuilder.Entity<StatusComponent>(e =>
        {
            e.ToTable("Component");
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Description).HasMaxLength(300);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        });

        modelBuilder.Entity<Incident>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
            e.Property(x => x.Impact).HasConversion<string>().HasMaxLength(10).IsUnicode(false);
            e.Property(x => x.StartedUtc).HasPrecision(0);
            e.Property(x => x.ResolvedUtc).HasPrecision(0);
            e.HasIndex(x => x.ResolvedUtc);
            e.HasMany(x => x.Updates).WithOne().HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Components).WithOne().HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<IncidentUpdate>(e =>
        {
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
            e.Property(x => x.Body).HasMaxLength(4000);
            e.Property(x => x.CreatedUtc).HasPrecision(0);
        });

        modelBuilder.Entity<IncidentComponent>(e =>
        {
            e.HasKey(x => new { x.IncidentId, x.ComponentId });
            e.HasOne<StatusComponent>().WithMany().HasForeignKey(x => x.ComponentId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
