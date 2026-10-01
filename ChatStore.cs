using System.Text.Json.Nodes;

namespace Otto;

/// Saved chats, one JSON file each in %LOCALAPPDATA%\Otto\chats. Saved after every request, so nothing
/// is lost when you start a new chat, quit, or restart. Screenshots and pasted images are left out (big,
/// and stale anyway); the conversation itself is kept word for word.
static class ChatStore
{
    const int Keep = 100; // oldest chats beyond this are removed
    static string Dir => Path.Combine(Paths.Data, "chats");

    public sealed record Summary(string Id, string Title, DateTime When);

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
                    DateTime.TryParse(j["saved"]?.ToString(), out var d) ? d : f.LastWriteTime));
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
