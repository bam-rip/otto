using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Otto;

static class Program
{
    static bool WaitForMutex(Mutex m)
    {
        try { return m.WaitOne(TimeSpan.FromSeconds(60)); }
        catch (AbandonedMutexException) { return true; } // the old copy exited without releasing it: ours now
    }

    /// Debug switches (--api-test, --mail-test, --update-test...) run tools with no window and no one watching,
    /// so a release build only honours them when OTTO_DEV=1 is set. Debug builds always do.
#if DEBUG
    static bool Dev => true;
#else
    static bool Dev => Environment.GetEnvironmentVariable("OTTO_DEV") == "1";
#endif

    [STAThread]
    static void Main()
    {
        Migration.FromJarvis(); // used to be called Jarvis; carry its data over once
        // debug: Otto.exe --dump-ui → writes what the agent would "see" of the front window, no API calls
        if (Dev && Environment.GetCommandLineArgs().Contains("--dump-ui"))
        {
            Thread.Sleep(1500);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (text, count) = UiTree.Describe(p => p, CancellationToken.None);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-ui.txt"), $"{count} elements in {sw.ElapsedMilliseconds} ms\n{text}");
            return;
        }
        // debug: Otto.exe --bench → timings of the local per-step work, in %TEMP%\otto-bench.txt
        if (Dev && Environment.GetCommandLineArgs().Contains("--bench"))
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-bench.txt"), Desktop.Bench());
            return;
        }
        // debug: Otto.exe --api-test "prompt" → runs one request headless, log in %TEMP%\otto-api-test.txt
        var args = Environment.GetCommandLineArgs();
        int at = (Dev ? Array.IndexOf(args, "--api-test") : -1);
        if (at >= 0)
        {
            var log = new System.Text.StringBuilder();
            double cost = 0;
            int streamAt = 0, chunks = 0;
            long tokensUsed = 0;
            var a = new Agent
            {
                Confirm = q => { log.AppendLine("CONFIRM? " + q + " -> no"); return false; },
                OnText = t => log.AppendLine("Otto: " + t),
                OnStream = (t, isNew) =>
                {
                    if (isNew) { streamAt = log.Length; chunks = 0; }
                    log.Length = streamAt;
                    log.AppendLine($"Otto (streamed in {++chunks} chunks): {t}");
                },
                OnTool = t => log.AppendLine("TOOL: " + t),
                OnUsage = (usd, tokens, _) => { cost += usd ?? 0; tokensUsed += tokens; },
                OnControl = _ => { },
            };
            // --attach file goes with the first message (tests images and files)
            int ai = Array.IndexOf(args, "--attach");
            var attach = ai >= 0 ? new List<Attachment> { Attachment.FromFile(args[ai + 1]) } : null;
            // several prompts separated by " || " run as one conversation, to test memory across turns
            foreach (var prompt in (args.ElementAtOrDefault(at + 1) ?? "Say OK.").Split(" || "))
            {
                log.AppendLine("USER: " + prompt);
                try { a.RunAsync(prompt, CancellationToken.None, attach).GetAwaiter().GetResult(); }
                catch (Exception e) { log.AppendLine("ERROR: " + e.Message); }
                attach = null;
            }
            // history round trip: save, list, reload, then remove the test chat
            var testId = "test-" + ChatStore.NewId();
            ChatStore.Save(testId, a.Snapshot());
            var listed = ChatStore.List().FirstOrDefault(c => c.Id == testId);
            var reloaded = ChatStore.Load(testId);
            log.AppendLine($"HISTORY: title \"{listed?.Title}\", {reloaded?.Count ?? 0} messages reloaded, first turn text: \"{(reloaded?.Count > 0 ? Agent.TurnText(reloaded[0]!) : null)}\"");
            ChatStore.Delete(testId);
            log.AppendLine($"cost ${cost:0.0000} ({tokensUsed} tokens, {Providers.Current().Provider.Label}: {Providers.Current().Fast})");
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-api-test.txt"), log.ToString());
            return;
        }
        // Otto.exe --overlay-demo → shows the "in control" glow for 6 seconds with a few click ripples
        if (Dev && Environment.GetCommandLineArgs().Contains("--overlay-demo"))
        {
            ApplicationConfiguration.Initialize();
            ControlOverlay.Init();
            ControlOverlay.Show(true);
            var t = new System.Windows.Forms.Timer { Interval = 1000 };
            int ticks = 0;
            t.Tick += (_, _) =>
            {
                var s = Desktop.Screen;
                ControlOverlay.Pulse(new Point(s.X + s.Width * (ticks % 3 + 1) / 4, s.Y + s.Height / 2));
                if (++ticks == 6) Application.Exit();
            };
            t.Start();
            Application.Run();
            return;
        }
        // Otto.exe --list-models → the current provider's model list (no cost), in %TEMP%\otto-models.txt
        if (Dev && Environment.GetCommandLineArgs().Contains("--list-models"))
        {
            var c = Providers.Current();
            string outp;
            try { outp = string.Join("\n", Llm.ListModelsAsync(c.Provider, c.BaseUrl, c.Key).GetAwaiter().GetResult()); }
            catch (Exception e) { outp = "ERROR " + e.Message; }
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-models.txt"), outp);
            return;
        }
        // Otto.exe --transcribe file.wav → what the voice transcription makes of a recording, in %TEMP%\otto-transcript.txt
        int tr = (Dev ? Array.IndexOf(Environment.GetCommandLineArgs(), "--transcribe") : -1);
        if (tr >= 0)
        {
            string outp;
            try { outp = Llm.TranscribeAsync(Providers.Current(), File.ReadAllBytes(Environment.GetCommandLineArgs()[tr + 1]), CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception e) { outp = "ERROR " + e.Message; }
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-transcript.txt"), outp);
            return;
        }
        // Otto.exe --search "query" → what web_search returns (no AI cost), in %TEMP%\otto-search.txt
        int se = (Dev ? Array.IndexOf(Environment.GetCommandLineArgs(), "--search") : -1);
        if (se >= 0)
        {
            string outp;
            try { outp = Tools.Run("web_search", new System.Text.Json.Nodes.JsonObject { ["query"] = Environment.GetCommandLineArgs()[se + 1] }, _ => false, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception e) { outp = "ERROR " + e.Message; }
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-search.txt"), outp);
            return;
        }
        // Otto.exe --mail-test → the 5 newest inbox emails through the IMAP connector, in %TEMP%\otto-mail.txt
        if (Dev && Environment.GetCommandLineArgs().Contains("--mail-test"))
        {
            string outp;
            try { outp = Tools.Run("email_list", new System.Text.Json.Nodes.JsonObject { ["count"] = 5 }, _ => false, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception e) { outp = "ERROR " + e.Message; }
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-mail.txt"), outp);
            return;
        }
        // Otto.exe --click-test → drives tests\click-test-window.ps1 (start it first): checks the Buy-button backstop,
        // the password-box refusal, and how long each guarded click takes. Log in %TEMP%\otto-click-test.txt
        if (Dev && Environment.GetCommandLineArgs().Contains("--click-test"))
        {
            var log = new System.Text.StringBuilder();
            for (int i = 0; i < 50 && Win32.Title(Win32.Foreground()) != "Otto click test"; i++) Thread.Sleep(200);
            log.AppendLine("front window: " + Win32.Title(Win32.Foreground()));
            void Step(string what, System.Text.Json.Nodes.JsonObject step)
            {
                var asked = new List<string>();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string result;
                try
                {
                    result = Desktop.Run(new System.Text.Json.Nodes.JsonObject { ["observe"] = "none", ["steps"] = new System.Text.Json.Nodes.JsonArray { step } },
                        q => { asked.Add(q.Replace('\n', ' ')); return false; }, CancellationToken.None).GetAwaiter().GetResult().ToString();
                }
                catch (Exception e) { result = "ERROR " + e.Message + " @ " + string.Join(" | ", (e.StackTrace ?? "").Split('\n').Take(8).Select(l => l.Trim())); }
                log.AppendLine($"{what}: {sw.ElapsedMilliseconds} ms, asked: {(asked.Count == 0 ? "no" : string.Join(" / ", asked))}, result: {result.Clip(400)}");
                Thread.Sleep(300);
            }
            Step("click 'Add to cart'", new() { ["action"] = "click", ["name"] = "Add to cart" });
            Step("click 'Place your order'", new() { ["action"] = "click", ["name"] = "Place your order" });
            Step("click password box", new() { ["action"] = "click", ["name"] = "Password" });
            Step("type into password box", new() { ["action"] = "type", ["text"] = "hunter2" });
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-click-test.txt"), log.ToString());
            return;
        }
        // Otto.exe --update-test → check GitHub and install a newer release over this exe, log in %TEMP%\otto-update.txt
        if (Dev && Environment.GetCommandLineArgs().Contains("--update-test"))
        {
            string log;
            try
            {
                var r = Updater.CheckNowAsync().GetAwaiter().GetResult();
                if (r == null) log = $"no newer release than {Updater.Current}";
                else { Updater.InstallAsync(r).GetAwaiter().GetResult(); log = $"installed {r.Tag} over {Updater.Current}"; }
            }
            catch (Exception e) { log = "ERROR " + e.Message; }
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-update.txt"), log);
            return;
        }
        // release signing (maintainer only): --make-signing-key prints the public key; --sign-release file.zip writes file.zip.sig
        if (Dev && Environment.GetCommandLineArgs().Contains("--make-signing-key"))
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-public-key.txt"), Updater.Signing.MakeKey());
            return;
        }
        if (Dev && Environment.GetCommandLineArgs().Contains("--protect-signing-key"))
        {
            string outp;
            try { outp = Updater.Signing.Protect() ? "protected" : "already protected"; Environment.ExitCode = 0; }
            catch (Exception e) { outp = "ERROR " + e.Message; Environment.ExitCode = 1; }
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-protect-key.txt"), outp);
            return;
        }
        int sr = Dev ? Array.IndexOf(Environment.GetCommandLineArgs(), "--sign-release") : -1;
        if (sr >= 0)
        {
            try { Updater.Signing.Sign(Environment.GetCommandLineArgs()[sr + 1]); Environment.ExitCode = 0; }
            catch (Exception e) { File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-sign-error.txt"), e.Message); Environment.ExitCode = 1; }
            return;
        }
        // Otto.exe --settings → just the settings window
        if (Environment.GetCommandLineArgs().Contains("--settings"))
        {
            ApplicationConfiguration.Initialize();
            SettingsWindow.Show();
            return;
        }
        // Otto.exe --render-ui → the chat (hovering a wide bubble) and the history list drawn offscreen,
        // as %TEMP%\otto-ui-chat.png and otto-ui-history.png, for checking layout without the real panel
        if (Dev && Environment.GetCommandLineArgs().Contains("--render-ui"))
        {
            ApplicationConfiguration.Initialize();
            RenderUi.Run();
            return;
        }
        using var mutex = new Mutex(true, "Otto.SingleInstance", out bool first);
        bool updated = Environment.GetCommandLineArgs().Contains("--updated");
        // right after an update the previous copy is still closing: wait for it instead of quitting
        if (!first && updated && !WaitForMutex(mutex)) return;
        // started again while Otto is running: show the running one instead of silently doing nothing
        if (!first && !updated && !SingleInstance.TakeOver(mutex)) return;
        Updater.CleanUpAfterUpdate();
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}

/// Owns everything: tray icon, the slide-out panel, global hotkeys, the agent and the mic.
sealed class TrayApp : ApplicationContext
{
    const int KeyPanel = 1, KeyTalk = 2, KeyKill = 3;
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
    ToolStripMenuItem? updateItem;
    string chatId = ChatStore.NewId();
    bool costKnown = true, replySounded, toldFallback;
    volatile bool controlling;

    public TrayApp()
    {
        _ = panel.Handle; // create the handle now so background threads can Invoke onto it
        SingleInstance.Listen(panel, () => panel.ShowPanel());
        ControlOverlay.Init();

        agent = new Agent
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
                if (usd is double d) chatCost += d; else costKnown = false;
                // most tokens are the same instructions re-sent every step; say how many were cheap cache re-reads
                var reused = chatCached > 0 ? $", {chatCached * 100 / Math.Max(1, chatTokens)}% cached" : "";
                panel.SetCost(!costKnown ? $"{chatTokens / 1000.0:0.#}k tokens this chat{reused}"
                    : chatCost < 0.01 ? "under 1¢ this chat" : $"${chatCost:0.00} this chat");
            },
        };

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

        tray = new NotifyIcon
        {
            Icon = Icons.Tray(),
            Text = "Otto",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        tray.BalloonTipClicked += (_, _) => panel.ShowPanel();
        tray.MouseClick +=(_, e) => { if (e.Button == MouseButtons.Left) panel.Toggle(); };

        var failed = new List<string>();
        if (!keys.Register(KeyPanel, Hotkeys.Ctrl | Hotkeys.Shift, Keys.J)) failed.Add("Ctrl+Shift+J");
        if (!keys.Register(KeyTalk, Hotkeys.Ctrl | Hotkeys.Alt, Keys.J)) failed.Add("Ctrl+Alt+J");
        if (!keys.Register(KeyKill, Hotkeys.Ctrl | Hotkeys.Alt, Keys.End)) failed.Add("Ctrl+Alt+End");
        keys.Pressed += id =>
        {
            if (id == KeyPanel) panel.Toggle();
            else if (id == KeyTalk) ToggleMic();
            else if (id == KeyKill) Kill();
        };

        if (failed.Count > 0) panel.AddSystem("Another app already owns: " + string.Join(", ", failed));
        if (Providers.Problem(Providers.Current()) != null)
        {
            // first run: ask straight away instead of making them find the tray menu
            panel.BeginInvoke(() =>
            {
                SettingsWindow.Show();
                panel.AddSystem(Providers.Problem(Providers.Current()) ?? "All set. Press Ctrl+Shift+J any time to open me.");
            });
        }
        if (Environment.GetCommandLineArgs().Contains("--show")) { panel.Pinned = true; panel.ShowPanel(); }
    }

    ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip();
        m.Items.Add("Open", null, (_, _) => panel.ShowPanel());
        m.Items.Add("Settings (AI provider, key)…", null, (_, _) => SettingsWindow.Show());
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
        m.Items.Add("Quit", null, (_, _) => ExitThread());
        return m;
    }

    async void Run(string text)
    {
        if (cts != null) { panel.AddSystem("Still working on the last request. Stop it first (Ctrl+Alt+End) or wait."); return; }
        // Otto's own program file is being replaced: anything started now would run half old, half new
        if (installing) { panel.AddSystem("Updating Otto. Ask again in a few seconds, once it has restarted."); return; }
        lastReply = null;
        if (Providers.Problem(Providers.Current()) is string problem) { panel.AddSystem(problem); return; }
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

    static bool StartsWithWindows()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("Otto") != null;
    }

    static void SetStartWithWindows(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue("Otto", $"\"{Environment.ProcessPath}\"");
        else k.DeleteValue("Otto", false);
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
