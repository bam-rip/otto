using System.Text.Json;

namespace Otto;

/// One-click requests the user saves for things they ask often ("Morning briefing" → "summarise my unread email
/// and today's calendar"). Shown at the top of a new chat and in the tray menu. Kept in
/// %LOCALAPPDATA%\Otto\quick-actions.json.
static class QuickActions
{
    public sealed record Action(string Name, string Prompt);

    static string FilePath => Path.Combine(Paths.Data, "quick-actions.json");

    public static List<Action> All
    {
        get
        {
            try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<Action>>(File.ReadAllText(FilePath)) ?? new() : new(); }
            catch (JsonException)
            {
                // damaged: keep a copy before the next save replaces it
                try { File.Copy(FilePath, Path.Combine(Paths.Data, "quick-actions.damaged.json"), overwrite: false); } catch (IOException) { }
                return new();
            }
            catch (IOException) { return new(); }
        }
        set
        {
            Directory.CreateDirectory(Paths.Data);
            SafeFile.WriteAllText(FilePath, JsonSerializer.Serialize(
                value.Where(a => a.Name.Trim().Length > 0 && a.Prompt.Trim().Length > 0).Select(a => new Action(a.Name.Trim(), a.Prompt.Trim())),
                new JsonSerializerOptions { WriteIndented = true }));
            Changed?.Invoke();
        }
    }

    public static event System.Action? Changed;
}
