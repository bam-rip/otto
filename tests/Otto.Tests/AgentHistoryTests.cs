using System.Text.Json.Nodes;

namespace Otto.Tests;

/// The conversation history is sent back to the API on every step, so its shape has hard rules
/// (every tool_use answered by a tool_result) and its size is managed by pruning old screen readings.
public class AgentHistoryTests
{
    static JsonObject User(string text) => new() { ["role"] = "user", ["content"] = text };
    static JsonObject Assistant(string text) => new() { ["role"] = "assistant", ["content"] = text };

    static JsonObject ToolCall(string id, string name) => new()
    {
        ["role"] = "assistant",
        ["content"] = new JsonArray { new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = new JsonObject() } },
    };

    static JsonObject ToolResult(string id, JsonNode content) => new()
    {
        ["role"] = "user",
        ["content"] = new JsonArray { new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = content } },
    };

    static string Listing(string title) => $"Front window: {title}\nOther windows: Notepad\n[1] button \"Save\" @10,10\n[2] button \"Open\" @50,10\n[3] field \"\" @90,10";

    static JsonArray Screenshot() => new()
    {
        new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "image/jpeg", ["data"] = "AAAA" } },
    };

    static JsonNode? ResultContent(JsonArray history, string id) =>
        history.SelectMany(m => m!["content"] as JsonArray ?? new JsonArray())
               .First(b => b?["tool_use_id"]?.GetValue<string>() == id)!["content"];

    // ---- what counts as a message you typed ----

    [Fact]
    public void Typed_messages_are_turn_starts_tool_results_and_summaries_are_not()
    {
        Assert.True(Agent.IsTurnStart(User("[3:41 PM] open notepad")));
        Assert.True(Agent.IsTurnStart(new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "[3:41 PM] what's this" }, Screenshot()[0]!.DeepClone() },
        }));
        Assert.False(Agent.IsTurnStart(ToolResult("t1", "Done.")));
        Assert.False(Agent.IsTurnStart(Assistant("Hi.")));
        Assert.False(Agent.IsTurnStart(User(Agent.SummaryHeader + "\nYou asked me to tidy Downloads.")));
    }

    [Fact]
    public void TurnText_drops_the_time_stamp_and_the_attachment_note()
    {
        Assert.Equal("open notepad", Agent.TurnText(User("[3:41 PM] open notepad")));
        Assert.Equal("summarise this", Agent.TurnText(User("[9:05 AM] summarise this\n\n(Attached: files C:\\a.pdf)")));
        Assert.Null(Agent.TurnText(ToolResult("t1", "Done.")));
    }

    // ---- interrupted turns stay valid for the API ----

    [Fact]
    public void Interrupted_tool_calls_get_error_results_so_the_history_stays_valid()
    {
        var history = new JsonArray { User("[1:00 PM] do it") };
        var call = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = "On it." },
                new JsonObject { ["type"] = "tool_use", ["id"] = "a", ["name"] = "computer", ["input"] = new JsonObject() },
                new JsonObject { ["type"] = "tool_use", ["id"] = "b", ["name"] = "open", ["input"] = new JsonObject() },
            },
        };
        history.Add(call);

        Agent.KeepInterruptedTurn(history, startCount: 0, "Stopped by the user.");

        Assert.Equal(4, history.Count);
        var results = history[2]!["content"]!.AsArray();
        Assert.Equal(new[] { "a", "b" }, results.Select(r => r!["tool_use_id"]!.GetValue<string>()));
        Assert.All(results, r => Assert.True(r!["is_error"]!.GetValue<bool>()));
        Assert.Equal("assistant", history[3]!["role"]!.GetValue<string>());
    }

    [Fact]
    public void A_turn_that_already_answered_is_left_alone()
    {
        var history = new JsonArray { User("[1:00 PM] hi"), Assistant("Hello.") };
        Agent.KeepInterruptedTurn(history, startCount: 0, "Stopped by the user.");
        Assert.Equal(2, history.Count);
    }

    // ---- pruning old screen readings ----

    static JsonArray ScreenHistory(params JsonNode[] results)
    {
        var history = new JsonArray { User("[1:00 PM] go") };
        for (int i = 0; i < results.Length; i++)
        {
            history.Add(ToolCall("c" + i, "computer"));
            history.Add(ToolResult("c" + i, results[i]));
        }
        return history;
    }

    [Fact]
    public void Old_readings_shrink_to_their_first_line_and_the_newest_stay_whole()
    {
        var history = ScreenHistory(Enumerable.Range(0, 6).Select(i => (JsonNode)Listing("Window " + i)).ToArray());

        Agent.PruneObservations(history, keep: 2);

        for (int i = 0; i < 4; i++)
            Assert.Equal($"Front window: Window {i} [older screen reading removed]", ResultContent(history, "c" + i)!.ToString());
        Assert.Equal(Listing("Window 4"), ResultContent(history, "c4")!.ToString());
        Assert.Equal(Listing("Window 5"), ResultContent(history, "c5")!.ToString());
    }

    [Fact]
    public void Pruning_waits_until_readings_pile_up_so_the_cache_survives_a_few_steps()
    {
        var history = ScreenHistory(Enumerable.Range(0, 5).Select(i => (JsonNode)Listing("W" + i)).ToArray());
        Agent.PruneObservations(history, keep: 2);
        Assert.Equal(Listing("W0"), ResultContent(history, "c0")!.ToString()); // 5 <= keep + 3: untouched
    }

    [Fact]
    public void Short_results_never_push_a_reading_out()
    {
        // The bug this guards: "Nothing changed since your last look" while that look had been pruned,
        // because "Done." results used to take up the kept slots.
        var history = ScreenHistory(
            Listing("A"), Listing("B"), Listing("C"), Listing("D"), Listing("E"), Listing("F"),
            JsonValue.Create("Done.")!, JsonValue.Create("Done.")!,
            JsonValue.Create(Desktop.Unchanged + ": the front window's controls are exactly as in your last look.")!);

        Agent.PruneObservations(history, keep: Desktop.KeptReadings);

        Assert.Equal(Listing("E"), ResultContent(history, "c4")!.ToString());
        Assert.Equal(Listing("F"), ResultContent(history, "c5")!.ToString());
        Assert.Equal("Done.", ResultContent(history, "c6")!.ToString());
        Assert.StartsWith(Desktop.Unchanged, ResultContent(history, "c8")!.ToString());
    }

    [Fact]
    public void Screenshots_are_pruned_and_other_tools_results_are_kept()
    {
        var history = new JsonArray { User("[1:00 PM] go") };
        history.Add(ToolCall("f", "fetch_page"));
        history.Add(ToolResult("f", "[200] https://example.com\n\nThe answer is 42."));
        for (int i = 0; i < 6; i++)
        {
            history.Add(ToolCall("s" + i, "computer"));
            history.Add(ToolResult("s" + i, Screenshot()));
        }

        Agent.PruneObservations(history, keep: 2);

        Assert.Equal("[older screen reading removed]", ResultContent(history, "s0")!.ToString());
        Assert.IsType<JsonArray>(ResultContent(history, "s5"));
        Assert.Contains("The answer is 42.", ResultContent(history, "f")!.ToString());
    }

    [Fact]
    public void A_new_request_drops_every_old_reading()
    {
        var history = ScreenHistory(Listing("A"));
        Agent.PruneObservations(history, keep: 0);
        Assert.Equal("Front window: A [older screen reading removed]", ResultContent(history, "c0")!.ToString());
    }

    [Fact]
    public void IsReading_matches_listings_and_screenshots_only()
    {
        Assert.True(Desktop.IsReading(JsonValue.Create(Listing("X"))));
        Assert.True(Desktop.IsReading(Screenshot()));
        Assert.False(Desktop.IsReading(JsonValue.Create("Done.")));
        Assert.False(Desktop.IsReading(JsonValue.Create("Opened notepad. Front window: Untitled - Notepad")));
        Assert.False(Desktop.IsReading(JsonValue.Create("Front window: X [older screen reading removed]")));
        Assert.False(Desktop.IsReading(new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "hi" } }));
    }
}

public class TrimOldResultsTests
{
    static JsonObject User(string text) => new() { ["role"] = "user", ["content"] = text };
    static JsonObject Use(string id) => new() { ["role"] = "assistant", ["content"] = new JsonArray { new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = "fetch_page", ["input"] = new JsonObject() } } };
    static JsonObject Result(string id, string text) => new() { ["role"] = "user", ["content"] = new JsonArray { new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = text } } };
    static string Content(JsonArray m, int i) => m[i]!["content"]![0]!["content"]!.ToString();

    [Fact]
    public void Big_results_from_older_turns_are_shortened_recent_ones_kept_whole()
    {
        var big = new string('x', 5000);
        var m = new JsonArray
        {
            User("read this page"), Use("a"), Result("a", big), new JsonObject { ["role"] = "assistant", ["content"] = "done" },
            User("and this one"), Use("b"), Result("b", big), new JsonObject { ["role"] = "assistant", ["content"] = "done" },
            User("thanks, now this"), Use("c"), Result("c", big),
        };
        Agent.TrimOldResults(m);
        Assert.EndsWith(Agent.TrimNote, Content(m, 2));   // two turns back: shortened
        Assert.Equal(1200 + 1 + Agent.TrimNote.Length, Content(m, 2).Length);
        Assert.Equal(big, Content(m, 6));                  // last two turns: whole
        Assert.Equal(big, Content(m, 10));

        var once = Content(m, 2);
        Agent.TrimOldResults(m);                           // doing it again changes nothing
        Assert.Equal(once, Content(m, 2));
    }

    [Fact]
    public void Small_results_are_left_alone()
    {
        var m = new JsonArray { User("1"), Use("a"), Result("a", "short"), User("2"), User("3") };
        Agent.TrimOldResults(m);
        Assert.Equal("short", Content(m, 2));
    }
}
