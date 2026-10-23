using Microsoft.EntityFrameworkCore;

namespace Commonlyz.Modules.TaskBoard.Data;

public sealed class Board
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class BoardColumn
{
    public int Id { get; set; }
    public int BoardId { get; set; }
    public required string Name { get; set; }
    public decimal Position { get; set; }
}

/// <summary>
/// Ordered by a fractional <see cref="Position"/>: moving a card writes one row (the midpoint between its
/// new neighbours) instead of renumbering the whole column.
/// </summary>
public sealed class Card
{
    public int Id { get; set; }
    public int BoardId { get; set; }
    public int ColumnId { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public decimal Position { get; set; }
    public DateOnly? DueDate { get; set; }
    public string? LabelColor { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? ArchivedUtc { get; set; }
    public List<ChecklistItem> Checklist { get; set; } = [];
}

public sealed class ChecklistItem
{
    public int Id { get; set; }
    public int CardId { get; set; }
    public required string Text { get; set; }
    public bool IsDone { get; set; }
    public int Position { get; set; }
}

public sealed class TaskBoardDb(DbContextOptions<TaskBoardDb> options) : DbContext(options)
{
    public DbSet<Board> Boards => Set<Board>();
    public DbSet<BoardColumn> Columns => Set<BoardColumn>();
    public DbSet<Card> Cards => Set<Card>();
    public DbSet<ChecklistItem> ChecklistItems => Set<ChecklistItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(TaskBoardModule.Key);

        modelBuilder.Entity<Board>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.CreatedUtc).HasPrecision(0);
        });

        modelBuilder.Entity<BoardColumn>(e =>
        {
            e.ToTable("Column");
            e.Property(x => x.Name).HasMaxLength(60);
            e.Property(x => x.Position).HasPrecision(19, 9);
            e.HasIndex(x => new { x.BoardId, x.Position });
            e.HasOne<Board>().WithMany().HasForeignKey(x => x.BoardId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Card>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Description).HasMaxLength(10_000);
            e.Property(x => x.Position).HasPrecision(19, 9);
            e.Property(x => x.LabelColor).HasMaxLength(7).IsUnicode(false);
            e.Property(x => x.CreatedUtc).HasPrecision(0);
            e.Property(x => x.ArchivedUtc).HasPrecision(0);
            e.HasIndex(x => new { x.ColumnId, x.Position }).HasFilter("[ArchivedUtc] IS NULL");
            e.HasOne<BoardColumn>().WithMany().HasForeignKey(x => x.ColumnId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Board>().WithMany().HasForeignKey(x => x.BoardId).OnDelete(DeleteBehavior.NoAction);
            e.HasMany(x => x.Checklist).WithOne().HasForeignKey(x => x.CardId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChecklistItem>(e => e.Property(x => x.Text).HasMaxLength(300));
    }
}
