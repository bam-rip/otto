using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Otto;

/// Guards that don't rely on the model behaving. The model can be talked into things by text it reads
/// (a web page, an email, a downloaded file: "prompt injection"), so the dangerous moves are checked here,
/// in code, where nothing it reads can change them.
static class Safety
{
    // ---- untrusted content ----

    /// Set once this task has read something an outsider could have written. Until the next message from the
    /// user, the moves an injected instruction would need (running commands, saving lasting notes or routines,
    /// launching programs) ask the user first.
    public static bool Untrusted { get; private set; }

    /// What made it untrusted, for the question shown to the user.
    static string source = "";

    public static void NewTurn() { Untrusted = false; source = ""; }

    static readonly Dictionary<string, string> Sources = new()
    {
        ["web_search"] = "web search results", ["fetch_page"] = "a web page", ["read_file"] = "a file",
        ["email_list"] = "your email", ["email_read"] = "an email", ["calendar_list"] = "calendar invites",
        ["screen"] = "a web page or email on screen",
    };

    public static void Saw(string tool)
    {
        if (!Sources.TryGetValue(tool, out var what)) return;
        if (!Untrusted) source = what;
        Untrusted = true;
    }

    /// The question to ask before a gated action while untrusted, or null when no question is needed.
    public static string? AskIfUntrusted(string action) =>
        Untrusted
            ? $"Otto read {source} during this task, and text like that can contain instructions planted by someone else. " +
              $"Did you ask for this?\n\n{action}"
            : null;

    /// After reading untrusted content, a web address carrying a lot of data in it could be smuggling something
    /// out (e.g. https://evil.example/?d=<your notes>), so those ask first.
    public static bool LooksLikeSmuggling(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Query.Length > 120 || u.AbsolutePath.Length > 200);

    // ---- paths ----

    /// \\server\share paths: even checking whether one exists makes Windows send your sign-in hash to that
    /// server, so Otto never touches them on its own.
    public static bool IsNetworkPath(string path)
    {
        var p = Environment.ExpandEnvironmentVariables(path.Trim()).Replace('/', '\\');
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.StartsWith(@"\\?\") || p.StartsWith(@"\\.\")) return false; // local device paths
        if (p.StartsWith(@"\\")) return true;
        if (p.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return Uri.TryCreate(p.Replace('\\', '/'), UriKind.Absolute, out var u) && u.IsUnc; // file://server/share
        return false;
    }

    public static void RefuseNetworkPath(string path)
    {
        if (IsNetworkPath(path))
            throw new UnauthorizedAccessException($"{path} is a network location. Otto doesn't open network paths itself (it can leak your Windows sign-in); open it yourself if you trust it.");
    }

    static readonly string[] Runnable =
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".msi", ".msix",
        ".appx", ".appinstaller", ".reg", ".lnk", ".url", ".scr", ".pif", ".cpl", ".msc", ".jar", ".chm", ".application",
        ".appref-ms", ".settingcontent-ms", ".iso", ".img", ".vhd", ".vhdx", ".dll", ".sys", ".inf", ".py", ".pyw",
    };

    public static bool IsRunnable(string path) => Runnable.Contains(Path.GetExtension(path.Trim().TrimEnd('"')).ToLowerInvariant());

    /// Places where a written file runs by itself later (or changes Otto), so writing there always asks.
    public static string? SensitiveWrite(string fullPath)
    {
        if (IsRunnable(fullPath)) return "a file that can run as a program";
        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var docs = F(Environment.SpecialFolder.MyDocuments);
        var risky = new (string dir, string what)[]
        {
            (F(Environment.SpecialFolder.Startup), "the Startup folder (runs at every sign-in)"),
            (F(Environment.SpecialFolder.CommonStartup), "the Startup folder (runs at every sign-in)"),
            (Path.Combine(docs, "WindowsPowerShell"), "PowerShell's profile folder (runs with every PowerShell)"),
            (Path.Combine(docs, "PowerShell"), "PowerShell's profile folder (runs with every PowerShell)"),
            (Paths.Data, "Otto's own data (notes, routines, chats)"),
            (Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "", "Otto's program folder"),
            (F(Environment.SpecialFolder.Windows), "the Windows folder"),
            (F(Environment.SpecialFolder.ProgramFiles), "Program Files"),
            (F(Environment.SpecialFolder.ProgramFilesX86), "Program Files"),
        };
        foreach (var (dir, what) in risky)
            if (dir.Length > 3 && IsInside(fullPath, dir)) return what;
        var name = Path.GetFileName(fullPath).ToLowerInvariant();
        if (name is ".gitconfig" or ".bashrc" or ".profile" or "hosts") return "a settings file other programs trust";
        return null;
    }

    static bool IsInside(string path, string dir)
    {
        var d = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
        return Path.GetFullPath(path).StartsWith(d, StringComparison.OrdinalIgnoreCase);
    }

    // ---- links ----

    /// Link types 'open' hands straight to Windows. Others (search-ms:, ms-msdt:, ms-officecmd:,
    /// ms-appinstaller: and friends) have been used in real attacks, so they ask first.
    static readonly string[] SafeSchemes =
    {
        "http", "https", "mailto", "ms-settings", "ms-screenclip", "ms-windows-store", "shell",
        "spotify", "steam", "discord", "zoommtg", "msteams", "slack", "tel", "calculator", "ms-clock", "ms-photos",
    };

    /// The link's scheme when it's one that should ask first; null for safe links and plain paths/names.
    public static string? RiskyScheme(string target)
    {
        var m = Regex.Match(target.Trim(), @"^([a-zA-Z][a-zA-Z0-9+.\-]{1,30}):");
        if (!m.Success) return null;
        var scheme = m.Groups[1].Value.ToLowerInvariant();
        if (scheme.Length == 1) return null; // a drive letter: C:\...
        return SafeSchemes.Contains(scheme) ? null : scheme;
    }

    // ---- network ----

    /// Loopback, private LAN, link-local (incl. cloud metadata at 169.254.169.254) and the like. fetch_page
    /// won't connect to these, so a planted link can't make Otto poke at your router or local services.
    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6Multicast;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || b[0] == 0
            || b[0] == 172 && b[1] >= 16 && b[1] <= 31
            || b[0] == 192 && b[1] == 168
            || b[0] == 169 && b[1] == 254
            || b[0] == 100 && b[1] >= 64 && b[1] <= 127 // carrier-grade NAT
            || b[0] >= 224; // multicast and reserved
    }

    /// A handler whose connections refuse private addresses, checked at connect time so redirects and DNS
    /// tricks (a public name that resolves to 192.168.x.x) are caught too.
    public static SocketsHttpHandler PublicOnlyHandler() => new()
    {
        AutomaticDecompression = DecompressionMethods.All,
        ConnectCallback = async (ctx, ct) =>
        {
            var addrs = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
            var ok = addrs.FirstOrDefault(a => !IsPrivate(a))
                     ?? throw new HttpRequestException($"{ctx.DnsEndPoint.Host} is on your local network or this PC; Otto only reads public web pages. Open it in the browser instead.");
            if (addrs.Any(IsPrivate)) // mixed answers: don't gamble on which one the socket would pick
                throw new HttpRequestException($"{ctx.DnsEndPoint.Host} points at a local address; Otto only reads public web pages.");
            var socket = new Socket(ok.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try { await socket.ConnectAsync(ok, ctx.DnsEndPoint.Port, ct); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        },
    };

    // ---- screen control ----

    /// Buttons whose click spends money, signs, submits or destroys. Clicking one asks first even if the model
    /// didn't flag the step, since that flag is only the model's own judgement.
    static readonly Regex Consequential = new(
        @"\b(buy|buy now|pay|pay now|purchase|place (your )?order|complete (your )?(order|purchase)|confirm (and pay|order|purchase|payment)|" +
        @"check ?out|proceed to (checkout|payment)|subscribe|start (free )?trial|donate|send money|transfer( money| funds)?|" +
        @"sign (and submit|document|here)|e-?sign|submit (application|form|order|payment)|delete (account|permanently|forever)|" +
        @"permanently delete|empty (recycle bin|trash|bin))\b",
        RegexOptions.IgnoreCase);

    public static bool IsConsequential(string? label) => !string.IsNullOrWhiteSpace(label) && Consequential.IsMatch(label);
}
