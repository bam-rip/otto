using System.Text.Json;
using System.Text.Json.Nodes;

namespace Otto;

/// Things Otto learns once instead of rediscovering (and paying for) every time:
/// short notes about the user's apps and preferences, and saved routines that replay without the model.
/// Both live in %LOCALAPPDATA%\Otto and go into the (cached) system prompt.
static class Memory
{
    const int MaxNotesChars = 3_000; // every char rides along on every request, so keep it tight
    static string Dir => Paths.Data;
    static string NotesPath => Path.Combine(Dir, "notes.txt");
    static string RoutinesPath => Path.Combine(Dir, "routines.json");
    static readonly object gate = new();

    public static JsonNode ToolDefinitions() => JsonNode.Parse("""
    [
      {"name":"remember","description":"Save a one-line lasting note (app layout, working shortcut, preference, where something lives). 'replace' = text of a note to correct.",
       "input_schema":{"type":"object","properties":{"note":{"type":"string"},"replace":{"type":"string"}},"required":["note"]}},
      {"name":"save_routine","description":"After a multi-step job worked, save its calls to replay later without you. Click by 'name', never element/x,y. {placeholders} for parts that change.",
       "input_schema":{"type":"object","properties":{
         "name":{"type":"string"},
         "description":{"type":"string","description":"what it does; what each placeholder means"},
         "calls":{"type":"array","description":"{tool: computer|open|run_powershell|write_file, input}",
           "items":{"type":"object","properties":{"tool":{"type":"string"},"input":{"type":"object"}},"required":["tool","input"]}}},
        "required":["name","description","calls"]}},
      {"name":"run_routine","description":"Replay a saved routine. If it fails partway, finish by hand and save a fixed one.",
       "input_schema":{"type":"object","properties":{"name":{"type":"string"},"values":{"type":"object"}},"required":["name"]}},
      {"name":"forget_routine","description":"Delete a saved routine.",
       "input_schema":{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}}
    ]
    """)!;

    /// Goes into the system prompt. Changes only when something is saved, so the cache survives.
    public static string PromptSection()
    {
        lock (gate)
        {
            // built once per change, not re-read from disk on every step of every task
            var stamp = (File.GetLastWriteTimeUtc(NotesPath), File.GetLastWriteTimeUtc(RoutinesPath), Dir);
            if (section != null && stamp == sectionStamp) return section;
            sectionStamp = stamp;
            return section = BuildSection();
        }
    }

    static string? section;
    static (DateTime, DateTime, string) sectionStamp;

    static string BuildSection()
    {
        var notes = File.Exists(NotesPath) ? File.ReadAllText(NotesPath).Trim() : "";
        JsonObject routines;
        try { routines = LoadRoutines(); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or IOException) { routines = new JsonObject(); }
        var s = "";
        if (notes.Length > 0) s += "\nThings you've learned about this PC and user:\n" + notes + "\n";
        if (routines.Count > 0)
            s += "\nSaved routines (run_routine is far cheaper than redoing these by hand):\n" +
                 string.Join("\n", routines.Select(kv => $"- {kv.Key}: {kv.Value!["description"]}")) + "\n";
        return s;
    }

    public static string Remember(JsonNode input)
    {
        var note = (input["note"]?.GetValue<string>() ?? "").Replace('\n', ' ').Trim();
        if (note.Length == 0) throw new ArgumentException("empty note");
        lock (gate)
        {
            Directory.CreateDirectory(Dir);
            var lines = File.Exists(NotesPath) ? File.ReadAllLines(NotesPath).ToList() : new List<string>();
            if (input["replace"]?.GetValue<string>() is string old && old.Trim().Length > 0)
                lines.RemoveAll(l => l.Contains(old.Trim(), StringComparison.OrdinalIgnoreCase));
            lines.Add("- " + note.TrimStart('-', ' '));
            // oldest notes fall off first when it gets too long
            while (lines.Sum(l => l.Length + 1) > MaxNotesChars && lines.Count > 1) lines.RemoveAt(0);
            SafeFile.WriteAllLines(NotesPath, lines);
            section = null;
        }
        return "Noted.";
    }

    public static string SaveRoutine(JsonNode input)
    {
        var name = Id(input["name"]?.GetValue<string>());
        var calls = Desktop.ArrayOf(input["calls"]) ?? throw new ArgumentException("missing 'calls'");
        if (calls.Count == 0) throw new ArgumentException("no calls");
        foreach (var c in calls)
        {
            var tool = c?["tool"]?.GetValue<string>();
            if (tool is not ("computer" or "open" or "run_powershell" or "write_file"))
                throw new ArgumentException($"routines can't use '{tool}'");
            if (tool == "computer" && Desktop.ArrayOf(c!["input"]?["steps"]) is JsonArray steps &&
                steps.Any(s => s?["element"] != null || s?["x"] != null))
                throw new ArgumentException("element numbers and x,y change between runs; use 'name' to click by label");
        }
        string note;
        lock (gate)
        {
            Directory.CreateDirectory(Dir);
            var all = Writable(out note);
            all[name] = new JsonObject { ["description"] = input["description"]?.GetValue<string>() ?? "", ["calls"] = calls.DeepClone() };
            SafeFile.WriteAllText(RoutinesPath, all.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            section = null; // our own save: rebuild even if the file's time didn't visibly move
        }
        return $"Saved routine '{name}'.{note}";
    }

    public static string Forget(JsonNode input)
    {
        var name = Id(input["name"]?.GetValue<string>());
        string note;
        lock (gate)
        {
            var all = Writable(out note);
            if (!all.Remove(name)) return $"No routine called '{name}'.{note}";
            SafeFile.WriteAllText(RoutinesPath, all.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            section = null; // our own save: rebuild even if the file's time didn't visibly move
        }
        return "Deleted." + note;
    }

    /// Replays a routine with no model calls in between. Only the last computer call reports the screen.
    public static async Task<JsonNode> RunRoutine(JsonNode input, Func<string, bool> confirm, Action<string> onTool, CancellationToken ct)
    {
        var name = Id(input["name"]?.GetValue<string>());
        JsonObject routine;
        lock (gate)
            routine = LoadRoutines()[name]?.AsObject() ?? throw new ArgumentException($"No routine called '{name}'.");

        var calls = Fill(routine["calls"]!, input["values"] as JsonObject);

        JsonNode last = JsonValue.Create("Done.")!;
        for (int i = 0; i < calls.Count; i++)
        {
            var tool = calls[i]!["tool"]!.GetValue<string>();
            var callInput = calls[i]!["input"]!.AsObject();
            bool final = i == calls.Count - 1;
            onTool($"↻ {Tools.Describe(tool, callInput)}");
            try
            {
                if (tool == "computer")
                {
                    if (!final) callInput["observe"] = "none";
                    last = await Desktop.Run(callInput, confirm, ct);
                }
                else last = await Tools.Run(tool, callInput, confirm, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                var screen = await Desktop.Run(new JsonObject { ["steps"] = new JsonArray() }, confirm, ct);
                var msg = $"Routine '{name}' failed at call {i + 1} of {calls.Count} ({tool}): {e.Message}. Here's the screen now:";
                if (screen is JsonArray arr) { arr.Insert(0, new JsonObject { ["type"] = "text", ["text"] = msg }); return arr; }
                return JsonValue.Create(msg + "\n" + screen)!;
            }
            if (last is JsonValue v && v.ToString().StartsWith("User declined")) return last;
        }
        return last;
    }

    /// The routine's calls with each {placeholder} replaced by its value. Values are JSON-escaped, so quotes and
    /// backslashes in them (paths, messages) can't break or inject into the stored calls.
    internal static JsonArray Fill(JsonNode calls, JsonObject? values)
    {
        var json = calls.ToJsonString();
        if (values != null)
            foreach (var (k, v) in values)
                // a placeholder is a plain word; a "name" made of JSON ({"target":"x"}) would match the routine's own structure
                if (k.Length is > 0 and <= 40 && k.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or ' '))
                    json = json.Replace("{" + k + "}", JsonEncodedText.Encode(v?.ToString() ?? "").ToString());
        return JsonNode.Parse(json)!.AsArray();
    }

    /// Throws if the file exists but can't be read or parsed: callers that write must not treat that as
    /// "no routines" and overwrite it (see Writable).
    static JsonObject LoadRoutines() =>
        File.Exists(RoutinesPath) ? JsonNode.Parse(File.ReadAllText(RoutinesPath)) as JsonObject
            ?? throw new JsonException("routines.json isn't a JSON object") : new JsonObject();

    /// The routines, for changing. A damaged routines.json is moved aside (not lost) and replaced by an empty
    /// one; 'note' says where it went so the model can tell the user.
    static JsonObject Writable(out string note)
    {
        note = "";
        try { return LoadRoutines(); }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            var aside = Path.Combine(Dir, $"routines.damaged-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Move(RoutinesPath, aside);
            note = $" (routines.json was damaged, so it was moved to {aside} and a new one started)";
            return new JsonObject();
        }
    }

    static string Id(string? s) =>
        string.IsNullOrWhiteSpace(s) ? throw new ArgumentException("missing 'name'") : s.Trim().ToLowerInvariant().Replace(' ', '_');
}
