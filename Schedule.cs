using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Otto;

/// Reminders ("remind me at 5 to call Mum") and scheduled tasks ("every weekday at 8, summarise my unread
/// email"), kept in %LOCALAPPDATA%\Otto\schedule.json. Otto checks them while it runs; anything that came due
/// while the PC was off fires when Otto next starts, once, instead of being silently skipped.
static class Schedule
{
    public sealed record Item(string Id, string Kind, string Text, DateTime Next, string Repeat, DateTime Created);

    public static readonly string[] Repeats = { "none", "daily", "weekdays", "weekly", "monthly" };
    static string FilePath => Path.Combine(Paths.Data, "schedule.json");
    static readonly object gate = new();

    public static List<Item> All()
    {
        lock (gate)
        {
            if (!File.Exists(FilePath)) return new();
            try { return JsonSerializer.Deserialize<List<Item>>(File.ReadAllText(FilePath)) ?? new(); }
            catch (JsonException) { return new(); }
        }
    }

    static void Save(List<Item> items)
    {
        lock (gate)
        {
            Directory.CreateDirectory(Paths.Data);
            SafeFile.WriteAllText(FilePath, JsonSerializer.Serialize(items.OrderBy(i => i.Next), new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public static Item Add(string kind, string text, DateTime when, string repeat, DateTime? now = null)
    {
        var t = now ?? DateTime.Now;
        if (kind is not ("reminder" or "task")) throw new ArgumentException("kind must be 'reminder' or 'task'");
        if (!Repeats.Contains(repeat)) throw new ArgumentException("repeat must be one of: " + string.Join(", ", Repeats));
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("nothing to remind or do");
        if (when <= t)
        {
            if (repeat == "none") throw new ArgumentException($"{when:ddd d MMM h:mm tt} has already passed; pick a time after {t:ddd d MMM h:mm tt}.");
            when = NextAfter(when, repeat, t);
        }
        var item = new Item(Guid.NewGuid().ToString("N")[..8], kind, text.Trim(), when, repeat, t);
        lock (gate) { var all = All(); all.Add(item); Save(all); }
        return item;
    }

    public static bool Remove(string id)
    {
        lock (gate)
        {
            var all = All();
            int n = all.RemoveAll(i => i.Id == id.Trim());
            if (n > 0) Save(all);
            return n > 0;
        }
    }

    /// Items due at 'now'. Each is moved on to its next time (or removed) before it's returned, so a crash or
    /// quit while it runs can't fire it twice.
    public static List<Item> TakeDue(DateTime? now = null)
    {
        var t = now ?? DateTime.Now;
        lock (gate)
        {
            var all = All();
            var due = all.Where(i => i.Next <= t).ToList();
            if (due.Count == 0) return due;
            all.RemoveAll(i => i.Next <= t);
            foreach (var i in due.Where(i => i.Repeat != "none"))
                all.Add(i with { Next = NextAfter(i.Next, i.Repeat, t) });
            Save(all);
            return due;
        }
    }

    /// The first time after 'now' in the series that started at 'from' (skipping any missed while off).
    internal static DateTime NextAfter(DateTime from, string repeat, DateTime now)
    {
        var next = from;
        for (int guard = 0; next <= now && guard < 100_000; guard++)
        {
            next = repeat switch
            {
                "daily" => next.AddDays(1),
                "weekly" => next.AddDays(7),
                "monthly" => from.AddMonths(MonthsBetween(from, next) + 1), // same day of month where the month has it
                "weekdays" => NextWeekday(next),
                _ => throw new ArgumentException("not repeating"),
            };
        }
        return next;
    }

    static int MonthsBetween(DateTime a, DateTime b) => (b.Year - a.Year) * 12 + b.Month - a.Month;

    static DateTime NextWeekday(DateTime t)
    {
        do t = t.AddDays(1); while (t.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        return t;
    }

    public static string Describe(Item i) =>
        $"{(i.Kind == "task" ? "Task" : "Reminder")}: {i.Text} ({When(i)})";

    public static string When(Item i)
    {
        var when = i.Next.Date == DateTime.Today ? $"today {i.Next:h:mm tt}"
                 : i.Next.Date == DateTime.Today.AddDays(1) ? $"tomorrow {i.Next:h:mm tt}"
                 : $"{i.Next:ddd d MMM h:mm tt}";
        return i.Repeat switch
        {
            "daily" => $"every day at {i.Next:h:mm tt}",
            "weekdays" => $"weekdays at {i.Next:h:mm tt}",
            "weekly" => $"every {i.Next:dddd} at {i.Next:h:mm tt}",
            "monthly" => $"monthly on the {i.Next.Day}{Ordinal(i.Next.Day)} at {i.Next:h:mm tt}",
            _ => when,
        };
    }

    static string Ordinal(int d) => d is 11 or 12 or 13 ? "th" : (d % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };

    // ---------------- tools ----------------

    public static JsonNode ToolDefinitions() => JsonNode.Parse("""
    [
      {"name":"schedule","description":"Set a reminder (a notification at a time) or a task (a request you run later on your own, unattended, without the screen). Work out 'when' from today's date and time.",
       "input_schema":{"type":"object","properties":{
         "kind":{"type":"string","enum":["reminder","task"]},
         "text":{"type":"string","description":"reminder: what to remind them; task: the request to carry out, written as the user would ask it"},
         "when":{"type":"string","description":"local date and time, e.g. 2026-10-06T17:00"},
         "repeat":{"type":"string","enum":["none","daily","weekdays","weekly","monthly"]}},
        "required":["kind","text","when"]}},
      {"name":"schedule_list","description":"List the reminders and scheduled tasks.","input_schema":{"type":"object","properties":{}}},
      {"name":"schedule_cancel","description":"Cancel a reminder or scheduled task by id (from schedule_list).",
       "input_schema":{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}}
    ]
    """)!;

    public static bool Handles(string tool) => tool.StartsWith("schedule");

    public static string Run(string name, JsonNode input)
    {
        switch (name)
        {
            case "schedule":
            {
                var whenText = Tools.S(input, "when");
                if (!DateTime.TryParse(whenText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var when))
                    throw new ArgumentException($"'{whenText}' isn't a date and time; use e.g. 2026-10-06T17:00");
                var item = Add(Tools.S(input, "kind"), Tools.S(input, "text"), when, input["repeat"]?.GetValue<string>() ?? "none");
                return $"Scheduled (id {item.Id}). {Describe(item)}.";
            }
            case "schedule_list":
            {
                var all = All();
                return all.Count == 0 ? "Nothing scheduled." : string.Join("\n", all.Select(i => $"[{i.Id}] {Describe(i)}"));
            }
            case "schedule_cancel":
                return Remove(Tools.S(input, "id")) ? "Cancelled." : "No scheduled item with that id.";
        }
        throw new ArgumentException($"Unknown tool {name}");
    }
}
