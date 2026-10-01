using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Otto;

/// Quiet update check against GitHub releases: at most once a day, in the background. A newer release shows
/// a slim bar in the panel and a tray item; nothing pops up. Updating downloads the new Otto.exe, swaps it in
/// next to the running one, and restarts. Settings, keys and chats live elsewhere, so they're untouched.
static class Updater
{
    const string Repo = "bam-rip/otto";
    const string Reg = @"Software\Otto";
    static readonly HttpClient Http = CreateHttp();

    public sealed record Release(Version Version, string Tag, string PageUrl, string ZipUrl, string ZipName);

    public static Version Current =>
        Assembly.GetExecutingAssembly().GetName().Version is Version v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    /// Updates the user said "not now" to (×) stay hidden until an even newer one comes out.
    public static string? Dismissed
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(Reg); return k?.GetValue("DismissedUpdate") as string; }
        set { using var k = Registry.CurrentUser.CreateSubKey(Reg); if (value == null) k.DeleteValue("DismissedUpdate", false); else k.SetValue("DismissedUpdate", value); }
    }

    static DateTime LastCheck
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(Reg); return DateTime.TryParse(k?.GetValue("LastUpdateCheck") as string, out var d) ? d : DateTime.MinValue; }
        set { using var k = Registry.CurrentUser.CreateSubKey(Reg); k.SetValue("LastUpdateCheck", value.ToString("o")); }
    }

    static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"Otto/{Current}"); // GitHub's API requires a user agent
        h.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        // Lets a developer test against a private repo (e.g. OTTO_GITHUB_TOKEN = output of `gh auth token`).
        // Not needed once the repo is public.
        if (Environment.GetEnvironmentVariable("OTTO_GITHUB_TOKEN") is { Length: > 0 } token)
            h.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);
        return h;
    }

    /// The daily background check. Returns a newer release, or null (also null when off, too soon, or offline).
    public static async Task<Release?> CheckIfDueAsync()
    {
        if (!Prefs.CheckUpdates || DateTime.Now - LastCheck < TimeSpan.FromHours(23)) return null;
        try
        {
            var r = await LatestAsync();
            LastCheck = DateTime.Now;
            return r != null && r.Version > Current && r.Tag != Dismissed ? r : null;
        }
        catch { return null; } // offline or GitHub hiccup: try again tomorrow
    }

    /// "Check for updates now": ignores the daily limit and the "not now" choice.
    public static async Task<Release?> CheckNowAsync()
    {
        var r = await LatestAsync();
        LastCheck = DateTime.Now;
        return r != null && r.Version > Current ? r : null;
    }

    static async Task<Release?> LatestAsync()
    {
        using var res = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        if (!res.IsSuccessStatusCode) return null; // 404 = no releases yet (or the repo is private)
        var j = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        var tag = j["tag_name"]?.GetValue<string>() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return null;
        var asset = j["assets"]?.AsArray().FirstOrDefault(a => a?["name"]?.GetValue<string>()?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true);
        if (asset == null) return null;
        // the API URL + octet-stream works for private repos too (browser_download_url doesn't with a token)
        return new Release(new Version(v.Major, v.Minor, Math.Max(0, v.Build)), tag, j["html_url"]?.GetValue<string>() ?? "",
            asset["url"]!.GetValue<string>(), asset["name"]!.GetValue<string>());
    }

    /// Downloads the release, puts the new Otto.exe in place of this one, and starts it. The caller then quits.
    /// The running exe can't be overwritten but can be renamed, so: rename ours to Otto.old.exe, move the new
    /// one in, start it; it deletes Otto.old.exe once we've gone.
    public static async Task InstallAsync(Release r, IProgress<string>? progress = null)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Can't tell where Otto is installed.");
        var dir = Path.GetDirectoryName(exe)!;
        var work = Path.Combine(Path.GetTempPath(), "otto-update-" + r.Tag);
        Directory.CreateDirectory(work);
        var zip = Path.Combine(work, r.ZipName);

        progress?.Report("Downloading…");
        using (var req = new HttpRequestMessage(HttpMethod.Get, r.ZipUrl))
        {
            req.Headers.Accept.Clear();
            req.Headers.Accept.ParseAdd("application/octet-stream");
            using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            res.EnsureSuccessStatusCode();
            await using var fs = File.Create(zip);
            await res.Content.CopyToAsync(fs);
        }

        progress?.Report("Installing…");
        var extracted = Path.Combine(work, "files");
        if (Directory.Exists(extracted)) Directory.Delete(extracted, true);
        ZipFile.ExtractToDirectory(zip, extracted);
        var fresh = Directory.GetFiles(extracted, "Otto.exe", SearchOption.AllDirectories).FirstOrDefault()
                    ?? throw new InvalidOperationException("The download didn't contain Otto.exe.");

        var old = Path.Combine(dir, "Otto.old.exe");
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try { File.Move(fresh, exe); }
        catch { File.Move(old, exe); throw; } // put ourselves back if the swap fails

        try { Directory.Delete(work, true); } catch { }
        Process.Start(new ProcessStartInfo(exe, "--updated --show") { UseShellExecute = false, WorkingDirectory = dir });
    }

    /// First thing after an update: remove the previous exe (we couldn't delete it while it was running).
    public static void CleanUpAfterUpdate()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return;
        var old = Path.Combine(Path.GetDirectoryName(exe)!, "Otto.old.exe");
        for (int i = 0; i < 20 && File.Exists(old); i++)
        {
            try { File.Delete(old); }
            catch { Thread.Sleep(250); } // the old process is still shutting down
        }
    }
}
