using Commonlyz.Core;
using Commonlyz.Core.Data;
using Commonlyz.Modules.TaskBoard.Data;
using Microsoft.EntityFrameworkCore;

namespace Commonlyz.Modules.TaskBoard;

public sealed record BoardView(Board Board, IReadOnlyList<(BoardColumn Column, IReadOnlyList<Card> Cards)> Columns);

public sealed class TaskBoardService(IDbContextFactory<TaskBoardDb> dbFactory, TimeProvider clock)
{
    /// <summary>Below this gap between neighbours a column is renumbered (1, 2, 3, …) before inserting.</summary>
    private const decimal MinGap = 0.000001m;

    public async Task<IReadOnlyList<(Board Board, int OpenCards)>> ListBoardsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var boards = await db.Boards.AsNoTracking().OrderBy(b => b.Name).ToListAsync();
        var counts = await db.Cards.Where(c => c.ArchivedUtc == null).GroupBy(c => c.BoardId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        return [.. boards.Select(b => (b, counts.GetValueOrDefault(b.Id)))];
    }

    public async Task<int> CreateBoardAsync(string name)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var board = new Board { Name = Text.Required(name, "Board name", 100), CreatedUtc = clock.UtcNowSeconds() };
        db.Boards.Add(board);
        await db.SaveChangesAsync();
        db.Columns.AddRange(new[] { "To do", "Doing", "Done" }.Select((n, i) => new BoardColumn { BoardId = board.Id, Name = n, Position = i + 1 }));
        await db.SaveChangesAsync();
        return board.Id;
    }

    public async Task DeleteBoardAsync(int boardId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.InTransactionAsync(async () =>
        {
            await db.Cards.Where(c => c.BoardId == boardId).ExecuteDeleteAsync();
            await db.Boards.Where(b => b.Id == boardId).ExecuteDeleteAsync();
        });
    }

    public async Task<BoardView> GetBoardAsync(int boardId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var board = AppException.NotFound(await db.Boards.AsNoTracking().FirstOrDefaultAsync(b => b.Id == boardId), "Board");
        var columns = await db.Columns.AsNoTracking().Where(c => c.BoardId == boardId).OrderBy(c => c.Position).ToListAsync();
        var cards = await db.Cards.AsNoTracking().Include(c => c.Checklist)
            .Where(c => c.BoardId == boardId && c.ArchivedUtc == null).OrderBy(c => c.Position).ToListAsync();
        return new BoardView(board, [.. columns.Select(col => (col, (IReadOnlyList<Card>)[.. cards.Where(c => c.ColumnId == col.Id)]))]);
    }

    // ── Columns ──────────────────────────────────────────────────────────────

    public async Task AddColumnAsync(int boardId, string name)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var last = await db.Columns.Where(c => c.BoardId == boardId).MaxAsync(c => (decimal?)c.Position) ?? 0;
        db.Columns.Add(new BoardColumn { BoardId = boardId, Name = Text.Required(name, "Column name", 60), Position = Math.Floor(last) + 1 });
        await db.SaveChangesAsync();
    }

    public async Task RenameColumnAsync(int columnId, string name)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var column = AppException.NotFound(await db.Columns.FindAsync(columnId), "Column");
        column.Name = Text.Required(name, "Column name", 60);
        await db.SaveChangesAsync();
    }

    public async Task MoveColumnAsync(int columnId, int direction)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var column = AppException.NotFound(await db.Columns.FindAsync(columnId), "Column");
        var siblings = await db.Columns.Where(c => c.BoardId == column.BoardId).OrderBy(c => c.Position).ToListAsync();
        var index = siblings.FindIndex(c => c.Id == columnId);
        var target = index + Math.Sign(direction);
        if (target < 0 || target >= siblings.Count)
            return;
        (siblings[index], siblings[target]) = (siblings[target], siblings[index]);
        for (var i = 0; i < siblings.Count; i++)
            siblings[i].Position = i + 1;
        await db.SaveChangesAsync();
    }

    public async Task DeleteColumnAsync(int columnId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        AppException.ThrowIf(await db.Cards.AnyAsync(c => c.ColumnId == columnId && c.ArchivedUtc == null), "Move or archive this column's cards first.");
        await db.Columns.Where(c => c.Id == columnId).ExecuteDeleteAsync();
    }

    // ── Cards ────────────────────────────────────────────────────────────────

    public async Task AddCardAsync(int columnId, string title)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var column = AppException.NotFound(await db.Columns.FindAsync(columnId), "Column");
        var last = await db.Cards.Where(c => c.ColumnId == columnId && c.ArchivedUtc == null).MaxAsync(c => (decimal?)c.Position) ?? 0;
        db.Cards.Add(new Card
        {
            BoardId = column.BoardId, ColumnId = columnId, Title = Text.Required(title, "Card title", 300),
            Position = Math.Floor(last) + 1, CreatedUtc = clock.UtcNowSeconds(),
        });
        await db.SaveChangesAsync();
    }

    public async Task<Card> GetCardAsync(int cardId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var card = AppException.NotFound(await db.Cards.AsNoTracking().Include(c => c.Checklist).FirstOrDefaultAsync(c => c.Id == cardId), "Card");
        card.Checklist = [.. card.Checklist.OrderBy(i => i.Position)];
        return card;
    }

    public async Task UpdateCardAsync(int cardId, string title, string? description, DateOnly? dueDate, string? labelColor)
    {
        AppException.ThrowIf(labelColor is not null && !Text.IsColor(labelColor), "Invalid label colour.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var card = AppException.NotFound(await db.Cards.FindAsync(cardId), "Card");
        card.Title = Text.Required(title, "Title", 300);
        card.Description = Text.Optional(description, "Description", 10_000);
        card.DueDate = dueDate;
        card.LabelColor = labelColor;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Moves a card to <paramref name="columnId"/>, just before <paramref name="beforeCardId"/> (or at the end).
    /// Writes a single position: the midpoint of the new neighbours.
    /// </summary>
    public async Task MoveCardAsync(int cardId, int columnId, int? beforeCardId)
    {
        if (beforeCardId == cardId)
            return;
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.InTransactionAsync(async () =>
        {
            var card = AppException.NotFound(await db.Cards.FindAsync(cardId), "Card");
            var column = AppException.NotFound(await db.Columns.FindAsync(columnId), "Column");
            AppException.ThrowIf(column.BoardId != card.BoardId, "Cards can only move within their board.");

            var position = await PositionBeforeAsync(db, columnId, cardId, beforeCardId);
            if (position is null)
            {
                await RebalanceAsync(db, columnId);
                position = await PositionBeforeAsync(db, columnId, cardId, beforeCardId);
            }

            card.ColumnId = columnId;
            card.Position = position!.Value;
            await db.SaveChangesAsync();
        });
    }

    /// <returns>The new position, or null when the neighbours are too close and the column needs rebalancing.</returns>
    private static async Task<decimal?> PositionBeforeAsync(TaskBoardDb db, int columnId, int movingCardId, int? beforeCardId)
    {
        var positions = await db.Cards.Where(c => c.ColumnId == columnId && c.ArchivedUtc == null && c.Id != movingCardId)
            .OrderBy(c => c.Position).Select(c => new { c.Id, c.Position }).ToListAsync();

        var index = beforeCardId is { } before ? positions.FindIndex(p => p.Id == before) : -1;
        if (index < 0)
            return positions.Count == 0 ? 1 : Math.Floor(positions[^1].Position) + 1;

        var next = positions[index].Position;
        var previous = index == 0 ? next - 1 : positions[index - 1].Position;
        return next - previous < MinGap * 2 ? null : (previous + next) / 2;
    }

    private static async Task RebalanceAsync(TaskBoardDb db, int columnId)
    {
        var cards = await db.Cards.Where(c => c.ColumnId == columnId && c.ArchivedUtc == null).OrderBy(c => c.Position).ToListAsync();
        for (var i = 0; i < cards.Count; i++)
            cards[i].Position = i + 1;
        await db.SaveChangesAsync();
    }

    public async Task ArchiveCardAsync(int cardId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = clock.UtcNowSeconds();
        await db.Cards.Where(c => c.Id == cardId).ExecuteUpdateAsync(s => s.SetProperty(c => c.ArchivedUtc, now));
    }

    public async Task DeleteCardAsync(int cardId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Cards.Where(c => c.Id == cardId).ExecuteDeleteAsync();
    }

    // ── Checklist ────────────────────────────────────────────────────────────

    public async Task AddChecklistItemAsync(int cardId, string text)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        AppException.ThrowIf(!await db.Cards.AnyAsync(c => c.Id == cardId), "Card not found.");
        var next = (await db.ChecklistItems.Where(i => i.CardId == cardId).MaxAsync(i => (int?)i.Position) ?? 0) + 1;
        db.ChecklistItems.Add(new ChecklistItem { CardId = cardId, Text = Text.Required(text, "Item", 300), Position = next });
        await db.SaveChangesAsync();
    }

    public async Task ToggleChecklistItemAsync(int itemId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.ChecklistItems.Where(i => i.Id == itemId).ExecuteUpdateAsync(s => s.SetProperty(i => i.IsDone, i => !i.IsDone));
    }

    public async Task DeleteChecklistItemAsync(int itemId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.ChecklistItems.Where(i => i.Id == itemId).ExecuteDeleteAsync();
    }
}
