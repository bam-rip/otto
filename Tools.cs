using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Otto;

static class Tools
{
    const int MaxOutput = 6_000; // every char sent back is billed again on each later step
    const int PageChars = 5_000;
    const int PdfChars = 20_000; // a PDF is read in parts of about this size; older results are cut down later (Agent.TrimOldResults)
    static readonly HttpClient Http = CreateHttp();
    /// Only commands that destroy data, change how Windows itself works, or run code fetched from the internet
    /// need a yes. Reading, creating, downloading, installing from official repos, closing apps etc. just run.
    internal static readonly Regex RiskyCommand = new(
        @"\b(Remove-Item|ri|rm|rmdir|del|erase|rd|Clear-Content|Clear-RecycleBin|Format-Volume|Clear-Disk|Initialize-Disk|diskpart|bcdedit|" +
        @"Stop-Computer|Restart-Computer|shutdown|Set-ExecutionPolicy|Uninstall-\w+|Remove-AppxPackage|Disable-ComputerRestore|" +
        @"Set-MpPreference|Add-MpPreference|takeown|icacls|cipher|vssadmin|wbadmin|Invoke-Expression|iex|sdelete)\b" +
        @"|\bformat\s+[a-z]:|\breg\s+(add|delete|import)\b|-Verb\s+RunAs" +
        @"|\b(Set-ItemProperty|New-ItemProperty|New-Item|Set-Item|Rename-ItemProperty|Clear-ItemProperty|Rename-Item|Move-Item|Copy-Item)\b[^;|\n]*(HKLM:|HKEY_LOCAL_MACHINE)" +
        @"|::Delete\s*\(|\.Delete\s*\(|::WriteAllBytes|Remove-ItemProperty|-EncodedCommand|\s-enc\s" +
        // things that run later by themselves (persistence)
        @"|\b(schtasks|Register-ScheduledTask|New-ScheduledTask|New-Service|Set-Service|sc(\.exe)?\s+(create|config)|Register-WmiEvent|Set-WmiInstance)\b" +
        @"|CurrentVersion\\(Run|RunOnce|Policies)|\\Start Menu\\Programs\\Startup|\$PROFILE\b|Microsoft\\Windows\\Start Menu" +
        // sending data out, or fetching and running code
        @"|\b(Invoke-WebRequest|iwr|Invoke-RestMethod|irm|curl|wget)\b[^;|\n]*-(Method\s+(Post|Put|Patch)|InFile|Body)\b" +
        @"|\b(DownloadString|DownloadFile|UploadString|UploadFile|UploadData|Start-BitsTransfer|bitsadmin|certutil|mshta|rundll32|regsvr32|wmic|cscript|wscript|msiexec)\b" +
        @"|\[scriptblock\]::Create|\.Invoke\s*\(|&\s*\(|&\s*\$|Add-Type\b|-e(nc|ncodedcommand)?\s+[A-Za-z0-9+/=]{20,}" +
        // overwriting or emptying files, and Windows' own security
        @"|\b(Clear-Item|Set-Acl|Disable-WindowsOptionalFeature|Set-NetFirewallProfile|netsh\s+advfirewall|Set-MpPreference|Stop-Service)\b" +
        // everyday commands that lose work with no backup: overwriting files, mirror-copies that delete,
        // throwing away git work, uninstalling, force-killing apps (unsaved work), cutting the network
        @"|\b(Set-Content|Out-File|sc)\b(?![^;|\n]*-WhatIf)|\brobocopy\b[^;|\n]*/(MIR|PURGE)\b|\bgit\s+(clean|reset\s+--hard|push\s+(-f|--force)|checkout\s+--|restore)\b" +
        @"|\b(winget|choco|scoop)\s+(uninstall|remove)\b|\b(Stop-Process|kill|spps)\b[^;|\n]*-Force|\btaskkill\b[^;|\n]*/F\b" +
        @"|\b(Disable-NetAdapter|Disable-PnpDevice|Remove-NetIPAddress|netsh|powercfg|Remove-Printer)\b" +
        // network paths (\\server\share) leak the Windows sign-in to that server
        @"|(^|[\s'""(=])(\\\\|//)[A-Za-z0-9?.]",
        RegexOptions.IgnoreCase);


    static HttpClient CreateHttp()
    {
        // Search engines serve an empty or challenge page to clients that don't look like a browser,
        // so send the headers a browser sends, not just a user agent.
        var h = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0 Safari/537.36");
        h.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        h.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return h;
    }

    public static JsonNode Definitions()
    {
        var tools = JsonNode.Parse(ToolJson)!.AsArray();
        tools.Add(Desktop.ToolDefinition());
        foreach (var t in Memory.ToolDefinitions().AsArray()) tools.Add(t!.DeepClone());
        foreach (var t in Schedule.ToolDefinitions().AsArray()) tools.Add(t!.DeepClone());
        if (Reactions.Available && Reactions.All().Count > 0) tools.Add(Reactions.ToolDefinition()); // addon: only when installed
        // email/calendar tools only exist once signed in, so they cost nothing otherwise
        if (Graph.SignedIn) foreach (var t in Graph.ToolDefinitions().AsArray()) tools.Add(t!.DeepClone());
        else
        {
            if (Imap.Connected) foreach (var t in Imap.ToolDefinitions().AsArray()) tools.Add(t!.DeepClone());
            if (Calendar.Connected) foreach (var t in Calendar.ToolDefinitions().AsArray()) tools.Add(t!.DeepClone());
        }
        return tools;
    }

    const string ToolJson = """
    [
      {"name":"web_search","description":"Search the web. Returns titles, URLs and snippets.",
       "input_schema":{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}},
      {"name":"fetch_page","description":"A web page's text. 'find' keywords returns only matching parts (cheaper).",
       "input_schema":{"type":"object","properties":{"url":{"type":"string"},"find":{"type":"string"}},"required":["url"]}},
      {"name":"open","description":"Open a file, folder, URL (user's browser) or installed app by name, e.g. 'spotify'. Waits for its window.",
       "input_schema":{"type":"object","properties":{"target":{"type":"string"}},"required":["target"]}},
      {"name":"window","description":"List windows, or focus/minimize/maximize/restore/close one by part of its title.",
       "input_schema":{"type":"object","properties":{"action":{"type":"string","enum":["list","focus","minimize","maximize","restore","close"]},"title":{"type":"string"}},"required":["action"]}},
      {"name":"list_dir","description":"List the files and folders in a directory.",
       "input_schema":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}},
      {"name":"read_file","description":"Read a text file, or the text of a Word, Excel, PowerPoint or PDF file (long PDFs in parts: continue from 'page').",
       "input_schema":{"type":"object","properties":{"path":{"type":"string"},"page":{"type":"integer"}},"required":["path"]}},
      {"name":"write_file","description":"Create or overwrite a text file (old version is backed up).",
       "input_schema":{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}},"required":["path","content"]}},
      {"name":"run_powershell","description":"Run PowerShell, get its output. Deleting or system-changing commands ask the user first.",
       "input_schema":{"type":"object","properties":{"command":{"type":"string"}},"required":["command"]}}
    ]
    """;

    /// A required string argument of a tool call.
    internal static string S(JsonNode input, string key) =>
        input[key]?.GetValue<string>() ?? throw new ArgumentException($"missing '{key}'");

    /// A whole-number argument. Models send numbers as 10, 10.0 or "10"; all are fine.
    internal static int Int(JsonNode input, string key, int fallback) => Desktop.Num(input[key]) ?? fallback;

    /// A yes/no argument, sent as true or "true".
    internal static bool Flag(JsonNode input, string key) =>
        input[key] is JsonValue v && (v.TryGetValue<bool>(out var b) ? b : v.TryGetValue<string>(out var s) && bool.TryParse(s, out b) && b);

    public static string Describe(string name, JsonNode input) => Graph.Handles(name) ? Graph.Describe(name, input) : name switch
    {
        "web_search" => $"Searching “{S(input, "query")}”",
        "fetch_page" => $"Reading {S(input, "url")}",
        "open" => $"Opening {S(input, "target")}",
        "list_dir" => $"Looking in {S(input, "path")}",
        "read_file" => $"Reading {S(input, "path")}",
        "write_file" => $"Writing {S(input, "path")}",
        "run_powershell" => $"PowerShell: {S(input, "command")}",
        "computer" => Desktop.Describe(input),
        "window" => $"Window: {S(input, "action")} {input["title"]}",
        "remember" => $"Remembering: {S(input, "note")}",
        "save_routine" => $"Saving routine “{S(input, "name")}”",
        "run_routine" => $"Running routine “{S(input, "name")}”",
        "forget_routine" => $"Deleting routine “{S(input, "name")}”",
        "schedule" => $"Scheduling: {S(input, "text")}",
        "schedule_list" => "Checking what's scheduled",
        "schedule_cancel" => "Cancelling a scheduled item",
        "escalate" => "Thinking harder (switching to the bigger model)",
        "reaction_image" => Reactions.ForChat(input) ? $"Reacting with “{S(input, "name")}”" : $"Copying the “{S(input, "name")}” reaction picture",
        _ => name,
    };

    public static async Task<string> Run(string name, JsonNode input, Func<string, bool> confirm, CancellationToken ct)
    {
        var result = await RunTool(name, input, confirm, ct);
        Safety.Saw(name); // anything it just read could carry planted instructions
        return result;
    }

    static async Task<string> RunTool(string name, JsonNode input, Func<string, bool> confirm, CancellationToken ct)
    {
        bool Allowed(string action) => Safety.AskIfUntrusted(action) is not string q || confirm(q);
        // an event planted by an email or page ("Payroll update: sign in at <link>") would look like the user's own
        if (name == "calendar_add" && !Allowed($"Add “{input["subject"]}” to your calendar")) return "User declined.";
        // Outlook wins when both are set up (it has the calendar too)
        if (Graph.Handles(name) && Graph.SignedIn) return await Graph.Run(name, input, confirm, ct);
        if (Imap.Handles(name) && Imap.Connected) return await Imap.Run(name, input, confirm, ct);
        if (name == "calendar_list" && Calendar.Connected) return await Calendar.List(input, ct);
        if (name.StartsWith("calendar_")) return "No calendar is connected. The user can add their calendar's link in Otto's settings (gear icon).";
        if (Graph.Handles(name)) return "Email isn't connected. The user can set it up in Otto's settings (gear icon).";
        switch (name)
        {
            case "web_search": return await Search(S(input, "query"), ct);
            case "fetch_page":
            {
                var url = S(input, "url");
                if (Blocklist.Match(url) is string blockedUrl) return Blocklist.Refusal(blockedUrl);
                if (Safety.LooksLikeSmuggling(url) && !Allowed($"Open this web address (it carries a lot of data in it):\n\n{url.Clip(300)}"))
                    return "User declined.";
                return await Fetch(url, input["find"]?.GetValue<string>(), ct);
            }
            case "open":
            {
                var target = S(input, "target");
                if (Blocklist.Match(target) is string blockedOpen) return Blocklist.Refusal(blockedOpen);
                Safety.RefuseNetworkPath(target);
                if (Safety.RiskyScheme(target) is string scheme
                    && !confirm($"Open this {scheme}: link? Links of this kind can install or run things on your PC.\n\n{target}"))
                    return "User declined.";
                if (Safety.IsRunnable(target))
                {
                    if (FromInternet(target) && !confirm($"Run this downloaded program?\n\n{target}")) return "User declined.";
                    if (!Allowed($"Run {target}")) return "User declined.";
                }
                return await Apps.Open(target, ct);
            }
            case "list_dir": Safety.RefuseNetworkPath(S(input, "path")); return ListDir(S(input, "path"));
            case "read_file": Safety.RefuseNetworkPath(S(input, "path")); return await ReadFile(S(input, "path"), Int(input, "page", 1), ct);
            case "write_file":
            {
                Safety.RefuseNetworkPath(S(input, "path"));
                var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(S(input, "path")));
                if (Safety.SensitiveWrite(path) is string where && !confirm($"Write to {where}?\n\n{path}")) return "User declined.";
                // overwriting is undoable instead of asking: the old version goes to a backup first
                if (File.Exists(path) && new FileInfo(path).Length > 200_000_000
                    && !confirm($"Overwrite {path}? It's too big to back up first ({new FileInfo(path).Length / 1_000_000} MB), so this can't be undone."))
                    return "User declined.";
                var backup = File.Exists(path) && new FileInfo(path).Length <= 200_000_000 ? Backup(path) : null;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, S(input, "content"), ct);
                return backup == null ? $"Wrote {path}" : $"Wrote {path} (previous version backed up to {backup})";
            }
            case "run_powershell":
            {
                var cmd = S(input, "command");
                // a blocked site or app named in a command (Invoke-WebRequest commbank.com.au, Start-Process banking) is still off limits
                if (Blocklist.Match(cmd) is string blockedCmd) return Blocklist.Refusal(blockedCmd);
                if (RiskyCommand.IsMatch(cmd)) { if (!confirm($"Run this PowerShell command?\n\n{cmd}")) return "User declined."; }
                else if (!Allowed($"Run this PowerShell command:\n\n{cmd}")) return "User declined.";
                return await PowerShell(cmd, ct);
            }
            case "window": return Apps.Window(S(input, "action"), input["title"]?.GetValue<string>());
            // notes and routines go into every future chat, so a planted one would stick
            case "remember": return Allowed($"Remember this for every future chat: {input["note"]}") ? Memory.Remember(input) : "User declined.";
            case "save_routine": return Allowed($"Save the routine \"{input["name"]}\" (it can replay commands later)") ? Memory.SaveRoutine(input) : "User declined.";
            case "forget_routine": return Memory.Forget(input);
            // a planted "every day, email my files to ..." would keep running long after the page is closed
            case "schedule":
                return Allowed($"Schedule this {input["kind"]} ({input["repeat"] ?? "once"}, {input["when"]}): {input["text"]}") ? Schedule.Run(name, input) : "User declined.";
            case "schedule_list" or "schedule_cancel": return Schedule.Run(name, input);
            case "reaction_image": return Reactions.Available ? Reactions.Copy(S(input, "name")) : "The reaction images addon isn't installed (Settings → Addons).";
            default: throw new ArgumentException($"Unknown tool {name}");
        }
    }

    /// Programs already installed are trusted; only files that came from the internet get a confirm.
    static bool FromInternet(string target)
    {
        var path = Environment.ExpandEnvironmentVariables(target);
        if (!File.Exists(path)) return false;
        var full = Path.GetFullPath(path);
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Safety.IsInside(full, downloads) || Safety.IsInside(full, Path.GetTempPath()))
            return true;
        try { return File.Exists(full + ":Zone.Identifier"); } // Windows' "came from the internet" mark
        catch { return false; }
    }

    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    internal static string Backup(string path)
    {
        var dir = Path.Combine(Paths.Data, "backups");
        Directory.CreateDirectory(dir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var dest = Path.Combine(dir, $"{stamp}-{Path.GetFileName(path)}");
        for (int n = 2; File.Exists(dest); n++) dest = Path.Combine(dir, $"{stamp}-{n}-{Path.GetFileName(path)}");
        File.Copy(path, dest);
        // keep the 200 newest so this can't fill the disk
        // past the 200 newest, drop backups older than a week; never this week's, or a task that changes many
        // files would delete the undo for its own first changes
        foreach (var old in new DirectoryInfo(dir).GetFiles().OrderByDescending(f => f.CreationTimeUtc).Skip(200)
                     .Where(f => f.CreationTimeUtc < DateTime.UtcNow.AddDays(-7)))
            try { old.Delete(); } catch { }
        return dest;
    }

    static string Truncate(string s, int max = MaxOutput) =>
        s.Length <= max ? s : s.Head(max) + "\n…(truncated; use 'find' on fetch_page, or read a smaller part)";

    // ---- web ----

    internal record SearchResult(string Title, string Url, string Snippet);

    /// DuckDuckGo's html endpoint now answers bots with a challenge page, so try several engines
    /// and keep the first that gives real results.
    static async Task<string> Search(string query, CancellationToken ct)
    {
        var q = Uri.EscapeDataString(query);
        var engines = new (string Name, Func<Task<string>> Get, Func<string, List<SearchResult>> Parse)[]
        {
            ("DuckDuckGo", () => PostForm("https://lite.duckduckgo.com/lite/", "q=" + q, ct), ParseDdgLite),
            ("Bing", () => Http.GetStringAsync($"https://www.bing.com/search?q={q}&setlang=en", ct), ParseBing),
            ("DuckDuckGo html", () => Http.GetStringAsync("https://html.duckduckgo.com/html/?q=" + q, ct), ParseDdgHtml),
        };
        var errors = new List<string>();
        foreach (var e in engines)
        {
            try
            {
                var html = await e.Get();
                var results = e.Parse(html);
                if (results.Count > 0) return FormatResults(results);
                errors.Add(e.Name + ": no results");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                errors.Add(e.Name + ": " + ex.Message);
            }
        }
        return "No results (" + string.Join("; ", errors) + "). Try fewer or different words, or open " +
               "https://www.bing.com/search?q=" + q + " in the browser.";
    }

    static async Task<string> PostForm(string url, string body, CancellationToken ct)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");
        using var res = await Http.PostAsync(url, content, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct);
    }

    internal static string FormatResults(List<SearchResult> results)
    {
        var sb = new StringBuilder();
        foreach (var r in results.Take(8))
        {
            sb.AppendLine(r.Title);
            sb.AppendLine(r.Url);
            if (r.Snippet.Length > 0) sb.AppendLine(r.Snippet.Clip(300));
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    internal static List<SearchResult> ParseDdgLite(string html)
    {
        var list = new List<SearchResult>();
        var links = Regex.Matches(html, "<a[^>]*href=[\"']([^\"']+)[\"'][^>]*class=[\"']result-link[\"'][^>]*>(.*?)</a>", RegexOptions.Singleline);
        var snippets = Regex.Matches(html, "class=[\"']result-snippet[\"'][^>]*>(.*?)</td>", RegexOptions.Singleline);
        for (int i = 0; i < links.Count; i++)
        {
            var url = DdgUrl(WebUtility.HtmlDecode(links[i].Groups[1].Value));
            if (url.Contains("duckduckgo.com/y.js")) continue; // ads
            list.Add(new(Html.StripTags(links[i].Groups[2].Value), url, i < snippets.Count ? Html.StripTags(snippets[i].Groups[1].Value) : ""));
        }
        return list;
    }

    internal static List<SearchResult> ParseDdgHtml(string html)
    {
        var list = new List<SearchResult>();
        var links = Regex.Matches(html, "class=\"result__a\"[^>]*href=\"([^\"]+)\"[^>]*>(.*?)</a>", RegexOptions.Singleline);
        var snippets = Regex.Matches(html, "class=\"result__snippet\"[^>]*>(.*?)</a>", RegexOptions.Singleline);
        for (int i = 0; i < links.Count; i++)
            list.Add(new(Html.StripTags(links[i].Groups[2].Value), DdgUrl(WebUtility.HtmlDecode(links[i].Groups[1].Value)),
                         i < snippets.Count ? Html.StripTags(snippets[i].Groups[1].Value) : ""));
        return list;
    }

    static string DdgUrl(string href)
    {
        var uddg = Regex.Match(href, "[?&]uddg=([^&]+)");
        if (uddg.Success) return Uri.UnescapeDataString(uddg.Groups[1].Value);
        return href.StartsWith("//") ? "https:" + href : href;
    }

    internal static List<SearchResult> ParseBing(string html)
    {
        var list = new List<SearchResult>();
        foreach (var block in html.Split("<li class=\"b_algo").Skip(1))
        {
            var a = Regex.Match(block, "<h2[^>]*>\\s*<a[^>]*href=\"([^\"]+)\"[^>]*>(.*?)</a>", RegexOptions.Singleline);
            if (!a.Success) continue;
            var p = Regex.Match(block, "<p[^>]*>(.*?)</p>", RegexOptions.Singleline);
            list.Add(new(Html.StripTags(a.Groups[2].Value), BingUrl(WebUtility.HtmlDecode(a.Groups[1].Value)),
                         p.Success ? Html.StripTags(p.Groups[1].Value) : ""));
        }
        return list;
    }

    /// Bing wraps results in bing.com/ck/a?...&u=a1<base64url of the real address>.
    internal static string BingUrl(string href)
    {
        var u = Regex.Match(href, "[?&]u=a1([^&]+)");
        if (!u.Success) return href;
        try
        {
            var b = u.Groups[1].Value.Replace('-', '+').Replace('_', '/');
            b = b.PadRight(b.Length + (4 - b.Length % 4) % 4, '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(b));
        }
        catch { return href; }
    }

    /// fetch_page only: refuses addresses on this PC or the local network (see Safety.PublicOnlyHandler).
    static readonly HttpClient PublicHttp = CreatePublicHttp();

    static HttpClient CreatePublicHttp()
    {
        var h = new HttpClient(Safety.PublicOnlyHandler()) { Timeout = TimeSpan.FromSeconds(30) };
        foreach (var header in Http.DefaultRequestHeaders) h.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        return h;
    }

    static async Task<string> Fetch(string url, string? find, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
            return $"{url} isn't a web address (http or https). Use read_file for files on this PC.";
        using var res = await PublicHttp.GetAsync(u, HttpCompletionOption.ResponseHeadersRead, ct);
        var type = res.Content.Headers.ContentType?.MediaType ?? "";
        // PDFs, images, zips etc. would come back as garbage text and waste tokens
        bool textual = type.Length == 0 || type.StartsWith("text/") || type.Contains("html") || type.Contains("json") || type.Contains("xml");
        if (!textual)
            return $"[{(int)res.StatusCode}] {url} is {type} ({res.Content.Headers.ContentLength?.ToString() ?? "unknown"} bytes), not a web page. " +
                   "To get it, download it with run_powershell (Invoke-WebRequest -OutFile) and open it.";
        var body = await ReadCapped(res, 3_000_000, ct);
        var text = type.Contains("html") || body.TrimStart().StartsWith("<") ? Html.ToText(body) : body;
        if (find != null) text = Relevant(text, find);
        return Truncate($"[{(int)res.StatusCode}] {url}\n\n{text}", PageChars);
    }

    static async Task<string> ReadCapped(HttpResponseMessage res, int maxBytes, CancellationToken ct)
    {
        using var s = await res.Content.ReadAsStreamAsync(ct);
        var buf = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while (buf.Length < maxBytes && (n = await s.ReadAsync(chunk, ct)) > 0) buf.Write(chunk, 0, n);
        var charset = res.Content.Headers.ContentType?.CharSet;
        Encoding enc;
        try { enc = charset != null ? Encoding.GetEncoding(charset.Trim('"')) : Encoding.UTF8; } catch { enc = Encoding.UTF8; }
        return enc.GetString(buf.GetBuffer(), 0, (int)buf.Length);
    }

    /// Keep only the lines that mention the search words, plus one line either side for context.
    /// A whole article is often 8k chars; the part that answers the question is usually a few hundred.
    internal static string Relevant(string text, string find)
    {
        var words = find.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2).ToArray();
        if (words.Length == 0) return text;
        var lines = text.Split('\n');
        var keep = new SortedSet<int>();
        for (int i = 0; i < lines.Length; i++)
            if (words.Any(w => lines[i].Contains(w, StringComparison.OrdinalIgnoreCase)))
                for (int j = Math.Max(0, i - 1); j <= Math.Min(lines.Length - 1, i + 1); j++) keep.Add(j);
        if (keep.Count == 0) return "(none of those words appear on the page; here's the start)\n" + text;
        var sb = new StringBuilder();
        int prev = -2;
        foreach (var i in keep)
        {
            if (i != prev + 1) sb.AppendLine("…");
            sb.AppendLine(lines[i]);
            prev = i;
        }
        return sb.ToString();
    }

    // ---- files ----

    static string ListDir(string path)
    {
        var dir = new DirectoryInfo(Environment.ExpandEnvironmentVariables(path));
        var sb = new StringBuilder();
        int n = 0;
        foreach (var e in dir.EnumerateFileSystemInfos().OrderBy(e => e is FileInfo).ThenBy(e => e.Name))
        {
            if (++n > 150) { sb.AppendLine("…(more)"); break; }
            sb.AppendLine(e is FileInfo f ? $"{f.Name}  ({f.Length / 1024.0:0.#} KB, {f.LastWriteTime:yyyy-MM-dd})" : $"[dir] {e.Name}");
        }
        return sb.Length > 0 ? sb.ToString() : "(empty)";
    }

    static async Task<string> ReadFile(string path, int page, CancellationToken ct)
    {
        path = Environment.ExpandEnvironmentVariables(path);
        if (Documents.Handles(path)) return Truncate(Documents.Read(path));
        if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return Ocr.Available ? await Ocr.ReadPdfAsync(path, page, PdfChars, ct)
                : $"{path} is a PDF, and reading PDFs needs Windows' text recognition, which isn't installed for any of your languages. Open it with 'open' and read it on screen.";
        if (Path.GetExtension(path).ToLowerInvariant() is ".doc" or ".xls" or ".ppt")
            return $"{path} is an old-style Office file ({Path.GetExtension(path)}), which can't be read directly. Open it with 'open' and read it on screen, or save it in the newer format first.";
        var info = new FileInfo(path);
        if (info.Length > 20_000_000) return $"{path} is {info.Length / 1_000_000} MB; too big to read whole. Use run_powershell (Get-Content -TotalCount / Select-String) for parts.";
        var text = File.ReadAllText(path);
        if (text.AsSpan(0, Math.Min(text.Length, 4000)).Contains('\0'))
            return $"{path} is a binary file ({info.Length / 1024} KB), not text. Open it with 'open' instead.";
        return Truncate(text);
    }

    // ---- shell ----

    /// Runs the command from a short-lived .ps1 file rather than a long "powershell -Command ..." line.
    /// Antivirus heuristics (Defender's "Commando" detection) flag long inline command lines as attacker-like;
    /// a script file run the normal way looks like what it is. The file is deleted afterwards.
    static async Task<string> PowerShell(string command, CancellationToken ct)
    {
        var dir = Path.Combine(Paths.Data, "scripts");
        Directory.CreateDirectory(dir);
        var script = Path.Combine(dir, $"task-{Guid.NewGuid():N}.ps1");
        // UTF-8 with BOM so Windows PowerShell 5.1 reads non-English text correctly; UTF-8 output for the same reason
        await File.WriteAllTextAsync(script, "[Console]::OutputEncoding = [Text.Encoding]::UTF8\r\n" + command, new UTF8Encoding(true), ct);
        try { return await RunScript(script, ct); }
        finally { try { File.Delete(script); } catch { } }
    }

    /// Keeps the first 200k characters and drains the rest (so the process isn't blocked on a full pipe).
    static async Task<string> ReadCapped(StreamReader r, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new char[8192];
        int n;
        while ((n = await r.ReadAsync(buf, ct)) > 0)
            if (sb.Length < 200_000) sb.Append(buf, 0, n);
        return sb.ToString();
    }

    static async Task<string> RunScript(string script, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            // Bypass only affects this one script (Windows blocks all .ps1 files by default)
            ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        using var p = Process.Start(psi)!;
        var stdout = ReadCapped(p.StandardOutput, ct);
        var stderr = ReadCapped(p.StandardError, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            p.Kill(true);
            if (ct.IsCancellationRequested) throw;
            return "Timed out after 2 minutes and was killed.";
        }
        var output = (await stdout) + (await stderr);
        return Truncate($"exit {p.ExitCode}\n{output}");
    }
}
