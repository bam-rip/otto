using System.Globalization;

namespace Otto;

/// A monthly spending limit. Otto adds up what each request cost (when the provider's prices are known, which
/// today is Claude) and stops starting new work once the month's total reaches the limit, and stops a task
/// that crosses it midway. A warning shows at 80%.
static class Spending
{
    /// Settable so tests use their own registry key instead of the user's real totals.
    internal static string Key { get; set; } = @"Software\Otto";
    static readonly object gate = new();

    /// 0 = no limit.
    public static double Limit
    {
        get => Read("MonthlyLimit", 0);
        set => Write("MonthlyLimit", Math.Max(0, value));
    }

    static string ThisMonth(DateTime now) => now.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// Spent so far this calendar month (USD). Starts again at 0 on the 1st.
    public static double ThisMonthTotal(DateTime? now = null)
    {
        lock (gate) return ReadString("SpendMonth") == ThisMonth(now ?? DateTime.Now) ? Read("SpendUsd", 0) : 0;
    }

    /// Adds a cost; returns a message when this pushed the month past 80% or past the limit (each said once a month).
    public static string? Add(double usd, DateTime? now = null)
    {
        var t = now ?? DateTime.Now;
        if (!double.IsFinite(usd) || usd <= 0) return null; // a NaN saved once would switch the limit off for good
        lock (gate)
        {
            var total = ThisMonthTotal(t) + usd;
            Write("SpendUsd", total);
            WriteString("SpendMonth", ThisMonth(t));
            var limit = Limit;
            if (limit <= 0) return null;
            string? Once(string what, string msg)
            {
                var mark = $"{ThisMonth(t)}:{what}";
                if (ReadString("SpendWarned")?.Contains(mark) == true) return null;
                WriteString("SpendWarned", (ReadString("SpendWarned") ?? "") + mark + ";");
                return msg;
            }
            if (total >= limit) return Once("over", $"That reached your ${limit:0.##} monthly limit (${total:0.00} this month). I'll stop here; raise the limit in Settings → Spending to carry on.");
            if (total >= limit * 0.8) return Once("80", $"Heads up: ${total:0.00} of your ${limit:0.##} monthly limit used.");
            return null;
        }
    }

    /// Why a new request can't start, or null.
    public static string? Blocked(DateTime? now = null)
    {
        var limit = Limit;
        var total = ThisMonthTotal(now);
        return limit > 0 && total >= limit
            ? $"You've reached your ${limit:0.##} monthly limit (${total:0.00} spent this month). Raise it in Settings → Spending, or wait for the 1st."
            : null;
    }

    static double Read(string name, double fallback) =>
        double.TryParse(ReadString(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : fallback;

    static void Write(string name, double v) => WriteString(name, v.ToString("R", CultureInfo.InvariantCulture));

    static string? ReadString(string name) => Reg.Get(Key, name);
    static void WriteString(string name, string v) => Reg.Set(Key, name, v);
}
