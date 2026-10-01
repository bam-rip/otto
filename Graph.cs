using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Otto;

/// Outlook / Hotmail / Microsoft 365 email and calendar through Microsoft Graph.
/// Sign-in uses the device-code flow (enter a code at microsoft.com/devicelogin), so Otto never sees the
/// password. Microsoft requires each app to have its own free "app registration"; its client ID goes in
/// settings (see README). The refresh token is kept in Windows Credential Manager.
static class Graph
{
    const string Scopes = "offline_access User.Read Mail.ReadWrite Mail.Send Calendars.ReadWrite";
    const string Login = "https://login.microsoftonline.com/common/oauth2/v2.0";
    const string Api = "https://graph.microsoft.com/v1.0";
    const string TokenTarget = "Otto:microsoft";
    const string Reg = @"Software\Otto\Mail";
    const int MaxBody = 4_000;

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    static string? accessToken;
    static DateTime accessExpires;

    public static string ClientId
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(Reg); return k?.GetValue("ClientId") as string ?? ""; }
        set { using var k = Registry.CurrentUser.CreateSubKey(Reg); k.SetValue("ClientId", value.Trim()); }
    }

    public static string? Account
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(Reg); return k?.GetValue("Account") as string; }
        set { using var k = Registry.CurrentUser.CreateSubKey(Reg); if (value == null) k.DeleteValue("Account", false); else k.SetValue("Account", value); }
    }

    public static bool SignedIn => ClientId.Length > 0 && KeyStore.ApiKey(TokenTarget, null) != null;

    // ---------------- sign-in ----------------

    public sealed record DeviceCode(string UserCode, string Url, string DeviceCodeValue, int Interval, DateTime Expires);

    public static async Task<DeviceCode> StartSignIn()
    {
        if (ClientId.Length == 0) throw new InvalidOperationException("Paste the app's client ID first.");
        var j = await PostForm(Login + "/devicecode", new() { ["client_id"] = ClientId, ["scope"] = Scopes });
        return new DeviceCode(j["user_code"]!.GetValue<string>(), j["verification_uri"]!.GetValue<string>(),
            j["device_code"]!.GetValue<string>(), j["interval"]?.GetValue<int>() ?? 5,
            DateTime.Now.AddSeconds(j["expires_in"]?.GetValue<int>() ?? 900));
    }

    /// Polls until the user finishes signing in on the web page. Returns the account's email address.
    public static async Task<string> FinishSignIn(DeviceCode code, CancellationToken ct)
    {
        int interval = code.Interval;
        while (DateTime.Now < code.Expires)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            var j = await PostForm(Login + "/token", new()
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = ClientId,
                ["device_code"] = code.DeviceCodeValue,
            }, throwOnError: false);
            var error = j["error"]?.GetValue<string>();
            if (error == "authorization_pending") continue;
            if (error == "slow_down") { interval += 5; continue; }
            if (error != null) throw new InvalidOperationException(j["error_description"]?.GetValue<string>()?.Split('\n')[0] ?? error);
            Keep(j);
            var me = await Get("/me?$select=mail,userPrincipalName", ct);
            var who = me["mail"]?.GetValue<string>() ?? me["userPrincipalName"]?.GetValue<string>() ?? "your account";
            Account = who;
            return who;
        }
        throw new TimeoutException("The sign-in code expired. Try again.");
    }

    public static void SignOut()
    {
        KeyStore.Delete(TokenTarget);
        Account = null;
        accessToken = null;
    }

    static void Keep(JsonNode tokens)
    {
        accessToken = tokens["access_token"]!.GetValue<string>();
        accessExpires = DateTime.Now.AddSeconds((tokens["expires_in"]?.GetValue<int>() ?? 3600) - 120);
        if (tokens["refresh_token"]?.GetValue<string>() is string refresh) KeyStore.Save(refresh, TokenTarget);
    }

    static async Task<string> Token(CancellationToken ct)
    {
        if (accessToken != null && DateTime.Now < accessExpires) return accessToken;
        var refresh = KeyStore.ApiKey(TokenTarget, null) ?? throw new InvalidOperationException("Not signed in to email. Sign in from Otto's settings.");
        var j = await PostForm(Login + "/token", new()
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["refresh_token"] = refresh,
            ["scope"] = Scopes,
        }, throwOnError: false, ct);
        if (j["error"] != null)
        {
            SignOut();
            throw new InvalidOperationException("The email sign-in expired. Sign in again from Otto's settings.");
        }
        Keep(j);
        return accessToken!;
    }

    // ---------------- tools ----------------

    public static JsonNode ToolDefinitions() => JsonNode.Parse("""
    [
      {"name":"email_list","description":"List emails from the user's mailbox, newest first. Returns id, from, subject, date, read/unread and a one-line preview.",
       "input_schema":{"type":"object","properties":{
         "folder":{"type":"string","description":"inbox (default), sentitems, drafts, junkemail, archive"},
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
        "required":["body"]}},
      {"name":"calendar_list","description":"List calendar events in a date range (default: today and the next 7 days).",
       "input_schema":{"type":"object","properties":{"from":{"type":"string","description":"date or date-time, e.g. 2026-10-02"},"days":{"type":"integer"}}}},
      {"name":"calendar_add","description":"Add an event to the user's calendar. Times are local, ISO format like 2026-10-03T14:00.",
       "input_schema":{"type":"object","properties":{
         "subject":{"type":"string"},"start":{"type":"string"},"end":{"type":"string"},
         "location":{"type":"string"},"notes":{"type":"string"},"all_day":{"type":"boolean"}},
        "required":["subject","start"]}}
    ]
    """)!;

    public static bool Handles(string tool) => tool.StartsWith("email_") || tool.StartsWith("calendar_");

    public static string Describe(string name, JsonNode input) => name switch
    {
        "email_list" => input["search"] != null ? $"Searching email for “{input["search"]}”" : "Checking email",
        "email_read" => "Reading an email",
        "email_draft" => $"Drafting email to {Join(input["to"])}",
        "email_send" => input["reply_to_id"] != null ? "Replying to an email" : $"Emailing {Join(input["to"])}",
        "calendar_list" => "Checking the calendar",
        "calendar_add" => $"Adding “{input["subject"]}” to the calendar",
        _ => name,
    };

    static string Join(JsonNode? list) => Desktop.ArrayOf(list) is JsonArray a ? string.Join(", ", a.Select(x => x?.ToString())) : "";

    public static async Task<string> Run(string name, JsonNode input, Func<string, bool> confirm, CancellationToken ct)
    {
        switch (name)
        {
            case "email_list":
            {
                var folder = input["folder"]?.GetValue<string>() ?? "inbox";
                int count = Math.Clamp(input["count"]?.GetValue<int>() ?? 10, 1, 25);
                var q = $"/me/mailFolders/{Uri.EscapeDataString(folder)}/messages?$top={count}&$select=id,from,subject,receivedDateTime,isRead,bodyPreview";
                if (input["search"]?.GetValue<string>() is { Length: > 0 } s) q += "&$search=" + Uri.EscapeDataString($"\"{s.Replace("\"", "")}\"");
                else if (input["unread_only"]?.GetValue<bool>() != true) q += "&$orderby=receivedDateTime desc";
                // Outlook can't combine search with filters; unread-only applies to plain listing
                else if (input["unread_only"]?.GetValue<bool>() == true) q += "&$filter=isRead eq false";
                var list = (await Get(q, ct))["value"]!.AsArray();
                if (list.Count == 0) return "No emails found.";
                var sb = new StringBuilder();
                foreach (var m in list)
                {
                    var from = m!["from"]?["emailAddress"];
                    sb.AppendLine($"[{m["id"]}]");
                    sb.AppendLine($"  {(m["isRead"]?.GetValue<bool>() == false ? "UNREAD " : "")}{Local(m["receivedDateTime"])} from {from?["name"]} <{from?["address"]}>");
                    sb.AppendLine($"  {m["subject"]}: {Clip(m["bodyPreview"]?.ToString() ?? "", 140)}");
                }
                return sb.ToString();
            }
            case "email_read":
            {
                var id = Uri.EscapeDataString(S(input, "id"));
                var m = await Get($"/me/messages/{id}?$select=from,toRecipients,ccRecipients,subject,receivedDateTime,body", ct);
                _ = Send(HttpMethod.Patch, $"/me/messages/{id}", new JsonObject { ["isRead"] = true }, ct);
                var body = m["body"]?["contentType"]?.GetValue<string>() == "html" ? HtmlToText(m["body"]!["content"]!.ToString()) : m["body"]?["content"]?.ToString() ?? "";
                return $"From: {Addr(m["from"])}\nTo: {Addrs(m["toRecipients"])}\nCc: {Addrs(m["ccRecipients"])}\n" +
                       $"Date: {Local(m["receivedDateTime"])}\nSubject: {m["subject"]}\n\n{Clip(body, MaxBody)}";
            }
            case "email_draft":
            {
                var msg = Message(input);
                var made = await Send(HttpMethod.Post, "/me/messages", msg, ct);
                return $"Saved as a draft (id {made?["id"]}). It's in the Drafts folder in Outlook.";
            }
            case "email_send":
            {
                if (input["reply_to_id"]?.GetValue<string>() is string replyTo)
                {
                    var orig = await Get($"/me/messages/{Uri.EscapeDataString(replyTo)}?$select=from,subject", ct);
                    if (!confirm($"Send this reply to {Addr(orig["from"])}?\n\nRe: {orig["subject"]}\n\n{Clip(S(input, "body"), 600)}")) return "User declined.";
                    await Send(HttpMethod.Post, $"/me/messages/{Uri.EscapeDataString(replyTo)}/reply", new JsonObject { ["comment"] = S(input, "body") }, ct);
                    return "Reply sent.";
                }
                var msg = Message(input);
                if (!confirm($"Send this email to {Join(input["to"])}?\n\nSubject: {input["subject"]}\n\n{Clip(S(input, "body"), 600)}")) return "User declined.";
                await Send(HttpMethod.Post, "/me/sendMail", new JsonObject { ["message"] = msg, ["saveToSentItems"] = true }, ct);
                return "Sent.";
            }
            case "calendar_list":
            {
                var from = input["from"]?.GetValue<string>() is string f && DateTime.TryParse(f, out var d) ? d : DateTime.Today;
                int days = Math.Clamp(input["days"]?.GetValue<int>() ?? 7, 1, 62);
                var q = $"/me/calendarView?startDateTime={from.ToUniversalTime():o}&endDateTime={from.AddDays(days).ToUniversalTime():o}" +
                        "&$select=subject,start,end,location,isAllDay&$orderby=start/dateTime&$top=50";
                var list = (await Get(q, ct, localTime: true))["value"]!.AsArray();
                if (list.Count == 0) return $"Nothing on the calendar from {from:ddd d MMM} for {days} days.";
                var sb = new StringBuilder();
                foreach (var e in list)
                {
                    var start = DateTime.Parse(e!["start"]!["dateTime"]!.ToString());
                    var end = DateTime.Parse(e["end"]!["dateTime"]!.ToString());
                    var when = e["isAllDay"]?.GetValue<bool>() == true ? $"{start:ddd d MMM} (all day)" : $"{start:ddd d MMM h:mm tt}-{end:h:mm tt}";
                    var where = e["location"]?["displayName"]?.ToString();
                    sb.AppendLine($"{when}: {e["subject"]}{(string.IsNullOrEmpty(where) ? "" : $" @ {where}")}");
                }
                return sb.ToString();
            }
            case "calendar_add":
            {
                var start = DateTime.Parse(S(input, "start"));
                bool allDay = input["all_day"]?.GetValue<bool>() == true;
                var end = input["end"]?.GetValue<string>() is string e ? DateTime.Parse(e) : allDay ? start.Date.AddDays(1) : start.AddHours(1);
                var tz = TimeZoneInfo.Local.Id; // Windows time zone ids are what Graph expects
                var ev = new JsonObject
                {
                    ["subject"] = S(input, "subject"),
                    ["start"] = new JsonObject { ["dateTime"] = (allDay ? start.Date : start).ToString("s"), ["timeZone"] = tz },
                    ["end"] = new JsonObject { ["dateTime"] = (allDay ? end.Date : end).ToString("s"), ["timeZone"] = tz },
                    ["isAllDay"] = allDay,
                };
                if (input["location"]?.GetValue<string>() is { Length: > 0 } loc) ev["location"] = new JsonObject { ["displayName"] = loc };
                if (input["notes"]?.GetValue<string>() is { Length: > 0 } notes) ev["body"] = new JsonObject { ["contentType"] = "text", ["content"] = notes };
                await Send(HttpMethod.Post, "/me/events", ev, ct);
                return $"Added “{ev["subject"]}” on {start:ddd d MMM}{(allDay ? "" : $" at {start:h:mm tt}")}.";
            }
            default: throw new ArgumentException($"Unknown tool {name}");
        }
    }

    static JsonObject Message(JsonNode input)
    {
        JsonArray Recipients(JsonNode? list) => new((Desktop.ArrayOf(list) ?? new JsonArray())
            .Select(a => (JsonNode)new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = a!.ToString() } }).ToArray());
        var to = Recipients(input["to"]);
        if (to.Count == 0) throw new ArgumentException("needs at least one 'to' address");
        return new JsonObject
        {
            ["subject"] = input["subject"]?.GetValue<string>() ?? "",
            ["body"] = new JsonObject { ["contentType"] = "text", ["content"] = S(input, "body") },
            ["toRecipients"] = to,
            ["ccRecipients"] = Recipients(input["cc"]),
        };
    }

    // ---------------- HTTP ----------------

    static async Task<JsonNode> Get(string path, CancellationToken ct, bool localTime = false)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Api + path);
        req.Headers.Add("Authorization", "Bearer " + await Token(ct));
        if (localTime) req.Headers.Add("Prefer", $"outlook.timezone=\"{TimeZoneInfo.Local.Id}\"");
        if (path.Contains("$search")) req.Headers.Add("ConsistencyLevel", "eventual");
        using var res = await Http.SendAsync(req, ct);
        return await Read(res, ct) ?? new JsonObject();
    }

    static async Task<JsonNode?> Send(HttpMethod method, string path, JsonNode body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, Api + path) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Add("Authorization", "Bearer " + await Token(ct));
        using var res = await Http.SendAsync(req, ct);
        return await Read(res, ct);
    }

    static async Task<JsonNode?> Read(HttpResponseMessage res, CancellationToken ct)
    {
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            string msg;
            try { msg = JsonNode.Parse(text)?["error"]?["message"]?.GetValue<string>() ?? text; } catch { msg = text; }
            throw new InvalidOperationException($"Outlook said ({(int)res.StatusCode}): {Clip(msg, 300)}");
        }
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    static async Task<JsonNode> PostForm(string url, Dictionary<string, string> form, bool throwOnError = true, CancellationToken ct = default)
    {
        using var res = await Http.PostAsync(url, new FormUrlEncodedContent(form), ct);
        var j = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct)) ?? new JsonObject();
        if (throwOnError && !res.IsSuccessStatusCode)
            throw new InvalidOperationException(j["error_description"]?.GetValue<string>()?.Split('\n')[0] ?? $"sign-in failed ({(int)res.StatusCode})");
        return j;
    }

    // ---------------- small helpers ----------------

    static string S(JsonNode input, string key) => input[key]?.GetValue<string>() ?? throw new ArgumentException($"missing '{key}'");
    static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "…";
    static string Addr(JsonNode? r) => r?["emailAddress"] is JsonNode e ? $"{e["name"]} <{e["address"]}>" : "";
    static string Addrs(JsonNode? list) => list is JsonArray a ? string.Join(", ", a.Select(Addr)) : "";
    static string Local(JsonNode? utc) => DateTime.TryParse(utc?.ToString(), out var d) ? d.ToLocalTime().ToString("ddd d MMM h:mm tt") : "";

    static string HtmlToText(string html)
    {
        html = Regex.Replace(html, "<(script|style|head)[^>]*>.*?</\\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, "<(br|/p|/div|/li|/tr|/h[1-6])[^>]*>", "\n", RegexOptions.IgnoreCase);
        var text = WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", ""));
        text = Regex.Replace(text, "[ \t\u00a0]+", " ");
        return Regex.Replace(text, "\\s*\n\\s*(\n\\s*)+", "\n\n").Trim();
    }
}

/// The "enter this code" window for signing in to Outlook/Hotmail.
static class GraphSignIn
{
    /// Returns the signed-in address, or null if cancelled / failed (with the reason in 'error').
    public static string? Show(IWin32Window owner, out string? error)
    {
        error = null;
        Graph.DeviceCode code;
        try { code = Graph.StartSignIn().GetAwaiter().GetResult(); }
        catch (Exception e) { error = e.Message; return null; }

        using var f = new Form
        {
            Text = "Sign in to Outlook",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, TopMost = true,
            AutoScaleMode = AutoScaleMode.Font, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Font = new Font("Segoe UI", 9.5f), Padding = new Padding(18),
            Icon = Icons.App(),
        };
        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        f.Controls.Add(flow);
        flow.Controls.Add(new Label { Text = "1. Copy this code:", AutoSize = true });
        flow.Controls.Add(new Label { Text = code.UserCode, AutoSize = true, Font = new Font("Consolas", 22f, FontStyle.Bold), Margin = new Padding(0, 6, 0, 10) });
        flow.Controls.Add(new Label { Text = $"2. Paste it at {code.Url}, then sign in and allow access.", AutoSize = true });
        var open = new Button { Text = "Copy code and open the page", AutoSize = true, Padding = new Padding(8, 3, 8, 3), Margin = new Padding(0, 12, 0, 6) };
        flow.Controls.Add(open);
        var status = new Label { Text = "Waiting for you to finish signing in…", AutoSize = true, ForeColor = Color.DimGray };
        flow.Controls.Add(status);
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Margin = new Padding(0, 14, 0, 0) };
        flow.Controls.Add(cancel);
        f.CancelButton = cancel;

        open.Click += (_, _) => { Clipboard.SetText(code.UserCode); Tools.OpenUrl(code.Url); };
        string? who = null, failure = null;
        using var cts = new CancellationTokenSource();
        f.Shown += async (_, _) =>
        {
            try { who = await Graph.FinishSignIn(code, cts.Token); f.DialogResult = DialogResult.OK; }
            catch (OperationCanceledException) { }
            catch (Exception e) { failure = e.Message; status.Text = "Sign-in failed: " + e.Message; status.ForeColor = Color.Firebrick; }
        };
        f.FormClosing += (_, _) => cts.Cancel();
        f.ShowDialog(owner);
        error = failure;
        return who;
    }
}
