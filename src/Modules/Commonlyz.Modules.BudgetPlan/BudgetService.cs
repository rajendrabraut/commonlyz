using Commonlyz.Core;
using Commonlyz.Core.Data;
using Commonlyz.Modules.BudgetPlan.Data;
using Microsoft.EntityFrameworkCore;

namespace Commonlyz.Modules.BudgetPlan;

public sealed record CategoryLine(Category Category, long Assigned, long Activity, long Available);

public sealed record GroupLines(CategoryGroup Group, IReadOnlyList<CategoryLine> Lines);

/// <param name="ReadyToAssign">Income received up to the end of the month minus everything assigned up to that month.</param>
public sealed record BudgetMonth(DateOnly Month, long ReadyToAssign, IReadOnlyList<GroupLines> Groups)
{
    public long TotalAvailable => Groups.Sum(g => g.Lines.Sum(l => l.Available));
}

public sealed record AccountBalance(Account Account, long Balance, long Cleared);

public sealed class BudgetService(IDbContextFactory<BudgetPlanDb> dbFactory, TimeProvider clock)
{
    public static DateOnly MonthOf(DateOnly date) => new(date.Year, date.Month, 1);

    // ── Budget ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Envelope maths. For each category: Available = Σ assigned (all months ≤ M) + Σ activity (all dates ≤ end of M).
    /// Overspending carries forward as a negative balance. Invariant (tested):
    /// Σ Available + Ready to Assign = Σ account balances as of the end of M.
    /// </summary>
    public async Task<BudgetMonth> GetMonthAsync(DateOnly month)
    {
        month = MonthOf(month);
        var next = month.AddMonths(1);
        await using var db = await dbFactory.CreateDbContextAsync();

        var groups = await db.Groups.AsNoTracking().Include(g => g.Categories).OrderBy(g => g.Position).ToListAsync();
        var assigned = await db.Assignments.Where(a => a.Month <= month)
            .GroupBy(a => a.CategoryId)
            .Select(g => new { g.Key, Total = g.Sum(a => a.AssignedMinor), ThisMonth = g.Where(a => a.Month == month).Sum(a => a.AssignedMinor) })
            .ToDictionaryAsync(x => x.Key);
        var activity = await db.Transactions.Where(t => t.CategoryId != null && t.Date < next)
            .GroupBy(t => t.CategoryId!.Value)
            .Select(g => new { g.Key, Total = g.Sum(t => t.AmountMinor), ThisMonth = g.Where(t => t.Date >= month).Sum(t => t.AmountMinor) })
            .ToDictionaryAsync(x => x.Key);

        var income = await db.Transactions.Where(t => t.CategoryId == null && t.TransferId == null && t.Date < next).SumAsync(t => (long?)t.AmountMinor) ?? 0;
        var totalAssigned = assigned.Values.Sum(a => a.Total);

        var lines = groups.Select(g => new GroupLines(g, [.. g.Categories.OrderBy(c => c.Position).Select(c =>
        {
            var a = assigned.GetValueOrDefault(c.Id);
            var act = activity.GetValueOrDefault(c.Id);
            return new CategoryLine(c, a?.ThisMonth ?? 0, act?.ThisMonth ?? 0, (a?.Total ?? 0) + (act?.Total ?? 0));
        })])).ToList();

        return new BudgetMonth(month, income - totalAssigned, lines);
    }

    public async Task AssignAsync(int categoryId, DateOnly month, long amountMinor)
    {
        month = MonthOf(month);
        await using var db = await dbFactory.CreateDbContextAsync();
        AppException.ThrowIf(!await db.Categories.AnyAsync(c => c.Id == categoryId), "Category not found.");
        var row = await db.Assignments.FindAsync(categoryId, month);
        if (row is null)
            db.Assignments.Add(new MonthlyAssignment { CategoryId = categoryId, Month = month, AssignedMinor = amountMinor });
        else
            row.AssignedMinor = amountMinor;
        await db.SaveChangesAsync();
    }

    // ── Categories ───────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<CategoryGroup>> GroupsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var groups = await db.Groups.AsNoTracking().Include(g => g.Categories).OrderBy(g => g.Position).ToListAsync();
        foreach (var g in groups)
            g.Categories = [.. g.Categories.OrderBy(c => c.Position)];
        return groups;
    }

    public async Task<int> AddGroupAsync(string name)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var group = new CategoryGroup
        {
            Name = Text.Required(name, "Group name", 100),
            Position = (await db.Groups.MaxAsync(g => (int?)g.Position) ?? 0) + 1,
        };
        db.Groups.Add(group);
        await db.SaveChangesAsync();
        return group.Id;
    }

    public async Task AddCategoryAsync(int groupId, string name)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        AppException.ThrowIf(!await db.Groups.AnyAsync(g => g.Id == groupId), "Group not found.");
        db.Categories.Add(new Category
        {
            GroupId = groupId,
            Name = Text.Required(name, "Category name", 100),
            Position = (await db.Categories.Where(c => c.GroupId == groupId).MaxAsync(c => (int?)c.Position) ?? 0) + 1,
        });
        await db.SaveChangesAsync();
    }

    public async Task RenameCategoryAsync(int categoryId, string name)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var category = AppException.NotFound(await db.Categories.FindAsync(categoryId), "Category");
        category.Name = Text.Required(name, "Category name", 100);
        await db.SaveChangesAsync();
    }

    /// <summary>Starter categories for an empty budget.</summary>
    public async Task SeedStarterCategoriesAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.Groups.AnyAsync())
            return;
        var starter = new (string Group, string[] Categories)[]
        {
            ("Bills", ["Rent / mortgage", "Utilities", "Phone & internet", "Insurance"]),
            ("Everyday", ["Groceries", "Transport", "Eating out", "Household"]),
            ("Goals", ["Emergency fund", "Holidays", "Gifts"]),
        };
        for (var i = 0; i < starter.Length; i++)
        {
            db.Groups.Add(new CategoryGroup
            {
                Name = starter[i].Group,
                Position = i + 1,
                Categories = [.. starter[i].Categories.Select((c, n) => new Category { Name = c, Position = n + 1 })],
            });
        }
        await db.SaveChangesAsync();
    }

    // ── Accounts & transactions ──────────────────────────────────────────────

    public async Task<IReadOnlyList<AccountBalance>> AccountsAsync(bool includeClosed = false)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var accounts = await db.Accounts.AsNoTracking().Where(a => includeClosed || a.ClosedUtc == null).OrderBy(a => a.Name).ToListAsync();
        var sums = await db.Transactions.GroupBy(t => t.AccountId)
            .Select(g => new { g.Key, Balance = g.Sum(t => t.AmountMinor), Cleared = g.Where(t => t.Cleared).Sum(t => t.AmountMinor) })
            .ToDictionaryAsync(x => x.Key);
        return [.. accounts.Select(a => new AccountBalance(a, sums.GetValueOrDefault(a.Id)?.Balance ?? 0, sums.GetValueOrDefault(a.Id)?.Cleared ?? 0))];
    }

    /// <summary>Creates an account; a non-zero starting balance is recorded as an income (Ready to Assign) transaction.</summary>
    public async Task<int> CreateAccountAsync(string name, AccountType type, long startingBalanceMinor, DateOnly date)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var account = new Account { Name = Text.Required(name, "Account name", 100), Type = type };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        if (startingBalanceMinor != 0)
        {
            db.Transactions.Add(new Txn
            {
                AccountId = account.Id, Date = date, Payee = "Starting balance", AmountMinor = startingBalanceMinor, Cleared = true, CreatedUtc = clock.UtcNowSeconds(),
            });
            await db.SaveChangesAsync();
        }
        return account.Id;
    }

    public async Task<(Account Account, IReadOnlyList<Txn> Transactions)> RegisterAsync(int accountId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var account = AppException.NotFound(await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId), "Account");
        var txns = await db.Transactions.AsNoTracking().Where(t => t.AccountId == accountId)
            .OrderByDescending(t => t.Date).ThenByDescending(t => t.Id).Take(500).ToListAsync();
        return (account, txns);
    }

    /// <param name="categoryId">Required for outflows. An inflow without a category is income for Ready to Assign.</param>
    public async Task AddTransactionAsync(int accountId, DateOnly date, string? payee, int? categoryId, long amountMinor, string? memo)
    {
        AppException.ThrowIf(amountMinor == 0, "Amount can't be zero.");
        AppException.ThrowIf(amountMinor < 0 && categoryId is null, "Choose a category for money going out.");
        await using var db = await dbFactory.CreateDbContextAsync();
        AppException.ThrowIf(!await db.Accounts.AnyAsync(a => a.Id == accountId && a.ClosedUtc == null), "Account not found or closed.");
        AppException.ThrowIf(categoryId is { } c && !await db.Categories.AnyAsync(x => x.Id == c), "Category not found.");
        db.Transactions.Add(new Txn
        {
            AccountId = accountId, Date = date, Payee = Text.Optional(payee, "Payee", 100), CategoryId = categoryId,
            AmountMinor = amountMinor, Memo = Text.Optional(memo, "Memo", 300), CreatedUtc = clock.UtcNowSeconds(),
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Moves money between accounts: two linked rows, no category, so the budget is unaffected.</summary>
    public async Task TransferAsync(int fromAccountId, int toAccountId, DateOnly date, long amountMinor, string? memo)
    {
        AppException.ThrowIf(amountMinor <= 0, "Transfer amount must be positive.");
        AppException.ThrowIf(fromAccountId == toAccountId, "Choose two different accounts.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var names = await db.Accounts.Where(a => (a.Id == fromAccountId || a.Id == toAccountId) && a.ClosedUtc == null).ToDictionaryAsync(a => a.Id, a => a.Name);
        AppException.ThrowIf(names.Count != 2, "Both accounts must exist and be open.");

        var transferId = Guid.NewGuid();
        var now = clock.UtcNowSeconds();
        memo = Text.Optional(memo, "Memo", 300);
        db.Transactions.AddRange(
            new Txn { AccountId = fromAccountId, Date = date, Payee = $"Transfer to {names[toAccountId]}", AmountMinor = -amountMinor, TransferId = transferId, Memo = memo, CreatedUtc = now },
            new Txn { AccountId = toAccountId, Date = date, Payee = $"Transfer from {names[fromAccountId]}", AmountMinor = amountMinor, TransferId = transferId, Memo = memo, CreatedUtc = now });
        await db.SaveChangesAsync();   // one SaveChanges = one transaction
    }

    public async Task ToggleClearedAsync(long txnId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Transactions.Where(t => t.Id == txnId).ExecuteUpdateAsync(s => s.SetProperty(t => t.Cleared, t => !t.Cleared));
    }

    /// <summary>Deleting one side of a transfer deletes both.</summary>
    public async Task DeleteTransactionAsync(long txnId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var transferId = await db.Transactions.Where(t => t.Id == txnId).Select(t => t.TransferId).FirstOrDefaultAsync();
        await (transferId is { } id
            ? db.Transactions.Where(t => t.TransferId == id).ExecuteDeleteAsync()
            : db.Transactions.Where(t => t.Id == txnId).ExecuteDeleteAsync());
    }

    public async Task SetClosedAsync(int accountId, bool closed)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var account = AppException.NotFound(await db.Accounts.FindAsync(accountId), "Account");
        account.ClosedUtc = closed ? clock.UtcNowSeconds() : null;
        await db.SaveChangesAsync();
    }
}
