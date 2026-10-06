using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;

namespace Otto;

/// Sees and drives the real desktop. Coordinates the model uses are in screenshot pixels;
/// they're scaled back to the real screen here.
static partial class Desktop
{
    // ~790 image tokens at 16:9 (1280 wide was ~1230). 'zoom' covers the cases where detail matters.
    const int ShotWidth = 1024;

    public static Rectangle Screen => System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
    static double Scale => Math.Min(1.0, ShotWidth / (double)Screen.Width);
    public static Size ShotSize => new((int)Math.Round(Screen.Width * Scale), (int)Math.Round(Screen.Height * Scale));

    public static JsonNode ToolDefinition() => JsonNode.Parse($$$"""
    {"name":"computer","description":"Use the screen ({{{ShotSize.Width}}}x{{{ShotSize.Height}}} px). Runs 'steps' in order, then reports the screen: by default a text list like [7] button \"Save\" @412,88 (click with \"element\":7).",
     "input_schema":{"type":"object","properties":{
       "observe":{"type":"string","enum":["ui","screenshot","none"],"description":"default ui"},
       "steps":{"type":"array","items":{"type":"object","properties":{
         "action":{"type":"string","enum":["screenshot","click","double_click","right_click","middle_click","move","drag","scroll","type","key","wait","zoom"],
           "description":"clicks/move: element or x,y. drag: to x2,y2. scroll: direction, amount. type: text. key: e.g. 'ctrl+s'. wait: seconds. zoom: box x,y,x2,y2 at full detail."},
         "element":{"type":"integer"},
         "name":{"type":"string","description":"control label (for routines)"},
         "x":{"type":"integer"},"y":{"type":"integer"},"x2":{"type":"integer"},"y2":{"type":"integer"},
         "direction":{"type":"string","enum":["up","down","left","right"]},
         "amount":{"type":"integer"},"text":{"type":"string"},"keys":{"type":"string"},"seconds":{"type":"number"}},
         "required":["action"]}},
       "confirm":{"type":"string","description":"Set ONLY for paying, signing, submitting a formal document, or permanent deletion: what will happen, e.g. 'Place the $40 order'. The user approves first."}},
      "required":["steps"]}}
    """)!;

    /// Models occasionally send an array argument as a JSON string ("[{...}]"); accept both.
    public static JsonArray? ArrayOf(JsonNode? n)
    {
        if (n is JsonArray a) return a;
        if (n is JsonValue v && v.TryGetValue<string>(out var s))
        {
            try { return JsonNode.Parse(s) as JsonArray; } catch (System.Text.Json.JsonException) { }
        }
        if (n is JsonObject o) return new JsonArray { o.DeepClone() }; // a single step not wrapped in a list
        return null;
    }

    public static string Describe(JsonNode input)
    {
        var steps = ArrayOf(input["steps"]);
        if (steps == null || steps.Count == 0) return "Looking at the screen";
        return string.Join(" · ", steps.Select(s => DescribeStep(s!)));
    }

    static string DescribeStep(JsonNode s)
    {
        string a = s["action"]?.GetValue<string>() ?? "?";
        string xy = s["name"] != null ? $" “{s["name"]}”" : s["element"] != null ? $" #{s["element"]}" : s["x"] != null ? $" at {s["x"]},{s["y"]}" : "";
        return a switch
        {
            "screenshot" => "Looking at the screen",
            "type" => $"Typing “{(s["text"]?.GetValue<string>() ?? "").Clip(50)}”",
            "key" => $"Pressing {s["keys"]}",
            "scroll" => $"Scrolling {s["direction"]}",
            "drag" => $"Dragging{xy} → {s["x2"]},{s["y2"]}",
            "wait" => $"Waiting {s["seconds"] ?? 1}s",
            "zoom" => "Zooming in",
            _ => $"{char.ToUpper(a[0])}{a[1..].Replace('_', ' ')}{xy}",
        };
    }

    public static async Task<JsonNode> Run(JsonNode input, Func<string, bool> confirm, CancellationToken ct)
    {
        var steps = ArrayOf(input["steps"]) ?? new JsonArray { new JsonObject { ["action"] = "screenshot" } };
        var key = input.ToJsonString();
        repeats = key == lastInput ? repeats + 1 : 0;
        lastInput = key;
        bool approved = false;
        if (NeedsApproval(input, steps) is string why)
        {
            if (!confirm(why)) return JsonValue.Create("User declined. Don't retry this; ask them what they'd like instead.")!;
            approved = true;
            await Task.Delay(250, ct); // let the panel slide out of the way again (its close animation is 150 ms)
        }
        // Backstop for when the model didn't flag a step: clicking a Buy / Place order / Delete permanently
        // button asks anyway. Checked on the control actually under the pointer, however the click was aimed.
        async Task<bool> MayClick(Point p)
        {
            if (approved || UiTree.LabelAt(p) is not string label || !Safety.IsConsequential(label)) return true;
            if (!confirm($"Click \"{label.Trim().Clip(80)}\"? It looks like it pays, signs, submits or deletes something.")) return false;
            approved = true;
            await Task.Delay(250, ct);
            return true;
        }
        const string Declined = "User declined that click. Don't retry it; ask them what they'd like instead.";

        Blocklist.CheckFront(); // a blocked app or site in front: don't even look at it
        bool acted = false;
        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i]!;
            int n = i + 1;
            string action = step["action"]?.GetValue<string>() ?? throw new ArgumentException($"step {n}: missing 'action'");
            int I(string k) => Num(step[k]) ?? throw new ArgumentException($"step {n} ('{action}') needs '{k}' as a number");
            Point P(string kx, string ky)
            {
                if (kx == "x" && step["name"] is JsonNode label)
                    return UiTree.FindByName(label.GetValue<string>());
                if (kx == "x" && step["element"] is JsonNode el)
                    return UiTree.TryGetElement(Num(el) ?? -1, out var c) ? c
                        : throw new ArgumentException($"step {n}: no element {el} in the last ui list; look again");
                return ToScreen(I(kx), I(ky));
            }
            if (acted && action != "wait") await Task.Delay(80, ct); // small gap so apps keep up with a batch
            // never type or click into Otto's own chat panel (it would send the text as a new request)
            if (action is not ("screenshot" or "wait" or "zoom" or "move") && FrontIsOtto())
                throw new InvalidOperationException($"step {n}: the front window is Otto's own panel, not the app you meant. Bring the target window to the front first (window tool 'focus').");

            switch (action)
            {
                case "screenshot": continue;
                case "zoom":
                    if (acted) await Settle(ct);
                    readings++;
                    return Capture(Rect(P("x", "y"), P("x2", "y2")), mark: false);
                case "move": MoveTo(P("x", "y")); break;
                case "click": { var p = P("x", "y"); if (!await MayClick(p)) return JsonValue.Create(Declined)!; Click(p, Btn.Left, 1); break; }
                case "double_click": { var p = P("x", "y"); if (!await MayClick(p)) return JsonValue.Create(Declined)!; Click(p, Btn.Left, 2); break; }
                case "right_click": Click(P("x", "y"), Btn.Right, 1); break;
                case "middle_click": Click(P("x", "y"), Btn.Middle, 1); break;
                case "drag": await Drag(P("x", "y"), P("x2", "y2"), ct); break;
                case "scroll":
                    if (step["x"] != null || step["element"] != null || step["name"] != null) MoveTo(P("x", "y"));
                    Scroll(step["direction"]?.GetValue<string>() ?? "down", Num(step["amount"]) ?? 3);
                    break;
                case "type":
                    if (IsCommandWindow(Win32.Foreground()) && step["text"]?.GetValue<string>() is string typed)
                    {
                        var ask = Tools.RiskyCommand.IsMatch(typed) ? $"Type this command into {Win32.Title(Win32.Foreground())}?\n\n{typed.Clip(400)}"
                                : Safety.AskIfUntrusted($"Type this into {Win32.Title(Win32.Foreground())}:\n\n{typed.Clip(400)}");
                        if (ask != null && !confirm(ask)) return JsonValue.Create(Declined)!;
                    }
                    if (UiTree.FocusIsPassword())
                        throw new InvalidOperationException($"step {n}: that's a password box. Otto never types passwords; ask the user to sign in themselves.");
                    await TypeText(step["text"]?.GetValue<string>() ?? throw new ArgumentException($"step {n}: 'type' needs 'text'"), ct);
                    break;
                case "key":
                    // Enter or Space on a focused Buy / Delete button clicks it just the same
                    if (!approved && (step["keys"]?.GetValue<string>() ?? "").Trim().ToLowerInvariant() is "enter" or "return" or "space"
                        && UiTree.FocusedLabel() is string focused && Safety.IsConsequential(focused))
                    {
                        if (!confirm($"Press {step["keys"]} on \"{focused.Trim().Clip(80)}\"? It looks like it pays, signs, submits or deletes something."))
                            return JsonValue.Create(Declined)!;
                        approved = true;
                    }
                    PressCombo(step["keys"]?.GetValue<string>() ?? throw new ArgumentException($"step {n}: 'key' needs 'keys'"));
                    break;
                case "wait":
                    await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(Seconds(step["seconds"]), 0.1, 10)), ct);
                    continue;
                default: throw new ArgumentException($"step {n}: unknown action {action}");
            }
            acted = true;
        }

        if (acted) await Settle(ct);
        var seen = Observe(input["observe"]?.GetValue<string>() ?? "ui", ct);
        if (repeats >= 2 && seen is JsonValue v && v.ToString().StartsWith(Unchanged))
            return JsonValue.Create(v + " You've made this exact call 3 times with no effect. Try something different: a keyboard shortcut, another control, a screenshot, or escalate.")!;
        return seen;
    }

    /// Wait until the screen stops changing instead of a fixed pause: instant apps return in ~0.2 s,
    /// slow ones (a page loading, a window opening) get up to 1.5 s, so it's both faster and more reliable.
    static async Task Settle(CancellationToken ct)
    {
        await Task.Delay(80, ct);
        var prev = Thumbnail();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1_400)
        {
            await Task.Delay(80, ct);
            var cur = Thumbnail();
            if (Difference(cur, prev) < 0.4) return;
            prev = cur;
        }
    }

    public static string Bench()
    {
        string T(string label, Action a, int n = 5)
        {
            a();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < n; i++) a();
            return $"{label}: {sw.Elapsed.TotalMilliseconds / n:0} ms\n";
        }
        return T("change check (thumbnail)", () => Thumbnail())
             + T("settle when screen is still", () => Settle(CancellationToken.None).Wait(), 3)
             + T("screenshot 1024px jpeg", () => Capture(Screen, mark: true))
             + T("read front window as text", () => UiTree.Describe(ToShot, CancellationToken.None), 3);
    }

    // any Otto process, not just this one: a headless --api-test run must not type into the user's own panel either
    static bool FrontIsOtto() => Win32.ProcessName(Win32.Foreground()) == "otto";

    static double Seconds(JsonNode? n) =>
        n is JsonValue v && (v.TryGetValue<double>(out var d) || v.TryGetValue<string>(out var s) && double.TryParse(s, out d)) && double.IsFinite(d) ? d : 1;

    internal static int? Num(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (int)Math.Round(d);
        if (v.TryGetValue<string>(out var s) && double.TryParse(s, out d)) return (int)Math.Round(d);
        return null;
    }

    internal const string Unchanged = "Nothing changed";

    /// How many of the newest screen readings the agent keeps in full (Agent.PruneObservations); older ones
    /// shrink to a stub. "Nothing changed" may only refer to a reading inside that window.
    internal const int KeptReadings = 2;

    /// A full screen reading: a UiTree listing or anything with a screenshot. Short results ("Done.",
    /// "Nothing changed: ...", errors) are not readings; they cost little and never replace one.
    internal static bool IsReading(JsonNode? content) =>
        content is JsonArray blocks ? blocks.Any(b => b?["type"]?.GetValue<string>() == "image")
        : content is JsonValue v && v.TryGetValue<string>(out var s) && s.Contains(UiTree.ListingMarker);

    static string? lastInput, lastUi;
    static byte[]? lastThumb;
    // readings: full readings returned this turn, counted the same way the agent counts them (IsReading)
    static int repeats, readings, lastUiAt, lastThumbAt;

    /// Called at the start of each user turn: earlier observations may no longer be in the history.
    public static void NewTurn() { lastInput = lastUi = null; lastThumb = null; repeats = readings = 0; }

    // "Nothing changed" is only safe if that earlier reading is still in the model's history.
    static bool Recent(int at) => readings - at < KeptReadings;

    /// What the model gets back. If the screen is the same as its last look (still in its history),
    /// a one-line note replaces the whole list or image.
    static JsonNode Observe(string observe, CancellationToken ct)
    {
        if (observe == "none") return JsonValue.Create("Done.")!;
        Blocklist.CheckFront(); // it may have navigated somewhere blocked: don't send that screen to the AI
        if (UiTree.ShowsOutsideContent(Win32.Foreground())) Safety.Saw("screen");
        if (observe == "ui")
        {
            var (text, count) = UiTree.Describe(ToShot, ct);
            if (count >= 3)
            {
                if (text == lastUi && Recent(lastUiAt)) return JsonValue.Create($"{Unchanged}: the front window's controls are exactly as in your last look.")!;
                lastUi = text;
                lastUiAt = ++readings;
                return JsonValue.Create(text)!;
            }
            // canvases, games and some old apps expose nothing useful: show the picture instead
            var shot = ScreenshotUnlessSame();
            if (shot is JsonArray arr) arr.Insert(0, new JsonObject { ["type"] = "text", ["text"] = text + "(Few readable controls here, so here's a screenshot.)" });
            return shot;
        }
        return ScreenshotUnlessSame();
    }

    static JsonNode ScreenshotUnlessSame()
    {
        var thumb = Thumbnail();
        if (lastThumb != null && Recent(lastThumbAt) && Difference(thumb, lastThumb) < 0.6)
            return JsonValue.Create($"{Unchanged}: the screen looks the same as your last screenshot.")!;
        lastThumb = thumb;
        lastThumbAt = ++readings;
        return Capture(Screen, mark: true);
    }

    /// 96x54 greyscale copy of the screen: enough to tell "something happened" from "nothing did".
    static byte[] Thumbnail()
    {
        var b = Screen;
        using var full = new Bitmap(b.Width, b.Height, PixelFormat.Format24bppRgb);
        try { using var g = Graphics.FromImage(full); g.CopyFromScreen(b.Location, Point.Empty, b.Size); }
        catch (System.ComponentModel.Win32Exception) { return Array.Empty<byte>(); } // secure desktop (UAC, lock screen)
        using var small = new Bitmap(96, 54, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear; // averages every pixel, so small changes still register
            g.DrawImage(full, 0, 0, 96, 54);
        }
        var px = new byte[96 * 54];
        var data = small.LockBits(new Rectangle(0, 0, 96, 54), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[data.Stride];
            for (int y = 0; y < 54; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                for (int x = 0; x < 96; x++) // BGR
                    px[y * 96 + x] = (byte)((row[x * 3 + 2] * 3 + row[x * 3 + 1] * 6 + row[x * 3]) / 10);
            }
        }
        finally { small.UnlockBits(data); }
        return px;
    }

    /// Mean absolute difference in grey levels (0-255) per thumbnail cell, but a small change (a ticked box, a
    /// new dialog) must still count, so any single cell moving more than 24 levels returns 99 ("different").
    /// The thresholds here and in its callers (0.4 between frames while settling, 0.6 for "same screenshot")
    /// are hand-tuned values whose origin isn't recorded (UNVALIDATED); a blinking caret or animated ad can exceed them.
    static double Difference(byte[] a, byte[] b)
    {
        if (a.Length == 0 || a.Length != b.Length) return 99;
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int d = Math.Abs(a[i] - b[i]);
            if (d > 24) return 99;
            sum += d;
        }
        return sum / a.Length;
    }

    static Point ToShot(Point p) =>
        new((int)Math.Round((p.X - Screen.X) * Scale), (int)Math.Round((p.Y - Screen.Y) * Scale));

    static readonly string[] Terminals =
    {
        "cmd", "powershell", "pwsh", "windowsterminal", "openconsole", "conhost", "wt", "mintty", "bash", "wsl",
        "alacritty", "wezterm-gui", "hyper", "tabby", "putty", "kitty",
    };

    /// A console or the Win+R box: typing there runs commands, so it gets the same checks as run_powershell.
    internal static bool IsCommandWindow(IntPtr h) =>
        Terminals.Contains(Win32.ProcessName(h)) || Win32.ProcessName(h) == "explorer" && Win32.Title(h) == "Run";

    /// The model flags consequential steps itself. The only hard backstop is permanent delete,
    /// which skips the Recycle Bin and can't be undone.
    static string? NeedsApproval(JsonNode input, JsonArray steps)
    {
        if (input["confirm"]?.GetValue<string>() is string said && said.Trim().Length > 0)
            return said.Trim();
        foreach (var s in steps)
        {
            var combo = (s?["keys"]?.GetValue<string>() ?? "").Replace(" ", "").ToLowerInvariant();
            if (combo is "shift+delete" or "shift+del") return "Permanently delete the selected item (skips the Recycle Bin)?";
        }
        return null;
    }

    static Rectangle Rect(Point a, Point b) =>
        Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X) + 1, Math.Max(a.Y, b.Y) + 1);

    static Point ToScreen(int x, int y)
    {
        var s = ShotSize;
        if (x < 0 || y < 0 || x >= s.Width || y >= s.Height)
            throw new ArgumentException($"{x},{y} is off the {s.Width}x{s.Height} screenshot.");
        return new Point(Screen.X + (int)Math.Round(x / Scale), Screen.Y + (int)Math.Round(y / Scale));
    }

    /// Grab part of the screen, shrunk to at most ShotWidth wide. The overlay is excluded from capture.
    static JsonArray Capture(Rectangle area, bool mark)
    {
        using var full = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
        try { using var g = Graphics.FromImage(full); g.CopyFromScreen(area.Location, Point.Empty, area.Size); }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException("Can't see the screen right now: a Windows security prompt (UAC) or the lock screen is up. Ask the user to deal with it.");
        }

        double k = Math.Min(1.0, ShotWidth / (double)area.Width);
        var size = new Size(Math.Max(1, (int)Math.Round(area.Width * k)), Math.Max(1, (int)Math.Round(area.Height * k)));
        using var small = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(full, 0, 0, size.Width, size.Height);
            if (mark)
            {
                // draw the mouse so the model knows where it is
                var c = Cursor.Position;
                float cx = (c.X - area.X) * (float)k, cy = (c.Y - area.Y) * (float)k;
                using var pen = new Pen(Color.Red, 2);
                g.DrawEllipse(pen, cx - 6, cy - 6, 12, 12);
            }
        }

        return new JsonArray
        {
            new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "image/jpeg", ["data"] = Convert.ToBase64String(Jpeg.Encode(small, 75)) },
            },
        };
    }
}
