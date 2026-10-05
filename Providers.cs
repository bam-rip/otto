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
        // the settings scroll inside the window (they're taller than some screens); Save/Cancel stay put below
        var grid = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(14, 10, 14, 6) };
        var scroller = new Panel { AutoScroll = true, Dock = DockStyle.Fill };
        scroller.Controls.Add(grid);
        f.Controls.Add(scroller);

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

        Add(Caption("API key in use (keys are stored in Windows Credential Manager)"));
        var keyRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        var keys = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = W - 290 };
        var addKey = new Button { Text = "Add key…", AutoSize = true, Padding = new Padding(6, 0, 6, 0), Margin = new Padding(8, 0, 0, 0) };
        var removeKey = new Button { Text = "Remove", AutoSize = true, Padding = new Padding(6, 0, 6, 0), Margin = new Padding(6, 0, 0, 0) };
        var getKey = new LinkLabel { Text = "Get a key", AutoSize = true, Margin = new Padding(10, 6, 0, 0) };
        keyRow.Controls.AddRange(new Control[] { keys, addKey, removeKey, getKey });
        Add(keyRow);
        Add(Hint("Save more than one key and Otto moves to the next one by itself when a key runs out of quota."));

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

        // ---- any other email, over IMAP ----
        Add(Heading("Other email (Gmail, Yahoo, iCloud and more)"));
        grid.Controls[grid.Controls.Count - 1].Margin = new Padding(0, 22, 0, 8);
        Add(Caption("Email address"));
        var imapAddress = new TextBox { Width = W, Text = Imap.Address, PlaceholderText = "you@gmail.com" };
        Add(imapAddress);
        Add(Caption("App password (not your normal password)"));
        var imapRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        var imapPassword = new TextBox { Width = W - 130, UseSystemPasswordChar = true };
        var imapConnect = new Button { AutoSize = true, Padding = new Padding(8, 2, 8, 2), Margin = new Padding(10, 0, 0, 0) };
        imapRow.Controls.AddRange(new Control[] { imapPassword, imapConnect });
        Add(imapRow);
        var servers = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = Padding.Empty };
        servers.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, W / 2));
        servers.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, W / 2));
        var imapServer = new TextBox { Width = W / 2 - 8, Text = Imap.ImapServer, PlaceholderText = "imap.example.com:993" };
        var smtpServer = new TextBox { Width = W / 2, Text = Imap.SmtpServer, PlaceholderText = "smtp.example.com:465" };
        servers.Controls.Add(Caption("Incoming (IMAP) server"), 0, 0);
        servers.Controls.Add(Caption("Outgoing (SMTP) server"), 1, 0);
        servers.Controls.Add(imapServer, 0, 1);
        servers.Controls.Add(smtpServer, 1, 1);
        Add(servers);
        var imapStatus = Hint("");
        Add(imapStatus);
        var appPasswordHelp = new LinkLabel { Text = "How do I get an app password?", AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        Add(appPasswordHelp);
        void ImapState()
        {
            imapConnect.Text = Imap.Connected ? "Disconnect" : "Connect";
            imapPassword.Enabled = imapAddress.Enabled = imapServer.Enabled = smtpServer.Enabled = !Imap.Connected;
            imapPassword.PlaceholderText = Imap.Connected ? "Saved" : "Paste the app password";
            imapStatus.Text = Imap.Connected
                ? $"Connected to {Imap.Address}. Otto can read and search your email and write drafts, and asks before sending anything." +
                  (Graph.SignedIn ? " (Outlook is connected too; Otto uses Outlook while it is.)" : "")
                : "Not connected.";
        }
        ImapState();
        imapAddress.TextChanged += (_, _) =>
        {
            // fill in the servers for providers Otto knows
            if (Imap.Known(imapAddress.Text) is Imap.Servers k)
            {
                imapServer.Text = $"{k.Imap}:{k.ImapPort}";
                smtpServer.Text = $"{k.Smtp}:{k.SmtpPort}";
            }
        };
        appPasswordHelp.LinkClicked += (_, _) =>
        {
            var known = Imap.Known(imapAddress.Text);
            const string steps =
                "Email providers don't let apps use your normal password. Instead you make an \"app password\" just for Otto. " +
                "You can delete it any time to cut Otto off.\n\n" +
                "Gmail: 2-Step Verification must be on in your Google account. Then go to myaccount.google.com/apppasswords, " +
                "type Otto as the name, and click Create. Copy the 16-letter password into Otto (spaces don't matter).\n\n" +
                "Yahoo, iCloud, Fastmail and others: look for \"app passwords\" in your account's security settings.";
            var open = known?.AppPasswordUrl;
            if (MessageBox.Show(f, steps + (open != null ? "\n\nClick OK to open that page now." : ""), "Getting an app password",
                    open != null ? MessageBoxButtons.OKCancel : MessageBoxButtons.OK, MessageBoxIcon.Information) == DialogResult.OK && open != null)
                Tools.OpenUrl(open);
        };
        imapConnect.Click += async (_, _) =>
        {
            if (Imap.Connected) { Imap.Disconnect(); imapPassword.Text = ""; ImapState(); return; }
            var address = imapAddress.Text.Trim();
            var pass = imapPassword.Text.Replace(" ", "").Trim(); // Google shows app passwords in groups of four
            if (!address.Contains('@') || pass.Length == 0 || imapServer.Text.Trim().Length == 0 || smtpServer.Text.Trim().Length == 0)
            {
                imapStatus.Text = "Fill in the address, app password and both servers first.";
                return;
            }
            imapConnect.Enabled = false;
            imapStatus.Text = "Checking…";
            try
            {
                await Imap.Connect(address, pass, imapServer.Text, smtpServer.Text);
                if (f.IsDisposed) return;
                imapPassword.Text = "";
                ImapState();
            }
            catch (Exception ex)
            {
                if (f.IsDisposed) return;
                imapStatus.Text = ex is MailKit.Security.AuthenticationException
                    ? "The address or app password was refused. Make sure it's an app password, not your normal one."
                    : "Couldn't connect: " + ex.Message.Clip(120);
            }
            finally { if (!imapConnect.IsDisposed) imapConnect.Enabled = true; }
        };

        // ---- calendar, from an iCal link ----
        Add(Caption("Calendar link (read-only, for Google, Apple or Outlook.com calendars)"));
        var calRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        var calLink = new TextBox { Width = W - 130, UseSystemPasswordChar = true, PlaceholderText = Calendar.Connected ? "Saved" : "Paste the secret iCal address" };
        var calSave = new Button { AutoSize = true, Padding = new Padding(8, 2, 8, 2), Margin = new Padding(10, 0, 0, 0) };
        calRow.Controls.AddRange(new Control[] { calLink, calSave });
        Add(calRow);
        var calStatus = Hint("");
        Add(calStatus);
        var calHelp = new LinkLabel { Text = "Where do I find it?", AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        Add(calHelp);
        void CalState()
        {
            calSave.Text = Calendar.Connected ? "Remove" : "Save";
            calLink.Enabled = !Calendar.Connected;
            calLink.PlaceholderText = Calendar.Connected ? "Saved" : "Paste the secret iCal address";
            calStatus.Text = Calendar.Connected ? "Connected. Otto can read your calendar (it can't add or change events)." : "Not connected.";
        }
        CalState();
        calHelp.LinkClicked += (_, _) => MessageBox.Show(f,
            "Google Calendar: open calendar.google.com on a computer, click the gear → Settings, pick your calendar on the left, " +
            "scroll to \"Integrate calendar\" and copy \"Secret address in iCal format\".\n\n" +
            "Apple (iCloud): share the calendar as a public calendar and copy its link.\n" +
            "Outlook.com: Settings → Calendar → Shared calendars → Publish a calendar → copy the ICS link.\n\n" +
            "Keep the link private: anyone with it can see your calendar. You can reset it in the same place.",
            "Calendar link", MessageBoxButtons.OK, MessageBoxIcon.Information);
        calSave.Click += async (_, _) =>
        {
            if (Calendar.Connected) { Calendar.SetLink(null); calLink.Text = ""; CalState(); return; }
            calSave.Enabled = false;
            calStatus.Text = "Checking…";
            try
            {
                Calendar.SetLink(calLink.Text);
                var test = await Calendar.List(new System.Text.Json.Nodes.JsonObject { ["days"] = 1 }, CancellationToken.None);
                if (f.IsDisposed) return;
                calLink.Text = "";
                CalState();
            }
            catch (Exception ex)
            {
                Calendar.SetLink(null);
                if (!f.IsDisposed) calStatus.Text = "Couldn't use that link: " + Agent.ErrorText(ex).Clip(120);
            }
            finally { if (!calSave.IsDisposed) calSave.Enabled = true; }
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

        Add(Heading("What Otto remembers"));
        grid.Controls[grid.Controls.Count - 1].Margin = new Padding(0, 22, 0, 8);
        var memoryRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        var showNotes = new Button { Text = "Notes…", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
        var showRoutines = new Button { Text = "Routines…", AutoSize = true, Padding = new Padding(8, 2, 8, 2), Margin = new Padding(8, 0, 0, 0) };
        memoryRow.Controls.AddRange(new Control[] { showNotes, showRoutines });
        Add(memoryRow);
        Add(Hint("Otto reads these at the start of every chat. Check them now and then, and delete anything you didn't ask it to remember."));
        void OpenMemory(string file)
        {
            var path = Path.Combine(Paths.Data, file);
            if (!File.Exists(path)) { MessageBox.Show(f, "Nothing saved yet.", "Otto", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            System.Diagnostics.Process.Start("notepad.exe", $"\"{path}\"");
        }
        showNotes.Click += (_, _) => OpenMemory("notes.txt");
        showRoutines.Click += (_, _) => OpenMemory("routines.json");

        Add(Heading("Updates"));
        grid.Controls[grid.Controls.Count - 1].Margin = new Padding(0, 22, 0, 8);
        var updates = new CheckBox { Text = "Check for updates once a day", AutoSize = true, Checked = Prefs.CheckUpdates };
        Add(updates);
        Add(Hint($"You have Otto {Updater.Current}. New versions show as a small bar in the panel; nothing installs without your click."));

        var buttons = new FlowLayoutPanel { AutoSize = false, FlowDirection = FlowDirection.RightToLeft, Size = new Size(W, 40), Margin = new Padding(0, 22, 0, 4) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(10, 2, 10, 2) };
        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true, Padding = new Padding(10, 2, 10, 2) };
        buttons.Controls.AddRange(new Control[] { cancel, ok });
        buttons.Dock = DockStyle.Bottom;
        buttons.Height = 52;
        buttons.Padding = new Padding(14, 8, 30, 8);
        f.Controls.Add(buttons);
        scroller.BringToFront(); // so Fill takes what's left above the docked buttons
        f.AcceptButton = ok;
        f.CancelButton = cancel;

        Provider P() => Providers.All[Math.Max(0, provider.SelectedIndex)];
        List<KeyRing.Entry> keyList = new();
        KeyRing.Entry? SelectedKey() => keys.SelectedIndex >= 0 && keys.SelectedIndex < keyList.Count ? keyList[keys.SelectedIndex] : null;
        void FillKeys(int? select = null)
        {
            var p = P();
            keyList = KeyRing.List(p);
            keys.Items.Clear();
            foreach (var e in keyList) keys.Items.Add($"{e.Label}  ({e.Hint})");
            if (keyList.Count == 0) keys.Items.Add(p.NeedsKey ? "No key yet. Click Add key." : "None (optional for local servers)");
            int want = select ?? KeyRing.Active(p);
            keys.SelectedIndex = Math.Max(0, keyList.FindIndex(e => e.Slot == want));
            removeKey.Enabled = keyList.Count > 0;
        }
        addKey.Click += (_, _) =>
        {
            if (KeyPrompt.Ask(f, P().Label, keyList.Count + 1) is not var (label, value)) return;
            FillKeys(KeyRing.Add(P(), label, value));
        };
        removeKey.Click += (_, _) =>
        {
            if (SelectedKey() is not KeyRing.Entry e) return;
            if (MessageBox.Show(f, $"Remove the key \"{e.Label}\" ({e.Hint}) from Otto?", "Remove key",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            KeyRing.Remove(P(), e.Slot);
            FillKeys();
        };
        void Fill()
        {
            var p = P();
            FillKeys();
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
            var k = SelectedKey() is KeyRing.Entry e ? KeyStore.ApiKey(p.KeyTargetFor(e.Slot), p.KeyEnvVar) : p.SavedKey();
            status.Text = "Loading…";
            try
            {
                var list = await Llm.ListModelsAsync(p, baseUrl.Text.Trim().TrimEnd('/'), k);
                fast.Items.Clear(); smart.Items.Clear();
                fast.Items.AddRange(list.Cast<object>().ToArray());
                smart.Items.AddRange(list.Cast<object>().ToArray());
                status.Text = $"{list.Count} models. Pick from the dropdowns.";
            }
            catch (Exception ex) { status.Text = "Couldn't load: " + ex.Message.Clip(60); }
        };

        // as tall as the settings need, but never taller than the screen
        f.AutoSize = false;
        var want = grid.GetPreferredSize(Size.Empty);
        int maxH = Screen.FromPoint(Cursor.Position).WorkingArea.Height - 60;
        f.ClientSize = new Size(want.Width + SystemInformation.VerticalScrollBarWidth + f.Padding.Horizontal,
                                Math.Min(want.Height + buttons.Height + f.Padding.Vertical, maxH));
        if (f.ShowDialog() != DialogResult.OK) return;
        var chosen = P();
        Providers.Selected = chosen;
        if (SelectedKey() is KeyRing.Entry active) KeyRing.SetActive(chosen, active.Slot);
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

/// Small dialog for adding a key: a name to tell it apart, and the key itself.
static class KeyPrompt
{
    public static (string Label, string Key)? Ask(IWin32Window owner, string providerLabel, int number)
    {
        using var f = new Form
        {
            Text = "Add a key for " + providerLabel,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            TopMost = true,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Font = new Font("Segoe UI", 9.5f),
            Padding = new Padding(14),
        };
        var grid = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        f.Controls.Add(grid);
        grid.Controls.Add(new Label { Text = "Name (so you can tell your keys apart)", AutoSize = true, Margin = new Padding(0, 0, 0, 4) });
        var name = new TextBox { Width = 380, Text = $"Key {number}" };
        grid.Controls.Add(name);
        grid.Controls.Add(new Label { Text = "API key", AutoSize = true, Margin = new Padding(0, 12, 0, 4) });
        var key = new TextBox { Width = 380, UseSystemPasswordChar = true, PlaceholderText = "Paste your key" };
        grid.Controls.Add(key);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Size = new Size(380, 40), Margin = new Padding(0, 16, 0, 0) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var ok = new Button { Text = "Add", DialogResult = DialogResult.OK, AutoSize = true, Enabled = false };
        key.TextChanged += (_, _) => ok.Enabled = key.Text.Trim().Length > 0;
        buttons.Controls.AddRange(new Control[] { cancel, ok });
        grid.Controls.Add(buttons);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        f.Shown += (_, _) => key.Focus();
        if (f.ShowDialog(owner) != DialogResult.OK || key.Text.Trim().Length == 0) return null;
        return (name.Text.Trim(), key.Text.Trim());
    }
}
