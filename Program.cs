using Microsoft.Win32;

namespace Otto;

static class Program
{
    [STAThread]
    static void Main()
    {
        Migration.FromJarvis(); // used to be called Jarvis; carry its data over once
        // debug: Otto.exe --dump-ui → writes what the agent would "see" of the front window, no API calls
        if (Environment.GetCommandLineArgs().Contains("--dump-ui"))
        {
            Thread.Sleep(1500);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (text, count) = UiTree.Describe(p => p, CancellationToken.None);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-ui.txt"), $"{count} elements in {sw.ElapsedMilliseconds} ms\n{text}");
            return;
        }
        // debug: Otto.exe --bench → timings of the local per-step work, in %TEMP%\otto-bench.txt
        if (Environment.GetCommandLineArgs().Contains("--bench"))
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-bench.txt"), Desktop.Bench());
            return;
        }
        // debug: Otto.exe --api-test "prompt" → runs one request headless, log in %TEMP%\otto-api-test.txt
        var args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, "--api-test");
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
            // several prompts separated by " || " run as one conversation, to test memory across turns
            foreach (var prompt in (args.ElementAtOrDefault(at + 1) ?? "Say OK.").Split(" || "))
            {
                log.AppendLine("USER: " + prompt);
                try { a.RunAsync(prompt, CancellationToken.None).GetAwaiter().GetResult(); }
                catch (Exception e) { log.AppendLine("ERROR: " + e.Message); }
            }
            log.AppendLine($"cost ${cost:0.0000} ({tokensUsed} tokens, {Providers.Current().Provider.Label}: {Providers.Current().Fast})");
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-api-test.txt"), log.ToString());
            return;
        }
        // Otto.exe --overlay-demo → shows the "in control" glow for 6 seconds with a few click ripples
        if (Environment.GetCommandLineArgs().Contains("--overlay-demo"))
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
        if (Environment.GetCommandLineArgs().Contains("--list-models"))
        {
            var c = Providers.Current();
            string outp;
            try { outp = string.Join("\n", Llm.ListModelsAsync(c.Provider, c.BaseUrl, c.Key).GetAwaiter().GetResult()); }
            catch (Exception e) { outp = "ERROR " + e.Message; }
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-models.txt"), outp);
            return;
        }
        // Otto.exe --settings → just the settings window
        if (Environment.GetCommandLineArgs().Contains("--settings"))
        {
            ApplicationConfiguration.Initialize();
            SettingsWindow.Show();
            return;
        }
        using var mutex = new Mutex(true, "Otto.SingleInstance", out bool first);
        if (!first) return;
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
    bool costKnown = true, replySounded, toldFallback;
    volatile bool controlling;

    public TrayApp()
    {
        _ = panel.Handle; // create the handle now so background threads can Invoke onto it
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
        panel.ClearRequested += () => { if (cts == null) { agent.Reset(); panel.ClearLog(); chatCost = 0; chatTokens = 0; chatCached = 0; costKnown = true; panel.SetCost(""); } };
        panel.SettingsRequested += SettingsWindow.Show;

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
        var sounds = new ToolStripMenuItem("Sounds") { Checked = Sfx.Enabled };
        sounds.Click += (_, _) => { Sfx.Enabled = !sounds.Checked; sounds.Checked = Sfx.Enabled; };
        m.Items.Add(sounds);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Quit", null, (_, _) => ExitThread());
        return m;
    }

    static string Shorten(string s) => s.Length <= 200 ? s : s[..200] + "…";

    async void Run(string text)
    {
        if (cts != null) { panel.AddSystem("Still working on the last request. Stop it first (Ctrl+Alt+End) or wait."); return; }
        lastReply = null;
        if (Providers.Problem(Providers.Current()) is string problem) { panel.AddSystem(problem); return; }
        panel.AddUser(text);
        Speaker.Stop();
        Sfx.Send();
        replySounded = false;
        cts = new CancellationTokenSource();
        panel.SetBusy(true);
        try
        {
            await Task.Run(() => agent.RunAsync(text, cts.Token));
            if (Prefs.ReadAloud && lastReply != null) Speaker.Say(lastReply);
            // the panel auto-hides when you click away, so tell you when the job's done
            if (!panel.Visible)
                tray.ShowBalloonTip(5000, "Otto is done", Shorten(lastReply ?? "Finished."), ToolTipIcon.None);
        }
        catch (OperationCanceledException) { panel.AddSystem("Stopped."); }
        catch (Exception e) { panel.AddSystem("Error: " + e.Message); Sfx.Error(); }
        finally
        {
            var done = cts;
            cts = null; // clear first, so a Stop pressed right now can't hit a disposed token
            done.Dispose();
            panel.SetBusy(false);
        }
    }

    async void ToggleMic()
    {
        try
        {
            if (!voice.Listening)
            {
                panel.ShowPanel();
                Speaker.Stop();
                await voice.StartAsync();
                panel.SetListening(true);
                if (voice.FallbackReason is string why && !toldFallback) { toldFallback = true; panel.AddSystem(why); }
                return;
            }
            panel.SetListening(false);
            var said = await voice.StopAsync();
            if (string.IsNullOrWhiteSpace(said)) panel.AddSystem("Didn't catch anything.");
            else Run(said);
        }
        catch (Exception e)
        {
            panel.SetListening(false);
            panel.AddSystem("Mic error: " + e.Message);
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
