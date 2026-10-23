using Microsoft.EntityFrameworkCore;

namespace Commonlyz.Modules.SplitShare.Data;

public sealed class ExpenseGroup
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string Currency { get; set; } = "EUR";
    public DateTime CreatedUtc { get; set; }
    public List<Member> Members { get; set; } = [];
}

public sealed class Member
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    public required string Name { get; set; }
}

/// <summary>Money is stored as integer minor units (cents). Invariant: Σ shares = amount.</summary>
public sealed class Expense
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    public int PaidByMemberId { get; set; }
    public long AmountMinor { get; set; }
    public required string Description { get; set; }
    public DateOnly ExpenseDate { get; set; }
    public SplitType SplitType { get; set; }
    public DateTime CreatedUtc { get; set; }
    public List<ExpenseShare> Shares { get; set; } = [];
}

public sealed class ExpenseShare
{
    public int ExpenseId { get; set; }
    public int MemberId { get; set; }
    public long ShareMinor { get; set; }
}

/// <summary>Records that someone paid someone back. No money moves through the app.</summary>
public sealed class Settlement
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    public int FromMemberId { get; set; }
    public int ToMemberId { get; set; }
    public long AmountMinor { get; set; }
    public DateTime SettledUtc { get; set; }
}

public sealed class SplitShareDb(DbContextOptions<SplitShareDb> options) : DbContext(options)
{
    public DbSet<ExpenseGroup> Groups => Set<ExpenseGroup>();
    public DbSet<Member> Members => Set<Member>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<ExpenseShare> Shares => Set<ExpenseShare>();
    public DbSet<Settlement> Settlements => Set<Settlement>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SplitShareModule.Key);

        modelBuilder.Entity<ExpenseGroup>(e =>
        {
            e.ToTable("Group");
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Currency).HasMaxLength(3).IsUnicode(false).IsFixedLength();
            e.Property(x => x.CreatedUtc).HasPrecision(0);
            e.HasMany(x => x.Members).WithOne().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Member>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => new { x.GroupId, x.Name }).IsUnique();
        });

        modelBuilder.Entity<Expense>(e =>
        {
            e.ToTable("Expense", t => t.HasCheckConstraint("CK_Expense_Amount", "[AmountMinor] > 0"));
            e.Property(x => x.Description).HasMaxLength(200);
            e.Property(x => x.SplitType).HasConversion<string>().HasMaxLength(10).IsUnicode(false);
            e.Property(x => x.CreatedUtc).HasPrecision(0);
            e.HasOne<ExpenseGroup>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Member>().WithMany().HasForeignKey(x => x.PaidByMemberId).OnDelete(DeleteBehavior.NoAction);
            e.HasMany(x => x.Shares).WithOne().HasForeignKey(x => x.ExpenseId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.GroupId, x.ExpenseDate });
        });

        modelBuilder.Entity<ExpenseShare>(e =>
        {
            e.ToTable("ExpenseShare", t => t.HasCheckConstraint("CK_ExpenseShare_NonNegative", "[ShareMinor] >= 0"));
            e.HasKey(x => new { x.ExpenseId, x.MemberId });
            e.HasOne<Member>().WithMany().HasForeignKey(x => x.MemberId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<Settlement>(e =>
        {
            e.ToTable("Settlement", t =>
            {
                t.HasCheckConstraint("CK_Settlement_Amount", "[AmountMinor] > 0");
                t.HasCheckConstraint("CK_Settlement_Parties", "[FromMemberId] <> [ToMemberId]");
            });
            e.Property(x => x.SettledUtc).HasPrecision(0);
            e.HasOne<ExpenseGroup>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Member>().WithMany().HasForeignKey(x => x.FromMemberId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<Member>().WithMany().HasForeignKey(x => x.ToMemberId).OnDelete(DeleteBehavior.NoAction);
        });
    }
}
