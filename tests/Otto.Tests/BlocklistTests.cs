using System.Text.Json.Nodes;

namespace Otto.Tests;

[Collection("Data folder")]
public class BlocklistTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "otto-block-" + Guid.NewGuid().ToString("N"));
    readonly string old = Paths.Data;

    public BlocklistTests()
    {
        Directory.CreateDirectory(dir);
        Paths.Data = dir;
        Blocklist.Entries = new() { "commbank.com.au", "https://www.paypal.com/", "Banking", "# a comment" };
    }

    public void Dispose() { Paths.Data = old; Directory.Delete(dir, true); }

    [Theory]
    [InlineData("https://www.commbank.com.au/netbank/login", "commbank.com.au")] // subdomain of a blocked site
    [InlineData("commbank.com.au", "commbank.com.au")]
    [InlineData("https://paypal.com/myaccount", "https://www.paypal.com/")]  // entry typed with https:// and www
    [InlineData("CommBank Banking - Google Chrome", "Banking")]               // a window title
    [InlineData("Banking", "Banking")]                                       // an app name for 'open'
    [InlineData("https://www.westpac.com.au", null)]
    [InlineData("https://notcommbank.com.au.evil.example", "commbank.com.au")] // contains it: blocked, erring safe
    [InlineData("spotify", null)]
    public void Matches_sites_subdomains_apps_and_titles(string text, string? hit) => Assert.Equal(hit, Blocklist.Match(text));

    [Fact]
    public void Comments_and_blank_lines_are_ignored() => Assert.Equal(3, Blocklist.Entries.Count);

    [Fact]
    public async Task Open_and_fetch_page_refuse_blocked_places()
    {
        var open = await Tools.Run("open", new JsonObject { ["target"] = "https://www.commbank.com.au" }, _ => true, CancellationToken.None);
        Assert.StartsWith("Blocked", open);
        var fetch = await Tools.Run("fetch_page", new JsonObject { ["url"] = "https://www.paypal.com/au/home" }, _ => true, CancellationToken.None);
        Assert.StartsWith("Blocked", fetch);
    }
}

[Collection("Data folder")]
public class QuickActionTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "otto-quick-" + Guid.NewGuid().ToString("N"));
    readonly string old = Paths.Data;
    public QuickActionTests() { Directory.CreateDirectory(dir); Paths.Data = dir; }
    public void Dispose() { Paths.Data = old; Directory.Delete(dir, true); }

    [Fact]
    public void Saved_quick_actions_come_back_and_blank_ones_are_dropped()
    {
        Assert.Empty(QuickActions.All);
        bool changed = false;
        void Note() => changed = true;
        QuickActions.Changed += Note;
        try
        {
            QuickActions.All = new() { new("Morning briefing", " Summarise my unread email and today's calendar "), new("", "no name"), new("No prompt", "  ") };
            var a = Assert.Single(QuickActions.All);
            Assert.Equal("Summarise my unread email and today's calendar", a.Prompt);
            Assert.True(changed);
        }
        finally { QuickActions.Changed -= Note; }
    }
}
