using Microsoft.Win32;

namespace Otto;

/// An AI service Otto can talk to. Everything except Anthropic speaks the OpenAI-style chat API,
/// so one connector (Llm.OpenAiAsync) covers them all, including local servers like Ollama or LM Studio.
sealed record Provider(string Id, string Label, string BaseUrl, string Fast, string Smart, bool Vision, string KeyUrl)
{
    public bool IsAnthropic => Id == "anthropic";
    public bool NeedsKey => Id != "custom";
    /// Credential Manager entry for key slot 0. Anthropic keeps the original "Otto" entry so existing installs keep working.
    public string KeyTarget => IsAnthropic ? "Otto" : "Otto:" + Id;
    /// Slot 0 is the original entry; extra keys are "<target>#<slot>".
    public string KeyTargetFor(int slot) => slot == 0 ? KeyTarget : $"{KeyTarget}#{slot}";
    /// An environment variable that overrides the saved key (developer convenience; Claude only).
    public string? KeyEnvVar => IsAnthropic ? "ANTHROPIC_API_KEY" : null;
    /// The key in use: the env override, else the active saved key.
    public string? SavedKey() => KeyStore.ApiKey(KeyTargetFor(KeyRing.Active(this)), KeyEnvVar);
}

/// Several saved keys per provider (say a personal and a work key, or two free-tier keys). The keys live in
/// Credential Manager; which slots exist, their names, and the active one live in the registry.
static class KeyRing
{
    public sealed record Entry(int Slot, string Label, string Hint);

    public static List<Entry> List(Provider p)
    {
        var list = new List<Entry>();
        foreach (var slot in Slots(p))
        {
            var key = KeyStore.ApiKey(p.KeyTargetFor(slot), null);
            if (key == null) continue; // removed outside Otto
            list.Add(new(slot, Providers.Get(p, $"keylabel.{slot}", $"Key {slot + 1}"), Mask(key)));
        }
        return list;
    }

    /// "…a1b2": enough to tell keys apart without showing them.
    public static string Mask(string key) => key.Length <= 8 ? "…" : "…" + key[^4..];

    static IEnumerable<int> Slots(Provider p)
    {
        foreach (var part in Providers.Get(p, "keyslots", "0").Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(part, out var n)) yield return n;
    }

    static void SetSlots(Provider p, IEnumerable<int> slots) => Providers.Set(p, "keyslots", string.Join(",", slots));

    public static int Active(Provider p) => int.TryParse(Providers.Get(p, "activekey", "0"), out var n) ? n : 0;
    public static void SetActive(Provider p, int slot) => Providers.Set(p, "activekey", slot.ToString());

    public static int Add(Provider p, string label, string key)
    {
        var slots = Slots(p).ToList();
        // slot 0 is empty on a fresh install; fill it first so single-key setups stay as before
        int slot = KeyStore.ApiKey(p.KeyTarget, null) == null ? 0 : Enumerable.Range(1, 99).First(n => !slots.Contains(n));
        KeyStore.Save(key, p.KeyTargetFor(slot));
        if (!slots.Contains(slot)) slots.Add(slot);
        SetSlots(p, slots);
        Providers.Set(p, $"keylabel.{slot}", label.Length > 0 ? label : $"Key {slot + 1}");
        if (List(p).Count == 1) SetActive(p, slot);
        return slot;
    }

    public static void Remove(Provider p, int slot)
    {
        KeyStore.Delete(p.KeyTargetFor(slot));
        SetSlots(p, Slots(p).Where(s => s != slot));
        if (Active(p) == slot && List(p).FirstOrDefault() is Entry next) SetActive(p, next.Slot);
    }

    /// Move to the saved key after the active one. False when there's no other key.
    public static bool Next(Provider p, out Entry? now)
    {
        var list = List(p);
        now = null;
        if (list.Count < 2) return false;
        int i = list.FindIndex(e => e.Slot == Active(p));
        now = list[(i + 1) % list.Count];
        SetActive(p, now.Slot);
        return true;
    }
}

/// The settings in effect for one request.
sealed record AiConfig(Provider Provider, string BaseUrl, string Fast, string Smart, bool Vision, string? Key);

static class Providers
{
    // Model names change often; these are starting points. The settings window can load the live list.
    public static readonly Provider[] All =
    {
        new("anthropic", "Anthropic (Claude)", "https://api.anthropic.com/v1", "claude-haiku-4-5-20251001", "claude-sonnet-5-5", true, "https://console.anthropic.com/settings/keys"),
        new("openai", "OpenAI (ChatGPT)", "https://api.openai.com/v1", "gpt-5-mini", "gpt-5", true, "https://platform.openai.com/api-keys"),
        new("gemini", "Google (Gemini)", "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-flash-latest", "gemini-pro-latest", true, "https://aistudio.google.com/apikey"),
        new("xai", "xAI (Grok)", "https://api.x.ai/v1", "grok-4-fast", "grok-4", true, "https://console.x.ai"),
        new("deepseek", "DeepSeek", "https://api.deepseek.com/v1", "deepseek-chat", "deepseek-reasoner", false, "https://platform.deepseek.com/api_keys"),
        new("openrouter", "OpenRouter (many models, one key)", "https://openrouter.ai/api/v1", "google/gemini-flash-latest", "anthropic/claude-sonnet-4.5", true, "https://openrouter.ai/keys"),
        new("custom", "Custom / local (Ollama, LM Studio…)", "http://localhost:11434/v1", "", "", false, ""),
    };

    const string Reg = @"Software\Otto\AI";

    public static Provider ById(string? id) => All.FirstOrDefault(p => p.Id == id) ?? All[0];

    public static Provider Selected
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(Reg); return ById(k?.GetValue("provider") as string); }
        set { using var k = Registry.CurrentUser.CreateSubKey(Reg); k.SetValue("provider", value.Id); }
    }

    public static string Get(Provider p, string field, string fallback)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Reg);
        return k?.GetValue($"{p.Id}.{field}") as string is { Length: > 0 } v ? v : fallback;
    }

    public static void Set(Provider p, string field, string value)
    {
        using var k = Registry.CurrentUser.CreateSubKey(Reg);
        k.SetValue($"{p.Id}.{field}", value);
    }

    public static AiConfig Current()
    {
        var p = Selected;
        return new AiConfig(p,
            Get(p, "baseurl", p.BaseUrl).TrimEnd('/'),
            Get(p, "fast", p.Fast),
            Get(p, "smart", p.Smart),
            Get(p, "vision", p.Vision ? "1" : "0") == "1",
            p.SavedKey());
    }

    /// Ready to send a request? Returns what's missing, or null.
    public static string? Problem(AiConfig c)
    {
        if (c.Provider.NeedsKey && string.IsNullOrWhiteSpace(c.Key)) return $"No API key for {c.Provider.Label} yet. Open settings (the gear icon) to add one.";
        if (string.IsNullOrWhiteSpace(c.Fast)) return "No model chosen yet. Open settings (the gear icon) and pick one.";
        // a key sent over plain http to another machine can be read by anyone on the network path
        if (!string.IsNullOrWhiteSpace(c.Key) && Uri.TryCreate(c.BaseUrl, UriKind.Absolute, out var u) && u.Scheme == "http" && !u.IsLoopback)
            return $"The server address {c.BaseUrl} isn't encrypted (http), so your API key would travel in the clear. Use an https address, or a server on this PC.";
        return null;
    }
}

/// On/off preferences, as DWORDs in HKCU\Software\Otto (value names kept from earlier versions).
static class Prefs
{
    const string Key = @"Software\Otto";

    public static bool AnimateReplies { get => Get("AnimateReplies", true); set => Set("AnimateReplies", value); }
    public static bool VoiceAutoSend { get => Get("VoiceAutoSend", false); set => Set("VoiceAutoSend", value); }
    public static bool ReadAloud { get => Get("ReadAloud", false); set => Set("ReadAloud", value); }
    public static bool Sounds { get => Get("Sounds", true); set => Set("Sounds", value); }
    public static bool CheckUpdates { get => Get("CheckUpdates", true); set => Set("CheckUpdates", value); }
    public static bool Welcomed { get => Get("Welcomed", false); set => Set("Welcomed", value); }

    static bool Get(string name, bool fallback)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key);
        return k?.GetValue(name) is int v ? v != 0 : fallback;
    }

    static void Set(string name, bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(Key);
        k.SetValue(name, on ? 1 : 0);
    }
}

/// Small dialog for adding a key: a name to tell it apart, and the key itself.
static class KeyPrompt
{
    public static (string Label, string Key)? Ask(IWin32Window owner, string providerLabel, int number)
    {
        using var f = Ui.Dialog("Add a key for " + providerLabel, out var body);
        body.Controls.Add(Ui.Caption("Name (so you can tell your keys apart)"));
        var name = Ui.TextBox(420, $"Key {number}");
        body.Controls.Add(name);
        body.Controls.Add(Ui.Caption("API key"));
        var key = Ui.TextBox(420, password: true, placeholder: "Paste your key");
        body.Controls.Add(key);
        var ok = Ui.DialogButtons(f, body, "Add");
        ok.Enabled = false;
        key.TextChanged += (_, _) => ok.Enabled = key.Text.Trim().Length > 0;
        f.Shown += (_, _) => key.Focus();
        if (f.ShowDialog(owner) != DialogResult.OK || key.Text.Trim().Length == 0) return null;
        return (name.Text.Trim(), key.Text.Trim());
    }
}

