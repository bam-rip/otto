using System.Text.Json.Nodes;

namespace Otto;

/// The email tools, the same for every email service (Outlook through Graph, everything else through IMAP),
/// apart from what a listing includes and which folder names the service uses.
static class EmailTools
{
    public static JsonArray Definitions(string listReturns, string folders) => JsonNode.Parse(Template.Replace("LIST_RETURNS", listReturns).Replace("FOLDERS", folders))!.AsArray();

    const string Template = """
    [
      {"name":"email_list","description":"List emails from the user's mailbox, newest first. Returns LIST_RETURNS.",
       "input_schema":{"type":"object","properties":{
         "folder":{"type":"string","description":"FOLDERS"},
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
    """;
}
