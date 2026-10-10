using System.IO.Compression;

namespace Otto;

/// Optional extras downloaded on request (Settings → Addons), so Otto itself stays small. Each is a zip on the
/// "addons" GitHub release, signed with the same release key as updates; Otto checks the signature before
/// unpacking anything. Addons hold content only (pictures, lists), never code, and are unpacked into
/// %LOCALAPPDATA%\Otto\addons\<id>. Removing one deletes that folder.
static class Addons
{
    public sealed record Addon(string Id, string Name, string Description, string Size);

    public static readonly Addon[] Catalog =
    {
        new("reactions", "Reaction images",
            "38 square reaction pictures Otto can paste into chats and comments when you ask, e.g. \"reply with a facepalm\".", "about 1.4 MB"),
    };

    const string ReleaseUrl = "https://github.com/bam-rip/otto/releases/download/addons/";
    const long MaxDownload = 100_000_000;
    const long MaxUnpacked = 300_000_000; // a small zip can claim to unpack to far more; refuse before writing it
    /// Content only: anything that could run, or be opened as something that runs, is refused even when signed.
    static readonly string[] AllowedTypes = { ".png", ".jpg", ".jpeg", ".json", ".txt" };

    static string Root => Path.Combine(Paths.Data, "addons");
    public static string Dir(string id) => Path.Combine(Root, id);
    public static bool IsInstalled(string id) => File.Exists(Path.Combine(Dir(id), "addon.json"));


    public static async Task InstallAsync(Addon a, CancellationToken ct = default)
    {
        var zip = await Updater.Download(ReleaseUrl + a.Id + ".zip", MaxDownload, ct);
        var sig = await Updater.Download(ReleaseUrl + a.Id + ".zip.sig", 10_000, ct);
        byte[] signature;
        try { signature = Convert.FromBase64String(System.Text.Encoding.ASCII.GetString(sig).Trim()); }
        catch (FormatException) { signature = Array.Empty<byte>(); }
        if (!Updater.Verify(zip, signature))
            throw new InvalidOperationException("The download isn't signed by Otto's release key, so it wasn't installed.");
        Unpack(zip, a.Id);
    }

    /// Unpacks into a fresh folder beside the old one, then swaps it in, so a failed or partial unpack never leaves
    /// a half-installed addon.
    internal static void Unpack(byte[] zipBytes, string id)
    {
        Directory.CreateDirectory(Root);
        var fresh = Dir(id) + ".new";
        if (Directory.Exists(fresh)) Directory.Delete(fresh, true);
        Directory.CreateDirectory(fresh);
        try
        {
            using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
            if (zip.Entries.Sum(e => e.Length) > MaxUnpacked) throw new InvalidDataException("The addon unpacks to more than it should.");
            if (zip.GetEntry("addon.json") == null) throw new InvalidDataException("That isn't an Otto addon (no addon.json).");
            var root = Path.GetFullPath(fresh) + Path.DirectorySeparatorChar;
            foreach (var e in zip.Entries)
            {
                if (e.FullName.EndsWith('/')) continue; // a folder entry
                var dest = Path.GetFullPath(Path.Combine(fresh, e.FullName));
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Bad path in the addon: {e.FullName}");
                if (!AllowedTypes.Contains(Path.GetExtension(dest).ToLowerInvariant()) || Safety.IsRunnable(dest))
                    throw new InvalidDataException($"The addon contains a file type Otto doesn't accept: {e.Name}");
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                e.ExtractToFile(dest);
            }
            if (Directory.Exists(Dir(id))) Directory.Delete(Dir(id), true);
            Directory.Move(fresh, Dir(id));
        }
        catch
        {
            try { Directory.Delete(fresh, true); } catch (IOException) { }
            throw;
        }
    }

    public static void Remove(string id)
    {
        if (Directory.Exists(Dir(id))) Directory.Delete(Dir(id), true);
    }

    /// Space the addon takes on disk, e.g. "1.4 MB".
    public static string SizeOnDisk(string id)
    {
        if (!Directory.Exists(Dir(id))) return "";
        long bytes = new DirectoryInfo(Dir(id)).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        return bytes >= 1_000_000 ? $"{bytes / 1_000_000.0:0.0} MB" : $"{Math.Max(1, bytes / 1000)} KB";
    }
}
