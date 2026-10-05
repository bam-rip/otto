using System.Text.Json.Nodes;

namespace Otto;

/// Owns everything: tray icon, the slide-out panel, global hotkeys, the agent and the mic.
sealed class TrayApp : ApplicationContext
{
    const int KeyPanel = 1, KeyTalk = 2, KeyKill = 3, KeyAsk = 4;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    readonly NotifyIcon tray;
    readonly ChatPanel panel = new();
    readonly Hotkeys keys = new();
    readonly Agent agent;
    readonly Voice voice = new();
    CancellationTokenSource? cts;
    string? lastReply;
    double chatCost;
    long chatTokens, chatCached;
    Updater.Release? pendingUpdate;
    readonly System.Windows.Forms.Timer updateTimer = new();

    // ---- reminders and scheduled tasks ----
    readonly System.Windows.Forms.Timer scheduleTimer = new() { Interval = 20_000 };
    readonly Queue<(Schedule.Item item, bool late)> taskQueue = new();
    bool runningScheduled;
    string? pendingUserText; // asked while a scheduled task was running; starts right after it
    string? notifiedChat;    // the chat a "task done" notification opens
    ToolStripMenuItem? updateItem;
    string chatId = ChatStore.NewId();
    bool costKnown = true, replySounded, toldFallback;
    volatile bool controlling;

    public TrayApp()
    {
        _ = panel.Handle; // create the handle now so background threads can Invoke onto it
        SingleInstance.Listen(panel, () => panel.ShowPanel());
        ControlOverlay.Init();
        agent = CreateAgent();
        WirePanel();
        StartUpdateChecks();
        tray = CreateTray();
        ConnectFeatures();
        if (RegisterHotkeys() is { Count: > 0 } failed) panel.AddSystem("Another app already owns: " + string.Join(", ", failed));
        FirstRun();
        if (Environment.GetCommandLineArgs().Contains("--show")) { panel.Pinned = true; panel.ShowPanel(); }
    }

    /// The agent, wired to the panel: confirmations, replies (whole or typed out), tool notes, screen control and cost.
    Agent CreateAgent()
    {
        return new Agent
        {
            Confirm = q =>
            {
                bool ok = panel.Confirm(q);
                if (controlling) panel.HidePanel(); // back out of the way so it doesn't eat the next click
                return ok;
            },
            // Not animated: each message appears whole, with the reply sound on the first one of the request.
            OnText = t =>
            {
                lastReply = t;
                if (!replySounded) { replySounded = true; Sfx.Reply(); }
                panel.AddOtto(t);
            },
            // Animated: text types out with a typewriter tick (no reply sound).
            OnStream = (t, isNew) =>
            {
                lastReply = t;
                Sfx.Type();
                panel.StreamOtto(t, isNew);
            },
            Animate = () => Prefs.AnimateReplies,
            OnTool = t => panel.AddTool(t),
            OnControl = on =>
            {
                if (on == controlling) return;
                controlling = on;
                if (on) panel.HidePanel(); // get out of the way of the clicks
                ControlOverlay.Show(on);
            },
            OnUsage = (usd, tokens, cached) =>
            {
                chatTokens += tokens;
                chatCached += cached;
                if (usd is double d)
                {
                    chatCost += d;
                    if (Spending.Add(d) is string note)
                    {
                        panel.AddSystem(note);
                        if (Spending.Blocked() != null) cts?.Cancel(); // crossed the limit mid-task: stop here
                    }
                }
                else costKnown = false;
                // most tokens are the same instructions re-sent every step; say how many were cheap cache re-reads
                var reused = chatCached > 0 ? $", {chatCached * 100 / Math.Max(1, chatTokens)}% cached" : "";
                panel.SetCost(!costKnown ? $"{chatTokens / 1000.0:0.#}k tokens this chat{reused}"
                    : chatCost < 0.01 ? "under 1¢ this chat" : $"${chatCost:0.00} this chat");
            },
        };
    }

    /// What the panel's buttons and menus do.
    void WirePanel()
    {
        panel.Submit += Run;
        panel.StopRequested += Kill;
        panel.MicToggled += ToggleMic;
        panel.ClearRequested += () =>
        {
            if (cts != null) return;
            ChatStore.Save(chatId, agent.Snapshot()); // the old chat stays in history
            chatId = ChatStore.NewId();
            agent.Reset();
            panel.ClearLog();
            ResetCounter();
        };
        panel.HistoryRequested += ShowHistory;
        panel.History.OpenRequested += id => { OpenChat(id); panel.HideHistory(); };
        panel.History.DeleteRequested += id => { ChatStore.Delete(id); panel.History.Remove(id); };
        panel.History.DeleteAllRequested += () =>
        {
            ChatStore.DeleteAll();
            panel.ShowHistory(new List<ChatStore.Summary>(), chatId);
        };
        panel.UpdateClicked += () => InstallUpdate();
        panel.UpdateNotesClicked += () => { if (pendingUpdate != null) Tools.OpenUrl(pendingUpdate.PageUrl); };
        panel.UpdateDismissed += () =>
        {
            if (pendingUpdate != null) Updater.Dismissed = pendingUpdate.Tag; // hide until a newer one
            ShowUpdate(null);
        };
        panel.UndoRequested += () =>
            Run("Undo what you just did in your last task: put moved or renamed files back, restore overwritten files from their backups, " +
                "and reverse setting changes. Don't touch anything else. Then tell me in one line what you undid, or what can't be undone.");
        ControlOverlay.StopClicked += Kill;
        panel.SettingsRequested += SettingsWindow.Show;
        panel.RetryRequested += () =>
        {
            if (cts != null) return;
            panel.RemoveLastTurn();
            if (agent.UndoLastTurn() is string again) Run(again);
        };
        panel.EditRequested += () =>
        {
            if (cts != null) return;
            panel.RemoveLastTurn();
            if (agent.UndoLastTurn() is string text) panel.SetInput(text);
        };
    }

    /// The quiet daily update check, and a note after an update.
    void StartUpdateChecks()
    {
        // quiet daily check: first a minute after start (not to slow login), then every few hours
        // (the Updater itself only asks GitHub once a day)
        updateTimer.Interval = 60_000;
        updateTimer.Tick += async (_, _) =>
        {
            updateTimer.Interval = 6 * 60 * 60_000;
            if (pendingUpdate == null && await Updater.CheckIfDueAsync() is Updater.Release r) ShowUpdate(r);
        };
        updateTimer.Start();
        if (Environment.GetCommandLineArgs().Contains("--updated"))
            panel.AddSystem($"Updated to Otto {Updater.Current}.");
    }

    /// The tray icon: left click opens the panel, a clicked notification opens its chat.
    NotifyIcon CreateTray()
    {
        var tray = new NotifyIcon
        {
            Icon = Icons.Tray(),
            Text = "Otto",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        tray.BalloonTipClicked += (_, _) =>
        {
            if (notifiedChat is string id && cts == null && !runningScheduled) OpenChat(id);
            notifiedChat = null;
            panel.ShowPanel();
        };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) panel.Toggle(); };
        return tray;
    }

    /// Quick actions, settings links, and the reminder/scheduled-task clock.
    void ConnectFeatures()
    {
        _ = Task.Run(ChatStore.List); // read the history once in the background, so opening it first time is instant too
        QuickActions.Changed += panel.RefreshSuggestions;
        SettingsWindow.UpdateFound = ShowUpdate;
        SettingsWindow.WelcomeRequested = ShowWelcome;
        SettingsWindow.UninstallDone = ExitThread;
        scheduleTimer.Tick += (_, _) => RunDue();
        scheduleTimer.Start();
        panel.BeginInvoke(RunDue); // anything that came due while Otto was off
    }

    /// The global hotkeys; returns the ones another app already owns.
    List<string> RegisterHotkeys()
    {
        var failed = new List<string>();
        if (!keys.Register(KeyPanel, Hotkeys.Ctrl | Hotkeys.Shift, Keys.J)) failed.Add("Ctrl+Shift+J");
        if (!keys.Register(KeyTalk, Hotkeys.Ctrl | Hotkeys.Alt, Keys.J)) failed.Add("Ctrl+Alt+J");
        if (!keys.Register(KeyKill, Hotkeys.Ctrl | Hotkeys.Alt, Keys.End)) failed.Add("Ctrl+Alt+End");
        if (!keys.Register(KeyAsk, Hotkeys.Ctrl | Hotkeys.Alt, Keys.A)) failed.Add("Ctrl+Alt+A");
        keys.Pressed += id =>
        {
            if (id == KeyPanel) panel.Toggle();
            else if (id == KeyTalk) ToggleMic();
            else if (id == KeyKill) Kill();
            else if (id == KeyAsk) AskAboutThis();
        };
        return failed;
    }

    /// First start: the welcome tour (or settings, when no AI is set up yet).
    void FirstRun()
    {
        if (!Prefs.Welcomed && Providers.Problem(Providers.Current()) == null) Prefs.Welcomed = true; // set up before the tour existed
        if (!Prefs.Welcomed || Providers.Problem(Providers.Current()) != null)
        {
            // first run: the walkthrough, which includes picking an AI, instead of making them find the tray menu
            panel.BeginInvoke(() =>
            {
                if (!Prefs.Welcomed) ShowWelcome();
                else SettingsWindow.Show();
                panel.AddSystem(Providers.Problem(Providers.Current()) ?? "All set. Press Ctrl+Shift+J any time to open me.");
            });
        }
    }

    ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip();
        m.Items.Add("Open", null, (_, _) => panel.ShowPanel());
        m.Items.Add("Settings…", null, (_, _) => SettingsWindow.Show());
        var quick = new ToolStripMenuItem("Quick actions");
        quick.DropDownItems.Add("(none yet)"); // filled each time it opens
        quick.DropDownOpening += (_, _) =>
        {
            quick.DropDownItems.Clear();
            foreach (var a in QuickActions.All)
            {
                var prompt = a.Prompt;
                quick.DropDownItems.Add(a.Name, null, (_, _) => { panel.ShowPanel(); Run(prompt); });
            }
            if (quick.DropDownItems.Count == 0) quick.DropDownItems.Add(new ToolStripMenuItem("None yet: add them in Settings") { Enabled = false });
        };
        m.Items.Add(quick);
        m.Items.Add("Reminders and scheduled tasks…", null, (_, _) => SettingsWindow.Show(SettingsWindow.Reminders));
        m.Items.Add("Welcome tour", null, (_, _) => ShowWelcome());
        var startup = new ToolStripMenuItem("Start with Windows") { Checked = StartsWithWindows() };
        startup.Click += (_, _) => { SetStartWithWindows(!startup.Checked); startup.Checked = StartsWithWindows(); };
        m.Items.Add(startup);
        var sounds = new ToolStripMenuItem("Sounds") { Checked = Prefs.Sounds };
        sounds.Click += (_, _) => { Prefs.Sounds = !sounds.Checked; sounds.Checked = Prefs.Sounds; };
        m.Items.Add(sounds);
        updateItem = new ToolStripMenuItem("Update") { Visible = false };
        updateItem.Click += (_, _) => InstallUpdate();
        m.Items.Add(updateItem);
        m.Items.Add("Check for updates", null, async (_, _) =>
        {
            try
            {
                if (await Updater.CheckNowAsync() is Updater.Release r) { ShowUpdate(r); panel.ShowPanel(); }
                else tray.ShowBalloonTip(3000, "Otto", $"You're on the latest version ({Updater.Current}).", ToolTipIcon.None);
            }
            catch { tray.ShowBalloonTip(3000, "Otto", "Couldn't reach GitHub to check for updates.", ToolTipIcon.None); }
        });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Uninstall Otto…", null, (_, _) => { if (cts == null && Uninstall.Run()) ExitThread(); });
        m.Items.Add("Quit", null, (_, _) => ExitThread());
        return m;
    }

    async void Run(string text)
    {
        if (cts != null) { panel.AddSystem("Still working on the last request. Stop it first (Ctrl+Alt+End) or wait."); return; }
        // one thing at a time: the safety state (what's been read this turn) is shared
        if (runningScheduled)
        {
            pendingUserText = text;
            panel.AddSystem("Finishing a scheduled task first. I'll start on this the moment it's done.");
            return;
        }
        // Otto's own program file is being replaced: anything started now would run half old, half new
        if (installing) { panel.AddSystem("Updating Otto. Ask again in a few seconds, once it has restarted."); return; }
        lastReply = null;
        if (Providers.Problem(Providers.Current()) is string problem) { panel.AddSystem(problem); return; }
        if (Spending.Blocked() is string limit) { panel.AddSystem(limit); return; }
        var attached = panel.TakeAttachments();
        panel.AddUser(attached.Count == 0 ? text : $"{text}\n(attached: {string.Join(", ", attached.Select(a => a.Name))})");
        Speaker.Stop();
        Sfx.Send();
        replySounded = false;
        cts = new CancellationTokenSource();
        panel.SetBusy(true);
        try
        {
            await Task.Run(() => agent.RunAsync(text, cts.Token, attached));
            if (Prefs.ReadAloud && lastReply != null) Speaker.Say(lastReply);
            // the panel auto-hides when you click away, so tell you when the job's done
            if (!panel.Visible)
                tray.ShowBalloonTip(5000, "Otto is done", (lastReply ?? "Finished.").Clip(200), ToolTipIcon.None);
        }
        catch (OperationCanceledException) { panel.AddSystem("Stopped."); }
        catch (Exception e) { panel.AddSystem("Error: " + e.Message); Sfx.Error(); }
        finally
        {
            var done = cts;
            cts = null; // clear first, so a Stop pressed right now can't hit a disposed token
            done.Dispose();
            panel.SetBusy(false);
            ChatStore.Save(chatId, agent.Snapshot()); // after every request, so nothing is ever lost
        }
    }

    /// Reopen a saved chat from the history menu and carry on where it left off.
    void OpenChat(string id)
    {
        if (cts != null || ChatStore.Load(id) is not JsonArray saved) return;
        ChatStore.Save(chatId, agent.Snapshot());
        agent.Load(saved);
        chatId = id;
        panel.ClearLog();
        ResetCounter();
        foreach (var m in saved)
        {
            if (Agent.TurnText(m!) is string said) { panel.AddUser(said); continue; }
            if (m!["role"]?.GetValue<string>() != "assistant") continue;
            if (m["content"] is JsonValue v) { panel.AddOttoInstant(v.ToString()); continue; }
            foreach (var b in m["content"]!.AsArray())
            {
                var type = b?["type"]?.GetValue<string>();
                if (type == "text" && b!["text"]?.ToString() is { Length: > 0 } t) panel.AddOttoInstant(t);
                else if (type == "tool_use")
                {
                    string label;
                    try { label = Tools.Describe(b!["name"]!.ToString(), b["input"]!); } catch { label = b!["name"]!.ToString(); }
                    panel.AddTool(label);
                }
            }
        }
        panel.ShowPanel();
    }

    void ShowHistory()
    {
        if (agent.Snapshot().Count > 0) ChatStore.Save(chatId, agent.Snapshot()); // so the open chat shows, up to date
        panel.ShowHistory(ChatStore.List(), chatId);
    }

    void ShowUpdate(Updater.Release? r)
    {
        pendingUpdate = r;
        panel.SetUpdate(r == null ? null : Updater.IsPrepared(r) ? $"Otto {r.Version} is ready" : $"Otto {r.Version} is out");
        if (updateItem != null) { updateItem.Text = r == null ? "Update" : $"Update to {r.Version}"; updateItem.Visible = r != null; }
        if (r != null) PrepareUpdate(r);
    }

    /// Fetch and check the update quietly in the background, so the Update click is near-instant.
    async void PrepareUpdate(Updater.Release r)
    {
        try
        {
            await Updater.PrepareAsync(r);
            if (pendingUpdate == r && !installing) panel.SetUpdate($"Otto {r.Version} is ready");
        }
        catch { } // offline or a bad download: the Update click downloads it then, and reports any problem
    }

    bool installing;

    async void InstallUpdate()
    {
        var r = pendingUpdate;
        if (r == null || installing) return;
        if (cts != null) { panel.AddSystem("I'll be ready to update once this task finishes."); return; }
        installing = true;
        bool swapped = false;
        try
        {
            await Updater.InstallAsync(r, new Progress<string>(s => panel.SetUpdate($"Otto {r.Version}: {s}")), () => swapped = true);
        }
        catch (Exception e)
        {
            installing = false;
            if (!swapped)
            {
                panel.SetUpdate($"Otto {r.Version} is out");
                panel.AddSystem("Couldn't update: " + Agent.ErrorText(e));
                return;
            }
            // the new Otto.exe is in place but didn't start: quitting is still right, it starts at next sign-in
            // (or from the Start menu)
        }
        // The new copy is starting and waits for this one to close. This copy's program file has been swapped
        // out from under it, so it must not linger: close normally, and if anything holds that up, end the process.
        var hardStop = new System.Threading.Timer(_ => Environment.Exit(0), null, 5000, Timeout.Infinite);
        try { ExitThread(); }
        catch { Environment.Exit(0); }
        GC.KeepAlive(hardStop);
    }

    void ResetCounter() { chatCost = 0; chatTokens = 0; chatCached = 0; costKnown = true; panel.SetCost(""); }

    async void ToggleMic()
    {
        try
        {
            if (!voice.Listening)
            {
                panel.ShowPanel();
                Speaker.Stop();
                try { await voice.StartAsync(); }
                catch
                {
                    if (voice.NeedsWindowsSetting) Tools.OpenUrl("ms-settings:privacy-speech");
                    throw;
                }
                panel.SetListening(true);
                if (voice.Note is string why && !toldFallback) { toldFallback = true; panel.AddSystem(why); }
                return;
            }
            panel.SetListening(false);
            panel.SetTranscribing(true);
            string said;
            try { said = await voice.StopAsync(); }
            finally { panel.SetTranscribing(false); }
            if (string.IsNullOrWhiteSpace(said)) panel.AddSystem("Didn't catch anything.");
            // by default the words go in the box so you can check them; Enter sends
            else if (Prefs.VoiceAutoSend) Run(said);
            else panel.SetInput(said);
        }
        catch (Exception e)
        {
            panel.SetListening(false);
            panel.SetTranscribing(false);
            panel.AddSystem("Mic: " + e.Message);
        }
    }

    void Kill()
    {
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        Speaker.Stop();
        panel.CancelConfirms();
        if (voice.Listening) { voice.Abort(); panel.SetListening(false); }
    }

    void RunDue()
    {
        foreach (var item in Schedule.TakeDue())
        {
            var dueAgo = DateTime.Now - item.Next;
            bool late = dueAgo > TimeSpan.FromMinutes(2);
            if (item.Kind == "reminder")
            {
                var text = item.Text + (late ? $" (was due {item.Next:ddd h:mm tt}, while Otto was off)" : "");
                Notify("Reminder", text, null);
                panel.AddSystem("Reminder: " + text);
            }
            else if (dueAgo > TimeSpan.FromHours(24))
                Notify("Skipped a scheduled task", $"{item.Text.Clip(80)} was due {item.Next:ddd d MMM h:mm tt} while Otto was off.", null);
            else taskQueue.Enqueue((item, late));
        }
        if (!runningScheduled && cts == null && taskQueue.Count > 0) RunScheduled(taskQueue.Dequeue());
    }

    async void RunScheduled((Schedule.Item item, bool late) job)
    {
        if (Providers.Problem(Providers.Current()) is string notReady)
        {
            Notify($"Skipped: {job.item.Text.Clip(50)}", notReady, null); // don't drop it silently
            return;
        }
        if (Spending.Blocked() is string limit) { Notify($"Skipped: {job.item.Text.Clip(50)}", limit, null); return; }
        runningScheduled = true;
        var said = new System.Text.StringBuilder();
        var needed = new List<string>();
        using var stop = new CancellationTokenSource();
        var a = new Agent
        {
            Unattended = true,
            Confirm = q => { needed.Add(q); return false; }, // nobody is there to say yes
            OnText = t => said.AppendLine(t),
            Animate = () => false,
            OnTool = _ => { },
            OnUsage = (usd, tokens, cached) =>
            {
                if (usd is double d && Spending.Add(d) is string note) said.AppendLine(note);
                if (Spending.Blocked() != null) stop.Cancel(); // over the limit: stop mid-task
            },
            OnControl = _ => { },
        };
        var id = ChatStore.NewId();
        try
        {
            // off the UI thread, like your own requests, so a big page or file in the task can't freeze the panel
            var request = $"(Scheduled task{(job.late ? ", running late because the PC was off" : "")}. Nobody is watching: you can't use the screen, " +
                          $"and anything that needs the user's OK will be declined, so finish what you can and say what's left.)\n\n{job.item.Text}";
            await Task.Run(() => a.RunAsync(request, stop.Token));
        }
        catch (Exception e) { said.AppendLine("It didn't finish: " + Agent.ErrorText(e)); }
        ChatStore.Save(id, a.Snapshot());
        var reply = said.ToString().Trim();
        Notify($"Done: {job.item.Text.Clip(50)}", (needed.Count > 0 ? "Part of it needs your OK. " : "") + (reply.Length > 0 ? reply : "Finished.").Clip(220), id);
        runningScheduled = false;

        if (pendingUserText is string waiting) { pendingUserText = null; Run(waiting); }
        else if (taskQueue.Count > 0) RunScheduled(taskQueue.Dequeue());
    }

    void Notify(string title, string text, string? chatId)
    {
        notifiedChat = chatId;
        Sfx.Attention();
        tray.ShowBalloonTip(15_000, title, text.Clip(250), ToolTipIcon.None);
    }

    /// Ctrl+Alt+A: the selection (or a picture of the window) from the app in front goes into the panel.
    void AskAboutThis()
    {
        if (panel.ContainsFocus) return; // pressed in Otto itself: nothing to grab
        var c = AskAbout.Grab();
        panel.ShowPanel();
        if (c.Text != null) panel.SetInput($"About this, from {c.Window.Clip(40)}:\r\n\"{c.Text}\"\r\n\r\n");
        else if (c.Picture != null) { panel.AddAttachment(c.Picture); panel.SetInput(""); }
        else panel.AddSystem("Nothing to grab from that window.");
    }

    void ShowWelcome() => Welcome.Show(SettingsWindow.Show, StartsWithWindows, SetStartWithWindows);

    internal static bool StartsWithWindows()
    {
        return Reg.Get(RunKey, "Otto") != null;
    }

    internal static void SetStartWithWindows(bool on)
    {
        Reg.Set(RunKey, "Otto", on ? $"\"{Environment.ProcessPath}\"" : null);
    }

    protected override void ExitThreadCore()
    {
        cts?.Cancel();
        tray.Visible = false;
        tray.Dispose();
        keys.Dispose();
        voice.Dispose();
        panel.Dispose();
        base.ExitThreadCore();
    }
}
