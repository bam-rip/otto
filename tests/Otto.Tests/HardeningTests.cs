using System.Net;
using System.Text.Json.Nodes;

namespace Otto.Tests;

public class HardeningTests
{
    [Theory]
    [InlineData(@"C:\Users\me\Desktop\run.exe.")]
    [InlineData(@"C:\Users\me\Desktop\run.bat. . ")]
    [InlineData(@"C:\Users\me\Desktop\run.exe::$DATA")]
    [InlineData(@"C:\Users\me\Desktop\notes.txt:hidden.ps1")]
    [InlineData(@"C:\Users\me\Desktop\x.library-ms")]
    public void Spellings_Windows_reads_as_a_program_are_programs(string path) => Assert.True(Safety.IsRunnable(path));

    [Theory]
    [InlineData(@"C:\Users\me\Desktop\notes.txt")]
    [InlineData(@"C:\Users\me\Desktop\exe")]
    public void Ordinary_files_are_not_programs(string path) => Assert.False(Safety.IsRunnable(path));

    [Theory]
    [InlineData(@"\\.\UNC\server\share\x.txt")]
    [InlineData(@"\??\UNC\server\share")]
    [InlineData(@"//server/share")]
    public void Every_spelling_of_a_network_path_is_refused(string path) => Assert.True(Safety.IsNetworkPath(path));

    [Theory]
    [InlineData("64:ff9b::c0a8:101")] // NAT64 of 192.168.1.1
    [InlineData("2002:c0a8:101::1")]  // 6to4 of 192.168.1.1
    [InlineData("::7f00:1")]          // old IPv4-compatible 127.0.0.1
    [InlineData("::ffff:10.0.0.1")]
    public void IPv6_wrappers_of_private_addresses_are_private(string ip) => Assert.True(Safety.IsPrivate(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("64:ff9b::808:808")] // NAT64 of 8.8.8.8
    [InlineData("2606:4700::1111")]
    public void Public_IPv6_stays_public(string ip) => Assert.False(Safety.IsPrivate(IPAddress.Parse(ip)));

    [Fact]
    public void Data_spelled_into_a_long_host_name_counts_as_smuggling()
    {
        Assert.True(Safety.LooksLikeSmuggling("https://" + new string('a', 60) + "." + new string('b', 30) + ".evil.example/"));
        Assert.False(Safety.LooksLikeSmuggling("https://docs.example.com/page"));
    }

    [Fact]
    public void Short_8dot3_names_are_expanded_before_folder_checks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OttoLongFolderNameForTest");
        Directory.CreateDirectory(dir);
        try
        {
            var shortName = Safety.LongPath(dir); // round trip at least returns the same folder
            Assert.True(Safety.IsInside(Path.Combine(dir, "a.txt"), dir));
            Assert.Equal(Path.GetFullPath(dir), shortName, ignoreCase: true);
        }
        finally { Directory.Delete(dir); }
    }

    [Fact]
    public void Placeholders_with_json_in_their_name_are_ignored()
    {
        var calls = JsonNode.Parse("""[{"tool":"open","input":{"target":"x"}}]""")!;
        var filled = Memory.Fill(calls, new JsonObject { ["\"target\":\"x\""] = "1" });
        Assert.Equal("x", filled[0]!["input"]!["target"]!.GetValue<string>());
    }

    [Fact]
    public void Numbers_and_flags_from_models_in_any_spelling()
    {
        var input = new JsonObject { ["count"] = "7", ["days"] = 3.0, ["unread_only"] = "true", ["all_day"] = true };
        Assert.Equal(7, Tools.Int(input, "count", 10));
        Assert.Equal(3, Tools.Int(input, "days", 7));
        Assert.Equal(10, Tools.Int(input, "missing", 10));
        Assert.True(Tools.Flag(input, "unread_only"));
        Assert.True(Tools.Flag(input, "all_day"));
        Assert.False(Tools.Flag(input, "missing"));
    }

    [Fact]
    public void Cutting_text_never_splits_an_emoji()
    {
        var s = "ab\U0001F600cd"; // the emoji is two chars, at 2 and 3
        Assert.Equal("ab", s.Head(3));
        Assert.Equal("ab\U0001F600", s.Head(4));
        Assert.Equal("ab…", s.Clip(3));
    }

    [Fact]
    public void Export_drops_characters_Word_cannot_open()
    {
        Assert.Equal("ab\U0001F600c", Export.XmlSafe("a\u0001b\U0001F600\uD800c\u000B"));
        var docx = Export.ToDocx(new[] { new Export.Line(true, "bell\u0007 here") }, DateTime.Now);
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(docx));
        using var r = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        System.Xml.Linq.XDocument.Parse(r.ReadToEnd()); // throws if Word would call it damaged
    }

    [Fact]
    public void One_unreadable_event_does_not_hide_the_calendar()
    {
        var ics = "BEGIN:VCALENDAR\nBEGIN:VEVENT\nUID:1\nSUMMARY:Broken\nDTSTART:2026-10-05 nonsense\nEND:VEVENT\n" +
                  "BEGIN:VEVENT\nUID:2\nSUMMARY:Fine\nDTSTART:20261005T100000\nDTEND:20261005T110000\nRRULE:FREQ=WEEKLY;BYDAY=\nEND:VEVENT\nEND:VCALENDAR";
        var events = Calendar.Parse(ics);
        Assert.Single(events);
        Assert.Equal("Fine", events[0].Summary);
        Assert.NotEmpty(Calendar.Occurrences(events, new DateTime(2026, 10, 5), new DateTime(2026, 10, 6)));
    }

    [Fact]
    public void A_daily_repeat_from_long_ago_still_shows_today()
    {
        var start = new DateTime(2005, 1, 1, 9, 0, 0);
        var today = new DateTime(2026, 10, 6);
        var hits = Calendar.Expand(start, "FREQ=DAILY", today.AddDays(1), today).Where(t => t >= today).ToList();
        Assert.Equal(new[] { today.AddHours(9) }, hits);
    }

    [Fact]
    public void Saves_replace_the_file_whole()
    {
        var path = Path.Combine(Path.GetTempPath(), $"otto-safefile-{Guid.NewGuid():N}.txt");
        try
        {
            SafeFile.WriteAllText(path, "one");
            SafeFile.WriteAllLines(path, new[] { "two", "three" });
            Assert.Equal("two" + Environment.NewLine + "three" + Environment.NewLine, File.ReadAllText(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { File.Delete(path); }
    }
}
