using Commonlyz.Core;
using Commonlyz.Core.Data;
using Commonlyz.Modules.SplitShare.Data;
using Microsoft.EntityFrameworkCore;

namespace Commonlyz.Modules.SplitShare;

public sealed record GroupView(
    ExpenseGroup Group,
    IReadOnlyList<Expense> Expenses,
    IReadOnlyList<Settlement> Settlements,
    IReadOnlyDictionary<int, long> Balances,
    IReadOnlyList<(int From, int To, long AmountMinor)> SuggestedTransfers)
{
    public string MemberName(int id) => Group.Members.FirstOrDefault(m => m.Id == id)?.Name ?? "?";
}

public sealed class SplitShareService(IDbContextFactory<SplitShareDb> dbFactory, TimeProvider clock)
{
    public async Task<IReadOnlyList<(ExpenseGroup Group, long TotalMinor)>> ListGroupsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var groups = await db.Groups.AsNoTracking().Include(g => g.Members).OrderByDescending(g => g.Id).ToListAsync();
        var totals = await db.Expenses.GroupBy(e => e.GroupId).Select(g => new { g.Key, Total = g.Sum(e => e.AmountMinor) }).ToDictionaryAsync(x => x.Key, x => x.Total);
        return [.. groups.Select(g => (g, totals.GetValueOrDefault(g.Id)))];
    }

    public async Task<int> CreateGroupAsync(string name, string currency, IEnumerable<string> memberNames)
    {
        currency = Text.Required(currency, "Currency", 3).ToUpperInvariant();
        AppException.ThrowIf(currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper), "Currency must be a 3-letter code like EUR.");
        var members = memberNames.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        AppException.ThrowIf(members.Count < 2, "Add at least two people.");
        AppException.ThrowIf(members.Any(m => m.Length > 100), "Names must be at most 100 characters.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var group = new ExpenseGroup
        {
            Name = Text.Required(name, "Group name", 100),
            Currency = currency,
            CreatedUtc = clock.UtcNowSeconds(),
            Members = [.. members.Select(m => new Member { Name = m })],
        };
        db.Groups.Add(group);
        await db.SaveChangesAsync();
        return group.Id;
    }

    public async Task AddMemberAsync(int groupId, string name)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        AppException.ThrowIf(!await db.Groups.AnyAsync(g => g.Id == groupId), "Group not found.");
        db.Members.Add(new Member { GroupId = groupId, Name = Text.Required(name, "Name", 100) });
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            throw new AppException($"{name.Trim()} is already in the group.");
        }
    }

    public async Task DeleteGroupAsync(int groupId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Settlements.Where(s => s.GroupId == groupId).ExecuteDeleteAsync();
        await db.Expenses.Where(e => e.GroupId == groupId).ExecuteDeleteAsync();
        await db.Groups.Where(g => g.Id == groupId).ExecuteDeleteAsync();
        await tx.CommitAsync();
    }

    public async Task<GroupView> GetGroupAsync(int groupId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var group = AppException.NotFound(await db.Groups.AsNoTracking().Include(g => g.Members).FirstOrDefaultAsync(g => g.Id == groupId), "Group");
        var expenses = await db.Expenses.AsNoTracking().Include(e => e.Shares).Where(e => e.GroupId == groupId)
            .OrderByDescending(e => e.ExpenseDate).ThenByDescending(e => e.Id).ToListAsync();
        var settlements = await db.Settlements.AsNoTracking().Where(s => s.GroupId == groupId).OrderByDescending(s => s.SettledUtc).ToListAsync();

        var balances = ComputeBalances(group.Members.Select(m => m.Id), expenses, settlements);
        return new GroupView(group, expenses, settlements, balances, SplitCalculator.SimplifyDebts(balances));
    }

    /// <summary>Positive = is owed money. paid − owed + settlements sent − settlements received. Always sums to zero.</summary>
    public static Dictionary<int, long> ComputeBalances(IEnumerable<int> memberIds, IEnumerable<Expense> expenses, IEnumerable<Settlement> settlements)
    {
        var balances = memberIds.ToDictionary(id => id, _ => 0L);
        foreach (var e in expenses)
        {
            balances[e.PaidByMemberId] += e.AmountMinor;
            foreach (var s in e.Shares)
                balances[s.MemberId] -= s.ShareMinor;
        }
        foreach (var s in settlements)
        {
            balances[s.FromMemberId] += s.AmountMinor;
            balances[s.ToMemberId] -= s.AmountMinor;
        }
        return balances;
    }

    public async Task AddExpenseAsync(int groupId, string description, long amountMinor, int paidByMemberId, DateOnly date,
        SplitType splitType, IReadOnlyList<(int MemberId, decimal Value)> parts)
    {
        var shares = SplitCalculator.Split(amountMinor, splitType, parts);

        await using var db = await dbFactory.CreateDbContextAsync();
        var memberIds = await db.Members.Where(m => m.GroupId == groupId).Select(m => m.Id).ToListAsync();
        AppException.ThrowIf(!memberIds.Contains(paidByMemberId), "The payer isn't in this group.");
        AppException.ThrowIf(shares.Keys.Any(id => !memberIds.Contains(id)), "Everyone in the split must be in this group.");

        db.Expenses.Add(new Expense
        {
            GroupId = groupId,
            Description = Text.Required(description, "Description", 200),
            AmountMinor = amountMinor,
            PaidByMemberId = paidByMemberId,
            ExpenseDate = date,
            SplitType = splitType,
            CreatedUtc = clock.UtcNowSeconds(),
            Shares = [.. shares.Where(s => s.Value > 0).Select(s => new ExpenseShare { MemberId = s.Key, ShareMinor = s.Value })],
        });
        await db.SaveChangesAsync();
    }

    public async Task DeleteExpenseAsync(int expenseId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Expenses.Where(e => e.Id == expenseId).ExecuteDeleteAsync();
    }

    public async Task RecordSettlementAsync(int groupId, int fromMemberId, int toMemberId, long amountMinor)
    {
        AppException.ThrowIf(amountMinor <= 0, "Amount must be greater than zero.");
        AppException.ThrowIf(fromMemberId == toMemberId, "Choose two different people.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var ids = await db.Members.Where(m => m.GroupId == groupId).Select(m => m.Id).ToListAsync();
        AppException.ThrowIf(!ids.Contains(fromMemberId) || !ids.Contains(toMemberId), "Both people must be in this group.");
        db.Settlements.Add(new Settlement
        {
            GroupId = groupId, FromMemberId = fromMemberId, ToMemberId = toMemberId, AmountMinor = amountMinor, SettledUtc = clock.UtcNowSeconds(),
        });
        await db.SaveChangesAsync();
    }

    public async Task DeleteSettlementAsync(int settlementId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Settlements.Where(s => s.Id == settlementId).ExecuteDeleteAsync();
    }
}
