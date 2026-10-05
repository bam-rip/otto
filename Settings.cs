using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Otto;

/// Settings: a sidebar of sections on the left, the section on the right. Matches the panel (dark or light),
/// and every change applies as you make it, so there's no Save button to forget.
static class SettingsWindow
{
    public const string AI = "AI", Mail = "Email and calendar", Look = "Look and feel", Chat = "Chat and voice",
        Quick = "Quick actions", Reminders = "Reminders", Spend = "Spending", Safety = "Privacy and safety", About = "Updates and about";

    static readonly (string name, string glyph)[] Sections =
    {
        (AI, ""), (Mail, ""), (Look, ""), (Chat, ""), (Quick, ""),
        (Reminders, ""), (Spend, ""), (Safety, ""), (About, ""),
    };

    public static void Show() => Show(null);

    public static void Show(string? start)
    {
        using var f = new SettingsForm(start ?? AI);
        f.ShowDialog();
    }

    /// Opened from the tray or the panel's gear.
    sealed class SettingsForm : Form
    {
        readonly Panel content = new() { Dock = DockStyle.Fill, AutoScroll = true };
        readonly FlowLayoutPanel nav = new() { Dock = DockStyle.Left, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        readonly List<NavButton> navButtons = new();
        string current = "";
        const int W = 560; // content column width

        public SettingsForm(string start)
        {
            Text = "Otto settings";
            Icon = Icons.App();
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Segoe UI", 9.75f);
            ClientSize = new Size(900, 660);
            MinimumSize = new Size(760, 480);
            BackColor = Ui.Back;
            ForeColor = Ui.Fg;
            KeyPreview = true;
            KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            HandleCreated += (_, _) => Ui.DarkTitleBar(this);

            nav.Width = 250;
            nav.BackColor = Ui.Side;
            nav.Padding = new Padding(10, 18, 10, 10);
            nav.Controls.Add(new Label { Text = "Settings", AutoSize = true, Font = new Font("Segoe UI Light", 18f), ForeColor = Ui.Fg, Margin = new Padding(10, 0, 0, 18) });
            foreach (var (name, glyph) in Sections)
            {
                var b = new NavButton(name, glyph) { Width = 230 };
                b.Click += (_, _) => Open(name);
                navButtons.Add(b);
                nav.Controls.Add(b);
            }
            content.Padding = new Padding(36, 26, 24, 24);
            Controls.Add(content);
            Controls.Add(nav);
            Open(start);
            Shown += (_, _) => navButtons.FirstOrDefault(b => b.Selected)?.Focus(); // not the first dropdown, which would show highlighted
        }

        void Open(string name)
        {
            if (!Sections.Any(s => s.name == name)) name = AI; // an unknown name opens the first section
            current = name;
            foreach (var b in navButtons) b.Selected = b.Section == name;
            content.SuspendLayout();
            foreach (Control c in content.Controls) c.Dispose();
            content.Controls.Clear();
            var page = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Location = new Point(36, 26) };
            page.Controls.Add(new Label { Text = name, AutoSize = true, Font = new Font("Segoe UI Light", 20f), ForeColor = Ui.Fg, Margin = new Padding(0, 0, 0, 16) });
            Build(name, page);
            content.Controls.Add(page);
            content.ResumeLayout();
            content.AutoScrollPosition = Point.Empty;
        }

        void Build(string name, FlowLayoutPanel p)
        {
            switch (name)
            {
                case AI: BuildAI(p); break;
                case Mail: BuildMail(p); break;
                case Look: BuildLook(p); break;
                case Chat: BuildChat(p); break;
                case Quick: BuildQuick(p); break;
                case Reminders: BuildReminders(p); break;
                case Spend: BuildSpending(p); break;
                case Safety: BuildSafety(p); break;
                case About: BuildAbout(p); break;
            }
        }

        // ---------------- AI ----------------

        void BuildAI(FlowLayoutPanel p)
        {
            var card = Ui.Card(p, "Provider", "Which AI service Otto thinks with. You use your own account and pay them directly.");
            var provider = Ui.Combo(W - 40);
            provider.Items.AddRange(Providers.All.Select(x => (object)x.Label).ToArray());
            card.Controls.Add(provider);
            card.Controls.Add(Ui.Hint("Claude is the most tested with Otto. Google Gemini has a free tier. Others work, but some are weaker at controlling the PC.", W - 40));

            var keyCard = Ui.Card(p, "API key", "Kept in Windows Credential Manager. Save more than one and Otto moves to the next by itself when a key runs out of quota.");
            var keyRow = Ui.Row();
            var keys = Ui.Combo(W - 290);
            var addKey = Ui.Button("Add key…");
            var removeKey = Ui.Button("Remove");
            keyRow.Controls.AddRange(new Control[] { keys, addKey, removeKey });
            keyCard.Controls.Add(keyRow);
            var getKey = Ui.Link("Get a key from the provider");
            keyCard.Controls.Add(getKey);

            var modelCard = Ui.Card(p, "Models", "Otto starts each task on the everyday model and moves to the harder-tasks one only when it needs to.");
            modelCard.Controls.Add(Ui.Caption("Everyday model (cheap, used first)"));
            var fast = Ui.Combo(W - 40, editable: true);
            modelCard.Controls.Add(fast);
            modelCard.Controls.Add(Ui.Caption("Harder-tasks model"));
            var smart = Ui.Combo(W - 40, editable: true);
            modelCard.Controls.Add(smart);
            var loadRow = Ui.Row();
            var load = Ui.Button("Load model list");
            var status = Ui.Hint("", 360);
            status.Margin = new Padding(10, 9, 0, 0);
            loadRow.Controls.AddRange(new Control[] { load, status });
            modelCard.Controls.Add(loadRow);
            var vision = Ui.Toggle("These models can see images (screenshots)", false);
            modelCard.Controls.Add(vision);
            modelCard.Controls.Add(Ui.Caption("Server address"));
            var baseUrl = Ui.TextBox(W - 40);
            modelCard.Controls.Add(baseUrl);
            modelCard.Controls.Add(Ui.Hint("Only changeable for a custom or local server (Ollama, LM Studio).", W - 40));

            bool filling = false;
            Provider P() => Providers.All[Math.Max(0, provider.SelectedIndex)];
            List<KeyRing.Entry> keyList = new();
            void FillKeys(int? select = null)
            {
                var pr = P();
                keyList = KeyRing.List(pr);
                keys.Items.Clear();
                foreach (var e in keyList) keys.Items.Add($"{e.Label}  ({e.Hint})");
                if (keyList.Count == 0) keys.Items.Add(pr.NeedsKey ? "No key yet. Click Add key." : "None (optional for local servers)");
                int want = select ?? KeyRing.Active(pr);
                keys.SelectedIndex = Math.Max(0, keyList.FindIndex(e => e.Slot == want));
                removeKey.Enabled = keyList.Count > 0;
            }
            void Fill()
            {
                filling = true;
                var pr = P();
                FillKeys();
                getKey.Visible = pr.KeyUrl.Length > 0;
                baseUrl.Text = Providers.Get(pr, "baseurl", pr.BaseUrl);
                baseUrl.ReadOnly = pr.Id != "custom";
                fast.Items.Clear(); smart.Items.Clear();
                fast.Text = Providers.Get(pr, "fast", pr.Fast);
                smart.Text = Providers.Get(pr, "smart", pr.Smart);
                vision.Checked = Providers.Get(pr, "vision", pr.Vision ? "1" : "0") == "1";
                status.Text = "";
                filling = false;
            }
            provider.SelectedIndex = Array.IndexOf(Providers.All, Providers.Selected);
            Fill();

            // every change applies straight away
            provider.SelectedIndexChanged += (_, _) => { Providers.Selected = P(); Fill(); };
            keys.SelectedIndexChanged += (_, _) => { if (!filling && keys.SelectedIndex < keyList.Count) KeyRing.SetActive(P(), keyList[keys.SelectedIndex].Slot); };
            fast.TextChanged += (_, _) => { if (!filling && fast.Text.Trim().Length > 0) Providers.Set(P(), "fast", fast.Text.Trim()); };
            smart.TextChanged += (_, _) => { if (!filling) Providers.Set(P(), "smart", smart.Text.Trim().Length > 0 ? smart.Text.Trim() : fast.Text.Trim()); };
            vision.CheckedChanged += (_, _) => { if (!filling) Providers.Set(P(), "vision", vision.Checked ? "1" : "0"); };
            baseUrl.TextChanged += (_, _) => { if (!filling && P().Id == "custom") Providers.Set(P(), "baseurl", baseUrl.Text.Trim()); };
            addKey.Click += (_, _) =>
            {
                if (KeyPrompt.Ask(this, P().Label, keyList.Count + 1) is not var (label, value)) return;
                FillKeys(KeyRing.Add(P(), label, value));
            };
            removeKey.Click += (_, _) =>
            {
                if (keys.SelectedIndex < 0 || keys.SelectedIndex >= keyList.Count) return;
                var e = keyList[keys.SelectedIndex];
                if (MessageBox.Show(this, $"Remove the key \"{e.Label}\" ({e.Hint}) from Otto?", "Remove key", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                KeyRing.Remove(P(), e.Slot);
                FillKeys();
            };
            getKey.LinkClicked += (_, _) => Tools.OpenUrl(P().KeyUrl);
            load.Click += async (_, _) =>
            {
                var pr = P();
                status.Text = "Loading…";
                try
                {
                    var list = await Llm.ListModelsAsync(pr, baseUrl.Text.Trim().TrimEnd('/'), pr.SavedKey());
                    if (IsDisposed || status.IsDisposed) return;
                    fast.Items.Clear(); smart.Items.Clear();
                    fast.Items.AddRange(list.Cast<object>().ToArray());
                    smart.Items.AddRange(list.Cast<object>().ToArray());
                    status.Text = $"{list.Count} models. Pick from the lists.";
                }
                catch (Exception ex) { if (!status.IsDisposed) status.Text = "Couldn't load: " + Agent.ErrorText(ex).Clip(60); }
            };
        }

        // ---------------- email and calendar ----------------

        void BuildMail(FlowLayoutPanel p)
        {
            // Gmail and others
            var card = Ui.Card(p, "Gmail, Yahoo, iCloud and others", "Otto reads, searches and drafts email, and always asks before sending.");
            card.Controls.Add(Ui.Caption("Email address"));
            var address = Ui.TextBox(W - 40, Imap.Address, "you@gmail.com");
            card.Controls.Add(address);
            card.Controls.Add(Ui.Caption("App password (not your normal password)"));
            var row = Ui.Row();
            var password = Ui.TextBox(W - 170, password: true);
            var connect = Ui.Button("Connect", primary: true);
            row.Controls.AddRange(new Control[] { password, connect });
            card.Controls.Add(row);
            var servers = Ui.Row();
            var imapServer = Ui.TextBox((W - 50) / 2, Imap.ImapServer, "imap.example.com:993");
            var smtpServer = Ui.TextBox((W - 50) / 2, Imap.SmtpServer, "smtp.example.com:465");
            servers.Controls.AddRange(new Control[] { imapServer, smtpServer });
            card.Controls.Add(Ui.Caption("Incoming and outgoing servers (filled in for known providers)"));
            card.Controls.Add(servers);
            var status = Ui.Hint("", W - 40);
            card.Controls.Add(status);
            var help = Ui.Link("How do I get an app password?");
            card.Controls.Add(help);
            void State()
            {
                connect.Text = Imap.Connected ? "Disconnect" : "Connect";
                password.Enabled = address.Enabled = imapServer.Enabled = smtpServer.Enabled = !Imap.Connected;
                password.PlaceholderText = Imap.Connected ? "Saved" : "Paste the app password";
                status.Text = Imap.Connected ? $"Connected to {Imap.Address}." + (Graph.SignedIn ? " (Outlook is connected too; Otto uses Outlook while it is.)" : "") : "Not connected.";
            }
            State();
            address.TextChanged += (_, _) =>
            {
                if (Imap.Known(address.Text) is Imap.Servers k) { imapServer.Text = $"{k.Imap}:{k.ImapPort}"; smtpServer.Text = $"{k.Smtp}:{k.SmtpPort}"; }
            };
            help.LinkClicked += (_, _) =>
            {
                var open = Imap.Known(address.Text)?.AppPasswordUrl;
                const string steps =
                    "Email providers don't let apps use your normal password. Instead you make an \"app password\" just for Otto. " +
                    "You can delete it any time to cut Otto off.\n\n" +
                    "Gmail: 2-Step Verification must be on in your Google account. Then go to myaccount.google.com/apppasswords, " +
                    "type Otto as the name, and click Create. Copy the 16-letter password into Otto (spaces don't matter).\n\n" +
                    "Yahoo, iCloud, Fastmail and others: look for \"app passwords\" in your account's security settings.";
                if (MessageBox.Show(this, steps + (open != null ? "\n\nClick OK to open that page now." : ""), "Getting an app password",
                        open != null ? MessageBoxButtons.OKCancel : MessageBoxButtons.OK, MessageBoxIcon.Information) == DialogResult.OK && open != null)
                    Tools.OpenUrl(open);
            };
            connect.Click += async (_, _) =>
            {
                if (Imap.Connected) { Imap.Disconnect(); password.Text = ""; State(); return; }
                var pass = password.Text.Replace(" ", "").Trim(); // Google shows app passwords in groups of four
                if (!address.Text.Contains('@') || pass.Length == 0 || imapServer.Text.Trim().Length == 0 || smtpServer.Text.Trim().Length == 0)
                {
                    status.Text = "Fill in the address, app password and both servers first.";
                    return;
                }
                connect.Enabled = false;
                status.Text = "Checking…";
                try
                {
                    await Imap.Connect(address.Text.Trim(), pass, imapServer.Text, smtpServer.Text);
                    if (IsDisposed || status.IsDisposed) return;
                    password.Text = "";
                    State();
                }
                catch (Exception ex)
                {
                    if (status.IsDisposed) return;
                    status.Text = ex is MailKit.Security.AuthenticationException
                        ? "The address or app password was refused. Make sure it's an app password, not your normal one."
                        : "Couldn't connect: " + Agent.ErrorText(ex).Clip(120);
                }
                finally { if (!connect.IsDisposed) connect.Enabled = true; }
            };

            // calendar link
            var cal = Ui.Card(p, "Calendar", "Paste your calendar's private iCal link and Otto can answer \"what's on tomorrow?\". Read-only.");
            var calRow = Ui.Row();
            var link = Ui.TextBox(W - 170, password: true);
            var calSave = Ui.Button("Save", primary: true);
            calRow.Controls.AddRange(new Control[] { link, calSave });
            cal.Controls.Add(calRow);
            var calStatus = Ui.Hint("", W - 40);
            cal.Controls.Add(calStatus);
            var calHelp = Ui.Link("Where do I find it?");
            cal.Controls.Add(calHelp);
            void CalState()
            {
                calSave.Text = Calendar.Connected ? "Remove" : "Save";
                link.Enabled = !Calendar.Connected;
                link.PlaceholderText = Calendar.Connected ? "Saved" : "Paste the secret iCal address";
                calStatus.Text = Calendar.Connected ? "Connected. Otto can read your calendar (it can't add or change events)." : "Not connected.";
            }
            CalState();
            calHelp.LinkClicked += (_, _) => MessageBox.Show(this,
                "Google Calendar: open calendar.google.com on a computer, click the gear → Settings, pick your calendar on the left, " +
                "scroll to \"Integrate calendar\" and copy \"Secret address in iCal format\".\n\n" +
                "Apple (iCloud): share the calendar as a public calendar and copy its link.\n" +
                "Outlook.com: Settings → Calendar → Shared calendars → Publish a calendar → copy the ICS link.\n\n" +
                "Keep the link private: anyone with it can see your calendar. You can reset it in the same place.",
                "Calendar link", MessageBoxButtons.OK, MessageBoxIcon.Information);
            calSave.Click += async (_, _) =>
            {
                if (Calendar.Connected) { Calendar.SetLink(null); link.Text = ""; CalState(); return; }
                calSave.Enabled = false;
                calStatus.Text = "Checking…";
                try
                {
                    Calendar.SetLink(link.Text);
                    await Calendar.List(new System.Text.Json.Nodes.JsonObject { ["days"] = 1 }, CancellationToken.None);
                    if (calStatus.IsDisposed) return;
                    link.Text = "";
                    CalState();
                }
                catch (Exception ex)
                {
                    Calendar.SetLink(null);
                    if (!calStatus.IsDisposed) calStatus.Text = "Couldn't use that link: " + Agent.ErrorText(ex).Clip(120);
                }
                finally { if (!calSave.IsDisposed) calSave.Enabled = true; }
            };

            // Outlook
            var o = Ui.Card(p, "Outlook, Hotmail and Microsoft 365", "Email and calendar (including adding events) through Microsoft. Needs a free Microsoft app registration.");
            var oRow = Ui.Row();
            var clientId = Ui.TextBox(W - 170, Graph.ClientId, "App client ID, e.g. 1a2b3c4d-....");
            var signIn = Ui.Button("Sign in", primary: true);
            oRow.Controls.AddRange(new Control[] { clientId, signIn });
            o.Controls.Add(oRow);
            var oStatus = Ui.Hint("", W - 40);
            o.Controls.Add(oStatus);
            o.Controls.Add(Ui.Hint("Microsoft now only lets work, school or developer-program accounts register apps; a personal account alone can't.", W - 40));
            var howTo = Ui.Link("How do I get a client ID?");
            o.Controls.Add(howTo);
            void OState()
            {
                signIn.Text = Graph.SignedIn ? "Sign out" : "Sign in";
                oStatus.Text = Graph.SignedIn ? $"Connected to {Graph.Account}." : "Not connected.";
            }
            OState();
            howTo.LinkClicked += (_, _) =>
            {
                const string steps =
                    "1. Click OK to open Microsoft's app registrations page and sign in.\n" +
                    "2. Click \"New registration\". Name it Otto.\n" +
                    "3. Under \"Supported account types\" choose \"Accounts in any organizational directory and personal Microsoft accounts\".\n" +
                    "4. Leave Redirect URI empty and click Register.\n" +
                    "5. Open \"Authentication\", set \"Allow public client flows\" to Yes, and Save.\n" +
                    "6. Copy the \"Application (client) ID\" from the Overview page here and click Sign in.";
                if (MessageBox.Show(this, steps, "Getting a client ID", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK)
                    Tools.OpenUrl("https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade");
            };
            signIn.Click += async (_, _) =>
            {
                if (Graph.SignedIn) { Graph.SignOut(); OState(); return; }
                Graph.ClientId = clientId.Text;
                signIn.Enabled = false;
                try
                {
                    var (who, error) = await GraphSignIn.ShowAsync(this);
                    if (oStatus.IsDisposed) return;
                    OState();
                    if (who == null && error != null) oStatus.Text = "Couldn't sign in: " + error;
                }
                finally { if (!signIn.IsDisposed) signIn.Enabled = true; }
            };
        }

        // ---------------- look and feel ----------------

        void BuildLook(FlowLayoutPanel p)
        {
            var theme = Ui.Card(p, "Theme", null);
            theme.Controls.Add(Ui.Segmented(new[] { "Dark", "Light" }, Theme.Light ? 1 : 0, i =>
            {
                Theme.Light = i == 1;
                Theme.Apply();
                Open(Look); // repaint this window in the new colours
            }));
            theme.Controls.Add(Ui.Hint("Dark has a see-through blurred background; light is solid.", W - 40));

            var side = Ui.Card(p, "Panel position", "Which edge of the screen the panel slides in from.");
            side.Controls.Add(Ui.Segmented(new[] { "Right", "Left" }, Theme.PanelLeft ? 1 : 0, i => { Theme.PanelLeft = i == 1; Theme.Apply(); }));

            var size = Ui.Card(p, "Text size", "How big the text in chats and the message box is.");
            size.Controls.Add(Ui.Segmented(new[] { "Small", "Normal", "Large", "Extra large" }, Theme.TextSize, i => { Theme.TextSize = i; Theme.Apply(); }));
        }

        // ---------------- chat and voice ----------------

        void BuildChat(FlowLayoutPanel p)
        {
            var card = Ui.Card(p, "Replies", null);
            var animate = Ui.Toggle("Type replies out word by word", Prefs.AnimateReplies);
            animate.CheckedChanged += (_, _) => Prefs.AnimateReplies = animate.Checked;
            card.Controls.Add(animate);
            var sounds = Ui.Toggle("Sounds", Prefs.Sounds);
            sounds.CheckedChanged += (_, _) => Prefs.Sounds = sounds.Checked;
            card.Controls.Add(sounds);
            card.Controls.Add(Ui.Hint("Replace any sound with your own .wav in %LOCALAPPDATA%\\Otto\\sounds (send, reply, type, takeover, listen-on, listen-off, attention, error).", W - 40));

            var voice = Ui.Card(p, "Voice", "Press Ctrl+Alt+J, speak, and press it again.");
            var autoSend = Ui.Toggle("Send voice messages straight away", Prefs.VoiceAutoSend);
            autoSend.CheckedChanged += (_, _) => Prefs.VoiceAutoSend = autoSend.Checked;
            voice.Controls.Add(autoSend);
            voice.Controls.Add(Ui.Hint("Off: what you said goes into the message box first, so you can check it.", W - 40));
            var readAloud = Ui.Toggle("Read replies aloud", Prefs.ReadAloud);
            readAloud.CheckedChanged += (_, _) => Prefs.ReadAloud = readAloud.Checked;
            voice.Controls.Add(readAloud);
            voice.Controls.Add(Ui.Hint("Uses Windows' own voices; change the voice in Windows Settings → Time & Language → Speech.", W - 40));
        }

        // ---------------- quick actions ----------------

        void BuildQuick(FlowLayoutPanel p)
        {
            var card = Ui.Card(p, "Your quick actions", "One-click requests for things you ask often. They show at the top of a new chat and in the tray menu.");
            var list = Ui.List(W - 40, 220);
            card.Controls.Add(list);
            var row = Ui.Row();
            var add = Ui.Button("Add…", primary: true);
            var edit = Ui.Button("Edit…");
            var remove = Ui.Button("Remove");
            row.Controls.AddRange(new Control[] { add, edit, remove });
            card.Controls.Add(row);
            void Fill()
            {
                list.Items.Clear();
                foreach (var a in QuickActions.All) list.Items.Add($"{a.Name}  ·  {a.Prompt.Clip(60)}");
                edit.Enabled = remove.Enabled = list.SelectedIndex >= 0;
            }
            list.SelectedIndexChanged += (_, _) => edit.Enabled = remove.Enabled = list.SelectedIndex >= 0;
            add.Click += (_, _) =>
            {
                if (QuickActionPrompt.Ask(this, null) is QuickActions.Action a) { var all = QuickActions.All; all.Add(a); QuickActions.All = all; Fill(); }
            };
            edit.Click += (_, _) =>
            {
                int i = list.SelectedIndex;
                var all = QuickActions.All;
                if (i < 0 || i >= all.Count) return;
                if (QuickActionPrompt.Ask(this, all[i]) is QuickActions.Action a) { all[i] = a; QuickActions.All = all; Fill(); }
            };
            remove.Click += (_, _) =>
            {
                int i = list.SelectedIndex;
                var all = QuickActions.All;
                if (i < 0 || i >= all.Count) return;
                all.RemoveAt(i);
                QuickActions.All = all;
                Fill();
            };
            Fill();
            card.Controls.Add(Ui.Hint("Example: name \"Morning briefing\", request \"Summarise my unread email and what's on my calendar today\".", W - 40));
        }

        // ---------------- reminders ----------------

        void BuildReminders(FlowLayoutPanel p)
        {
            var card = Ui.Card(p, "Reminders and scheduled tasks", "To add one, just ask Otto, for example \"remind me at 5 to call Mum\" or \"every weekday at 8, summarise my unread email\".");
            var list = Ui.List(W - 40, 260);
            card.Controls.Add(list);
            var cancel = Ui.Button("Cancel selected");
            card.Controls.Add(cancel);
            List<Schedule.Item> items = new();
            void Fill()
            {
                items = Schedule.All();
                list.Items.Clear();
                foreach (var i in items) list.Items.Add($"{(i.Kind == "task" ? "Task" : "Reminder")}: {i.Text.Clip(50)}  ·  {Schedule.When(i)}");
                if (items.Count == 0) list.Items.Add("Nothing scheduled.");
                cancel.Enabled = false;
            }
            list.SelectedIndexChanged += (_, _) => cancel.Enabled = list.SelectedIndex >= 0 && list.SelectedIndex < items.Count;
            cancel.Click += (_, _) => { if (list.SelectedIndex >= 0 && list.SelectedIndex < items.Count) { Schedule.Remove(items[list.SelectedIndex].Id); Fill(); } };
            Fill();
            card.Controls.Add(Ui.Hint("Scheduled tasks run in the background without using your screen. Anything that needs your OK is left for you and mentioned in the notification.", W - 40));
        }

        // ---------------- spending ----------------

        void BuildSpending(FlowLayoutPanel p)
        {
            var card = Ui.Card(p, "Monthly limit", "Otto warns you at 80% and stops at the limit, even partway through a task. It starts again on the 1st.");
            var row = Ui.Row();
            row.Controls.Add(new Label { Text = "US$", AutoSize = true, ForeColor = Ui.Fg, Margin = new Padding(0, 7, 6, 0) });
            var limit = new NumericUpDown
            {
                Width = 110, DecimalPlaces = 2, Increment = 1, Maximum = 10_000, Minimum = 0, Value = (decimal)Spending.Limit,
                BackColor = Ui.Field, ForeColor = Ui.Fg, BorderStyle = BorderStyle.FixedSingle,
            };
            row.Controls.Add(limit);
            row.Controls.Add(new Label { Text = "a month (0 = no limit)", AutoSize = true, ForeColor = Ui.Dim, Margin = new Padding(8, 7, 0, 0) });
            card.Controls.Add(row);
            limit.ValueChanged += (_, _) => Spending.Limit = (double)limit.Value;

            var used = Ui.Card(p, "This month", null);
            used.Controls.Add(new Label { Text = $"US${Spending.ThisMonthTotal():0.00}", AutoSize = true, Font = new Font("Segoe UI Light", 22f), ForeColor = Ui.Fg });
            used.Controls.Add(Ui.Hint("Counted for Claude, whose prices Otto knows. Other providers don't report a price, so set a limit in their own console too " +
                                      "(Anthropic: console.anthropic.com → Limits). Google Gemini's free tier costs nothing.", W - 40));
        }

        // ---------------- privacy and safety ----------------

        void BuildSafety(FlowLayoutPanel p)
        {
            var block = Ui.Card(p, "Places Otto must never touch", "One per line: a website (commbank.com.au) or an app or window name (Banking). Otto won't open, read or click in them.");
            var box = Ui.TextBox(W - 40, string.Join("\r\n", Blocklist.Entries), multiline: true);
            box.Height = 130;
            block.Controls.Add(box);
            var saved = Ui.Hint("", W - 40);
            var save = Ui.Button("Save list", primary: true);
            save.Click += (_, _) =>
            {
                Blocklist.Entries = box.Text.Split('\n').Select(l => l.Trim()).ToList();
                saved.Text = Blocklist.Entries.Count == 0 ? "Saved. Nothing is blocked." : $"Saved. {Blocklist.Entries.Count} blocked.";
            };
            block.Controls.Add(save);
            block.Controls.Add(saved);

            var mem = Ui.Card(p, "What Otto remembers", "Otto reads these at the start of every chat. Check them now and then, and delete anything you didn't ask it to remember.");
            var row = Ui.Row();
            var notes = Ui.Button("Notes…");
            var routines = Ui.Button("Routines…");
            row.Controls.AddRange(new Control[] { notes, routines });
            mem.Controls.Add(row);
            void OpenFile(string file)
            {
                var path = Path.Combine(Paths.Data, file);
                if (!File.Exists(path)) { MessageBox.Show(this, "Nothing saved yet.", "Otto", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                Process.Start("notepad.exe", $"\"{path}\"");
            }
            notes.Click += (_, _) => OpenFile("notes.txt");
            routines.Click += (_, _) => OpenFile("routines.json");

            var info = Ui.Card(p, "How Otto keeps you safe", null);
            info.Controls.Add(Ui.Hint("Otto asks before buying, signing, submitting, sending email or permanently deleting, and before running commands after it has read a web page or email. " +
                                      "It never types passwords. Chats are encrypted with your Windows account.", W - 40));
            var links = Ui.Row();
            var privacy = Ui.Link("Privacy policy");
            var security = Ui.Link("Security review");
            privacy.LinkClicked += (_, _) => Tools.OpenUrl("https://github.com/bam-rip/otto/blob/main/PRIVACY.md");
            security.LinkClicked += (_, _) => Tools.OpenUrl("https://github.com/bam-rip/otto/blob/main/SECURITY.md");
            links.Controls.AddRange(new Control[] { privacy, security });
            info.Controls.Add(links);
        }

        // ---------------- updates and about ----------------

        void BuildAbout(FlowLayoutPanel p)
        {
            var upd = Ui.Card(p, "Updates", "New versions download in the background and show as a small bar in the panel. Nothing installs without your click.");
            var daily = Ui.Toggle("Check for updates once a day", Prefs.CheckUpdates);
            daily.CheckedChanged += (_, _) => Prefs.CheckUpdates = daily.Checked;
            upd.Controls.Add(daily);
            var row = Ui.Row();
            var check = Ui.Button("Check now");
            var status = Ui.Hint("", 340);
            status.Margin = new Padding(10, 9, 0, 0);
            row.Controls.AddRange(new Control[] { check, status });
            upd.Controls.Add(row);
            check.Click += async (_, _) =>
            {
                status.Text = "Checking…";
                try
                {
                    var r = await Updater.CheckNowAsync();
                    if (!status.IsDisposed) status.Text = r == null ? "You have the latest version." : $"Otto {r.Version} is out: see the bar in the panel.";
                    if (r != null) UpdateFound?.Invoke(r);
                }
                catch (Exception ex) { if (!status.IsDisposed) status.Text = "Couldn't check: " + Agent.ErrorText(ex).Clip(60); }
            };

            var about = Ui.Card(p, $"Otto {Updater.Current}", "An AI assistant that uses your PC the way you do. Made by bam-rip, MIT licensed.");
            var links = Ui.Row();
            var site = Ui.Link("GitHub");
            site.LinkClicked += (_, _) => Tools.OpenUrl("https://github.com/bam-rip/otto");
            var tour = Ui.Link("Welcome tour");
            tour.LinkClicked += (_, _) => WelcomeRequested?.Invoke();
            links.Controls.AddRange(new Control[] { site, tour });
            about.Controls.Add(links);
            var uninstall = Ui.Button("Uninstall Otto…");
            uninstall.Click += (_, _) => { if (Uninstall.Run()) { Close(); UninstallDone?.Invoke(); } };
            about.Controls.Add(uninstall);
        }
    }

    /// Set by the tray app: what "Check now", "Welcome tour" and "Uninstall" lead to.
    public static Action<Updater.Release>? UpdateFound;
    public static Action? WelcomeRequested, UninstallDone;

    sealed class NavButton : Control
    {
        public readonly string Section;
        readonly string glyph;
        bool selected, hot;
        public bool Selected { get => selected; set { selected = value; Invalidate(); } }

        public NavButton(string section, string glyph)
        {
            Section = section;
            this.glyph = glyph;
            Text = section;
            Height = 40;
            Margin = new Padding(0, 0, 0, 2);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = true;
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode is Keys.Enter or Keys.Space) OnClick(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Ui.Side);
            if (selected || hot) using (var b = new SolidBrush(selected ? Ui.Selected : Ui.Hover)) g.FillRectangle(b, ClientRectangle);
            if (selected) using (var a = new SolidBrush(ChatPanel.Accent)) g.FillRectangle(a, 0, 8, LogicalToDeviceUnits(3), Height - 16);
            using var gf = new Font(ChatPanel.GlyphFont, 11f);
            TextRenderer.DrawText(g, glyph, gf, new Rectangle(LogicalToDeviceUnits(14), 0, LogicalToDeviceUnits(24), Height), Ui.Fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(LogicalToDeviceUnits(48), 0, Width - LogicalToDeviceUnits(52), Height), Ui.Fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, ClientRectangle);
        }
    }
}

/// The small building blocks the settings window is made of, in the panel's colours.
static class Ui
{
    public static Color Back => Theme.Light ? Color.FromArgb(249, 249, 249) : Color.FromArgb(32, 32, 32);
    public static Color Side => Theme.Light ? Color.FromArgb(238, 238, 238) : Color.FromArgb(26, 26, 26);
    public static Color CardBack => Theme.Light ? Color.White : Color.FromArgb(43, 43, 43);
    public static Color Field => Theme.Light ? Color.FromArgb(251, 251, 251) : Color.FromArgb(56, 56, 56);
    public static Color Hover => Theme.Light ? Color.FromArgb(228, 228, 228) : Color.FromArgb(45, 45, 45);
    public static Color Selected => Theme.Light ? Color.FromArgb(220, 220, 220) : Color.FromArgb(52, 52, 52);
    public static Color Fg => Theme.Fg;
    public static Color Dim => Theme.Dim;
    const int CardWidth = 580; // fields inside are 520 wide plus a 10px gap, so 20px padding each side fits them

    /// A titled group with a light background; returns the panel to put its controls in.
    public static FlowLayoutPanel Card(FlowLayoutPanel page, string title, string? note)
    {
        var card = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            MinimumSize = new Size(CardWidth, 0), MaximumSize = new Size(CardWidth, 0), // every card the same width
            BackColor = CardBack, Padding = new Padding(20, 16, 20, 16), Margin = new Padding(0, 0, 0, 12),
        };
        card.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font("Segoe UI Semibold", 11f), ForeColor = Fg, Margin = new Padding(0, 0, 0, 4) });
        if (note != null) card.Controls.Add(Hint(note, CardWidth - 50));
        page.Controls.Add(card);
        return card;
    }

    public static Label Caption(string text) => new() { Text = text, AutoSize = true, ForeColor = Fg, Margin = new Padding(0, 10, 0, 4) };

    public static Label Hint(string text, int width) =>
        new() { Text = text, UseMnemonic = false, AutoSize = true, MaximumSize = new Size(width, 0), ForeColor = Dim, Margin = new Padding(0, 4, 0, 6) };

    public static FlowLayoutPanel Row() => new() { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 4) };

    public static TextBox TextBox(int width, string text = "", string placeholder = "", bool password = false, bool multiline = false) => new()
    {
        Width = width, Text = text, PlaceholderText = placeholder, UseSystemPasswordChar = password, Multiline = multiline,
        ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None, AcceptsReturn = multiline,
        BackColor = Field, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 2, 10, 2), Font = new Font("Segoe UI", 10f),
    };

    public static ComboBox Combo(int width, bool editable = false) => new()
    {
        Width = width, DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
        BackColor = Field, ForeColor = Fg, Margin = new Padding(0, 2, 10, 2), Font = new Font("Segoe UI", 10f),
    };

    public static ListBox List(int width, int height) => new()
    {
        Width = width, Height = height, BackColor = Field, ForeColor = Fg, BorderStyle = BorderStyle.None, IntegralHeight = false,
        Font = new Font("Segoe UI", 10f), Margin = new Padding(0, 6, 0, 6),
    };

    public static Button Button(string text, bool primary = false)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, MinimumSize = new Size(96, 34), Padding = new Padding(10, 0, 10, 0), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            BackColor = primary ? ChatPanel.Accent : Theme.Light ? Color.FromArgb(230, 230, 230) : Color.FromArgb(62, 62, 62),
            ForeColor = primary ? Color.White : Fg, Margin = new Padding(0, 2, 8, 2), Font = new Font("Segoe UI", 9.75f),
        };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    public static LinkLabel Link(string text) => new()
    {
        Text = text, AutoSize = true, LinkColor = Theme.Light ? Color.FromArgb(0, 95, 184) : Color.FromArgb(96, 175, 255),
        ActiveLinkColor = ChatPanel.Accent, LinkBehavior = LinkBehavior.HoverUnderline, Margin = new Padding(0, 6, 16, 2),
    };

    public static Toggle Toggle(string text, bool on) => new() { Text = text, Checked = on, Width = CardWidth - 40 };

    /// A row of options where one is chosen (Dark | Light).
    public static FlowLayoutPanel Segmented(string[] options, int chosen, Action<int> picked)
    {
        var row = Row();
        var buttons = new List<Button>();
        for (int i = 0; i < options.Length; i++)
        {
            int index = i;
            var b = Button(options[i], primary: i == chosen);
            b.Margin = new Padding(0, 4, 4, 4);
            b.Click += (_, _) =>
            {
                for (int k = 0; k < buttons.Count; k++)
                {
                    buttons[k].BackColor = k == index ? ChatPanel.Accent : Theme.Light ? Color.FromArgb(230, 230, 230) : Color.FromArgb(62, 62, 62);
                    buttons[k].ForeColor = k == index ? Color.White : Fg;
                }
                picked(index);
            };
            buttons.Add(b);
            row.Controls.Add(b);
        }
        return row;
    }

    public static void DarkTitleBar(Form f)
    {
        int on = Theme.Light ? 0 : 1;
        DwmSetWindowAttribute(f.Handle, 20, ref on, sizeof(int));
        DarkScrollbars(f);
    }

    /// Windows' own dark scrollbars for this window and everything in it, now and as controls are added.
    public static void DarkScrollbars(Control root)
    {
        if (Theme.Light) return;
        void Apply(Control c)
        {
            if (c.IsHandleCreated) SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
            else c.HandleCreated += (_, _) => SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
            foreach (Control child in c.Controls) Apply(child);
            c.ControlAdded += (_, e) => { if (e.Control != null) Apply(e.Control); };
        }
        Apply(root);
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string? app, string? idList);

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}

/// An on/off switch with its label, like Windows' own settings.
sealed class Toggle : Control
{
    bool on;
    public event EventHandler? CheckedChanged;
    public bool Checked { get => on; set { if (on == value) return; on = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); } }

    public Toggle()
    {
        Height = 36;
        Cursor = Cursors.Hand;
        TabStop = true;
        Margin = new Padding(0, 4, 0, 2);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) Checked = !Checked; }
    protected override void OnGotFocus(EventArgs e) => Invalidate();
    protected override void OnLostFocus(EventArgs e) => Invalidate();

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Ui.CardBack);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int w = LogicalToDeviceUnits(40), h = LogicalToDeviceUnits(20), y = (Height - h) / 2;
        var track = new Rectangle(1, y, w, h);
        using (var path = Pill(track))
        {
            if (on) using (var b = new SolidBrush(ChatPanel.Accent)) g.FillPath(b, path);
            else using (var pen = new Pen(Ui.Dim, LogicalToDeviceUnits(1))) g.DrawPath(pen, path);
        }
        int knob = h - LogicalToDeviceUnits(8);
        var kx = on ? track.Right - knob - LogicalToDeviceUnits(4) : track.X + LogicalToDeviceUnits(4);
        using (var kb = new SolidBrush(on ? Color.White : Ui.Dim)) g.FillEllipse(kb, kx, y + (h - knob) / 2, knob, knob);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(w + LogicalToDeviceUnits(14), 0, Width - w - LogicalToDeviceUnits(14), Height), Ui.Fg,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, ClientRectangle);
    }

    static System.Drawing.Drawing2D.GraphicsPath Pill(Rectangle r)
    {
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        p.AddArc(r.X, r.Y, r.Height, r.Height, 90, 180);
        p.AddArc(r.Right - r.Height, r.Y, r.Height, r.Height, 270, 180);
        p.CloseFigure();
        return p;
    }
}

/// Adding or editing a quick action: a name and the request it sends.
static class QuickActionPrompt
{
    public static QuickActions.Action? Ask(IWin32Window owner, QuickActions.Action? existing)
    {
        using var f = new Form
        {
            Text = existing == null ? "Add a quick action" : "Edit quick action", FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, TopMost = true,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Font = new Font("Segoe UI", 9.75f), Padding = new Padding(16),
            BackColor = Ui.Back, ForeColor = Ui.Fg,
        };
        f.HandleCreated += (_, _) => Ui.DarkTitleBar(f);
        var grid = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Fill };
        f.Controls.Add(grid);
        grid.Controls.Add(Ui.Caption("Name (what the button says)"));
        var name = Ui.TextBox(420, existing?.Name ?? "", "Morning briefing");
        grid.Controls.Add(name);
        grid.Controls.Add(Ui.Caption("Request (what Otto is asked)"));
        var prompt = Ui.TextBox(420, existing?.Prompt ?? "", "Summarise my unread email and what's on my calendar today", multiline: true);
        prompt.Height = 90;
        grid.Controls.Add(prompt);
        var row = Ui.Row();
        var ok = Ui.Button(existing == null ? "Add" : "Save", primary: true);
        ok.DialogResult = DialogResult.OK;
        var cancel = Ui.Button("Cancel");
        cancel.DialogResult = DialogResult.Cancel;
        row.Controls.AddRange(new Control[] { ok, cancel });
        grid.Controls.Add(row);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        if (f.ShowDialog(owner) != DialogResult.OK || name.Text.Trim().Length == 0 || prompt.Text.Trim().Length == 0) return null;
        return new(name.Text.Trim(), prompt.Text.Trim());
    }
}
