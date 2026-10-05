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
    static bool DevFlag(string flag) => Dev && Args.Contains(flag);
    /// Where a developer switch is in the arguments (its value follows it), or -1.
    static int DevIndex(string flag) => Dev ? Array.IndexOf(Args, flag) : -1;

    /// Runs the switch Otto was started with, if any. True when one ran (and Otto should exit).
    public static bool Run()
    {
        // debug: Otto.exe --dump-ui → writes what the agent would "see" of the front window, no API calls
        if (DevFlag("--dump-ui"))
        {
            Thread.Sleep(1500);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (text, count) = UiTree.Describe(p => p, CancellationToken.None);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-ui.txt"), $"{count} elements in {sw.ElapsedMilliseconds} ms\n{text}");
            return true;
        }
        // debug: Otto.exe --bench → timings of the local per-step work, in %TEMP%\otto-bench.txt
        if (DevFlag("--bench"))
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-bench.txt"), Desktop.Bench());
            return true;
        }
        // debug: Otto.exe --api-test "prompt" → runs one request headless, log in %TEMP%\otto-api-test.txt
        int at = DevIndex("--api-test");
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
            return true;
        }
        // Otto.exe --overlay-demo → shows the "in control" glow for 6 seconds with a few click ripples
        if (DevFlag("--overlay-demo"))
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
            return true;
        }
        // Otto.exe --list-models → the current provider's model list (no cost), in %TEMP%\otto-models.txt
        if (DevFlag("--list-models"))
        {
            var c = Providers.Current();
            string outp;
            try { outp = string.Join("\n", Llm.ListModelsAsync(c.Provider, c.BaseUrl, c.Key).GetAwaiter().GetResult()); }
            catch (Exception e) { outp = "ERROR " + e.Message; }
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-models.txt"), outp);
            return true;
        }
        // Otto.exe --transcribe file.wav → what the voice transcription makes of a recording, in %TEMP%\otto-transcript.txt
        int tr = DevIndex("--transcribe");
        if (tr >= 0)
        {
            string outp;
            try { outp = Llm.TranscribeAsync(Providers.Current(), File.ReadAllBytes(Args[tr + 1]), CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception e) { outp = "ERROR " + e.Message; }
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-transcript.txt"), outp);
            return true;
        }
        // Otto.exe --search "query" → what web_search returns (no AI cost), in %TEMP%\otto-search.txt
        int se = DevIndex("--search");
        if (se >= 0)
        {
            string outp;
            try { outp = Tools.Run("web_search", new System.Text.Json.Nodes.JsonObject { ["query"] = Args[se + 1] }, _ => false, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception e) { outp = "ERROR " + e.Message; }
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-search.txt"), outp);
            return true;
        }
        // Otto.exe --mail-test → the 5 newest inbox emails through the IMAP connector, in %TEMP%\otto-mail.txt
        if (DevFlag("--mail-test"))
        {
            string outp;
            try { outp = Tools.Run("email_list", new System.Text.Json.Nodes.JsonObject { ["count"] = 5 }, _ => false, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception e) { outp = "ERROR " + e.Message; }
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-mail.txt"), outp);
            return true;
        }
        // Otto.exe --click-test → drives tests\click-test-window.ps1 (start it first): checks the Buy-button backstop,
        // the password-box refusal, and how long each guarded click takes. Log in %TEMP%\otto-click-test.txt
        if (DevFlag("--click-test"))
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
            return true;
        }
        // Otto.exe --task-test "request" → runs it the way a scheduled task runs (unattended, no screen), log in %TEMP%\otto-task-test.txt
        int tt = DevIndex("--task-test");
        if (tt >= 0)
        {
            var log = new System.Text.StringBuilder();
            var a = new Agent
            {
                Unattended = true,
                Confirm = q => { log.AppendLine("WOULD ASK (declined): " + q.Replace('\n', ' ')); return false; },
                OnText = t => log.AppendLine("Otto: " + t), Animate = () => false,
                OnTool = t => log.AppendLine("TOOL: " + t), OnUsage = (_, _, _) => { }, OnControl = on => { if (on) log.AppendLine("!! TOOK SCREEN CONTROL"); },
            };
            try { a.RunAsync("(Scheduled task. Nobody is watching: you can't use the screen, and anything that needs the user's OK will be declined, so finish what you can and say what's left.)\n\n" + Args[tt + 1], CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception e) { log.AppendLine("ERROR " + Agent.ErrorText(e)); }
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-task-test.txt"), log.ToString());
            return true;
        }
        // Otto.exe --ask-test → what Ctrl+Alt+A grabs from tests\ask-test-window.ps1, in %TEMP%\otto-ask-test.txt
        if (DevFlag("--ask-test"))
        {
            ApplicationConfiguration.Initialize();
            for (int i = 0; i < 50 && Win32.Title(Win32.Foreground()) != "Otto ask test"; i++) Thread.Sleep(200);
            string before = Clipboard.ContainsText() ? Clipboard.GetText() : "(no text)";
            var c = AskAbout.Grab();
            string after = Clipboard.ContainsText() ? Clipboard.GetText() : "(no text)";
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-ask-test.txt"),
                $"window: {c.Window}\ntext: {c.Text ?? "(none)"}\npicture: {(c.Picture != null ? c.Picture.Name : "(none)")}\nclipboard kept: {before == after}");
            return true;
        }
        // Otto.exe --update-test → check GitHub and install a newer release over this exe, log in %TEMP%\otto-update.txt
        if (DevFlag("--update-test"))
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
            return true;
        }
        // release signing (maintainer only): --make-signing-key prints the public key; --sign-release file.zip writes file.zip.sig
        if (DevFlag("--make-signing-key"))
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-public-key.txt"), Updater.Signing.MakeKey());
            return true;
        }
        if (DevFlag("--protect-signing-key"))
        {
            string outp;
            try { outp = Updater.Signing.Protect() ? "protected" : "already protected"; Environment.ExitCode = 0; }
            catch (Exception e) { outp = "ERROR " + e.Message; Environment.ExitCode = 1; }
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-protect-key.txt"), outp);
            return true;
        }
        int sr = DevIndex("--sign-release");
        if (sr >= 0)
        {
            try { Updater.Signing.Sign(Args[sr + 1]); Environment.ExitCode = 0; }
            catch (Exception e) { File.WriteAllText(Path.Combine(Path.GetTempPath(), "otto-sign-error.txt"), e.Message); Environment.ExitCode = 1; }
            return true;
        }
        // Otto.exe --settings-selftest → see SettingsWindow.SelfTest
        if (DevFlag("--settings-selftest"))
        {
            ApplicationConfiguration.Initialize();
            SettingsWindow.SelfTest();
            return true;
        }
        // Otto.exe --welcome → just the first-run walkthrough (doesn't change start-with-Windows)
        if (DevFlag("--welcome"))
        {
            ApplicationConfiguration.Initialize();
            Welcome.Show(SettingsWindow.Show, () => false, _ => { });
            return true;
        }
        // Otto.exe --settings → just the settings window
        if (Args.Contains("--settings"))
        {
            ApplicationConfiguration.Initialize();
            int si = Array.IndexOf(Args, "--settings");
            SettingsWindow.Show(Args.ElementAtOrDefault(si + 1)); // optional section name, e.g. "Spending"
            return true;
        }
        // Otto.exe --render-ui → the chat (hovering a wide bubble) and the history list drawn offscreen,
        // as %TEMP%\otto-ui-chat.png and otto-ui-history.png, for checking layout without the real panel
        if (DevFlag("--render-ui"))
        {
            ApplicationConfiguration.Initialize();
            RenderUi.Run();
            return true;
        }
        return false;
    }
}
