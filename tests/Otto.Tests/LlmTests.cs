using System.Text.Json.Nodes;

namespace Otto.Tests;

/// History is kept in Anthropic's format and translated for OpenAI-style providers; cost is computed from
/// Anthropic's usage block.
public class LlmTests
{
    [Fact]
    public void Assistant_tool_calls_translate_to_openai_tool_calls_with_provider_extras()
    {
        var history = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "[1:00 PM] open notepad" },
            new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = "Opening it." },
                    new JsonObject
                    {
                        ["type"] = "tool_use", ["id"] = "call_1", ["name"] = "open",
                        ["input"] = new JsonObject { ["target"] = "notepad" },
                        [Llm.Extra] = new JsonObject { ["google"] = new JsonObject { ["thought_signature"] = "sig" } },
                    },
                },
            },
        };

        var outp = Llm.ToOpenAiMessages("system prompt", history, vision: true);

        Assert.Equal("system", outp[0]!["role"]!.GetValue<string>());
        Assert.Equal("[1:00 PM] open notepad", outp[1]!["content"]!.GetValue<string>());
        var msg = outp[2]!;
        Assert.Equal("Opening it.", msg["content"]!.GetValue<string>());
        var call = msg["tool_calls"]![0]!;
        Assert.Equal("call_1", call["id"]!.GetValue<string>());
        Assert.Equal("open", call["function"]!["name"]!.GetValue<string>());
        Assert.Equal("notepad", JsonNode.Parse(call["function"]!["arguments"]!.GetValue<string>())!["target"]!.GetValue<string>());
        Assert.Equal("sig", call["extra_content"]!["google"]!["thought_signature"]!.GetValue<string>());
    }

    static JsonArray ScreenshotResult() => new()
    {
        new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "tool_result", ["tool_use_id"] = "call_1",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "image/jpeg", ["data"] = "AAAA" } },
                    },
                },
            },
        },
    };

    [Fact]
    public void Screenshots_in_tool_results_follow_as_a_user_image_when_the_model_can_see()
    {
        var outp = Llm.ToOpenAiMessages(null, ScreenshotResult(), vision: true);

        Assert.Equal("tool", outp[0]!["role"]!.GetValue<string>());
        Assert.Equal("call_1", outp[0]!["tool_call_id"]!.GetValue<string>());
        var parts = outp[1]!["content"]!.AsArray();
        Assert.Equal("data:image/jpeg;base64,AAAA", parts.Last()!["image_url"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public void Screenshots_become_a_note_for_text_only_models()
    {
        var outp = Llm.ToOpenAiMessages(null, ScreenshotResult(), vision: false);

        Assert.Single(outp); // no image message
        Assert.Contains("can't see images", outp[0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void Provider_extras_are_removed_before_a_request_goes_to_anthropic()
    {
        var history = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = new JsonArray { new JsonObject { ["type"] = "tool_use", ["id"] = "x", [Llm.Extra] = "sig" } },
            },
        };
        Llm.StripExtras(history);
        Assert.Null(history[0]!["content"]![0]![Llm.Extra]);
    }

    [Theory]
    [InlineData("", "{}")]
    [InlineData("{\"target\":", "{}")] // cut off mid-call at max_tokens
    [InlineData("{\"target\":\"notepad\"}", "{\"target\":\"notepad\"}")]
    public void Tool_arguments_parse_or_fall_back_to_empty(string args, string expected) =>
        Assert.Equal(expected, Llm.ParseArgs(args).ToJsonString());

    // ---- cost ----

    static JsonObject Usage(long input = 0, long output = 0, long cacheWrite = 0, long cacheWrite1h = 0, long cacheRead = 0) => new()
    {
        ["input_tokens"] = input,
        ["output_tokens"] = output,
        ["cache_creation_input_tokens"] = cacheWrite,
        ["cache_read_input_tokens"] = cacheRead,
        ["cache_creation"] = new JsonObject { ["ephemeral_1h_input_tokens"] = cacheWrite1h },
    };

    [Fact]
    public void Haiku_cost_uses_list_price_and_cache_multipliers()
    {
        const string haiku = "claude-haiku-4-5-20251001";
        Assert.Equal(1.00, Agent.ClaudeCost(Usage(input: 1_000_000), haiku)!.Value, 6);
        Assert.Equal(5.00, Agent.ClaudeCost(Usage(output: 1_000_000), haiku)!.Value, 6);
        Assert.Equal(1.25, Agent.ClaudeCost(Usage(cacheWrite: 1_000_000), haiku)!.Value, 6);  // 5-minute write
        Assert.Equal(2.00, Agent.ClaudeCost(Usage(cacheWrite: 1_000_000, cacheWrite1h: 1_000_000), haiku)!.Value, 6); // 1-hour write
        Assert.Equal(0.10, Agent.ClaudeCost(Usage(cacheRead: 1_000_000), haiku)!.Value, 6);
    }

    [Fact]
    public void Unknown_models_have_no_cost_rather_than_a_guess()
    {
        Assert.Null(Agent.ClaudeCost(Usage(input: 1000), "claude-opus-5-5"));
        Assert.Null(Agent.ClaudeCost(Usage(input: 1000), "claude-sonnet-4-5x")); // prefix alone isn't a match
        Assert.Equal(3.00, Agent.ClaudeCost(Usage(input: 1_000_000), "claude-sonnet-4-5-20250929")!.Value, 6);
    }
}
