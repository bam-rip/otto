using Microsoft.Win32;

namespace Otto.Tests;

/// Runs against its own registry key so the user's real monthly total is never touched.
[Collection("Spending")]
public class SpendingTests : IDisposable
{
    readonly string key = @"Software\Otto-test-" + Guid.NewGuid().ToString("N")[..8];
    readonly string old = Spending.Key;

    public SpendingTests() => Spending.Key = key;
    public void Dispose() { Spending.Key = old; Registry.CurrentUser.DeleteSubKeyTree(key, false); }

    static readonly DateTime Oct = new(2026, 10, 15);

    [Fact]
    public void No_limit_never_blocks()
    {
        Assert.Null(Spending.Add(500, Oct));
        Assert.Null(Spending.Blocked(Oct));
        Assert.Equal(500, Spending.ThisMonthTotal(Oct));
    }

    [Fact]
    public void Warns_at_80_percent_once_then_blocks_at_the_limit()
    {
        Spending.Limit = 5;
        Assert.Null(Spending.Add(3.9, Oct));
        Assert.Contains("Heads up", Spending.Add(0.2, Oct));   // 4.10 of 5
        Assert.Null(Spending.Add(0.1, Oct));                   // said once
        Assert.Null(Spending.Blocked(Oct));
        Assert.Contains("reached", Spending.Add(1, Oct));      // 5.20
        Assert.NotNull(Spending.Blocked(Oct));
    }

    [Fact]
    public void A_new_month_starts_from_zero()
    {
        Spending.Limit = 5;
        Spending.Add(6, Oct);
        Assert.NotNull(Spending.Blocked(Oct));
        var nov = new DateTime(2026, 11, 1);
        Assert.Equal(0, Spending.ThisMonthTotal(nov));
        Assert.Null(Spending.Blocked(nov));
        Assert.Contains("Heads up", Spending.Add(4.5, nov));   // warnings reset each month too
    }
}
