using System.Text.Json.Nodes;
using MimeKit;

namespace Otto.Tests;

/// The parts of the IMAP connector that don't need a real mailbox.
public class ImapTests
{
    [Theory]
    [InlineData("me@gmail.com", "imap.gmail.com", 993, "smtp.gmail.com", 465)]
    [InlineData("Me@GoogleMail.com", "imap.gmail.com", 993, "smtp.gmail.com", 465)]
    [InlineData("me@icloud.com", "imap.mail.me.com", 993, "smtp.mail.me.com", 587)]
    [InlineData("me@yahoo.com.au", "imap.mail.yahoo.com", 993, "smtp.mail.yahoo.com", 465)]
    public void Known_providers_fill_in_their_servers(string address, string imap, int imapPort, string smtp, int smtpPort)
    {
        var s = Imap.Known(address)!;
        Assert.Equal((imap, imapPort, smtp, smtpPort), (s.Imap, s.ImapPort, s.Smtp, s.SmtpPort));
    }

    [Fact]
    public void Unknown_domains_need_servers_typed_in() => Assert.Null(Imap.Known("me@example.org"));

    [Theory]
    [InlineData("imap.example.com:143", "imap.example.com", 143)]
    [InlineData("imap.example.com", "imap.example.com", 993)]
    [InlineData(" mail.example.com:587 ", "mail.example.com", 587)]
    public void Server_and_port_are_split(string text, string host, int port) =>
        Assert.Equal((host, port), Imap.HostPort(text, 993));

    [Fact]
    public void Compose_builds_a_plain_text_email()
    {
        var msg = Imap.Compose(JsonNode.Parse("""{"to":["a@x.com","b@x.com"],"cc":["c@x.com"],"subject":"Hi","body":"Hello there"}""")!, from: "me@gmail.com");
        Assert.Equal("me@gmail.com", msg.From.Mailboxes.Single().Address);
        Assert.Equal(new[] { "a@x.com", "b@x.com" }, msg.To.Mailboxes.Select(m => m.Address));
        Assert.Equal("c@x.com", msg.Cc.Mailboxes.Single().Address);
        Assert.Equal("Hi", msg.Subject);
        Assert.Equal("Hello there", msg.TextBody);
    }

    [Fact]
    public void Compose_needs_someone_to_send_to() =>
        Assert.Throws<ArgumentException>(() => Imap.Compose(JsonNode.Parse("""{"subject":"Hi","body":"x"}""")!, from: "me@gmail.com"));

    [Fact]
    public void Replies_thread_and_go_to_reply_to()
    {
        var orig = new MimeMessage { Subject = "Lunch?", MessageId = "abc@x.com" };
        orig.From.Add(MailboxAddress.Parse("sam@x.com"));
        orig.ReplyTo.Add(MailboxAddress.Parse("sam-replies@x.com"));
        orig.References.Add("first@x.com");

        var reply = Imap.Reply(orig, "Sure", from: "me@gmail.com");
        Assert.Equal("Re: Lunch?", reply.Subject);
        Assert.Equal("sam-replies@x.com", reply.To.Mailboxes.Single().Address);
        Assert.Equal("abc@x.com", reply.InReplyTo);
        Assert.Equal(new[] { "first@x.com", "abc@x.com" }, reply.References);

        // replying to a reply doesn't stack "Re: Re:"
        orig.Subject = "RE: Lunch?";
        Assert.Equal("RE: Lunch?", Imap.Reply(orig, "x", from: "me@gmail.com").Subject);
    }
}
