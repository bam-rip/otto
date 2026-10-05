namespace Otto.Tests;

/// Parsers for the search engines web_search falls back through. Markup trimmed from real responses.
public class SearchTests
{
    [Fact]
    public void Ddg_lite_results_are_read_and_ads_skipped()
    {
        const string html = """
            <a rel="nofollow" href="https://duckduckgo.com/y.js?ad_provider=x" class='result-link'>Ad</a>
            <td class='result-snippet'>ad text</td>
            <a rel="nofollow" href="https://www.bom.gov.au/brisbane" class='result-link'>Brisbane | Bureau&#x27;s page</a>
            <td class='result-snippet'>
                Explore <b>weather</b> in Brisbane &amp; more
            </td>
            """;
        var r = Assert.Single(Tools.ParseDdgLite(html));
        Assert.Equal("Brisbane | Bureau's page", r.Title);
        Assert.Equal("https://www.bom.gov.au/brisbane", r.Url);
        Assert.Equal("Explore weather in Brisbane & more", r.Snippet);
    }

    [Fact]
    public void Bing_results_unwrap_the_redirect_link()
    {
        const string html = """
            <ol id="b_results"><li class="b_algo" data-id><h2 class="x"><a href="https://www.bing.com/ck/a?!&amp;&amp;p=abc&amp;u=a1aHR0cHM6Ly9leGFtcGxlLmNvbS9hP2I9MQ&amp;ntb=1">Example <strong>title</strong></a></h2>
            <div class="b_caption"><p class="b_lineclamp2">The snippet text</p></div></li>
            <li class="b_algo"><div>no heading, skipped</div></li></ol>
            """;
        var r = Assert.Single(Tools.ParseBing(html));
        Assert.Equal("Example title", r.Title);
        Assert.Equal("https://example.com/a?b=1", r.Url);
        Assert.Equal("The snippet text", r.Snippet);
    }

    [Fact]
    public void Ddg_html_unwraps_uddg_links()
    {
        const string html = """<a class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2F&amp;rut=1">Ex</a><a class="result__snippet" href="#">Snip</a>""";
        var r = Assert.Single(Tools.ParseDdgHtml(html));
        Assert.Equal("https://example.com/", r.Url);
    }

    [Fact]
    public void Challenge_pages_give_no_results_so_the_next_engine_is_tried()
    {
        const string challenge = "<html><body><form id='challenge-form'>anomaly detected</form></body></html>";
        Assert.Empty(Tools.ParseDdgLite(challenge));
        Assert.Empty(Tools.ParseDdgHtml(challenge));
        Assert.Empty(Tools.ParseBing(challenge));
    }
}
