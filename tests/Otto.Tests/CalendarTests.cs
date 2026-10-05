namespace Otto.Tests;

/// The iCal reader: what Google/Apple/Outlook calendar links actually contain.
public class CalendarTests
{
    static string Ics(params string[] events) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n" + string.Join("", events) + "END:VCALENDAR\r\n";

    static string Ev(params string[] lines) => "BEGIN:VEVENT\r\n" + string.Join("\r\n", lines) + "\r\nEND:VEVENT\r\n";

    static List<Calendar.Occurrence> Between(string ics, DateTime from, DateTime to) =>
        Calendar.Occurrences(Calendar.Parse(ics), from, to).ToList();

    [Fact]
    public void A_single_event_with_folded_lines_and_escapes()
    {
        var ics = Ics(Ev("UID:a", "DTSTART:20261006T090000", "DTEND:20261006T100000",
            "SUMMARY:Dentist\\, then pick up", " the car", "LOCATION:12 Main St"));
        var o = Assert.Single(Between(ics, new(2026, 10, 6), new(2026, 10, 7)));
        Assert.Equal("Dentist, then pick upthe car", o.Summary);
        Assert.Equal("12 Main St", o.Location);
        Assert.Equal(new DateTime(2026, 10, 6, 9, 0, 0), o.Start);
        Assert.Equal(new DateTime(2026, 10, 6, 10, 0, 0), o.End);
    }

    [Fact]
    public void All_day_events()
    {
        var ics = Ics(Ev("UID:b", "DTSTART;VALUE=DATE:20261010", "DTEND;VALUE=DATE:20261011", "SUMMARY:Mum's birthday"));
        var o = Assert.Single(Between(ics, new(2026, 10, 10), new(2026, 10, 11)));
        Assert.True(o.AllDay);
        Assert.Empty(Between(ics, new(2026, 10, 11), new(2026, 10, 12)));
    }

    [Fact]
    public void Utc_times_become_local()
    {
        var ics = Ics(Ev("UID:c", "DTSTART:20261006T000000Z", "DTEND:20261006T010000Z", "SUMMARY:Call"));
        var local = DateTime.SpecifyKind(new DateTime(2026, 10, 6, 0, 0, 0), DateTimeKind.Utc).ToLocalTime();
        var o = Assert.Single(Between(ics, local.Date.AddDays(-1), local.Date.AddDays(2)));
        Assert.Equal(local, o.Start);
    }

    [Fact]
    public void Time_zones_by_name()
    {
        var (t, _) = Calendar.Time("TZID=America/New_York", "20261006T090000");
        var expected = TimeZoneInfo.ConvertTime(new DateTime(2026, 10, 6, 9, 0, 0), TimeZoneInfo.FindSystemTimeZoneById("America/New_York"), TimeZoneInfo.Local);
        Assert.Equal(expected, t);
    }

    [Fact]
    public void Weekly_classes_on_set_days_with_a_cancelled_one_and_a_moved_one()
    {
        // Mondays and Wednesdays 10-11 from Mon 5 Oct; 7 Oct cancelled; 12 Oct moved to 2pm
        var ics = Ics(
            Ev("UID:lec", "DTSTART:20261005T100000", "DTEND:20261005T110000", "RRULE:FREQ=WEEKLY;BYDAY=MO,WE",
               "EXDATE:20261007T100000", "SUMMARY:MATH1052 lecture"),
            Ev("UID:lec", "RECURRENCE-ID:20261012T100000", "DTSTART:20261012T140000", "DTEND:20261012T150000", "SUMMARY:MATH1052 lecture (moved)"));
        var got = Between(ics, new(2026, 10, 5), new(2026, 10, 15)).Select(o => (o.Start, o.Summary)).ToList();
        Assert.Equal(new[]
        {
            (new DateTime(2026, 10, 5, 10, 0, 0), "MATH1052 lecture"),
            (new DateTime(2026, 10, 12, 14, 0, 0), "MATH1052 lecture (moved)"),
            (new DateTime(2026, 10, 14, 10, 0, 0), "MATH1052 lecture"),
        }, got);
    }

    [Fact]
    public void Repeats_stop_at_count_and_until()
    {
        var count = Ics(Ev("UID:d", "DTSTART:20261001T080000", "DTEND:20261001T083000", "RRULE:FREQ=DAILY;COUNT=3", "SUMMARY:Pills"));
        Assert.Equal(3, Between(count, new(2026, 9, 1), new(2026, 12, 1)).Count);

        var until = Ics(Ev("UID:e", "DTSTART:20261001T080000", "DTEND:20261001T083000", "RRULE:FREQ=WEEKLY;UNTIL=20261020T000000Z", "SUMMARY:Gym"));
        Assert.Equal(3, Between(until, new(2026, 9, 1), new(2026, 12, 1)).Count); // 1, 8, 15 Oct

        var monthly = Ics(Ev("UID:f", "DTSTART:20260115T120000", "DTEND:20260115T130000", "RRULE:FREQ=MONTHLY;INTERVAL=2", "SUMMARY:Haircut"));
        Assert.Equal(new[] { 1, 3, 5, 7, 9, 11 }, Between(monthly, new(2026, 1, 1), new(2027, 1, 1)).Select(o => o.Start.Month));
    }

    [Fact]
    public void Cancelled_events_are_left_out()
    {
        var ics = Ics(Ev("UID:g", "DTSTART:20261006T090000", "DTEND:20261006T100000", "STATUS:CANCELLED", "SUMMARY:Off"));
        Assert.Empty(Between(ics, new(2026, 10, 6), new(2026, 10, 7)));
    }

    [Theory]
    [InlineData("webcal://p01-calendars.icloud.com/published/2/abc", true)]
    [InlineData("https://calendar.google.com/calendar/ical/me%40gmail.com/private-abc/basic.ics", true)]
    [InlineData("http://example.com/cal.ics", false)]
    [InlineData("not a link", false)]
    public void Only_secure_calendar_links_are_accepted(string link, bool ok)
    {
        if (ok) Assert.StartsWith("https://", Calendar.Normalize(link));
        else Assert.Throws<ArgumentException>(() => Calendar.Normalize(link));
    }
}
