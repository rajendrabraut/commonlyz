using Commonlyz.Core;

namespace Commonlyz.Modules.SplitShare;

public enum SplitType
{
    /// <summary>Everyone selected pays the same.</summary>
    Equal,

    /// <summary>Each value is an amount in major units; they must add up to the total.</summary>
    Exact,

    /// <summary>Each value is a percentage; they must add up to 100.</summary>
    Percent,

    /// <summary>Each value is a weight ("2 shares vs 1 share").</summary>
    Shares,
}

/// <summary>
/// Splits an amount in integer minor units (cents). Shares always add up to the amount exactly:
/// the leftover cents from rounding go to the largest fractional remainders, ties broken by member id,
/// so the same input always produces the same split. €10.00 / 3 = 3.34 + 3.33 + 3.33.
/// </summary>
public static class SplitCalculator
{
    public static IReadOnlyDictionary<int, long> Split(long amountMinor, SplitType type, IReadOnlyList<(int MemberId, decimal Value)> parts)
    {
        AppException.ThrowIf(amountMinor <= 0, "Amount must be greater than zero.");
        AppException.ThrowIf(parts.Count == 0, "Choose at least one person to split with.");
        AppException.ThrowIf(parts.Select(p => p.MemberId).Distinct().Count() != parts.Count, "Each person can appear only once.");

        switch (type)
        {
            case SplitType.Equal:
                return Allocate(amountMinor, [.. parts.Select(p => (p.MemberId, 1m))]);

            case SplitType.Shares:
                AppException.ThrowIf(parts.Any(p => p.Value <= 0), "Shares must be greater than zero.");
                return Allocate(amountMinor, parts);

            case SplitType.Percent:
                AppException.ThrowIf(parts.Any(p => p.Value < 0), "Percentages can't be negative.");
                AppException.ThrowIf(parts.Sum(p => p.Value) != 100m, $"Percentages add up to {parts.Sum(p => p.Value)}%, not 100%.");
                return Allocate(amountMinor, parts);

            case SplitType.Exact:
                var exact = parts.ToDictionary(p => p.MemberId, p => p.Value * 100m);
                AppException.ThrowIf(exact.Values.Any(v => v < 0 || v != decimal.Truncate(v)), "Exact amounts must be positive with at most 2 decimals.");
                var sum = exact.Values.Sum();
                AppException.ThrowIf(sum != amountMinor, $"Exact amounts add up to {Format.Money((long)sum)}, not {Format.Money(amountMinor)}.");
                return exact.ToDictionary(kv => kv.Key, kv => (long)kv.Value);

            default:
                throw new AppException("Unknown split type.");
        }
    }

    /// <summary>Largest-remainder (Hamilton) apportionment of <paramref name="amount"/> by weight.</summary>
    private static Dictionary<int, long> Allocate(long amount, IReadOnlyList<(int MemberId, decimal Weight)> weights)
    {
        var total = weights.Sum(w => w.Weight);
        AppException.ThrowIf(total <= 0, "Weights must add up to more than zero.");

        var raw = weights.Select(w => (w.MemberId, Exact: amount * w.Weight / total)).ToList();
        var result = raw.ToDictionary(r => r.MemberId, r => (long)decimal.Floor(r.Exact));
        var leftover = amount - result.Values.Sum();

        foreach (var r in raw.OrderByDescending(r => r.Exact - decimal.Floor(r.Exact)).ThenBy(r => r.MemberId).Take((int)leftover))
            result[r.MemberId]++;

        return result;
    }

    /// <summary>
    /// Fewest-ish transfers that settle all balances (positive = is owed). Greedy largest-creditor/largest-debtor
    /// matching: not always globally minimal, but at most n-1 transfers and exact to the cent.
    /// </summary>
    public static IReadOnlyList<(int From, int To, long AmountMinor)> SimplifyDebts(IReadOnlyDictionary<int, long> balances)
    {
        var creditors = new PriorityQueue<int, long>();
        var debtors = new PriorityQueue<int, long>();
        var remaining = balances.Where(b => b.Value != 0).ToDictionary(b => b.Key, b => b.Value);
        foreach (var (id, balance) in remaining)
        {
            if (balance > 0) creditors.Enqueue(id, -balance);
            else debtors.Enqueue(id, balance);
        }

        var transfers = new List<(int, int, long)>();
        while (creditors.TryDequeue(out var creditor, out _) && debtors.TryDequeue(out var debtor, out _))
        {
            var amount = Math.Min(remaining[creditor], -remaining[debtor]);
            transfers.Add((debtor, creditor, amount));
            remaining[creditor] -= amount;
            remaining[debtor] += amount;
            if (remaining[creditor] > 0) creditors.Enqueue(creditor, -remaining[creditor]);
            if (remaining[debtor] < 0) debtors.Enqueue(debtor, remaining[debtor]);
        }
        return transfers;
    }
}
