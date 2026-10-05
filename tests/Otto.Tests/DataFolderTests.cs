using System.Text.Json.Nodes;

namespace Otto.Tests;

/// Everything here writes files, so it runs against a temp folder (Paths.Data), never the real
/// %LOCALAPPDATA%\Otto. All in one class so xunit runs these one at a time.
[Collection("Data folder")] // shares Paths.Data with SafetyTests, so never in parallel with it
public sealed class DataFolderTests : IDisposable
{
    readonly string original = Paths.Data;
    readonly string dir = Path.Combine(Path.GetTempPath(), "otto-tests-" + Guid.NewGuid().ToString("N"));

    public DataFolderTests()
    {
        Directory.CreateDirectory(dir);
        Paths.Data = dir;
    }

    public void Dispose()
    {
        Paths.Data = original;
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Two_backups_in_the_same_second_keep_both_versions()
    {
        var file = Path.Combine(dir, "notes.md");
        File.WriteAllText(file, "original");
        var first = Tools.Backup(file);
        File.WriteAllText(file, "second");
        var second = Tools.Backup(file);

        Assert.NotEqual(first, second);
        Assert.Equal("original", File.ReadAllText(first)); // what "undo" needs
        Assert.Equal("second", File.ReadAllText(second));
    }

    static JsonObject RoutineInput(string name) => new()
    {
        ["name"] = name,
        ["description"] = "opens notepad",
        ["calls"] = new JsonArray { new JsonObject { ["tool"] = "open", ["input"] = new JsonObject { ["target"] = "notepad" } } },
    };

    [Fact]
    public void A_damaged_routines_file_is_moved_aside_not_overwritten()
    {
        var path = Path.Combine(dir, "routines.json");
        File.WriteAllText(path, "{ \"half written");

        var result = Memory.SaveRoutine(RoutineInput("Notepad"));

        Assert.Contains("damaged", result);
        var aside = Directory.GetFiles(dir, "routines.damaged-*.json").Single();
        Assert.Equal("{ \"half written", File.ReadAllText(aside));
        Assert.NotNull(JsonNode.Parse(File.ReadAllText(path))!["notepad"]);
    }

    [Fact]
    public void A_damaged_routines_file_doesnt_break_the_prompt()
    {
        File.WriteAllText(Path.Combine(dir, "routines.json"), "not json");
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "- likes dark mode");

        var section = Memory.PromptSection();

        Assert.Contains("likes dark mode", section);
        Assert.DoesNotContain("Saved routines", section);
    }

    [Fact]
    public void Routines_round_trip()
    {
        Memory.SaveRoutine(RoutineInput("Open Notepad"));
        Assert.Contains("- open_notepad: opens notepad", Memory.PromptSection());
        Assert.Equal("Deleted.", Memory.Forget(new JsonObject { ["name"] = "open notepad" }));
        Assert.DoesNotContain("open_notepad", Memory.PromptSection());
    }

    [Fact]
    public void A_summarised_chat_is_titled_by_what_you_asked_not_the_summary()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = Agent.SummaryHeader + "\nYou asked about [things] and more." },
            new JsonObject { ["role"] = "assistant", ["content"] = "Got it." },
            new JsonObject { ["role"] = "user", ["content"] = "[2:15 PM] now email it to Sam" },
            new JsonObject { ["role"] = "assistant", ["content"] = "Sent." },
        };

        ChatStore.Save("test-chat", messages);

        Assert.Equal("now email it to Sam", ChatStore.List().Single(c => c.Id == "test-chat").Title);
    }
}
