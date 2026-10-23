using Commonlyz.Core;
using Commonlyz.Modules.SplitShare;
using Commonlyz.Modules.SplitShare.Data;

namespace Commonlyz.Tests.Unit;

public class SplitCalculatorTests
{
    [Fact]
    public void Equal_split_gives_leftover_cents_deterministically()
    {
        var shares = SplitCalculator.Split(1000, SplitType.Equal, [(3, 0), (1, 0), (2, 0)]);
        shares[1].ShouldBe(334);
        shares[2].ShouldBe(333);
        shares[3].ShouldBe(333);
    }

    [Theory]
    [InlineData(1L, 3)]
    [InlineData(100L, 7)]
    [InlineData(99_999L, 13)]
    [InlineData(1_000_000_007L, 9)]
    public void Shares_always_sum_to_amount(long amount, int people)
    {
        foreach (var type in new[] { SplitType.Equal, SplitType.Shares })
        {
            var parts = Enumerable.Range(1, people).Select(i => (i, (decimal)i)).ToList();
            SplitCalculator.Split(amount, type, parts).Values.Sum().ShouldBe(amount);
        }
    }

    [Fact]
    public void Percent_split_requires_100()
    {
        Should.Throw<AppException>(() => SplitCalculator.Split(1000, SplitType.Percent, [(1, 50), (2, 40)]));
        var shares = SplitCalculator.Split(1001, SplitType.Percent, [(1, 50), (2, 50)]);
        shares.Values.Sum().ShouldBe(1001);
    }

    [Fact]
    public void Exact_split_must_match_total()
    {
        Should.Throw<AppException>(() => SplitCalculator.Split(1000, SplitType.Exact, [(1, 5), (2, 4)]));
        SplitCalculator.Split(1000, SplitType.Exact, [(1, 6.5m), (2, 3.5m)])[1].ShouldBe(650);
    }

    [Fact]
    public void Balances_sum_to_zero_and_simplify_settles_everyone()
    {
        var expenses = new[]
        {
            new Expense { Description = "a", PaidByMemberId = 1, AmountMinor = 9000, Shares = [new() { MemberId = 1, ShareMinor = 3000 }, new() { MemberId = 2, ShareMinor = 3000 }, new() { MemberId = 3, ShareMinor = 3000 }] },
            new Expense { Description = "b", PaidByMemberId = 2, AmountMinor = 3001, Shares = [new() { MemberId = 2, ShareMinor = 1001 }, new() { MemberId = 3, ShareMinor = 2000 }] },
        };
        var balances = SplitShareService.ComputeBalances([1, 2, 3], expenses, []);
        balances.Values.Sum().ShouldBe(0);

        var transfers = SplitCalculator.SimplifyDebts(balances);
        var after = new Dictionary<int, long>(balances);
        foreach (var (from, to, amount) in transfers)
        {
            after[from] += amount;
            after[to] -= amount;
        }
        after.Values.ShouldAllBe(v => v == 0);
        transfers.Count.ShouldBeLessThanOrEqualTo(2);
    }
}
