using Microsoft.EntityFrameworkCore;

namespace Commonlyz.Modules.StockRoom.Data;

public sealed class Location
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
}

public sealed class Item
{
    public int Id { get; set; }
    public required string Sku { get; set; }
    public required string Name { get; set; }
    public string Unit { get; set; } = "pcs";
    public decimal? LowStockThreshold { get; set; }
    public string? Notes { get; set; }
}

public enum MovementReason
{
    Receive,
    Consume,
    Adjust,
    TransferIn,
    TransferOut,
}

/// <summary>
/// Append-only ledger. Quantity on hand is SUM(QuantityDelta); rows are never updated.
/// Corrections are new Adjust movements, so every change in the count is explained.
/// </summary>
public sealed class Movement
{
    public long Id { get; set; }
    public int ItemId { get; set; }
    public int LocationId { get; set; }
    public decimal QuantityDelta { get; set; }
    public MovementReason Reason { get; set; }
    public Guid? TransferId { get; set; }
    public string? Note { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class StockRoomDb(DbContextOptions<StockRoomDb> options) : DbContext(options)
{
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<Item> Items => Set<Item>();
    public DbSet<Movement> Movements => Set<Movement>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(StockRoomModule.Key);

        modelBuilder.Entity<Location>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Description).HasMaxLength(300);
        });

        modelBuilder.Entity<Item>(e =>
        {
            e.Property(x => x.Sku).HasMaxLength(50).IsUnicode(false);
            e.HasIndex(x => x.Sku).IsUnique();
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Unit).HasMaxLength(10);
            e.Property(x => x.LowStockThreshold).HasPrecision(18, 3);
            e.Property(x => x.Notes).HasMaxLength(1000);
        });

        modelBuilder.Entity<Movement>(e =>
        {
            e.ToTable("Movement", t => t.HasCheckConstraint("CK_Movement_NonZero", "[QuantityDelta] <> 0"));
            e.Property(x => x.QuantityDelta).HasPrecision(18, 3);
            e.Property(x => x.Reason).HasConversion<string>().HasMaxLength(12).IsUnicode(false);
            e.Property(x => x.Note).HasMaxLength(300);
            e.Property(x => x.CreatedBy).HasMaxLength(100);
            e.Property(x => x.CreatedUtc).HasPrecision(0);
            e.HasIndex(x => new { x.ItemId, x.LocationId }).IncludeProperties(x => x.QuantityDelta);
            e.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Location>().WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
