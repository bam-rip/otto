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

public class ChatEncryptionTests
{
    [Fact]
    public void Chats_are_encrypted_and_older_plain_ones_still_open()
    {
        const string json = """{"title":"secret plans","messages":[]}""";
        var sealedFile = ChatStore.Seal(json);
        Assert.DoesNotContain("secret plans", System.Text.Encoding.UTF8.GetString(sealedFile));
        Assert.Equal(json, ChatStore.Open(sealedFile, out bool plain));
        Assert.False(plain);

        Assert.Equal(json, ChatStore.Open(System.Text.Encoding.UTF8.GetBytes(json), out plain)); // saved by 1.2.3 or earlier
        Assert.True(plain);
    }
}

[Collection("Data folder")]
public class ChatListCacheTests
{
    [Fact]
    public void History_shows_changes_and_deletions_even_though_summaries_are_cached()
    {
        var dir = Path.Combine(Path.GetTempPath(), "otto-cache-" + Guid.NewGuid().ToString("N"));
        var old = Paths.Data; Paths.Data = dir;
        try
        {
            System.Text.Json.Nodes.JsonArray Chat(string text) => new() { new System.Text.Json.Nodes.JsonObject { ["role"] = "user", ["content"] = text } };
            ChatStore.Save("a", Chat("first title"));
            ChatStore.Save("b", Chat("other chat"));
            Assert.Equal(new[] { "other chat", "first title" }, ChatStore.List().Select(c => c.Title));

            Thread.Sleep(20); // a different write time
            ChatStore.Save("a", Chat("renamed after more messages"));
            Assert.Contains(ChatStore.List(), c => c.Title == "renamed after more messages");

            ChatStore.Delete("b");
            Assert.Equal(new[] { "renamed after more messages" }, ChatStore.List().Select(c => c.Title));
        }
        finally { Paths.Data = old; Directory.Delete(dir, true); }
    }
}
