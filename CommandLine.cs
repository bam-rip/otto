using System.Text.Json.Nodes;

namespace Otto;

/// Otto.exe's command-line switches: the developer tools (--api-test, --render-ui, --search, ...) and --settings.
/// Each writes what it found to %TEMP% and exits, so Otto's normal start never happens for them.
static class CommandLine
{
    /// The developer switches run tools with no window and no one watching, so a release build only honours them
    /// when OTTO_DEV=1 is set. Debug builds always do.
#if DEBUG
    internal static bool Dev => true;
#else
    internal static bool Dev => Environment.GetEnvironmentVariable("OTTO_DEV") == "1";
#endif

    static string[] Args => Environment.GetCommandLineArgs();

    /// Every switch, in the order they're checked; true = developer only. 'at' is where the switch is in the
    /// arguments, so a value after it is Args[at + 1].
    static readonly (string Flag, bool DevOnly, Action<int> Run)[] Commands =
    {
        ("--dump-ui", true, DumpUi),
        ("--bench", true, Bench),
        ("--api-test", true, ApiTest),
        ("--overlay-demo", true, OverlayDemo),
        ("--list-models", true, ListModels),
        ("--transcribe", true, Transcribe),
        ("--search", true, Search),
        ("--mail-test", true, MailTest),
        ("--click-test", true, ClickTest),
        ("--task-test", true, TaskTest),
        ("--ask-test", true, AskTest),
        ("--update-test", true, UpdateTest),
        ("--make-signing-key", true, MakeSigningKey),
        ("--protect-signing-key", true, ProtectSigningKey),
        ("--sign-release", true, SignRelease),
        ("--settings-selftest", true, SettingsSelftest),
        ("--welcome", true, WelcomeTour),
        ("--settings", false, Settings),
        ("--render-ui", true, RenderPanel),
    };

    /// Runs the switch Otto was started with, if any. True when one ran (and Otto should exit).
    public static bool Run()
    {
        foreach (var (flag, devOnly, run) in Commands)
        {
            int at = Array.IndexOf(Args, flag);
            if (at < 0 || devOnly && !Dev) continue;
            run(at);
            return true;
        }
        return false;
    }

    /// Otto.exe --dump-ui → writes what the agent would "see" of the front window, no API calls
    static void DumpUi(int at)
    {
        Thread.Sleep(1500);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (text, count) = UiTree.Describe(p => p, CancellationToken.None);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-ui.txt"), $"{count} elements in {sw.ElapsedMilliseconds} ms\n{text}");
    }

    /// Otto.exe --bench → timings of the local per-step work, in %TEMP%\otto-bench.txt
    static void Bench(int at)
    {
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-bench.txt"), Desktop.Bench());
    }

    /// Otto.exe --api-test "prompt" → runs one request headless, log in %TEMP%\otto-api-test.txt
    static void ApiTest(int at)
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
        int ai = Array.IndexOf(Args, "--attach");
        var attach = ai >= 0 ? new List<Attachment> { Attachment.FromFile(Args[ai + 1]) } : null;
        // several prompts separated by " || " run as one conversation, to test memory across turns
        foreach (var prompt in (Args.ElementAtOrDefault(at + 1) ?? "Say OK.").Split(" || "))
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
    }

    /// Otto.exe --overlay-demo → shows the "in control" glow for 6 seconds with a few click ripples
    static void OverlayDemo(int at)
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
    }

    /// Otto.exe --list-models → the current provider's model list (no cost), in %TEMP%\otto-models.txt
    static void ListModels(int at)
    {
        var c = Providers.Current();
        string outp;
        try { outp = string.Join("\n", Llm.ListModelsAsync(c.Provider, c.BaseUrl, c.Key).GetAwaiter().GetResult()); }
        catch (Exception e) { outp = "ERROR " + e.Message; }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-models.txt"), outp);
    }

    /// Otto.exe --transcribe file.wav → what the voice transcription makes of a recording, in %TEMP%\otto-transcript.txt
    static void Transcribe(int at)
    {
        string outp;
        try { outp = Llm.TranscribeAsync(Providers.Current(), File.ReadAllBytes(Args[at + 1]), CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception e) { outp = "ERROR " + e.Message; }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-transcript.txt"), outp);
    }

    /// Otto.exe --search "query" → what web_search returns (no AI cost), in %TEMP%\otto-search.txt
    static void Search(int at)
    {
        string outp;
        try { outp = Tools.Run("web_search", new System.Text.Json.Nodes.JsonObject { ["query"] = Args[at + 1] }, _ => false, CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception e) { outp = "ERROR " + e.Message; }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-search.txt"), outp);
    }

    /// Otto.exe --mail-test → the 5 newest inbox emails through the IMAP connector, in %TEMP%\otto-mail.txt
    static void MailTest(int at)
    {
        string outp;
        try { outp = Tools.Run("email_list", new System.Text.Json.Nodes.JsonObject { ["count"] = 5 }, _ => false, CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception e) { outp = "ERROR " + e.Message; }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-mail.txt"), outp);
    }

    /// Otto.exe --click-test → drives tests\click-test-window.ps1 (start it first): checks the Buy-button backstop,
    /// the password-box refusal, and how long each guarded click takes. Log in %TEMP%\otto-click-test.txt
    static void ClickTest(int at)
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
    }

    /// Otto.exe --task-test "request" → runs it the way a scheduled task runs (unattended, no screen), log in %TEMP%\otto-task-test.txt
    static void TaskTest(int at)
    {
        var log = new System.Text.StringBuilder();
        var a = new Agent
        {
            Unattended = true,
            Confirm = q => { log.AppendLine("WOULD ASK (declined): " + q.Replace('\n', ' ')); return false; },
            OnText = t => log.AppendLine("Otto: " + t), Animate = () => false,
            OnTool = t => log.AppendLine("TOOL: " + t), OnUsage = (_, _, _) => { }, OnControl = on => { if (on) log.AppendLine("!! TOOK SCREEN CONTROL"); },
        };
        try { a.RunAsync("(Scheduled task. Nobody is watching: you can't use the screen, and anything that needs the user's OK will be declined, so finish what you can and say what's left.)\n\n" + Args[at + 1], CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception e) { log.AppendLine("ERROR " + Agent.ErrorText(e)); }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-task-test.txt"), log.ToString());
    }

    /// Otto.exe --ask-test → what Ctrl+Alt+A grabs from tests\ask-test-window.ps1, in %TEMP%\otto-ask-test.txt
    static void AskTest(int at)
    {
        ApplicationConfiguration.Initialize();
        for (int i = 0; i < 50 && Win32.Title(Win32.Foreground()) != "Otto ask test"; i++) Thread.Sleep(200);
        string before = Clipboard.ContainsText() ? Clipboard.GetText() : "(no text)";
        var c = AskAbout.Grab();
        string after = Clipboard.ContainsText() ? Clipboard.GetText() : "(no text)";
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-ask-test.txt"),
            $"window: {c.Window}\ntext: {c.Text ?? "(none)"}\npicture: {(c.Picture != null ? c.Picture.Name : "(none)")}\nclipboard kept: {before == after}");
    }

    /// Otto.exe --update-test → check GitHub and install a newer release over this exe, log in %TEMP%\otto-update.txt
    static void UpdateTest(int at)
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
    }

    /// release signing (maintainer only): --make-signing-key prints the public key; --sign-release file.zip writes file.zip.sig
    static void MakeSigningKey(int at)
    {
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-public-key.txt"), Updater.Signing.MakeKey());
    }

    /// --protect-signing-key
    static void ProtectSigningKey(int at)
    {
        string outp;
        try { outp = Updater.Signing.Protect() ? "protected" : "already protected"; Environment.ExitCode = 0; }
        catch (Exception e) { outp = "ERROR " + e.Message; Environment.ExitCode = 1; }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-protect-key.txt"), outp);
    }

    /// --sign-release
    static void SignRelease(int at)
    {
        try { Updater.Signing.Sign(Args[at + 1]); Environment.ExitCode = 0; }
        catch (Exception e) { File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-sign-error.txt"), e.Message); Environment.ExitCode = 1; }
    }

    /// Otto.exe --settings-selftest → see SettingsWindow.SelfTest
    static void SettingsSelftest(int at)
    {
        ApplicationConfiguration.Initialize();
        SettingsWindow.SelfTest();
    }

    /// Otto.exe --welcome → just the first-run walkthrough (doesn't change start-with-Windows)
    static void WelcomeTour(int at)
    {
        ApplicationConfiguration.Initialize();
        Welcome.Show(SettingsWindow.Show, () => false, _ => { });
    }

    /// Otto.exe --settings → just the settings window
    static void Settings(int at)
    {
        ApplicationConfiguration.Initialize();
        int si = Array.IndexOf(Args, "--settings");
        SettingsWindow.Show(Args.ElementAtOrDefault(si + 1)); // optional section name, e.g. "Spending"
    }

    /// Otto.exe --render-ui → the chat (hovering a wide bubble) and the history list drawn offscreen,
    /// as %TEMP%\otto-ui-chat.png and otto-ui-history.png, for checking layout without the real panel
    static void RenderPanel(int at)
    {
        ApplicationConfiguration.Initialize();
        RenderUi.Run();
    }
}
