using Microsoft.EntityFrameworkCore;

namespace Commonlyz.Modules.ClientCollect.Data;

public sealed class Client
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Email { get; set; }
}

public enum RequestStatus
{
    Open,
    Complete,
    Archived,
}

public sealed class CollectRequest
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public Client? Client { get; set; }
    public required string Title { get; set; }
    public string? Message { get; set; }

    /// <summary>SHA-256 of the magic-link token. The token itself is shown once and never stored.</summary>
    public required byte[] TokenHash { get; set; }

    public DateOnly? DueDate { get; set; }
    public RequestStatus Status { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public DateTime? LastReminderUtc { get; set; }
    public List<RequestItem> Items { get; set; } = [];
}

public enum ItemKind
{
    Text,
    File,
}

public enum ItemStatus
{
    Pending,
    Submitted,
    ChangesRequested,
    Accepted,
}

public sealed class RequestItem
{
    public int Id { get; set; }
    public int RequestId { get; set; }
    public int Ordinal { get; set; }
    public ItemKind Kind { get; set; }
    public required string Prompt { get; set; }
    public ItemStatus Status { get; set; }
    public string? TextAnswer { get; set; }
    public string? ReviewComment { get; set; }
    public DateTime? SubmittedUtc { get; set; }
    public List<ItemFile> Files { get; set; } = [];
}

/// <summary>Uploaded file. Stored in the database (capped size) so backups capture everything in one place.</summary>
public sealed class ItemFile
{
    public int Id { get; set; }
    public int ItemId { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long SizeBytes { get; set; }
    public required byte[] Content { get; set; }
    public DateTime UploadedUtc { get; set; }
}

public sealed class ClientCollectDb(DbContextOptions<ClientCollectDb> options) : DbContext(options)
{
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<CollectRequest> Requests => Set<CollectRequest>();
    public DbSet<RequestItem> Items => Set<RequestItem>();
    public DbSet<ItemFile> Files => Set<ItemFile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(ClientCollectModule.Key);

        modelBuilder.Entity<Client>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Email).HasMaxLength(256);
        });

        modelBuilder.Entity<CollectRequest>(e =>
        {
            e.ToTable("Request");
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Message).HasMaxLength(2000);
            e.Property(x => x.TokenHash).HasMaxLength(32).IsFixedLength();
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(10).IsUnicode(false);
            e.Property(x => x.CreatedUtc).HasPrecision(0);
            e.Property(x => x.CompletedUtc).HasPrecision(0);
            e.Property(x => x.LastReminderUtc).HasPrecision(0);
            e.HasOne(x => x.Client).WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RequestItem>(e =>
        {
            e.ToTable("Item");
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(10).IsUnicode(false);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
            e.Property(x => x.Prompt).HasMaxLength(500);
            e.Property(x => x.TextAnswer).HasMaxLength(10_000);
            e.Property(x => x.ReviewComment).HasMaxLength(1000);
            e.Property(x => x.SubmittedUtc).HasPrecision(0);
            e.HasMany(x => x.Files).WithOne().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ItemFile>(e =>
        {
            e.ToTable("File");
            e.Property(x => x.FileName).HasMaxLength(260);
            e.Property(x => x.ContentType).HasMaxLength(100).IsUnicode(false);
            e.Property(x => x.UploadedUtc).HasPrecision(0);
        });
    }
}
