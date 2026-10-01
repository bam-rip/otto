using Microsoft.Win32;

namespace Otto;

/// An AI service Otto can talk to. Everything except Anthropic speaks the OpenAI-style chat API,
/// so one connector (Llm.OpenAiAsync) covers them all, including local servers like Ollama or LM Studio.
sealed record Provider(string Id, string Label, string BaseUrl, string Fast, string Smart, bool Vision, string KeyUrl)
{
    public bool IsAnthropic => Id == "anthropic";
    public bool NeedsKey => Id != "custom";
    /// Credential Manager entry. Anthropic keeps the original "Otto" entry so existing installs keep working.
    public string KeyTarget => IsAnthropic ? "Otto" : "Otto:" + Id;
    /// An environment variable that overrides the saved key (developer convenience; Claude only).
    public string? KeyEnvVar => IsAnthropic ? "ANTHROPIC_API_KEY" : null;
    public string? SavedKey() => KeyStore.ApiKey(KeyTarget, KeyEnvVar);
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
        return null;
    }
}

/// Settings window: provider, key, models, and how replies appear. Plain Windows dialog laid out with
/// auto-sizing rows, so nothing gets clipped at any display scaling.
static class SettingsWindow
{
    public static void Show()
    {
        using var f = new Form
        {
            Text = "Otto settings",
            Icon = Icons.App(),
            ShowIcon = true,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            TopMost = true,
            AutoScaleMode = AutoScaleMode.Font,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Font = new Font("Segoe UI", 9.5f),
            Padding = new Padding(10),
        };
        const int W = 520; // content width
        var grid = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(14, 10, 14, 6) };
        f.Controls.Add(grid);

        Label Heading(string text) => new() { Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 11f), Margin = new Padding(0, 6, 0, 8) };
        Label Caption(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 10, 0, 4) };
        Label Hint(string text) => new() { Text = text, UseMnemonic = false, AutoSize = true, MaximumSize = new Size(W, 0), ForeColor = Color.DimGray, Margin = new Padding(0, 4, 0, 0) };
        void Add(Control c) => grid.Controls.Add(c);

        // ---- AI ----
        Add(Heading("AI"));
        Add(Caption("Provider"));
        var provider = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = W };
        provider.Items.AddRange(Providers.All.Select(p => (object)p.Label).ToArray());
        Add(provider);

        Add(Caption("API key (stored in Windows Credential Manager)"));
        var keyRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        var key = new TextBox { UseSystemPasswordChar = true, Width = W - 90 };
        var getKey = new LinkLabel { Text = "Get a key", AutoSize = true, Margin = new Padding(12, 6, 0, 0) };
        keyRow.Controls.AddRange(new Control[] { key, getKey });
        Add(keyRow);

        Add(Caption("Server address"));
        var baseUrl = new TextBox { Width = W };
        Add(baseUrl);

        var models = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = Padding.Empty };
        models.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, W / 2));
        models.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, W / 2));
        var fast = new ComboBox { Width = W / 2 - 8 };
        var smart = new ComboBox { Width = W / 2 };
        models.Controls.Add(Caption("Everyday model (cheap, used first)"), 0, 0);
        models.Controls.Add(Caption("Harder-tasks model"), 1, 0);
        models.Controls.Add(fast, 0, 1);
        models.Controls.Add(smart, 1, 1);
        Add(models);

        var loadRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 10, 0, 0) };
        var load = new Button { Text = "Load model list", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
        var status = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(10, 8, 0, 0) };
        loadRow.Controls.AddRange(new Control[] { load, status });
        Add(loadRow);

        var vision = new CheckBox { Text = "These models can see images (screenshots)", AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
        Add(vision);
        Add(Hint("Claude is the most tested with Otto. Other providers work, but some are weaker at controlling the PC."));

        // ---- email & calendar ----
        Add(Heading("Email and calendar (Outlook / Hotmail)"));
        grid.Controls[grid.Controls.Count - 1].Margin = new Padding(0, 22, 0, 8);
        Add(Caption("App client ID (from your free Microsoft app registration)"));
        var mailRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        var clientId = new TextBox { Width = W - 130, Text = Graph.ClientId, PlaceholderText = "e.g. 1a2b3c4d-...." };
        var signIn = new Button { AutoSize = true, Padding = new Padding(8, 2, 8, 2), Margin = new Padding(10, 0, 0, 0) };
        mailRow.Controls.AddRange(new Control[] { clientId, signIn });
        Add(mailRow);
        var mailStatus = Hint("");
        Add(mailStatus);
        var howTo = new LinkLabel { Text = "How do I get a client ID? (5 minutes, free)", AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        Add(howTo);
        void MailState()
        {
            signIn.Text = Graph.SignedIn ? "Sign out" : "Sign in";
            mailStatus.Text = Graph.SignedIn ? $"Connected to {Graph.Account}. Otto can read your email and calendar, and asks before sending anything." : "Not connected.";
        }
        MailState();
        howTo.LinkClicked += (_, _) =>
        {
            const string steps =
                "Microsoft makes every app that reads Outlook mail register itself once. It's free:\n\n" +
                "1. Click OK to open the Microsoft app registrations page and sign in with any Microsoft account.\n" +
                "2. Click \"New registration\". Name it Otto.\n" +
                "3. Under \"Supported account types\" choose \"Accounts in any organizational directory and personal Microsoft accounts\".\n" +
                "4. Leave Redirect URI empty and click Register.\n" +
                "5. Open \"Authentication\" on the left, set \"Allow public client flows\" to Yes, and Save.\n" +
                "6. Copy the \"Application (client) ID\" from the Overview page into Otto's settings and click Sign in.\n\n" +
                "Otto only ever gets permission for your mail and calendar, and never sees your password.";
            if (MessageBox.Show(f, steps, "Getting a client ID", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK)
                Tools.OpenUrl("https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade");
        };
        signIn.Click += async (_, _) =>
        {
            if (Graph.SignedIn) { Graph.SignOut(); MailState(); return; }
            Graph.ClientId = clientId.Text;
            signIn.Enabled = false; // one sign-in at a time
            try
            {
                var (who, error) = await GraphSignIn.ShowAsync(f);
                if (f.IsDisposed) return;
                MailState();
                if (who == null && error != null) mailStatus.Text = "Couldn't sign in: " + error;
            }
            finally { if (!signIn.IsDisposed) signIn.Enabled = true; }
        };

        // ---- chat panel ----
        Add(Heading("Chat panel"));
        grid.Controls[grid.Controls.Count - 1].Margin = new Padding(0, 22, 0, 8);
        var animate = new CheckBox { Text = "Animate replies (text types out word by word)", AutoSize = true, Checked = Prefs.AnimateReplies };
        Add(animate);
        Add(Hint("On: a typewriter sound plays as the text appears. Off: the whole reply appears at once with the reply sound."));
        var autoSend = new CheckBox { Text = "Send voice messages straight away", AutoSize = true, Checked = Prefs.VoiceAutoSend, Margin = new Padding(0, 12, 0, 0) };
        Add(autoSend);
        Add(Hint("Off: what you said goes into the message box so you can check it, then press Enter."));
        var readAloud = new CheckBox { Text = "Read replies aloud", AutoSize = true, Checked = Prefs.ReadAloud, Margin = new Padding(0, 12, 0, 0) };
        Add(readAloud);
        Add(Hint("Uses Windows' own voices. Change the voice in Windows Settings → Time & Language → Speech."));

        Add(Heading("Updates"));
        grid.Controls[grid.Controls.Count - 1].Margin = new Padding(0, 22, 0, 8);
        var updates = new CheckBox { Text = "Check for updates once a day", AutoSize = true, Checked = Prefs.CheckUpdates };
        Add(updates);
        Add(Hint($"You have Otto {Updater.Current}. New versions show as a small bar in the panel; nothing installs without your click."));

        var buttons = new FlowLayoutPanel { AutoSize = false, FlowDirection = FlowDirection.RightToLeft, Size = new Size(W, 40), Margin = new Padding(0, 22, 0, 4) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(10, 2, 10, 2) };
        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true, Padding = new Padding(10, 2, 10, 2) };
        buttons.Controls.AddRange(new Control[] { cancel, ok });
        Add(buttons);
        f.AcceptButton = ok;
        f.CancelButton = cancel;

        Provider P() => Providers.All[Math.Max(0, provider.SelectedIndex)];
        void Fill()
        {
            var p = P();
            bool hasKey = p.SavedKey() != null;
            key.Text = "";
            key.PlaceholderText = hasKey ? "Saved. Leave blank to keep it." : p.NeedsKey ? "Paste your key" : "Optional for local servers";
            getKey.Visible = p.KeyUrl.Length > 0;
            baseUrl.Text = Providers.Get(p, "baseurl", p.BaseUrl);
            baseUrl.ReadOnly = p.Id != "custom";
            fast.Text = Providers.Get(p, "fast", p.Fast);
            smart.Text = Providers.Get(p, "smart", p.Smart);
            vision.Checked = Providers.Get(p, "vision", p.Vision ? "1" : "0") == "1";
            fast.Items.Clear(); smart.Items.Clear();
            status.Text = "";
        }
        provider.SelectedIndex = Array.IndexOf(Providers.All, Providers.Selected);
        provider.SelectedIndexChanged += (_, _) => Fill();
        Fill();

        getKey.LinkClicked += (_, _) => Tools.OpenUrl(P().KeyUrl);
        load.Click += async (_, _) =>
        {
            var p = P();
            var k = key.Text.Trim().Length > 0 ? key.Text.Trim() : p.SavedKey();
            status.Text = "Loading…";
            try
            {
                var list = await Llm.ListModelsAsync(p, baseUrl.Text.Trim().TrimEnd('/'), k);
                fast.Items.Clear(); smart.Items.Clear();
                fast.Items.AddRange(list.Cast<object>().ToArray());
                smart.Items.AddRange(list.Cast<object>().ToArray());
                status.Text = $"{list.Count} models. Pick from the dropdowns.";
            }
            catch (Exception e) { status.Text = "Couldn't load: " + e.Message.Clip(60); }
        };

        if (f.ShowDialog() != DialogResult.OK) return;
        var chosen = P();
        Providers.Selected = chosen;
        if (key.Text.Trim().Length > 0) KeyStore.Save(key.Text.Trim(), chosen.KeyTarget);
        if (chosen.Id == "custom") Providers.Set(chosen, "baseurl", baseUrl.Text.Trim());
        Providers.Set(chosen, "fast", fast.Text.Trim());
        Providers.Set(chosen, "smart", smart.Text.Trim().Length > 0 ? smart.Text.Trim() : fast.Text.Trim());
        Providers.Set(chosen, "vision", vision.Checked ? "1" : "0");
        Prefs.AnimateReplies = animate.Checked;
        Prefs.CheckUpdates = updates.Checked;
        Prefs.ReadAloud = readAloud.Checked;
        Prefs.VoiceAutoSend = autoSend.Checked;
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
