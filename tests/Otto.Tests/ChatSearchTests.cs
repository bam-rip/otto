using System.Text.Json.Nodes;

namespace Otto.Tests;

public class ChatSearchTests
{
    static readonly ChatStore.Summary Chat = new("x", "Tidy up my downloads folder", "Done.", DateTime.Now,
        "Tidy up my downloads folder Done. I moved 214 files into Documents. The two Japan itinerary PDFs went into Travel.");

    [Theory]
    [InlineData("downloads")]
    [InlineData("JAPAN")]
    [InlineData("japan travel")] // every word, in any order
    public void Matches_words_anywhere_in_the_chat(string query) => Assert.True(Chat.Matches(query));

    [Theory]
    [InlineData("tokyo")]
    [InlineData("japan tokyo")] // one missing word is enough to rule it out
    public void Does_not_match_missing_words(string query) => Assert.False(Chat.Matches(query));

    [Fact]
    public void Snippet_shows_the_match_unless_it_is_in_the_title()
    {
        Assert.Contains("Japan itinerary", Chat.Snippet("japan"));
        Assert.StartsWith("…", Chat.Snippet("japan"));
        Assert.Null(Chat.Snippet("downloads"));
    }

    [Fact]
    public void Search_text_has_what_was_said_but_not_tool_output()
    {
        var messages = JsonNode.Parse("""
            [
              {"role":"user","content":"find my   resume"},
              {"role":"assistant","content":[{"type":"text","text":"Looking."},{"type":"tool_use","id":"t1","name":"list_dir","input":{"path":"C:\\"}}]},
              {"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"SECRET-TOOL-OUTPUT"}]},
              {"role":"assistant","content":"It's in Documents."}
            ]
            """)!.AsArray();
        var text = ChatStore.SearchText(messages);
        Assert.Equal("find my resume Looking. It's in Documents.", text);
        Assert.Equal("It's in Documents.", ChatStore.Preview(messages));
    }
}
