using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Otto;

/// Saved chats, one JSON file each in %LOCALAPPDATA%\Otto\chats. Saved after every request, so nothing
/// is lost when you start a new chat, quit, or restart. Screenshots and pasted images are left out (big,
/// and stale anyway); the conversation itself is kept word for word. Files are encrypted with Windows' DPAPI,
/// tied to your Windows account: other accounts on the PC, or someone with the disk, can't read them.
static class ChatStore
{
    const int Keep = 100; // oldest chats beyond this are removed
    static string Dir => Path.Combine(Paths.Data, "chats");

    static readonly byte[] Magic = Encoding.ASCII.GetBytes("OTTO-DPAPI1\n");
    static readonly byte[] Entropy = Encoding.ASCII.GetBytes("Otto saved chat");

    internal static byte[] Seal(string json) =>
        Magic.Concat(ProtectedData.Protect(Encoding.UTF8.GetBytes(json), Entropy, DataProtectionScope.CurrentUser)).ToArray();

    /// Reads a chat file, encrypted or (from before 1.2.4) plain JSON.
    internal static string Open(byte[] file, out bool wasPlain)
    {
        wasPlain = !file.AsSpan().StartsWith(Magic);
        return wasPlain ? Encoding.UTF8.GetString(file)
            : Encoding.UTF8.GetString(ProtectedData.Unprotect(file[Magic.Length..], Entropy, DataProtectionScope.CurrentUser));
    }

    static string Read(string path)
    {
        var text = Open(File.ReadAllBytes(path), out bool plain);
        if (plain) try { SafeFile.WriteAllBytes(path, Seal(text)); } catch { } // encrypt older chats as they're read
        return text;
    }

    /// Text: everything you and Otto said (not tool output), for searching.
    public sealed record Summary(string Id, string Title, string Preview, DateTime When, string Text = "")
    {
        /// Every word of the query appears somewhere in the chat.
        public bool Matches(string query) =>
            query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                 .All(w => Title.Contains(w, StringComparison.OrdinalIgnoreCase) || Text.Contains(w, StringComparison.OrdinalIgnoreCase));

        /// A few words either side of the first match, when the match isn't in the title.
        public string? Snippet(string query)
        {
            foreach (var w in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (Title.Contains(w, StringComparison.OrdinalIgnoreCase)) continue;
                int at = Text.IndexOf(w, StringComparison.OrdinalIgnoreCase);
                if (at < 0) continue;
                int from = Math.Max(0, at - 40);
                int space = from == 0 ? -1 : Text.IndexOf(' ', from);
                if (space >= 0 && space < at) from = space + 1;
                return (from > 0 ? "…" : "") + Text[from..].Clip(110);
            }
            return null;
        }
    }

    public static string NewId() => DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");

    public static void Save(string id, JsonArray messages)
    {
        if (messages.Count == 0) return;
        try
        {
            Directory.CreateDirectory(Dir);
            var copy = (JsonArray)messages.DeepClone();
            StripImages(copy);
            var doc = new JsonObject
            {
                ["title"] = Title(copy),
                ["preview"] = Preview(copy),
                ["saved"] = DateTime.Now.ToString("o"),
                ["messages"] = copy,
            };
            var path = Path.Combine(Dir, id + ".json");
            SafeFile.WriteAllBytes(path, Seal(doc.ToJsonString()));

            foreach (var old in new DirectoryInfo(Dir).GetFiles("*.json").OrderByDescending(f => f.Name).Skip(Keep))
                try { old.Delete(); } catch { }
        }
        catch { /* history is a convenience; never break a request over it */ }
    }

    /// Summaries already read, by file, with the file's write time and size: opening history only decrypts and parses
    /// chats that changed since (100 chats took ~135 ms every time; now a few ms after the first).
    static readonly Dictionary<string, (DateTime written, long size, Summary summary)> summaries = new();

    public static List<Summary> List()
    {
        var list = new List<Summary>();
        if (!Directory.Exists(Dir)) return list;
        lock (summaries)
        {
            var seen = new HashSet<string>();
            foreach (var f in new DirectoryInfo(Dir).GetFiles("*.json").OrderByDescending(f => f.Name))
            {
                seen.Add(f.FullName);
                if (summaries.TryGetValue(f.FullName, out var known) && known.written == f.LastWriteTimeUtc && known.size == f.Length)
                {
                    list.Add(known.summary);
                    continue;
                }
                try
                {
                    var j = JsonNode.Parse(Read(f.FullName))!;
                    var summary = new Summary(Path.GetFileNameWithoutExtension(f.Name), j["title"]?.ToString() ?? "(chat)",
                        j["preview"]?.ToString() ?? "",
                        DateTime.TryParse(j["saved"]?.ToString(), out var d) ? d : f.LastWriteTime,
                        j["messages"] is JsonArray msgs ? SearchText(msgs) : "");
                    f.Refresh(); // Read() may have just re-saved an older plain chat encrypted
                    summaries[f.FullName] = (f.LastWriteTimeUtc, f.Length, summary);
                    list.Add(summary);
                }
                catch { }
            }
            foreach (var gone in summaries.Keys.Where(k => !seen.Contains(k)).ToList()) summaries.Remove(gone);
        }
        return list;
    }

    public static JsonArray? Load(string id)
    {
        try { return JsonNode.Parse(Read(Path.Combine(Dir, id + ".json")))?["messages"]?.AsArray(); }
        catch { return null; }
    }

    public static void Delete(string id) { try { File.Delete(Path.Combine(Dir, id + ".json")); } catch { } }

    public static void DeleteAll() { try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); } catch { } }

    /// First thing you asked, trimmed: what the history menu shows.
    static string Title(JsonArray messages)
    {
        foreach (var m in messages)
        {
            if (Agent.TurnText(m!) is string t)
            {
                t = t.Replace('\n', ' ').Trim();
                return t.Clip(60);
            }
        }
        return "(chat)";
    }

    /// Otto's last words in the chat, for the second line in the history list.
    internal static string Preview(JsonArray messages)
    {
        for (int i = messages.Count - 1; i >= 0; i--)
            if (OttoText(messages[i]) is { Length: > 0 } text) return text.Clip(120);
        return "";
    }

    /// What was said in the chat, one line of plain text: your messages and Otto's replies, no tool output.
    internal static string SearchText(JsonArray messages) =>
        string.Join(" ", messages.Select(m => m == null ? "" : Agent.TurnText(m) is string said ? Squash(said) : OttoText(m))
                                 .Where(t => t.Length > 0));

    static string OttoText(JsonNode? m)
    {
        if (m?["role"]?.GetValue<string>() != "assistant") return "";
        return Squash(m["content"] switch
        {
            JsonValue v => v.ToString(),
            JsonArray blocks => string.Join(" ", blocks.Where(b => b?["type"]?.GetValue<string>() == "text").Select(b => b!["text"]?.ToString())),
            _ => "",
        });
    }

    static string Squash(string s) => string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    static void StripImages(JsonArray messages)
    {
        foreach (var m in messages)
        {
            if (m?["content"] is not JsonArray blocks) continue;
            for (int i = 0; i < blocks.Count; i++)
            {
                if (blocks[i]?["type"]?.GetValue<string>() == "image")
                    blocks[i] = new JsonObject { ["type"] = "text", ["text"] = "[image]" };
                else if (blocks[i]?["content"] is JsonArray inner)
                    for (int k = 0; k < inner.Count; k++)
                        if (inner[k]?["type"]?.GetValue<string>() == "image")
                            inner[k] = new JsonObject { ["type"] = "text", ["text"] = "[screenshot]" };
            }
        }
    }
}

/// Something you attached to a message: a file (sent as its path) or an image (sent so the AI can see it).
sealed record Attachment(string Name, string? FilePath, byte[]? Jpeg)
{
    static readonly string[] ImageExt = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };

    public static Attachment FromFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ImageExt.Contains(ext) && ext != ".webp")
        {
            try
            {
                using var img = Image.FromFile(path);
                return new Attachment(Path.GetFileName(path), path, Encode(img));
            }
            catch { }
        }
        return new Attachment(Path.GetFileName(path), path, null);
    }

    public static Attachment FromImage(Image img, string name) => new(name, null, Encode(img));

    /// Shrink to at most 1280 px and JPEG it: plenty for the AI to read, far fewer tokens than a full-size PNG.
    static byte[] Encode(Image img)
    {
        double k = Math.Min(1.0, 1280.0 / Math.Max(img.Width, img.Height));
        using var small = new Bitmap(img, Math.Max(1, (int)(img.Width * k)), Math.Max(1, (int)(img.Height * k)));
        return Otto.Jpeg.Encode(small, 85);
    }
}
