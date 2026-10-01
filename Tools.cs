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
    static readonly HttpClient Http = CreateHttp();
    /// Only commands that destroy data, change how Windows itself works, or run code fetched from the internet
    /// need a yes. Reading, creating, downloading, installing from official repos, closing apps etc. just run.
    internal static readonly Regex RiskyCommand = new(
        @"\b(Remove-Item|ri|rm|rmdir|del|erase|rd|Clear-Content|Clear-RecycleBin|Format-Volume|Clear-Disk|Initialize-Disk|diskpart|bcdedit|" +
        @"Stop-Computer|Restart-Computer|shutdown|Set-ExecutionPolicy|Uninstall-\w+|Remove-AppxPackage|Disable-ComputerRestore|" +
        @"Set-MpPreference|Add-MpPreference|takeown|icacls|cipher|vssadmin|wbadmin|Invoke-Expression|iex|sdelete)\b" +
        @"|\bformat\s+[a-z]:|\breg\s+(add|delete|import)\b|-Verb\s+RunAs" +
        @"|\b(Set-ItemProperty|New-ItemProperty|New-Item|Set-Item|Rename-ItemProperty|Clear-ItemProperty|Rename-Item|Move-Item|Copy-Item)\b[^;|\n]*(HKLM:|HKEY_LOCAL_MACHINE)" +
        @"|::Delete\s*\(|\.Delete\s*\(|::WriteAllBytes|Remove-ItemProperty|-EncodedCommand|\s-enc\s",
        RegexOptions.IgnoreCase);

    static readonly string[] RiskyExtensions = { ".exe", ".bat", ".cmd", ".ps1", ".msi", ".vbs", ".js", ".reg", ".lnk", ".scr" };

    static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0 Safari/537.36");
        return h;
    }

    public static JsonNode Definitions()
    {
        var tools = JsonNode.Parse(ToolJson)!.AsArray();
        tools.Add(Desktop.ToolDefinition());
        foreach (var t in Memory.ToolDefinitions().AsArray()) tools.Add(t!.DeepClone());
        // email/calendar tools only exist once signed in, so they cost nothing otherwise
        if (Graph.SignedIn) foreach (var t in Graph.ToolDefinitions().AsArray()) tools.Add(t!.DeepClone());
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
      {"name":"read_file","description":"Read a text file or the text of a .docx document.",
       "input_schema":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}},
      {"name":"write_file","description":"Create or overwrite a text file (old version is backed up).",
       "input_schema":{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}},"required":["path","content"]}},
      {"name":"run_powershell","description":"Run PowerShell, get its output. Deleting or system-changing commands ask the user first.",
       "input_schema":{"type":"object","properties":{"command":{"type":"string"}},"required":["command"]}}
    ]
    """;

    /// A required string argument of a tool call.
    internal static string S(JsonNode input, string key) =>
        input[key]?.GetValue<string>() ?? throw new ArgumentException($"missing '{key}'");

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
        "escalate" => "Thinking harder (switching to the bigger model)",
        _ => name,
    };

    public static async Task<string> Run(string name, JsonNode input, Func<string, bool> confirm, CancellationToken ct)
    {
        if (Graph.Handles(name)) return await Graph.Run(name, input, confirm, ct);
        switch (name)
        {
            case "web_search": return await Search(S(input, "query"), ct);
            case "fetch_page": return await Fetch(S(input, "url"), input["find"]?.GetValue<string>(), ct);
            case "open":
            {
                var target = S(input, "target");
                if (RiskyExtensions.Contains(Path.GetExtension(target).ToLowerInvariant()) && FromInternet(target)
                    && !confirm($"Run this downloaded program?\n\n{target}"))
                    return "User declined.";
                return await Apps.Open(target, ct);
            }
            case "list_dir": return ListDir(S(input, "path"));
            case "read_file": return ReadFile(S(input, "path"));
            case "write_file":
            {
                var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(S(input, "path")));
                // overwriting is undoable instead of asking: the old version goes to a backup first
                var backup = File.Exists(path) ? Backup(path) : null;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, S(input, "content"), ct);
                return backup == null ? $"Wrote {path}" : $"Wrote {path} (previous version backed up to {backup})";
            }
            case "run_powershell":
            {
                var cmd = S(input, "command");
                if (RiskyCommand.IsMatch(cmd) && !confirm($"Run this PowerShell command?\n\n{cmd}")) return "User declined.";
                return await PowerShell(cmd, ct);
            }
            case "window": return Apps.Window(S(input, "action"), input["title"]?.GetValue<string>());
            case "remember": return Memory.Remember(input);
            case "save_routine": return Memory.SaveRoutine(input);
            case "forget_routine": return Memory.Forget(input);
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
        if (full.StartsWith(downloads, StringComparison.OrdinalIgnoreCase) || full.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
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
        foreach (var old in new DirectoryInfo(dir).GetFiles().OrderByDescending(f => f.CreationTimeUtc).Skip(200))
            try { old.Delete(); } catch { }
        return dest;
    }

    static string Truncate(string s, int max = MaxOutput) =>
        s.Length <= max ? s : s[..max] + "\n…(truncated; use 'find' on fetch_page, or read a smaller part)";

    // ---- web ----

    static async Task<string> Search(string query, CancellationToken ct)
    {
        var html = await Http.GetStringAsync("https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query), ct);
        var sb = new StringBuilder();
        var links = Regex.Matches(html, "class=\"result__a\"[^>]*href=\"([^\"]+)\"[^>]*>(.*?)</a>", RegexOptions.Singleline);
        var snippets = Regex.Matches(html, "class=\"result__snippet\"[^>]*>(.*?)</a>", RegexOptions.Singleline);
        for (int i = 0; i < links.Count && i < 10; i++)
        {
            var href = WebUtility.HtmlDecode(links[i].Groups[1].Value);
            var uddg = Regex.Match(href, "[?&]uddg=([^&]+)");
            if (uddg.Success) href = Uri.UnescapeDataString(uddg.Groups[1].Value);
            sb.AppendLine(Html.StripTags(links[i].Groups[2].Value));
            sb.AppendLine(href);
            if (i < snippets.Count) sb.AppendLine(Html.StripTags(snippets[i].Groups[1].Value));
            sb.AppendLine();
        }
        if (sb.Length == 0 && html.Contains("anomaly", StringComparison.OrdinalIgnoreCase))
            return "The search engine is rate-limiting right now. Search in the browser instead: open https://duckduckgo.com/?q=...";
        return sb.Length > 0 ? sb.ToString() : "No results.";
    }

    static async Task<string> Fetch(string url, string? find, CancellationToken ct)
    {
        using var res = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
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

    static string ReadFile(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path);
        if (path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(path);
            using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
            var xml = Regex.Replace(reader.ReadToEnd(), "</w:p>", "\n");
            return Truncate(Html.StripTags(xml));
        }
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
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
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
