using Microsoft.EntityFrameworkCore;

namespace Commonlyz.Modules.DailyJournal.Data;

/// <summary>Journals are private: every query is filtered by <see cref="UserId"/>.</summary>
public sealed class JournalEntry
{
    public const int MaxTitleLength = 200;

    public int Id { get; set; }
    public required string UserId { get; set; }

    /// <summary>The user's local calendar day, not a UTC timestamp, so late-night entries land on the right day.</summary>
    public DateOnly EntryDate { get; set; }

    public string? Title { get; set; }
    public string Body { get; set; } = "";
    public string? Mood { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public List<JournalTag> Tags { get; set; } = [];
}

public sealed class JournalTag
{
    public int Id { get; set; }
    public required string UserId { get; set; }
    public required string Name { get; set; }
    public List<JournalEntry> Entries { get; set; } = [];
}

public sealed class DailyJournalDb(DbContextOptions<DailyJournalDb> options) : DbContext(options)
{
    public DbSet<JournalEntry> Entries => Set<JournalEntry>();
    public DbSet<JournalTag> Tags => Set<JournalTag>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(DailyJournalModule.Key);

        modelBuilder.Entity<JournalEntry>(e =>
        {
            e.ToTable("Entry");
            e.Property(x => x.UserId).HasMaxLength(450);
            e.Property(x => x.Title).HasMaxLength(JournalEntry.MaxTitleLength);
            e.Property(x => x.Mood).HasMaxLength(20);
            e.Property(x => x.CreatedUtc).HasPrecision(0);
            e.Property(x => x.UpdatedUtc).HasPrecision(0);
            e.HasIndex(x => new { x.UserId, x.EntryDate });
            e.HasMany(x => x.Tags).WithMany(t => t.Entries).UsingEntity("EntryTag");
        });

        modelBuilder.Entity<JournalTag>(e =>
        {
            e.ToTable("Tag");
            e.Property(x => x.UserId).HasMaxLength(450);
            e.Property(x => x.Name).HasMaxLength(40);
            e.HasIndex(x => new { x.UserId, x.Name }).IsUnique();
        });
    }
}
