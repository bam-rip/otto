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

    const string Key = @"Software\Otto\AI";

    public static Provider ById(string? id) => All.FirstOrDefault(p => p.Id == id) ?? All[0];

    public static Provider Selected
    {
        get => ById(Reg.Get(Key, "provider"));
        set => Reg.Set(Key, "provider", value.Id);
    }

    public static string Get(Provider p, string field, string fallback) =>
        Reg.Get(Key, $"{p.Id}.{field}") is { Length: > 0 } v ? v : fallback;

    public static void Set(Provider p, string field, string value) => Reg.Set(Key, $"{p.Id}.{field}", value);

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

    /// A key sent over plain http to another machine can be read by anyone on the network path. Returns why it
    /// mustn't be sent, or null when it's fine (https, a server on this PC, or no key).
    public static string? KeyInTheClear(string baseUrl, string? key) =>
        !string.IsNullOrWhiteSpace(key) && Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) && u.Scheme == "http" && !u.IsLoopback
            ? $"The server address {baseUrl} isn't encrypted (http), so your API key would travel in the clear. Use an https address, or a server on this PC."
            : null;

    /// Ready to send a request? Returns what's missing, or null.
    public static string? Problem(AiConfig c)
    {
        if (c.Provider.NeedsKey && string.IsNullOrWhiteSpace(c.Key)) return $"No API key for {c.Provider.Label} yet. Open settings (the gear icon) to add one.";
        if (string.IsNullOrWhiteSpace(c.Fast)) return "No model chosen yet. Open settings (the gear icon) and pick one.";
        if (KeyInTheClear(c.BaseUrl, c.Key) is string clear) return clear;
        return null;
    }
}
