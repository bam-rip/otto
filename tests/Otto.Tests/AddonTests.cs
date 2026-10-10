using System.IO.Compression;
using System.Text;

namespace Otto.Tests;

[Collection("Data folder")] // changes Paths.Data
public class AddonTests : IDisposable
{
    readonly string old = Paths.Data;
    readonly string dir = Path.Combine(Path.GetTempPath(), $"otto-addons-{Guid.NewGuid():N}");

    public AddonTests() => Paths.Data = dir;

    public void Dispose()
    {
        Paths.Data = old;
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    static byte[] Zip(params (string name, byte[] data)[] files)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, data) in files)
                using (var s = z.CreateEntry(name).Open()) s.Write(data);
        return ms.ToArray();
    }

    static byte[] T(string s) => Encoding.UTF8.GetBytes(s);

    static byte[] Picture()
    {
        using var bmp = new Bitmap(8, 8);
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
        return ms.ToArray();
    }

    byte[] ReactionsZip(string json) => Zip(("addon.json", T("{\"id\":\"reactions\"}")), ("reactions.json", T(json)), ("pictures/facepalm.jpg", Picture()));

    [Fact]
    public void A_good_addon_unpacks_and_lists_its_reactions_with_meanings()
    {
        Addons.Unpack(ReactionsZip("""
            [{"name":"facepalm","file":"pictures/facepalm.jpg","meaning":"a facepalm","useWhen":"something is dumb"},
             {"name":"missing","file":"pictures/nope.jpg","meaning":"x","useWhen":"y"},
             {"name":"escape","file":"../../evil.jpg","meaning":"x","useWhen":"y"}]
            """), "reactions");
        Assert.True(Reactions.Available);
        var r = Assert.Single(Reactions.All()); // the missing picture and the one outside the folder are dropped
        Assert.Equal("facepalm", r.Name);
        var tool = Reactions.ToolDefinition().ToJsonString();
        Assert.Contains("facepalm: a facepalm. Use when something is dumb.", tool);
        Assert.Contains("never in your own replies", tool);
    }

    [Fact]
    public void Removing_an_addon_takes_its_tool_away()
    {
        Addons.Unpack(ReactionsZip("""[{"name":"facepalm","file":"pictures/facepalm.jpg","meaning":"m","useWhen":"u"}]"""), "reactions");
        Assert.Contains(Tools.Definitions().AsArray(), t => t?["name"]?.GetValue<string>() == "reaction_image");
        Addons.Remove("reactions");
        Assert.False(Reactions.Available);
        Assert.DoesNotContain(Tools.Definitions().AsArray(), t => t?["name"]?.GetValue<string>() == "reaction_image");
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("pictures/run.exe")]
    [InlineData("pictures/script.ps1")]
    [InlineData("pictures/page.html")]
    public void Bad_files_stop_the_whole_unpack_and_leave_the_old_version(string bad)
    {
        Addons.Unpack(ReactionsZip("[]"), "reactions");
        var ex = Assert.Throws<InvalidDataException>(() =>
            Addons.Unpack(Zip(("addon.json", T("{}")), (bad, T("x"))), "reactions"));
        Assert.NotNull(ex.Message);
        Assert.True(File.Exists(Path.Combine(Addons.Dir("reactions"), "pictures", "facepalm.jpg"))); // untouched
        Assert.False(Directory.Exists(Addons.Dir("reactions") + ".new"));
        Assert.False(File.Exists(Path.Combine(dir, "outside.png")));
    }

    [Fact]
    public void A_zip_without_a_manifest_isnt_an_addon() =>
        Assert.Throws<InvalidDataException>(() => Addons.Unpack(Zip(("a.png", Picture())), "reactions"));

    [Fact]
    public void An_unknown_reaction_names_the_ones_that_exist()
    {
        Addons.Unpack(ReactionsZip("""[{"name":"facepalm","file":"pictures/facepalm.jpg","meaning":"m","useWhen":"u"}]"""), "reactions");
        var ex = Assert.Throws<ArgumentException>(() => Reactions.Copy("random"));
        Assert.Contains("facepalm", ex.Message);
    }
}
