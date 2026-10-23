using Microsoft.EntityFrameworkCore;

namespace Commonlyz.Modules.SlotBook.Data;

public sealed class MeetingType
{
    public int Id { get; set; }
    public required string Slug { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? Location { get; set; }
    public int DurationMinutes { get; set; } = 30;
    public int BufferMinutes { get; set; }
    public int MinNoticeMinutes { get; set; } = 240;
    public int MaxDaysAhead { get; set; } = 30;

    /// <summary>IANA zone the availability rules are written in.</summary>
    public string TimeZoneId { get; set; } = "UTC";

    public bool IsActive { get; set; } = true;
    public List<AvailabilityRule> Rules { get; set; } = [];
}

/// <summary>Weekly window, e.g. Monday 09:00–17:00, in the meeting type's time zone.</summary>
public sealed class AvailabilityRule
{
    public int Id { get; set; }
    public int MeetingTypeId { get; set; }
    public DayOfWeek DayOfWeek { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
}

public enum BookingStatus
{
    Confirmed,
    Cancelled,
}

public sealed class Booking
{
    public int Id { get; set; }
    public int MeetingTypeId { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public required string GuestName { get; set; }
    public required string GuestEmail { get; set; }
    public string? Notes { get; set; }
    public BookingStatus Status { get; set; } = BookingStatus.Confirmed;
    public required string CancelToken { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class SlotBookDb(DbContextOptions<SlotBookDb> options) : DbContext(options)
{
    public DbSet<MeetingType> MeetingTypes => Set<MeetingType>();
    public DbSet<AvailabilityRule> Rules => Set<AvailabilityRule>();
    public DbSet<Booking> Bookings => Set<Booking>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SlotBookModule.Key);

        modelBuilder.Entity<MeetingType>(e =>
        {
            e.Property(x => x.Slug).HasMaxLength(64).IsUnicode(false);
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.Location).HasMaxLength(300);
            e.Property(x => x.TimeZoneId).HasMaxLength(64).IsUnicode(false);
            e.HasMany(x => x.Rules).WithOne().HasForeignKey(x => x.MeetingTypeId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AvailabilityRule>(e =>
            e.ToTable("AvailabilityRule", t => t.HasCheckConstraint("CK_Rule_Range", "[EndTime] > [StartTime]")));

        modelBuilder.Entity<Booking>(e =>
        {
            e.ToTable("Booking", t => t.HasCheckConstraint("CK_Booking_Range", "[EndUtc] > [StartUtc]"));
            e.Property(x => x.StartUtc).HasPrecision(0);
            e.Property(x => x.EndUtc).HasPrecision(0);
            e.Property(x => x.CreatedUtc).HasPrecision(0);
            e.Property(x => x.GuestName).HasMaxLength(100);
            e.Property(x => x.GuestEmail).HasMaxLength(256);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(10).IsUnicode(false);
            e.Property(x => x.CancelToken).HasMaxLength(64).IsUnicode(false);
            e.HasIndex(x => x.CancelToken).IsUnique();
            // Range scans for the overlap check lock this index's key range.
            e.HasIndex(x => new { x.StartUtc, x.EndUtc });
            e.HasOne<MeetingType>().WithMany().HasForeignKey(x => x.MeetingTypeId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
