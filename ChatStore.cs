using System.Text.Json.Nodes;

namespace Otto;

/// Saved chats, one JSON file each in %LOCALAPPDATA%\Otto\chats. Saved after every request, so nothing
/// is lost when you start a new chat, quit, or restart. Screenshots and pasted images are left out (big,
/// and stale anyway); the conversation itself is kept word for word.
static class ChatStore
{
    const int Keep = 100; // oldest chats beyond this are removed
    static string Dir => Path.Combine(Paths.Data, "chats");

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
            File.WriteAllText(Path.Combine(Dir, id + ".json"), doc.ToJsonString());
            foreach (var old in new DirectoryInfo(Dir).GetFiles("*.json").OrderByDescending(f => f.Name).Skip(Keep))
                try { old.Delete(); } catch { }
        }
        catch { /* history is a convenience; never break a request over it */ }
    }

    public static List<Summary> List()
    {
        var list = new List<Summary>();
        if (!Directory.Exists(Dir)) return list;
        foreach (var f in new DirectoryInfo(Dir).GetFiles("*.json").OrderByDescending(f => f.Name))
        {
            try
            {
                var j = JsonNode.Parse(File.ReadAllText(f.FullName))!;
                list.Add(new Summary(Path.GetFileNameWithoutExtension(f.Name), j["title"]?.ToString() ?? "(chat)",
                    j["preview"]?.ToString() ?? "",
                    DateTime.TryParse(j["saved"]?.ToString(), out var d) ? d : f.LastWriteTime,
                    j["messages"] is JsonArray msgs ? SearchText(msgs) : ""));
            }
            catch { }
        }
        return list;
    }

    public static JsonArray? Load(string id)
    {
        try { return JsonNode.Parse(File.ReadAllText(Path.Combine(Dir, id + ".json")))?["messages"]?.AsArray(); }
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
