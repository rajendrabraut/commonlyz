using Microsoft.EntityFrameworkCore;

namespace Commonlyz.Web.Notifications;

/// <summary>
/// Transactional outbox row. Writing it is a fast local insert; <see cref="EmailDispatcher"/> delivers it later,
/// so an SMTP outage never fails the request that triggered the email, and nothing is lost on restart.
/// </summary>
public sealed class OutboxEmail
{
    public const int MaxSubjectLength = 300;
    public const int MaxBodyLength = 100_000;

    public long Id { get; set; }
    public required string ToAddress { get; set; }
    public required string Subject { get; set; }
    public required string Body { get; set; }
    public DateTime CreatedUtc { get; set; }

    public int Attempts { get; set; }
    public DateTime NextAttemptUtc { get; set; }

    /// <summary>Claim held by one dispatcher while sending, so several app instances never send the same email twice.</summary>
    public DateTime? LeaseUntilUtc { get; set; }

    public DateTime? SentUtc { get; set; }
    public DateTime? FailedUtc { get; set; }
    public string? LastError { get; set; }

    public string Status => SentUtc is not null ? "Sent" : FailedUtc is not null ? "Failed" : Attempts > 0 ? "Retrying" : "Queued";
}

public sealed class NotificationsDb(DbContextOptions<NotificationsDb> options) : DbContext(options)
{
    public const string Schema = "notifications";

    public DbSet<OutboxEmail> Outbox => Set<OutboxEmail>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<OutboxEmail>(e =>
        {
            e.ToTable("OutboxEmail");
            e.Ignore(x => x.Status);
            e.Property(x => x.ToAddress).HasMaxLength(320);
            e.Property(x => x.Subject).HasMaxLength(OutboxEmail.MaxSubjectLength);
            e.Property(x => x.Body).HasMaxLength(OutboxEmail.MaxBodyLength);
            e.Property(x => x.LastError).HasMaxLength(1000);
            e.Property(x => x.CreatedUtc).HasPrecision(3);
            e.Property(x => x.NextAttemptUtc).HasPrecision(3);
            e.Property(x => x.LeaseUntilUtc).HasPrecision(3);
            e.Property(x => x.SentUtc).HasPrecision(3);
            e.Property(x => x.FailedUtc).HasPrecision(3);
            // The dispatcher's claim query: pending rows due now.
            e.HasIndex(x => x.NextAttemptUtc).HasFilter("[SentUtc] IS NULL AND [FailedUtc] IS NULL").HasDatabaseName("IX_OutboxEmail_Pending");
            e.HasIndex(x => x.CreatedUtc);
        });
    }
}
