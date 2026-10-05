using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
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

    public sealed record Release(Version Version, string Tag, string PageUrl, string ZipUrl, string ZipName, string SigUrl);

    /// Releases are signed with a key that never leaves the maintainer's PC (see Signing below). Otto only installs
    /// a download whose signature matches this public key, so a hijacked GitHub account or release can't push code.
    internal const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE5tXzWGbYRvXoIW+FAJ0WnahmhP1RAVxcUhxp7bXHQaJqU+QjjmvzwFGCL8UAwkItZUdo5KAXUW6PBlm5g8jCdQ==";

    /// Does sig (from the release's .sig file) prove data was signed by the release key?
    internal static bool Verify(byte[] data, byte[] sig, string publicKey = PublicKey)
    {
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return ec.VerifyData(data, sig, HashAlgorithmName.SHA256);
        }
        catch (Exception e) when (e is CryptographicException or FormatException) { return false; }
    }

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
        var assets = j["assets"]?.AsArray() ?? new JsonArray();
        var asset = assets.FirstOrDefault(a => a?["name"]?.GetValue<string>()?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true);
        if (asset == null) return null;
        var sigName = asset["name"]!.GetValue<string>() + ".sig";
        var sig = assets.FirstOrDefault(a => a?["name"]?.GetValue<string>() == sigName);
        if (sig == null) return null; // unsigned releases are never offered
        // the API URL + octet-stream works for private repos too (browser_download_url doesn't with a token)
        return new Release(new Version(v.Major, v.Minor, Math.Max(0, v.Build)), tag, j["html_url"]?.GetValue<string>() ?? "",
            asset["url"]!.GetValue<string>(), asset["name"]!.GetValue<string>(), sig["url"]!.GetValue<string>());
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
        var data = await Download(r.ZipUrl, 300_000_000);
        var sig = await Download(r.SigUrl, 10_000);
        progress?.Report("Checking…");
        if (!Verify(data, Convert.FromBase64String(System.Text.Encoding.ASCII.GetString(sig).Trim())))
            throw new InvalidOperationException("The download isn't signed by Otto's release key, so it wasn't installed. Nothing on your PC was changed.");
        await File.WriteAllBytesAsync(zip, data);

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

    static async Task<byte[]> Download(string url, long maxBytes)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Clear();
        req.Headers.Accept.ParseAdd("application/octet-stream");
        using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        res.EnsureSuccessStatusCode();
        if (res.Content.Headers.ContentLength > maxBytes) throw new InvalidOperationException("The download is far bigger than expected.");
        using var s = await res.Content.ReadAsStreamAsync();
        var buf = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(chunk)) > 0)
        {
            buf.Write(chunk, 0, n);
            if (buf.Length > maxBytes) throw new InvalidOperationException("The download is far bigger than expected.");
        }
        return buf.ToArray();
    }

    /// The maintainer's side: making the key once, and signing each release zip (used by publish.ps1).
    /// The private key lives in %USERPROFILE%\.otto\release-key.pem, never in the repo.
    internal static class Signing
    {
        static string KeyPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".otto", "release-key.pem");

        /// Makes the key if there isn't one; returns the public key to paste into PublicKey.
        public static string MakeKey()
        {
            if (File.Exists(KeyPath)) throw new InvalidOperationException($"{KeyPath} already exists; not replacing it.");
            using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
            File.WriteAllText(KeyPath, ec.ExportPkcs8PrivateKeyPem());
            return Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo());
        }

        /// Writes <file>.sig next to it.
        public static void Sign(string file)
        {
            using var ec = ECDsa.Create();
            ec.ImportFromPem(File.ReadAllText(KeyPath));
            if (Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo()) != PublicKey)
                throw new InvalidOperationException("This key doesn't match the public key built into Otto.");
            File.WriteAllText(file + ".sig", Convert.ToBase64String(ec.SignData(File.ReadAllBytes(file), HashAlgorithmName.SHA256)));
        }
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
