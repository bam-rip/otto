using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Otto;

/// Quiet update check against GitHub releases: at most once a day, in the background. A newer release shows
/// a slim bar in the panel and a tray item; nothing pops up. Updating downloads the new Otto.exe, swaps it in
/// next to the running one, and restarts. Settings, keys and chats live elsewhere, so they're untouched.
static class Updater
{
    const string Repo = "bam-rip/otto";
    const string Key = @"Software\Otto";
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
        get => Reg.Get(Key, "DismissedUpdate");
        set => Reg.Set(Key, "DismissedUpdate", value);
    }

    static DateTime LastCheck
    {
        get => DateTime.TryParse(Reg.Get(Key, "LastUpdateCheck"), out var d) ? d : DateTime.MinValue;
        set => Reg.Set(Key, "LastUpdateCheck", value.ToString("o"));
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

    /// Where a downloaded update waits until the user clicks Update.
    static string Staging(Release r) => Path.Combine(Paths.Data, "updates", r.Tag);

    /// Downloads and checks a release in the background as soon as it's found, so clicking Update only has to
    /// swap files and restart. Safe to call again: a finished download is reused.
    public static async Task PrepareAsync(Release r, IProgress<string>? progress = null)
    {
        var dir = Staging(r);
        var zip = Path.Combine(dir, r.ZipName);
        if (File.Exists(zip) && File.Exists(zip + ".sig") && VerifyFile(zip)) return;
        // only ever keep one waiting update
        var updates = Path.GetDirectoryName(dir)!;
        if (Directory.Exists(updates)) try { Directory.Delete(updates, true); } catch { }
        Directory.CreateDirectory(dir);

        progress?.Report("Downloading…");
        var data = await Download(r.ZipUrl, 300_000_000);
        var sig = await Download(r.SigUrl, 10_000);
        if (!Verify(data, Convert.FromBase64String(System.Text.Encoding.ASCII.GetString(sig).Trim())))
            throw new InvalidOperationException("The download isn't signed by Otto's release key, so it wasn't installed. Nothing on your PC was changed.");
        await File.WriteAllBytesAsync(zip + ".sig", sig);
        await File.WriteAllBytesAsync(zip, data);
    }

    public static bool IsPrepared(Release r)
    {
        var zip = Path.Combine(Staging(r), r.ZipName);
        return File.Exists(zip) && File.Exists(zip + ".sig");
    }

    static bool VerifyFile(string zip)
    {
        try { return Verify(File.ReadAllBytes(zip), Convert.FromBase64String(File.ReadAllText(zip + ".sig").Trim())); }
        catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException) { return false; }
    }

    /// Downloads (if not done already), then puts the new Otto.exe in place of this one and starts it. The caller
    /// then quits. The running exe can't be overwritten but can be renamed, so: rename ours to Otto.old.exe,
    /// move the new one in, start it; it deletes Otto.old.exe once we've gone.
    public static async Task InstallAsync(Release r, IProgress<string>? progress = null, Action? swapped = null)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Can't tell where Otto is installed.");
        var dir = Path.GetDirectoryName(exe)!;
        await PrepareAsync(r, progress);
        var work = Staging(r);
        var zip = Path.Combine(work, r.ZipName);
        // checked again now: the file sat on disk since it was downloaded
        if (!VerifyFile(zip))
        {
            try { Directory.Delete(work, true); } catch { }
            throw new InvalidOperationException("The downloaded update changed since it was checked, so it wasn't installed. It will download again next time.");
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
        swapped?.Invoke();

        Process.Start(new ProcessStartInfo(exe, "--updated --show") { UseShellExecute = false, WorkingDirectory = dir });
        try { Directory.Delete(work, true); } catch { }
    }

    internal static async Task<byte[]> Download(string url, long maxBytes, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Clear();
        req.Headers.Accept.ParseAdd("application/octet-stream");
        using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        if (res.Content.Headers.ContentLength > maxBytes) throw new InvalidOperationException("The download is far bigger than expected.");
        using var s = await res.Content.ReadAsStreamAsync(ct);
        var buf = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(chunk, ct)) > 0)
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
        static string KeyPath => Environment.GetEnvironmentVariable("OTTO_SIGNING_KEY") is { Length: > 0 } k ? k // for testing on a copy
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".otto", "release-key.pem");

        /// Makes the key if there isn't one; returns the public key to paste into PublicKey.
        public static string MakeKey()
        {
            if (File.Exists(KeyPath)) throw new InvalidOperationException($"{KeyPath} already exists; not replacing it.");
            using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
            File.WriteAllText(KeyPath, ec.ExportPkcs8PrivateKeyPem());
            return Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo());
        }

        /// The passphrase comes from OTTO_SIGNING_PASSPHRASE, set only for the signing step by publish.ps1.
        static string Passphrase => Environment.GetEnvironmentVariable("OTTO_SIGNING_PASSPHRASE") is { Length: >= 12 } p ? p
            : throw new InvalidOperationException("No release key passphrase given (at least 12 characters).");

        static ECDsa LoadKey()
        {
            var pem = File.ReadAllText(KeyPath);
            var ec = ECDsa.Create();
            try
            {
                if (pem.Contains("ENCRYPTED PRIVATE KEY")) ec.ImportFromEncryptedPem(pem, Passphrase);
                else ec.ImportFromPem(pem);
            }
            catch (CryptographicException) { ec.Dispose(); throw new InvalidOperationException("Wrong release key passphrase."); }
            return ec;
        }

        /// Re-saves an unprotected key locked with the passphrase (AES-256, 600k rounds), so a copy of the file is
        /// useless without it. Returns false if it was already protected.
        public static bool Protect()
        {
            var pem = File.ReadAllText(KeyPath);
            if (pem.Contains("ENCRYPTED PRIVATE KEY")) return false;
            using var ec = LoadKey();
            var locked = ec.ExportEncryptedPkcs8PrivateKeyPem(Passphrase,
                new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000));
            using (var check = ECDsa.Create()) check.ImportFromEncryptedPem(locked, Passphrase); // never write a file we can't read back
            SafeFile.WriteAllText(KeyPath, locked); // never half a key: the old file stays until the new one is complete
            return true;
        }

        /// Writes <file>.sig next to it.
        public static void Sign(string file)
        {
            using var ec = LoadKey();
            if (Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo()) != PublicKey)
                throw new InvalidOperationException("This key doesn't match the public key built into Otto.");
            File.WriteAllText(file + ".sig", Convert.ToBase64String(ec.SignData(File.ReadAllBytes(file), HashAlgorithmName.SHA256)));
        }
    }

    /// True for the published single-file build: its code isn't in a separate Otto.dll on disk.
#pragma warning disable IL3000 // an empty Location is exactly the single-file signal we want
    static bool IsSingleFile => string.IsNullOrEmpty(typeof(Updater).Assembly.Location);
#pragma warning restore IL3000

    /// First thing after an update: remove the previous exe (we couldn't delete it while it was running).
    public static void CleanUpAfterUpdate()
    {
        var exe = Environment.ProcessPath;
        if (exe == null || !exe.EndsWith("Otto.exe", StringComparison.OrdinalIgnoreCase)) return;
        var dir = Path.GetDirectoryName(exe)!;
        // A single-file Otto.exe needs nothing beside it. Files left by the old build-from-source installer
        // (Otto.dll, .deps.json, .runtimeconfig.json) would only confuse which code runs, so they go, but only
        // when this exe really is the self-contained one (it then has no Otto.dll of its own to load).
        if (IsSingleFile)
            foreach (var stale in new[] { "Otto.dll", "Otto.deps.json", "Otto.runtimeconfig.json", "Otto.pdb" })
                try { File.Delete(Path.Combine(dir, stale)); } catch { }
        var old = Path.Combine(dir, "Otto.old.exe");
        for (int i = 0; i < 20 && File.Exists(old); i++)
        {
            try { File.Delete(old); }
            catch { Thread.Sleep(250); } // the old process is still shutting down
        }
    }
}
