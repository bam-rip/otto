using System.Text.Json;
using System.Text.Json.Nodes;

namespace Otto;

/// Things Otto learns once instead of rediscovering (and paying for) every time:
/// short notes about the user's apps and preferences, and saved routines that replay without the model.
/// Both live in %LOCALAPPDATA%\Otto and go into the (cached) system prompt.
static class Memory
{
    const int MaxNotesChars = 3_000; // every char rides along on every request, so keep it tight
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Otto");
    static readonly string NotesPath = Path.Combine(Dir, "notes.txt");
    static readonly string RoutinesPath = Path.Combine(Dir, "routines.json");
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
            var notes = File.Exists(NotesPath) ? File.ReadAllText(NotesPath).Trim() : "";
            var routines = LoadRoutines();
            var s = "";
            if (notes.Length > 0) s += "\nThings you've learned about this PC and user:\n" + notes + "\n";
            if (routines.Count > 0)
                s += "\nSaved routines (run_routine is far cheaper than redoing these by hand):\n" +
                     string.Join("\n", routines.Select(kv => $"- {kv.Key}: {kv.Value!["description"]}")) + "\n";
            return s;
        }
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
            File.WriteAllLines(NotesPath, lines);
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
        lock (gate)
        {
            var all = LoadRoutines();
            all[name] = new JsonObject { ["description"] = input["description"]?.GetValue<string>() ?? "", ["calls"] = calls.DeepClone() };
            Directory.CreateDirectory(Dir);
            File.WriteAllText(RoutinesPath, all.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        return $"Saved routine '{name}'.";
    }

    public static string Forget(JsonNode input)
    {
        var name = Id(input["name"]?.GetValue<string>());
        lock (gate)
        {
            var all = LoadRoutines();
            if (!all.Remove(name)) return $"No routine called '{name}'.";
            File.WriteAllText(RoutinesPath, all.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        return "Deleted.";
    }

    /// Replays a routine with no model calls in between. Only the last computer call reports the screen.
    public static async Task<JsonNode> RunRoutine(JsonNode input, Func<string, bool> confirm, Action<string> onTool, CancellationToken ct)
    {
        var name = Id(input["name"]?.GetValue<string>());
        JsonObject routine;
        lock (gate)
            routine = LoadRoutines()[name]?.AsObject() ?? throw new ArgumentException($"No routine called '{name}'.");

        var json = routine["calls"]!.ToJsonString();
        if (input["values"] is JsonObject values)
            foreach (var (k, v) in values)
                json = json.Replace("{" + k + "}", JsonEncodedText.Encode(v?.ToString() ?? "").ToString());
        var calls = JsonNode.Parse(json)!.AsArray();

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

    static JsonObject LoadRoutines()
    {
        try { return File.Exists(RoutinesPath) ? JsonNode.Parse(File.ReadAllText(RoutinesPath))!.AsObject() : new JsonObject(); }
        catch { return new JsonObject(); }
    }

    static string Id(string? s) =>
        string.IsNullOrWhiteSpace(s) ? throw new ArgumentException("missing 'name'") : s.Trim().ToLowerInvariant().Replace(' ', '_');
}
