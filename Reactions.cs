using System.Text.Json;
using System.Text.Json.Nodes;

namespace Otto;

/// The "Reaction images" addon: square pictures with a meaning each ("facepalm: when something is obviously
/// dumb"). The reaction_image tool puts the one that fits on the clipboard, and Otto pastes it into the chat or
/// comment box like any picture. Only offered to the AI while the addon is installed, and only meant for when
/// you ask for a reaction or one clearly fits what you asked for, never as decoration on every reply.
static class Reactions
{
    public const string Id = "reactions";

    public sealed record Reaction(string Name, string File, string Meaning, string UseWhen);

    public static bool Available => Addons.IsInstalled(Id);

    static string ListPath => Path.Combine(Addons.Dir(Id), "reactions.json");
    static ((string, DateTime, long) stamp, List<Reaction> list) cache;
    static readonly object gate = new();

    public static List<Reaction> All()
    {
        if (!File.Exists(ListPath)) return new();
        var info = new FileInfo(ListPath);
        var stamp = (ListPath, info.LastWriteTimeUtc, info.Length); // a reinstall writes a new file, maybe within the same tick
        lock (gate)
        {
            if (cache.list != null && cache.stamp == stamp) return cache.list;
            List<Reaction> list;
            try { list = JsonSerializer.Deserialize<List<Reaction>>(File.ReadAllText(ListPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); }
            catch (JsonException) { list = new(); }
            // only pictures that are really there, inside the addon's own folder
            var dir = Path.GetFullPath(Addons.Dir(Id)) + Path.DirectorySeparatorChar;
            list = list.Where(r => r.Name.Length > 0 && Path.GetFullPath(Path.Combine(dir, r.File)) is var p && p.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && File.Exists(p)).ToList();
            cache = (stamp, list);
            return list;
        }
    }

    /// The tool, with every picture's meaning in its description, so the AI picks by what fits rather than at random.
    public static JsonNode ToolDefinition()
    {
        var menu = string.Join("\n", All().Select(r => $"{r.Name}: {r.Meaning}. Use when {r.UseWhen}."));
        return new JsonObject
        {
            ["name"] = "reaction_image",
            ["description"] = "Send a reaction picture. where 'chat': show it right here in your chat with the user. If they ask for a meme or reaction " +
                              "and don't name another app, that means here: use 'chat' and never ask where to send it. where 'paste' (only when they name an app or a post): copy it to the clipboard, then paste it (ctrl+v with the computer tool) into " +
                              "another app's chat or comment box. Only when the user asks for a reaction/meme, or asks you to reply somewhere and a picture " +
                              "clearly fits; never by habit or on every message. Pick the one whose meaning matches the moment; if none fits, don't send one. Pictures:\n" + menu,
            ["input_schema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["name"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(All().Select(r => (JsonNode)r.Name).ToArray()) },
                    ["where"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("chat", "paste") },
                },
                ["required"] = new JsonArray("name", "where"),
            },
        };
    }

    /// "paste" puts it on the clipboard for another app; anything else means show it in Otto's own chat.
    public static bool ForChat(JsonNode input) => input["where"]?.GetValue<string>() != "paste";

    /// The picture's file, or null when there's no reaction by that name (or the addon has gone).
    public static string? PathOf(string name) =>
        All().FirstOrDefault(x => x.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) is Reaction r ? Path.Combine(Addons.Dir(Id), r.File) : null;

    static string Unknown(string name) => $"No reaction called '{name}'. Pick one of: {string.Join(", ", All().Select(x => x.Name))}";

    public static string Copy(string name)
    {
        var path = PathOf(name) ?? throw new ArgumentException(Unknown(name));
        var r = All().First(x => x.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        // the clipboard belongs to a UI (STA) thread; the agent runs on a worker
        Exception? failed = null;
        var t = new Thread(() =>
        {
            try
            {
                using var img = Image.FromFile(path);
                var data = new DataObject();
                data.SetImage(img); // most apps
                using var png = new MemoryStream();
                img.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                data.SetData("PNG", false, new MemoryStream(png.ToArray())); // browsers prefer PNG
                data.SetFileDropList(new System.Collections.Specialized.StringCollection { path }); // upload boxes that take files
                Clipboard.SetDataObject(data, copy: true, retryTimes: 10, retryDelay: 100);
            }
            catch (Exception e) { failed = e; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failed != null) throw new InvalidOperationException("Couldn't copy the picture: " + failed.Message);
        return $"Copied the \"{r.Name}\" picture to the clipboard. Click into the chat or comment box and press ctrl+v, then send it if the user asked you to.";
    }
}
