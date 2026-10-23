using Microsoft.EntityFrameworkCore;

namespace Commonlyz.Modules.BudgetPlan.Data;

public enum AccountType
{
    Checking,
    Savings,
    Cash,
    CreditCard,
}

public sealed class Account
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public AccountType Type { get; set; }
    public DateTime? ClosedUtc { get; set; }
}

public sealed class CategoryGroup
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public int Position { get; set; }
    public List<Category> Categories { get; set; } = [];
}

public sealed class Category
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    public required string Name { get; set; }
    public int Position { get; set; }
    public bool IsHidden { get; set; }
}

/// <summary>Money assigned to a category for a month. <see cref="Month"/> is always the 1st.</summary>
public sealed class MonthlyAssignment
{
    public int CategoryId { get; set; }
    public DateOnly Month { get; set; }
    public long AssignedMinor { get; set; }
}

/// <summary>
/// Amounts are integer minor units; negative = money out. An inflow without a category is income
/// ("Ready to Assign"). Transfers are a pair of rows sharing <see cref="TransferId"/>, with no category.
/// </summary>
public sealed class Txn
{
    public long Id { get; set; }
    public int AccountId { get; set; }
    public DateOnly Date { get; set; }
    public string? Payee { get; set; }
    public int? CategoryId { get; set; }
    public long AmountMinor { get; set; }
    public string? Memo { get; set; }
    public bool Cleared { get; set; }
    public Guid? TransferId { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class BudgetPlanDb(DbContextOptions<BudgetPlanDb> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<CategoryGroup> Groups => Set<CategoryGroup>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<MonthlyAssignment> Assignments => Set<MonthlyAssignment>();
    public DbSet<Txn> Transactions => Set<Txn>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(BudgetPlanModule.Key);

        modelBuilder.Entity<Account>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(12).IsUnicode(false);
            e.Property(x => x.ClosedUtc).HasPrecision(0);
        });

        modelBuilder.Entity<CategoryGroup>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasMany(x => x.Categories).WithOne().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Category>(e => e.Property(x => x.Name).HasMaxLength(100));

        modelBuilder.Entity<MonthlyAssignment>(e =>
        {
            e.ToTable("Assignment", t => t.HasCheckConstraint("CK_Assignment_FirstOfMonth", "DAY([Month]) = 1"));
            e.HasKey(x => new { x.CategoryId, x.Month });
            e.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Txn>(e =>
        {
            e.ToTable("Transaction", t =>
            {
                t.HasCheckConstraint("CK_Transaction_NonZero", "[AmountMinor] <> 0");
                t.HasCheckConstraint("CK_Transaction_TransferNoCategory", "[TransferId] IS NULL OR [CategoryId] IS NULL");
            });
            e.Property(x => x.Payee).HasMaxLength(100);
            e.Property(x => x.Memo).HasMaxLength(300);
            e.Property(x => x.CreatedUtc).HasPrecision(0);
            e.HasIndex(x => new { x.AccountId, x.Date });
            e.HasIndex(x => new { x.CategoryId, x.Date }).HasFilter("[CategoryId] IS NOT NULL");
            e.HasIndex(x => x.TransferId).HasFilter("[TransferId] IS NOT NULL");
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
