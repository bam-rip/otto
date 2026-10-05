using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Otto;

/// Talks to whichever AI service is configured. The agent keeps its history in Anthropic's format;
/// for OpenAI-style services it's translated on the way out and the reply translated back, so the
/// rest of Otto never needs to know which one it's using.
static class Llm
{
    // keep the warmed connection alive between messages (the default drops it after a minute idle)
    static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromMinutes(5),
    };

    static DateTime lastWarm;

    /// Open the HTTPS connection ahead of time (called when the panel opens), so the first request
    /// skips the DNS + TLS handshake, typically 150-400 ms.
    public static void Warm()
    {
        if ((DateTime.Now - lastWarm).TotalSeconds < 60) return;
        lastWarm = DateTime.Now;
        var url = Providers.Current().BaseUrl;
        _ = Task.Run(async () =>
        {
            try { using var r = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url)); }
            catch { }
        });
    }

    /// One model call. 'system' and 'tools' may be null. Returns { content, stop_reason, usage } in
    /// Anthropic's shape whichever service answered. onStream gets text as it arrives (null = don't stream).
    public static Task<JsonNode> CompleteAsync(AiConfig cfg, string model, string? system, JsonArray? tools,
        JsonArray messages, int maxTokens, bool lowEffort, Action<string, bool>? onStream, CancellationToken ct) =>
        cfg.Provider.IsAnthropic
            ? AnthropicAsync(cfg, model, system, tools, messages, maxTokens, lowEffort, onStream, ct)
            : OpenAiAsync(cfg, model, system, tools, messages, maxTokens, onStream, ct);

    // ---------------- Anthropic ----------------

    static async Task<JsonNode> AnthropicAsync(AiConfig cfg, string model, string? system, JsonArray? tools,
        JsonArray messages, int maxTokens, bool lowEffort, Action<string, bool>? onStream, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            // cache tools + system + history so each step of a task re-reads them at ~10% of the price
            ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
            ["messages"] = StripExtras(messages),
        };
        if (system != null)
        {
            // the system prompt + tools barely change, so keep them cached for an hour: a message after a
            // coffee break re-reads them at 10% instead of re-writing them. The conversation uses the 5-minute cache.
            body["system"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = system,
                    ["cache_control"] = new JsonObject { ["type"] = "ephemeral", ["ttl"] = "1h" },
                },
            };
        }
        if (tools != null) body["tools"] = tools;
        // low effort = less thinking = fewer billed tokens (Haiku doesn't take the setting)
        if (lowEffort && !model.Contains("haiku")) body["output_config"] = new JsonObject { ["effort"] = "low" };
        body["stream"] = true;

        HttpRequestMessage Make()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, cfg.BaseUrl + "/messages")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            AnthropicHeaders(req, cfg.Key ?? throw new InvalidOperationException("No API key set."));
            return req;
        }

        using var res = await SendWithRetry(Make, ct);
        var content = new JsonArray();
        var usage = new JsonObject();
        string? stopReason = null;
        var json = new Dictionary<int, StringBuilder>(); // tool inputs arrive as JSON fragments
        var shown = new HashSet<int>();

        await foreach (var ev in Events(res, ct))
        {
            switch (ev["type"]?.GetValue<string>())
            {
                case "message_start":
                    if (ev["message"]?["usage"] is JsonObject u0) usage = (JsonObject)u0.DeepClone();
                    break;
                case "content_block_start":
                {
                    var block = ev["content_block"]!.DeepClone().AsObject();
                    if (block["type"]?.GetValue<string>() == "tool_use") json[content.Count] = new StringBuilder();
                    content.Add(block);
                    break;
                }
                case "content_block_delta":
                {
                    int i = ev["index"]!.GetValue<int>();
                    var block = content[i]!.AsObject();
                    var d = ev["delta"]!;
                    switch (d["type"]?.GetValue<string>())
                    {
                        case "text_delta":
                            var text = (block["text"]?.GetValue<string>() ?? "") + d["text"]!.GetValue<string>();
                            block["text"] = text;
                            if (text.Trim().Length > 0) onStream?.Invoke(text, shown.Add(i));
                            break;
                        case "input_json_delta": json[i].Append(d["partial_json"]!.GetValue<string>()); break;
                        case "thinking_delta": block["thinking"] = (block["thinking"]?.GetValue<string>() ?? "") + d["thinking"]!.GetValue<string>(); break;
                        case "signature_delta": block["signature"] = d["signature"]!.GetValue<string>(); break;
                    }
                    break;
                }
                case "content_block_stop":
                {
                    int i = ev["index"]!.GetValue<int>();
                    if (json.TryGetValue(i, out var sb)) content[i]!["input"] = ParseArgs(sb.ToString());
                    break;
                }
                case "message_delta":
                    stopReason = ev["delta"]?["stop_reason"]?.GetValue<string>() ?? stopReason;
                    if (ev["usage"] is JsonObject u1)
                        foreach (var (k, v) in u1) if (v != null) usage[k] = v.DeepClone();
                    break;
                case "error":
                    throw new Exception("API: " + (ev["error"]?["message"]?.GetValue<string>() ?? "stream error"));
            }
        }
        return new JsonObject { ["content"] = content, ["stop_reason"] = stopReason, ["usage"] = usage };
    }

    static void AnthropicHeaders(HttpRequestMessage req, string key)
    {
        req.Headers.Add("x-api-key", key);
        req.Headers.Add("anthropic-version", "2023-06-01");
        var ws = Environment.GetEnvironmentVariable("ANTHROPIC_WORKSPACE_ID");
        if (!string.IsNullOrEmpty(ws)) req.Headers.Add("anthropic-workspace-id", ws);
    }

    /// Provider-specific data kept on a tool_use block (e.g. Gemini's thought_signature). Only OpenAI-style
    /// requests send it back; it's removed before anything goes to Anthropic.
    internal const string Extra = "_extra";

    internal static JsonArray StripExtras(JsonArray messages)
    {
        foreach (var m in messages)
            if (m?["content"] is JsonArray blocks)
                foreach (var b in blocks)
                    if (b is JsonObject o) o.Remove(Extra);
        return messages;
    }

    // ---------------- OpenAI-style (OpenAI, Gemini, Grok, DeepSeek, OpenRouter, local) ----------------

    static async Task<JsonNode> OpenAiAsync(AiConfig cfg, string model, string? system, JsonArray? tools,
        JsonArray messages, int maxTokens, Action<string, bool>? onStream, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = ToOpenAiMessages(system, messages, cfg.Vision),
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
        };
        // OpenAI's newer models only accept max_completion_tokens; everyone else knows max_tokens
        body[cfg.Provider.Id == "openai" ? "max_completion_tokens" : "max_tokens"] = maxTokens;
        if (tools != null && tools.Count > 0)
        {
            body["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t!["name"]!.DeepClone(),
                    ["description"] = t["description"]?.DeepClone(),
                    ["parameters"] = t["input_schema"]!.DeepClone(),
                },
            }).ToArray());
        }

        HttpRequestMessage Make()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, cfg.BaseUrl + "/chat/completions")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrWhiteSpace(cfg.Key)) req.Headers.Add("Authorization", "Bearer " + cfg.Key);
            if (cfg.Provider.Id == "openrouter") req.Headers.Add("X-Title", "Otto");
            return req;
        }

        using var res = await SendWithRetry(Make, ct);
        var text = new StringBuilder();
        var calls = new SortedDictionary<int, (string id, string name, StringBuilder args)>();
        var extras = new Dictionary<int, JsonNode>();
        string? finish = null;
        JsonNode? usage = null;

        await foreach (var ev in Events(res, ct))
        {
            if (ev["error"] is JsonNode err) throw new Exception("API: " + (err["message"]?.ToString() ?? err.ToJsonString()));
            if (ev["usage"] is JsonObject u) usage = u;
            if (ev["choices"] is not JsonArray choices || choices.Count == 0) continue;
            var c = choices[0]!;
            if (c["finish_reason"]?.GetValue<string>() is string fr) finish = fr;
            var d = c["delta"];
            if (d == null) continue;
            if (d["content"]?.GetValue<string>() is { Length: > 0 } piece)
            {
                bool first = text.ToString().Trim().Length == 0;
                text.Append(piece);
                if (text.ToString().Trim().Length > 0) onStream?.Invoke(text.ToString(), first);
            }
            if (d["tool_calls"] is JsonArray tcs)
            {
                foreach (var tc in tcs)
                {
                    int i = tc!["index"]?.GetValue<int>() ?? calls.Count;
                    (string id, string name, StringBuilder args) cur = calls.TryGetValue(i, out var have) ? have : ("", "", new StringBuilder());
                    if (tc["id"]?.GetValue<string>() is { Length: > 0 } id) cur.id = id;
                    if (tc["function"]?["name"]?.GetValue<string>() is { Length: > 0 } nm) cur.name = nm;
                    if (tc["function"]?["arguments"]?.GetValue<string>() is string a) cur.args.Append(a);
                    if (tc["extra_content"] is JsonNode extra) extras[i] = extra.DeepClone();
                    calls[i] = cur;
                }
            }
        }

        var content = new JsonArray();
        if (text.Length > 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = text.ToString() });
        foreach (var (i, call) in calls)
        {
            var block = new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = call.id.Length > 0 ? call.id : "call_" + Guid.NewGuid().ToString("N")[..12], // Gemini sometimes omits ids
                ["name"] = call.name,
                ["input"] = ParseArgs(call.args.ToString()),
            };
            // Gemini 3 requires its thought_signature to come back with the call in later requests
            if (extras.TryGetValue(i, out var extra)) block[Extra] = extra;
            content.Add(block);
        }
        long prompt = usage?["prompt_tokens"]?.GetValue<long>() ?? 0;
        long cached = usage?["prompt_tokens_details"]?["cached_tokens"]?.GetValue<long>() ?? 0;
        return new JsonObject
        {
            ["content"] = content,
            ["stop_reason"] = calls.Count > 0 ? "tool_use" : finish == "length" ? "max_tokens" : "end_turn",
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = prompt - cached,
                ["cache_read_input_tokens"] = cached,
                ["output_tokens"] = usage?["completion_tokens"]?.GetValue<long>() ?? 0,
            },
        };
    }

    /// Anthropic-format history → OpenAI chat messages. Tool results become "tool" messages; screenshots
    /// can't go inside those, so they follow in a user message (or become a note if the model is text-only).
    internal static JsonArray ToOpenAiMessages(string? system, JsonArray messages, bool vision)
    {
        var outp = new JsonArray();
        if (system != null) outp.Add(new JsonObject { ["role"] = "system", ["content"] = system });
        foreach (var m in messages)
        {
            var role = m!["role"]!.GetValue<string>();
            if (m["content"] is JsonValue v) { outp.Add(new JsonObject { ["role"] = role, ["content"] = v.ToString() }); continue; }
            var blocks = m["content"]!.AsArray();

            if (role == "assistant")
            {
                var text = string.Concat(blocks.Where(b => b?["type"]?.GetValue<string>() == "text").Select(b => b!["text"]!.GetValue<string>()));
                var calls = new JsonArray(blocks.Where(b => b?["type"]?.GetValue<string>() == "tool_use").Select(b =>
                {
                    var call = new JsonObject
                    {
                        ["id"] = b!["id"]!.DeepClone(),
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = b["name"]!.DeepClone(), ["arguments"] = b["input"]!.ToJsonString() },
                    };
                    if (b[Extra] is JsonNode extra) call["extra_content"] = extra.DeepClone();
                    return (JsonNode)call;
                }).ToArray());
                var msg = new JsonObject { ["role"] = "assistant", ["content"] = text.Length > 0 ? text : null };
                if (calls.Count > 0) msg["tool_calls"] = calls;
                outp.Add(msg);
                continue;
            }

            // user turn: tool results and/or plain content
            var images = new List<JsonObject>(); // not a JsonArray: a node can only belong to one array
            var userParts = new JsonArray();
            foreach (var b in blocks)
            {
                var type = b!["type"]!.GetValue<string>();
                if (type == "tool_result")
                {
                    var sb = new StringBuilder();
                    if (b["content"] is JsonValue tv) sb.Append(tv.ToString());
                    else if (b["content"] is JsonArray parts)
                    {
                        foreach (var p in parts)
                        {
                            if (p?["type"]?.GetValue<string>() == "text") sb.AppendLine(p["text"]!.GetValue<string>());
                            else if (p?["type"]?.GetValue<string>() == "image")
                            {
                                if (vision) { images.Add(ImagePart(p)); sb.AppendLine("(screenshot attached below)"); }
                                else sb.AppendLine("(screenshot omitted: this model can't see images; rely on observe:'ui' text lists)");
                            }
                        }
                    }
                    outp.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = b["tool_use_id"]!.DeepClone(), ["content"] = sb.Length > 0 ? sb.ToString() : "(no output)" });
                }
                else if (type == "text") userParts.Add(new JsonObject { ["type"] = "text", ["text"] = b["text"]!.DeepClone() });
                else if (type == "image" && vision) userParts.Add(ImagePart(b));
            }
            foreach (var img in images) userParts.Add(img);
            if (userParts.Count > 0)
            {
                if (images.Count > 0) userParts.Insert(0, new JsonObject { ["type"] = "text", ["text"] = "Screenshot from your last action:" });
                outp.Add(new JsonObject { ["role"] = "user", ["content"] = userParts });
            }
        }
        return outp;
    }

    static JsonObject ImagePart(JsonNode img) => new()
    {
        ["type"] = "image_url",
        ["image_url"] = new JsonObject
        {
            ["url"] = $"data:{img["source"]!["media_type"]};base64,{img["source"]!["data"]}",
        },
    };

    // ---------------- voice transcription ----------------

    /// Gemini takes audio in a normal chat message; OpenAI has a dedicated transcription endpoint.
    /// Claude doesn't accept audio, and other OpenAI-style services vary too much to rely on.
    public static bool CanTranscribe(AiConfig cfg) =>
        !string.IsNullOrWhiteSpace(cfg.Key) && cfg.Provider.Id is "gemini" or "openai";

    public static async Task<string> TranscribeAsync(AiConfig cfg, byte[] wav, CancellationToken ct)
    {
        if (cfg.Provider.Id == "openai")
        {
            HttpRequestMessage MakeOpenAi()
            {
                var form = new MultipartFormDataContent
                {
                    { new StringContent("gpt-4o-mini-transcribe"), "model" },
                    { new ByteArrayContent(wav) { Headers = { ContentType = new("audio/wav") } }, "file", "speech.wav" },
                };
                var req = new HttpRequestMessage(HttpMethod.Post, cfg.BaseUrl + "/audio/transcriptions") { Content = form };
                req.Headers.Add("Authorization", "Bearer " + cfg.Key);
                return req;
            }
            using var res = await SendWithRetry(MakeOpenAi, ct);
            return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))?["text"]?.GetValue<string>()?.Trim() ?? "";
        }

        // Gemini (OpenAI-compatible endpoint): one user message with the instruction and the audio
        var body = new JsonObject
        {
            ["model"] = cfg.Fast,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = "Transcribe this voice message exactly as spoken, with normal punctuation. " +
                                       "Reply with only the transcript. If nothing intelligible was said, reply with nothing.",
                        },
                        new JsonObject
                        {
                            ["type"] = "input_audio",
                            ["input_audio"] = new JsonObject { ["data"] = Convert.ToBase64String(wav), ["format"] = "wav" },
                        },
                    },
                },
            },
            ["max_tokens"] = 800,
        };
        HttpRequestMessage MakeGemini()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, cfg.BaseUrl + "/chat/completions")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("Authorization", "Bearer " + cfg.Key);
            return req;
        }
        using var r = await SendWithRetry(MakeGemini, ct);
        var j = JsonNode.Parse(await r.Content.ReadAsStringAsync(ct));
        return j?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()?.Trim() ?? "";
    }

    // ---------------- shared ----------------

    /// Model list for the settings window: GET {base}/models works on every provider here.
    public static async Task<List<string>> ListModelsAsync(Provider p, string baseUrl, string? key)
    {
        if (Providers.KeyInTheClear(baseUrl, key) is string clear) throw new InvalidOperationException(clear);
        using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/models");
        if (p.IsAnthropic) AnthropicHeaders(req, key ?? throw new InvalidOperationException("paste a key first"));
        else if (!string.IsNullOrWhiteSpace(key)) req.Headers.Add("Authorization", "Bearer " + key);
        using var res = await Http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw ApiError((int)res.StatusCode, text);
        var data = JsonNode.Parse(text)?["data"]?.AsArray() ?? new JsonArray();
        return data.Select(m => m?["id"]?.GetValue<string>() ?? "").Where(s => s.Length > 0)
                   .Select(s => s.StartsWith("models/") ? s[7..] : s) // Gemini prefixes ids
                   .Where(IsChatModel)
                   .OrderBy(s => s).ToList();
    }

    /// Provider model lists include speech, image, video, music and embedding models that can't chat
    /// (picking one gives errors like "Multiturn chat is not enabled for this model").
    static readonly string[] NotChat =
    {
        "tts", "live", "transcribe", "embedding", "embed", "image", "imagen", "veo", "lyria", "aqa", "audio",
        "robotics", "nano-banana", "deep-research", "antigravity", "computer-use", "whisper", "dall-e",
        "moderation", "realtime", "translate", "rerank", "sora", "omni-moderation", "babbage", "davinci",
    };

    static bool IsChatModel(string id)
    {
        var s = id.ToLowerInvariant();
        return !NotChat.Any(s.Contains);
    }

    internal static JsonNode ParseArgs(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return new JsonObject();
        try { return JsonNode.Parse(s) ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); } // cut off mid-call
    }

    /// Server-sent events: each "data: {...}" line as JSON, until the stream ends or says [DONE].
    static async IAsyncEnumerable<JsonNode> Events(HttpResponseMessage res, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(ct));
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (!line.StartsWith("data:")) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") yield break;
            if (data.Length == 0) continue;
            JsonNode? ev;
            try { ev = JsonNode.Parse(data); } catch (JsonException) { continue; }
            if (ev != null) yield return ev;
        }
    }

    /// Overloaded (529), rate-limited (429), server hiccups (5xx) and dropped connections are usually gone
    /// a few seconds later, so retry those up to 3 times instead of failing the whole task.
    static async Task<HttpResponseMessage> SendWithRetry(Func<HttpRequestMessage> make, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            HttpResponseMessage res;
            try
            {
                using var req = make();
                res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException) when (attempt < 3) { await Task.Delay(1000 << attempt, ct); continue; }
            if (res.IsSuccessStatusCode) return res;
            int code = (int)res.StatusCode;
            var text = await res.Content.ReadAsStringAsync(ct);
            var wait = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(1500 << attempt);
            res.Dispose();
            // a used-up quota (daily/free-tier allowance) won't come back in seconds; retrying just wastes attempts
            bool quotaGone = code == 429 && text.Contains("quota", StringComparison.OrdinalIgnoreCase)
                             && (text.Contains("billing", StringComparison.OrdinalIgnoreCase) || text.Contains("exceeded your current", StringComparison.OrdinalIgnoreCase));
            if (attempt < 3 && !quotaGone && (code is 429 or 529 || code >= 500))
            {
                await Task.Delay(wait > TimeSpan.FromSeconds(20) ? TimeSpan.FromSeconds(20) : wait, ct);
                continue;
            }
            throw ApiError(code, text);
        }
    }

    static Exception ApiError(int status, string text)
    {
        string msg;
        try
        {
            var j = JsonNode.Parse(text);
            // Anthropic: {"error":{"message"}}, OpenAI-style: same, Gemini: [{"error":{...}}]
            var err = j is JsonArray arr ? arr.FirstOrDefault()?["error"] : j?["error"];
            msg = err?["message"]?.GetValue<string>() ?? text;
        }
        catch { msg = text; }
        return new ApiException(status, msg.Clip(300));
    }
}
