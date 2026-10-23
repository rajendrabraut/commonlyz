using Commonlyz.Core;
using Commonlyz.Core.Data;
using Commonlyz.Modules.StockRoom.Data;
using Microsoft.EntityFrameworkCore;

namespace Commonlyz.Modules.StockRoom;

public sealed record ItemStock(Item Item, decimal Total, IReadOnlyDictionary<int, decimal> ByLocation)
{
    public bool IsLow => Item.LowStockThreshold is { } t && Total <= t;
}

public sealed record HistoryRow(Movement Movement, string LocationName, decimal BalanceAtLocation);

public sealed class StockRoomService(IDbContextFactory<StockRoomDb> dbFactory, TimeProvider clock)
{
    // ── Catalogue ────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Location>> ListLocationsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Locations.AsNoTracking().OrderBy(l => l.Name).ToListAsync();
    }

    public async Task SaveLocationAsync(int? id, string name, string? description)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var location = id is { } existing
            ? AppException.NotFound(await db.Locations.FindAsync(existing), "Location")
            : db.Locations.Add(new Location { Name = "" }).Entity;
        location.Name = Text.Required(name, "Name", 100);
        location.Description = Text.Optional(description, "Description", 300);
        await SaveUniqueAsync(db, $"A location named '{location.Name}' already exists.");
    }

    public async Task<int> SaveItemAsync(int? id, string sku, string name, string unit, decimal? lowStock, string? notes)
    {
        AppException.ThrowIf(lowStock < 0, "Low-stock threshold can't be negative.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = id is { } existing
            ? AppException.NotFound(await db.Items.FindAsync(existing), "Item")
            : db.Items.Add(new Item { Sku = "", Name = "" }).Entity;
        item.Sku = Text.Required(sku, "SKU", 50).ToUpperInvariant();
        item.Name = Text.Required(name, "Name", 200);
        item.Unit = Text.Required(unit, "Unit", 10);
        item.LowStockThreshold = lowStock;
        item.Notes = Text.Optional(notes, "Notes", 1000);
        await SaveUniqueAsync(db, $"SKU '{item.Sku}' is already used.");
        return item.Id;
    }

    public async Task<IReadOnlyList<ItemStock>> ListStockAsync(string? search = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var items = db.Items.AsNoTracking();
        if (Text.NullIfBlank(search) is { } s)
            items = items.Where(i => i.Sku.Contains(s) || i.Name.Contains(s));
        var list = await items.OrderBy(i => i.Name).ToListAsync();

        var levels = await db.Movements.GroupBy(m => new { m.ItemId, m.LocationId })
            .Select(g => new { g.Key.ItemId, g.Key.LocationId, Qty = g.Sum(m => m.QuantityDelta) })
            .ToListAsync();
        var byItem = levels.GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.Where(x => x.Qty != 0).ToDictionary(x => x.LocationId, x => x.Qty));

        return [.. list.Select(i =>
        {
            var byLocation = byItem.GetValueOrDefault(i.Id) ?? [];
            return new ItemStock(i, byLocation.Values.Sum(), byLocation);
        })];
    }

    public async Task<(ItemStock Stock, IReadOnlyList<HistoryRow> History)> GetItemAsync(int itemId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = AppException.NotFound(await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId), "Item");
        var movements = await db.Movements.AsNoTracking().Where(m => m.ItemId == itemId).OrderBy(m => m.Id).ToListAsync();
        var locations = await db.Locations.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name);

        // Running balance per location explains every change in the count.
        var running = new Dictionary<int, decimal>();
        var history = new List<HistoryRow>(movements.Count);
        foreach (var m in movements)
        {
            running[m.LocationId] = running.GetValueOrDefault(m.LocationId) + m.QuantityDelta;
            history.Add(new HistoryRow(m, locations.GetValueOrDefault(m.LocationId, "?"), running[m.LocationId]));
        }
        history.Reverse();

        var byLocation = running.Where(r => r.Value != 0).ToDictionary(r => r.Key, r => r.Value);
        return (new ItemStock(item, byLocation.Values.Sum(), byLocation), history);
    }

    // ── Movements ────────────────────────────────────────────────────────────

    public Task ReceiveAsync(int itemId, int locationId, decimal quantity, string? note, string user) =>
        MoveAsync(itemId, locationId, Positive(quantity), MovementReason.Receive, note, user);

    public Task ConsumeAsync(int itemId, int locationId, decimal quantity, string? note, string user) =>
        MoveAsync(itemId, locationId, -Positive(quantity), MovementReason.Consume, note, user);

    /// <summary>Stocktake: sets the counted quantity; the ledger records the difference.</summary>
    public async Task AdjustToAsync(int itemId, int locationId, decimal countedQuantity, string? note, string user)
    {
        AppException.ThrowIf(countedQuantity < 0, "A count can't be negative.");
        await InTransactionAsync(async db =>
        {
            var current = await LockedBalanceAsync(db, itemId, locationId);
            var delta = countedQuantity - current;
            if (delta != 0)
                Add(db, itemId, locationId, delta, MovementReason.Adjust, note ?? "Stocktake", user, null);
        });
    }

    /// <summary>Two ledger rows (out of one location, into another) committed together.</summary>
    public async Task TransferAsync(int itemId, int fromLocationId, int toLocationId, decimal quantity, string? note, string user)
    {
        AppException.ThrowIf(fromLocationId == toLocationId, "Choose two different locations.");
        var qty = Positive(quantity);
        await InTransactionAsync(async db =>
        {
            var available = await LockedBalanceAsync(db, itemId, fromLocationId);
            AppException.ThrowIf(available < qty, $"Only {available:0.###} available at the source location.");
            var transferId = Guid.NewGuid();
            Add(db, itemId, fromLocationId, -qty, MovementReason.TransferOut, note, user, transferId);
            Add(db, itemId, toLocationId, qty, MovementReason.TransferIn, note, user, transferId);
        });
    }

    private Task MoveAsync(int itemId, int locationId, decimal delta, MovementReason reason, string? note, string user) =>
        InTransactionAsync(async db =>
        {
            if (delta < 0)
            {
                var available = await LockedBalanceAsync(db, itemId, locationId);
                AppException.ThrowIf(available + delta < 0, $"Only {available:0.###} in stock at that location.");
            }
            Add(db, itemId, locationId, delta, reason, note, user, null);
        });

    /// <summary>
    /// Current balance with UPDLOCK/HOLDLOCK on the item/location key range, so two concurrent withdrawals
    /// can't both see the same stock and drive it negative.
    /// </summary>
    private static Task<decimal> LockedBalanceAsync(StockRoomDb db, int itemId, int locationId) =>
        db.Database.SqlQuery<decimal>($"""
            SELECT ISNULL(SUM([QuantityDelta]), 0) AS [Value] FROM [stockroom].[Movement] WITH (UPDLOCK, HOLDLOCK)
            WHERE [ItemId] = {itemId} AND [LocationId] = {locationId}
            """).SingleAsync();

    private void Add(StockRoomDb db, int itemId, int locationId, decimal delta, MovementReason reason, string? note, string user, Guid? transferId) =>
        db.Movements.Add(new Movement
        {
            ItemId = itemId,
            LocationId = locationId,
            QuantityDelta = delta,
            Reason = reason,
            Note = Text.Optional(note, "Note", 300),
            CreatedBy = user.Length > 100 ? user[..100] : user,
            CreatedUtc = clock.UtcNowSeconds(),
            TransferId = transferId,
        });

    private async Task InTransactionAsync(Func<StockRoomDb, Task> work)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.InTransactionAsync(async () =>
        {
            await work(db);
            await db.SaveChangesAsync();
        }, System.Data.IsolationLevel.Serializable);
    }

    private static decimal Positive(decimal quantity)
    {
        AppException.ThrowIf(quantity <= 0, "Quantity must be greater than zero.");
        AppException.ThrowIf(decimal.Round(quantity, 3) != quantity, "Use at most 3 decimal places.");
        return quantity;
    }

    private static async Task SaveUniqueAsync(StockRoomDb db, string message)
    {
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            throw new AppException(message);
        }
    }
}
