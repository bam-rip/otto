using System.Text;
using System.Text.Json.Nodes;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace Otto;

/// Any email account that speaks IMAP/SMTP: Gmail, Yahoo, iCloud, Fastmail and most others. The user makes
/// an "app password" in their account's security settings and pastes it into Otto's settings; it's kept in
/// Windows Credential Manager. Same tools as the Outlook connector (Graph), minus the calendar.
static class Imap
{
    const string Key = @"Software\Otto\Imap";
    const string PasswordTarget = "Otto:imap";
    const int MaxBody = 4_000;

    public sealed record Servers(string Imap, int ImapPort, string Smtp, int SmtpPort, string? AppPasswordUrl);

    /// Known providers by address domain; anything else needs its servers typed in.
    public static Servers? Known(string address)
    {
        var domain = address.Split('@').LastOrDefault()?.Trim().ToLowerInvariant() ?? "";
        return domain switch
        {
            "gmail.com" or "googlemail.com" => new("imap.gmail.com", 993, "smtp.gmail.com", 465, "https://myaccount.google.com/apppasswords"),
            "yahoo.com" or "yahoo.com.au" or "yahoo.co.uk" or "ymail.com" => new("imap.mail.yahoo.com", 993, "smtp.mail.yahoo.com", 465, "https://login.yahoo.com/account/security/app-passwords"),
            "icloud.com" or "me.com" or "mac.com" => new("imap.mail.me.com", 993, "smtp.mail.me.com", 587, "https://account.apple.com/account/manage/section/security"),
            "fastmail.com" or "fastmail.fm" => new("imap.fastmail.com", 993, "smtp.fastmail.com", 465, "https://app.fastmail.com/settings/security/apps"),
            "aol.com" => new("imap.aol.com", 993, "smtp.aol.com", 465, "https://login.aol.com/account/security"),
            "zoho.com" => new("imap.zoho.com", 993, "smtp.zoho.com", 465, "https://accounts.zoho.com/home#security/app_password"),
            "gmx.com" or "gmx.net" => new("imap.gmx.com", 993, "mail.gmx.com", 587, null),
            _ => null,
        };
    }

    public static string Address { get => Get("address"); set => Set("address", value); }
    public static string ImapServer { get => Get("imap"); set => Set("imap", value); }
    public static string SmtpServer { get => Get("smtp"); set => Set("smtp", value); }

    public static bool Connected => Address.Length > 0 && KeyStore.ApiKey(PasswordTarget, null) != null;
    static bool IsGmail => ImapServer.StartsWith("imap.gmail.com", StringComparison.OrdinalIgnoreCase);

    static string Get(string name) => Reg.Get(Key, name) ?? "";
    static void Set(string name, string value) => Reg.Set(Key, name, value.Trim());

    /// "host:port" → parts, defaulting the port.
    internal static (string host, int port) HostPort(string s, int fallback)
    {
        s = s.Trim();
        int colon = s.LastIndexOf(':');
        return colon > 0 && int.TryParse(s[(colon + 1)..], out var p) ? (s[..colon], p) : (s, fallback);
    }

    /// Checks the address and app password really work (IMAP and SMTP) before saving them.
    public static async Task Connect(string address, string password, string imap, string smtp, CancellationToken ct = default)
    {
        using (var c = await OpenImap(address, password, imap, ct)) await c.DisconnectAsync(true, ct);
        using (var s = await OpenSmtp(address, password, smtp, ct)) await s.DisconnectAsync(true, ct);
        Address = address;
        ImapServer = imap;
        SmtpServer = smtp;
        KeyStore.Save(password, PasswordTarget);
    }

    public static void Disconnect()
    {
        KeyStore.Delete(PasswordTarget);
        Set("address", "");
    }

    static async Task<ImapClient> OpenImap(string address, string password, string server, CancellationToken ct)
    {
        var (host, port) = HostPort(server, 993);
        var c = new ImapClient { Timeout = 30_000 };
        try
        {
            await c.ConnectAsync(host, port, port == 993 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, ct);
            await c.AuthenticateAsync(address, password, ct);
            return c;
        }
        catch { c.Dispose(); throw; }
    }

    static async Task<SmtpClient> OpenSmtp(string address, string password, string server, CancellationToken ct)
    {
        var (host, port) = HostPort(server, 465);
        var s = new SmtpClient { Timeout = 30_000 };
        try
        {
            await s.ConnectAsync(host, port, port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, ct);
            await s.AuthenticateAsync(address, password, ct);
            return s;
        }
        catch { s.Dispose(); throw; }
    }

    static string Password => KeyStore.ApiKey(PasswordTarget, null) ?? throw new InvalidOperationException("Email isn't connected. Set it up in Otto's settings.");
    static Task<ImapClient> Open(CancellationToken ct) => OpenImap(Address, Password, ImapServer, ct);

    public static JsonNode ToolDefinitions() => JsonNode.Parse("""
    [
      {"name":"email_list","description":"List emails from the user's mailbox, newest first. Returns id, from, subject, date and read/unread.",
       "input_schema":{"type":"object","properties":{
         "folder":{"type":"string","description":"inbox (default), sent, drafts, junk, archive, or a folder name"},
         "unread_only":{"type":"boolean"},
         "search":{"type":"string","description":"words to search for (sender, subject, body)"},
         "count":{"type":"integer","description":"default 10, max 25"}}}},
      {"name":"email_read","description":"Read one email in full (by id from email_list). Marks it as read.",
       "input_schema":{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}},
      {"name":"email_draft","description":"Save an email as a draft without sending it. Use this when the user might want to check it first.",
       "input_schema":{"type":"object","properties":{"to":{"type":"array","items":{"type":"string"}},"subject":{"type":"string"},"body":{"type":"string"}},"required":["to","subject","body"]}},
      {"name":"email_send","description":"Send an email (or a reply, with reply_to_id). The user is always asked to approve it first.",
       "input_schema":{"type":"object","properties":{
         "to":{"type":"array","items":{"type":"string"}},"cc":{"type":"array","items":{"type":"string"}},
         "subject":{"type":"string"},"body":{"type":"string"},
         "reply_to_id":{"type":"string","description":"id of the email being replied to; to/subject can then be left out"}},
        "required":["body"]}}
    ]
    """)!;

    public static bool Handles(string tool) => tool.StartsWith("email_");

    public static async Task<string> Run(string name, JsonNode input, Func<string, bool> confirm, CancellationToken ct)
    {
        using var client = await Open(ct);
        try
        {
            switch (name)
            {
                case "email_list": return await List(client, input, ct);
                case "email_read": return await ReadOne(client, Tools.S(input, "id"), ct);
                case "email_draft":
                {
                    var msg = Compose(input);
                    var drafts = Folder(client, "drafts") ?? throw new InvalidOperationException("Couldn't find the Drafts folder.");
                    await drafts.OpenAsync(FolderAccess.ReadWrite, ct);
                    await drafts.AppendAsync(msg, MessageFlags.Draft | MessageFlags.Seen, ct);
                    return $"Saved as a draft in {drafts.FullName}.";
                }
                case "email_send":
                {
                    MimeMessage msg;
                    string ask;
                    if (input["reply_to_id"]?.GetValue<string>() is string replyTo)
                    {
                        var (folder, uid) = await Find(client, replyTo, FolderAccess.ReadOnly, ct);
                        var orig = await folder.GetMessageAsync(uid, ct);
                        msg = Reply(orig, Tools.S(input, "body"));
                        ask = $"Send this reply to {msg.To}?\n\n{msg.Subject}\n\n{Tools.S(input, "body").Clip(600)}";
                    }
                    else
                    {
                        msg = Compose(input);
                        ask = $"Send this email to {msg.To}?\n\nSubject: {msg.Subject}\n\n{Tools.S(input, "body").Clip(600)}";
                    }
                    if (!confirm(ask)) return "User declined.";
                    using (var smtp = await OpenSmtp(Address, Password, SmtpServer, ct))
                    {
                        await smtp.SendAsync(msg, ct);
                        await smtp.DisconnectAsync(true, ct);
                    }
                    // Gmail files sent mail itself; other servers need a copy put in Sent
                    if (!IsGmail && Folder(client, "sent") is IMailFolder sent)
                        try { await sent.OpenAsync(FolderAccess.ReadWrite, ct); await sent.AppendAsync(msg, MessageFlags.Seen, ct); } catch { }
                    return "Sent.";
                }
            }
            return "Unknown email tool.";
        }
        finally { try { await client.DisconnectAsync(true, CancellationToken.None); } catch { } }
    }

    // ids are "<folder>/<uid>", so a later email_read finds the message in the folder it was listed from
    static string Id(IMailFolder f, UniqueId uid) => $"{f.FullName}/{uid.Id}";

    static async Task<(IMailFolder, UniqueId)> Find(ImapClient c, string id, FolderAccess access, CancellationToken ct)
    {
        int slash = id.LastIndexOf('/');
        if (slash < 0 || !uint.TryParse(id[(slash + 1)..], out var n)) throw new ArgumentException("not an id from email_list: " + id);
        var folder = await c.GetFolderAsync(id[..slash], ct);
        await folder.OpenAsync(access, ct);
        return (folder, new UniqueId(n));
    }

    static IMailFolder? Folder(ImapClient c, string name)
    {
        IMailFolder? Special(SpecialFolder f)
        {
            if ((c.Capabilities & (ImapCapabilities.SpecialUse | ImapCapabilities.XList)) == 0) return null;
            try { return c.GetFolder(f); } catch { return null; }
        }
        IMailFolder? Named(params string[] names)
        {
            var all = c.GetFolders(c.PersonalNamespaces[0]);
            return all.FirstOrDefault(f => names.Any(n => f.Name.Equals(n, StringComparison.OrdinalIgnoreCase)));
        }
        return name.Trim().ToLowerInvariant() switch
        {
            "" or "inbox" => c.Inbox,
            "sent" or "sentitems" or "sent items" => Special(SpecialFolder.Sent) ?? Named("Sent", "Sent Items", "Sent Messages", "Sent Mail"),
            "drafts" => Special(SpecialFolder.Drafts) ?? Named("Drafts", "Draft"),
            "junk" or "junkemail" or "spam" => Special(SpecialFolder.Junk) ?? Named("Junk", "Spam", "Bulk Mail", "Junk E-mail"),
            "archive" => Special(SpecialFolder.Archive) ?? Special(SpecialFolder.All) ?? Named("Archive", "All Mail"),
            "trash" or "deleted" => Special(SpecialFolder.Trash) ?? Named("Trash", "Deleted", "Deleted Items", "Bin"),
            _ => Named(name),
        };
    }

    static async Task<string> List(ImapClient c, JsonNode input, CancellationToken ct)
    {
        var name = input["folder"]?.GetValue<string>() ?? "inbox";
        var folder = Folder(c, name) ?? throw new ArgumentException($"no folder called '{name}'");
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        int count = Math.Clamp(input["count"]?.GetValue<int>() ?? 10, 1, 25);

        SearchQuery q = SearchQuery.All;
        if (input["search"]?.GetValue<string>() is { Length: > 0 } s)
            // Gmail's own search syntax and ranking; everyone else gets a plain text search
            q = (c.Capabilities & ImapCapabilities.GMailExt1) != 0 ? SearchQuery.GMailRawSearch(s)
                : SearchQuery.SubjectContains(s).Or(SearchQuery.FromContains(s)).Or(SearchQuery.BodyContains(s));
        if (input["unread_only"]?.GetValue<bool>() == true) q = q.And(SearchQuery.NotSeen);

        var uids = await folder.SearchAsync(q, ct);
        if (uids.Count == 0) return "No emails found.";
        var newest = uids.OrderByDescending(u => u.Id).Take(count).ToList();
        var items = await folder.FetchAsync(newest, MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags, ct);
        var sb = new StringBuilder();
        foreach (var m in items.OrderByDescending(m => m.UniqueId.Id))
        {
            bool unread = m.Flags is MessageFlags f && !f.HasFlag(MessageFlags.Seen);
            sb.AppendLine($"[{Id(folder, m.UniqueId)}]");
            sb.AppendLine($"  {(unread ? "UNREAD " : "")}{m.Envelope?.Date?.LocalDateTime:ddd d MMM h:mm tt} from {m.Envelope?.From}");
            sb.AppendLine($"  {m.Envelope?.Subject}");
        }
        return sb.ToString();
    }

    static async Task<string> ReadOne(ImapClient c, string id, CancellationToken ct)
    {
        var (folder, uid) = await Find(c, id, FolderAccess.ReadWrite, ct);
        var m = await folder.GetMessageAsync(uid, ct);
        try { await folder.AddFlagsAsync(uid, MessageFlags.Seen, true, ct); } catch { }
        var body = m.TextBody ?? (m.HtmlBody != null ? Html.ToText(m.HtmlBody, paragraphs: true) : "");
        var files = m.Attachments.Select(a => a.ContentDisposition?.FileName ?? a.ContentType.Name).Where(n => n != null).ToList();
        return $"From: {m.From}\nTo: {m.To}\nCc: {m.Cc}\nDate: {m.Date.LocalDateTime:ddd d MMM yyyy h:mm tt}\nSubject: {m.Subject}\n" +
               (files.Count > 0 ? $"Attachments: {string.Join(", ", files)}\n" : "") + $"\n{body.Trim().Clip(MaxBody)}";
    }

    internal static MimeMessage Compose(JsonNode input, string? from = null)
    {
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(from ?? Address));
        foreach (var a in Desktop.ArrayOf(input["to"]) ?? new JsonArray()) msg.To.Add(MailboxAddress.Parse(a!.ToString()));
        foreach (var a in Desktop.ArrayOf(input["cc"]) ?? new JsonArray()) msg.Cc.Add(MailboxAddress.Parse(a!.ToString()));
        if (msg.To.Count == 0) throw new ArgumentException("needs at least one 'to' address");
        msg.Subject = input["subject"]?.GetValue<string>() ?? "";
        msg.Body = new TextPart("plain") { Text = Tools.S(input, "body") };
        return msg;
    }

    /// A reply that threads properly in the other person's mail app.
    internal static MimeMessage Reply(MimeMessage orig, string body, string? from = null)
    {
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(from ?? Address));
        msg.To.AddRange(orig.ReplyTo.Count > 0 ? orig.ReplyTo : orig.From);
        msg.Subject = orig.Subject?.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) == true ? orig.Subject : "Re: " + orig.Subject;
        if (orig.MessageId != null)
        {
            msg.InReplyTo = orig.MessageId;
            foreach (var r in orig.References) msg.References.Add(r);
            msg.References.Add(orig.MessageId);
        }
        msg.Body = new TextPart("plain") { Text = body };
        return msg;
    }
}
