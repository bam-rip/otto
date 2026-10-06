using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Otto;

/// Read-only calendar from a private iCal link (Google Calendar: Settings → your calendar → "Secret address in
/// iCal format"; Apple and Outlook.com have the same). No sign-in and no app registration. The link is a
/// secret (anyone with it can read the calendar), so it's kept in Credential Manager like a key.
static class Calendar
{
    const string LinkTarget = "Otto:ical";
    static readonly HttpClient Http = new(Safety.PublicOnlyHandler()) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 20_000_000 };

    public static string? Link => KeyStore.ApiKey(LinkTarget, null);
    public static bool Connected => Link != null;

    public static void SetLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) { KeyStore.Delete(LinkTarget); return; }
        KeyStore.Save(Normalize(url), LinkTarget);
    }

    /// webcal:// becomes https://; anything that isn't a secure web address is refused.
    internal static string Normalize(string url)
    {
        url = url.Trim();
        if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url["webcal://".Length..];
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https")
            throw new ArgumentException("That doesn't look like a calendar link (it should start with https:// or webcal://).");
        return url;
    }

    public static JsonNode ToolDefinitions() => JsonNode.Parse("""
    [
      {"name":"calendar_list","description":"List the user's calendar events in a date range (default: today and the next 7 days). Read-only.",
       "input_schema":{"type":"object","properties":{"from":{"type":"string","description":"date or date-time, e.g. 2026-10-02"},"days":{"type":"integer"}}}}
    ]
    """)!;

    public static async Task<string> List(JsonNode input, CancellationToken ct)
    {
        var from = input["from"]?.GetValue<string>() is string f && DateTime.TryParse(f, out var d) ? d.Date : DateTime.Today;
        int days = Math.Clamp(Tools.Int(input, "days", 7), 1, 62);
        var ics = await Fetch(ct);
        var events = Occurrences(Parse(ics), from, from.AddDays(days)).ToList();
        if (events.Count == 0) return $"Nothing on the calendar from {from:ddd d MMM} for {days} days.";
        var sb = new StringBuilder();
        foreach (var e in events.Take(60))
        {
            var when = e.AllDay ? $"{e.Start:ddd d MMM} (all day)" : $"{e.Start:ddd d MMM h:mm tt}-{e.End:h:mm tt}";
            sb.AppendLine($"{when}: {e.Summary}{(e.Location.Length > 0 ? $" @ {e.Location}" : "")}");
        }
        return sb.ToString();
    }

    static async Task<string> Fetch(CancellationToken ct)
    {
        var link = Link ?? throw new InvalidOperationException("No calendar connected. Add your calendar's iCal link in Otto's settings.");
        using var res = await Http.GetAsync(link, ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"The calendar link didn't work ({(int)res.StatusCode}). It may have been reset; copy it again from your calendar's settings.");
        return await res.Content.ReadAsStringAsync(ct);
    }

    // ---------------- iCal ----------------

    internal sealed record Event(string Uid, string Summary, string Location, DateTime Start, DateTime End, bool AllDay,
                                 string? Rule, HashSet<DateTime> Except, DateTime? RecurrenceId);

    internal sealed record Occurrence(string Summary, string Location, DateTime Start, DateTime End, bool AllDay);

    /// VEVENTs from an .ics file, times converted to this PC's local time.
    internal static List<Event> Parse(string ics)
    {
        // lines that start with a space or tab continue the previous one
        var lines = new List<string>();
        foreach (var raw in ics.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t') && lines.Count > 0) lines[^1] += raw[1..];
            else lines.Add(raw);
        }
        var events = new List<Event>();
        Dictionary<string, (string param, string value)>? cur = null;
        var except = new HashSet<DateTime>();
        foreach (var line in lines)
        {
            if (line == "BEGIN:VEVENT") { cur = new(); except = new(); continue; }
            if (line == "END:VEVENT" && cur != null)
            {
                // one event with a date nobody can read is skipped, not the whole calendar
                try { AddEvent(); } catch (Exception e) when (e is FormatException or ArgumentException or OverflowException) { }
                cur = null;
                continue;
            }
            void AddEvent()
            {
                if (cur.TryGetValue("DTSTART", out var ds))
                {
                    var (start, allDay) = Time(ds.param, ds.value);
                    DateTime end;
                    if (cur.TryGetValue("DTEND", out var de)) end = Time(de.param, de.value).Item1;
                    else if (cur.TryGetValue("DURATION", out var du)) end = start + Duration(du.value);
                    else end = allDay ? start.AddDays(1) : start;
                    DateTime? recId = cur.TryGetValue("RECURRENCE-ID", out var ri) ? Time(ri.param, ri.value).Item1 : null;
                    if (!(cur.TryGetValue("STATUS", out var st) && st.value == "CANCELLED" && recId == null))
                        events.Add(new Event(cur.GetValueOrDefault("UID").value ?? "", Text(cur.GetValueOrDefault("SUMMARY").value ?? "(no title)"),
                            Text(cur.GetValueOrDefault("LOCATION").value ?? ""), start, end, allDay,
                            cur.TryGetValue("RRULE", out var rr) ? rr.value : null, except, recId));
                }
            }
            if (cur == null) continue;
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var head = line[..colon];
            int semi = head.IndexOf(';');
            var name = (semi < 0 ? head : head[..semi]).ToUpperInvariant();
            var param = semi < 0 ? "" : head[(semi + 1)..];
            var value = line[(colon + 1)..];
            if (name == "EXDATE")
                foreach (var v in value.Split(','))
                    try { except.Add(Time(param, v).Item1); } catch (FormatException) { }
            else cur.TryAdd(name, (param, value));
        }
        return events;
    }

    /// Every occurrence overlapping [from, to), repeating events expanded, sorted by start.
    internal static IEnumerable<Occurrence> Occurrences(List<Event> events, DateTime from, DateTime to)
    {
        // edited single occurrences of a repeating event replace the regular one at that time
        var moved = events.Where(e => e.RecurrenceId != null).Select(e => (e.Uid, e.RecurrenceId!.Value)).ToHashSet();
        var all = new List<Occurrence>();
        foreach (var e in events)
        {
            var length = e.End - e.Start;
            IEnumerable<DateTime> starts = e.Rule == null || e.RecurrenceId != null ? new[] { e.Start } : Expand(e.Start, e.Rule, to, from);
            foreach (var s in starts)
            {
                if (e.Rule != null && e.RecurrenceId == null && (e.Except.Contains(s) || moved.Contains((e.Uid, s)))) continue;
                if (s < to && s + length > from || s >= from && s < to)
                    all.Add(new Occurrence(e.Summary, e.Location, s, s + length, e.AllDay));
            }
        }
        return all.OrderBy(o => o.Start);
    }

    /// The start times a repeat rule produces, up to 'until'. Covers what calendars actually use:
    /// DAILY/WEEKLY/MONTHLY/YEARLY with INTERVAL, COUNT, UNTIL and BYDAY.
    internal static IEnumerable<DateTime> Expand(DateTime start, string rule, DateTime until, DateTime? from = null)
    {
        var parts = rule.Split(';').Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
                        .ToDictionary(p => p[0].ToUpperInvariant(), p => p[1]);
        var freq = parts.GetValueOrDefault("FREQ", "DAILY");
        int interval = int.TryParse(parts.GetValueOrDefault("INTERVAL"), out var iv) && iv > 0 ? iv : 1;
        int? count = int.TryParse(parts.GetValueOrDefault("COUNT"), out var c) ? c : null;
        if (parts.TryGetValue("UNTIL", out var u)) { var end = Time("", u).Item1; if (end < until) until = end.AddSeconds(1); }
        var byDay = parts.TryGetValue("BYDAY", out var bd)
            ? bd.Split(',').Where(x => x.Length >= 2).Select(x => Day(x[^2..])).Where(x => x != null).Select(x => x!.Value).ToHashSet() : null;
        if (byDay?.Count == 0) byDay = null; // "BYDAY=" with no days: repeat on the start day, as if it were absent

        // a daily or weekly repeat from years ago jumps straight to the weeks asked about (when nothing is being
        // counted), instead of walking every day since it began and running out of steps before reaching today
        int first = 0;
        if (count == null && from > start && freq is "DAILY" or "WEEKLY")
            first = Math.Max(0, (int)((from.Value - start).TotalDays / (interval * (freq == "WEEKLY" ? 7 : 1))) - 1);
        int made = 0;
        for (int step = first; step < first + 5000; step++)
        {
            DateTime period = freq switch
            {
                "WEEKLY" => start.AddDays(7 * interval * step),
                "MONTHLY" => start.AddMonths(interval * step),
                "YEARLY" => start.AddYears(interval * step),
                _ => start.AddDays(interval * step),
            };
            if (period > until) yield break;
            IEnumerable<DateTime> inPeriod = new[] { period };
            if (freq == "WEEKLY" && byDay != null)
            {
                // the days of this week (weeks start Monday, as calendars write them) at the event's time of day
                var monday = period.Date.AddDays(-(((int)period.DayOfWeek + 6) % 7)) + period.TimeOfDay;
                inPeriod = Enumerable.Range(0, 7).Select(i => monday.AddDays(i)).Where(t => byDay.Contains(t.DayOfWeek) && t >= start);
            }
            foreach (var t in inPeriod)
            {
                if (t > until || count != null && made >= count) yield break;
                made++;
                yield return t;
            }
        }
    }

    static DayOfWeek? Day(string two) => two.ToUpperInvariant() switch
    {
        "MO" => DayOfWeek.Monday, "TU" => DayOfWeek.Tuesday, "WE" => DayOfWeek.Wednesday, "TH" => DayOfWeek.Thursday,
        "FR" => DayOfWeek.Friday, "SA" => DayOfWeek.Saturday, "SU" => DayOfWeek.Sunday, _ => null,
    };

    /// A DTSTART-style value in local time; true when it's a whole day (VALUE=DATE).
    internal static (DateTime, bool) Time(string param, string value)
    {
        value = value.Trim();
        if (value.Length == 8 && DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return (day, true);
        bool utc = value.EndsWith('Z');
        var t = DateTime.ParseExact(value.TrimEnd('Z'), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
        if (utc) return (DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime(), false);
        var tzid = param.Split(';').FirstOrDefault(p => p.StartsWith("TZID=", StringComparison.OrdinalIgnoreCase))?[5..].Trim('"');
        if (tzid != null)
        {
            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(tzid); // accepts IANA names like Australia/Brisbane
                return (TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(t, DateTimeKind.Unspecified), zone, TimeZoneInfo.Local), false);
            }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
        return (t, false); // floating time: already local
    }

    static TimeSpan Duration(string v)
    {
        // P1D, PT1H30M, P1W
        var m = System.Text.RegularExpressions.Regex.Match(v, @"P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?");
        int N(int g) => m.Groups[g].Success ? int.Parse(m.Groups[g].Value) : 0;
        return new TimeSpan(N(1) * 7 + N(2), N(3), N(4), N(5));
    }

    static string Text(string s) => s.Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\").Trim();
}
