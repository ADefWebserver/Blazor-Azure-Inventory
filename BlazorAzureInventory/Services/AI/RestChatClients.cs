using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using static BlazorAzureInventory.Services.AI.RestChatHelpers;

namespace BlazorAzureInventory.Services.AI;

/// <summary><see cref="IChatClient"/> over the Anthropic Messages REST API (Anthropic or Foundry), including tool calling.</summary>
public sealed class AnthropicChatClient(HttpClient http, Uri messagesUri, string apiKey, string model, int defaultMaxTokens) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(messages, options, stream: false);
        using var response = await http.SendAsync(request, cancellationToken);
        await RestChatHelpers.EnsureSuccessAsync(response, "Anthropic", cancellationToken);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = doc.RootElement;
        var contents = new List<AIContent>();
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                switch (GetString(block, "type"))
                {
                    case "text" when GetString(block, "text") is { Length: > 0 } text:
                        contents.Add(new TextContent(text));
                        break;
                    case "tool_use":
                        contents.Add(new FunctionCallContent(
                            GetString(block, "id") ?? "", GetString(block, "name") ?? "",
                            block.TryGetProperty("input", out var input) ? ParseArguments(input.GetRawText()) : new Dictionary<string, object?>()));
                        break;
                }
            }
        }

        var responseId = GetString(root, "id");
        var result = new ChatResponse(new ChatMessage(ChatRole.Assistant, contents) { MessageId = responseId })
        {
            ModelId = GetString(root, "model") ?? model,
            ResponseId = responseId,
            FinishReason = MapStopReason(GetString(root, "stop_reason"))
        };
        if (root.TryGetProperty("usage", out var usage)) result.Usage = ParseUsage(usage);
        return result;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(messages, options, stream: true);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await RestChatHelpers.EnsureSuccessAsync(response, "Anthropic", cancellationToken);

        string? messageId = null;
        var toolCalls = new Dictionary<int, (string Id, string Name, StringBuilder Json)>();

        await foreach (var data in RestChatHelpers.ReadSseDataAsync(response, cancellationToken))
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            var index = root.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : -1;

            switch (GetString(root, "type"))
            {
                case "message_start":
                    if (root.TryGetProperty("message", out var message)) messageId = GetString(message, "id");
                    break;

                case "content_block_start":
                    if (root.TryGetProperty("content_block", out var block) && GetString(block, "type") == "tool_use")
                    {
                        toolCalls[index] = (GetString(block, "id") ?? "", GetString(block, "name") ?? "", new StringBuilder());
                    }
                    break;

                case "content_block_delta" when root.TryGetProperty("delta", out var delta):
                    switch (GetString(delta, "type"))
                    {
                        case "text_delta" when GetString(delta, "text") is { Length: > 0 } text:
                            yield return Update(new TextContent(text));
                            break;
                        case "input_json_delta" when toolCalls.TryGetValue(index, out var call):
                            call.Json.Append(GetString(delta, "partial_json"));
                            break;
                    }
                    break;

                case "content_block_stop" when toolCalls.Remove(index, out var done):
                    yield return Update(new FunctionCallContent(done.Id, done.Name, ParseArguments(done.Json.ToString())));
                    break;

                case "message_delta":
                {
                    var update = Update();
                    if (root.TryGetProperty("delta", out var md)) update.FinishReason = MapStopReason(GetString(md, "stop_reason"));
                    if (root.TryGetProperty("usage", out var usage)) update.Contents.Add(new UsageContent(ParseUsage(usage)));
                    yield return update;
                    break;
                }

                case "error":
                    var kind = root.TryGetProperty("error", out var error) ? GetString(error, "type") : null;
                    throw new HttpRequestException($"Anthropic stream error: {kind ?? "unknown"}.");
            }
        }

        ChatResponseUpdate Update(AIContent? content = null) => new()
        {
            Role = ChatRole.Assistant,
            MessageId = messageId,
            ResponseId = messageId,
            ModelId = model,
            Contents = content is null ? [] : [content]
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }

    private HttpRequestMessage CreateRequest(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var list = messages.ToList();
        var system = string.Join("\n\n", list.Where(m => m.Role == ChatRole.System).Select(m => m.Text));

        var body = new JsonObject
        {
            ["model"] = options?.ModelId ?? model,
            ["max_tokens"] = options?.MaxOutputTokens ?? defaultMaxTokens,
            ["messages"] = BuildMessages(list),
            ["stream"] = stream
        };
        if (system.Length > 0) body["system"] = system;
        if (options?.Temperature is { } temperature) body["temperature"] = temperature;

        var tools = options?.ToolMode is NoneChatToolMode ? [] : options?.Tools?.OfType<AIFunctionDeclaration>().ToList() ?? [];
        if (tools.Count > 0)
        {
            body["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = ToObjectSchema(t.JsonSchema)
            }).ToArray());

            if (options?.ToolMode is RequiredChatToolMode required)
            {
                body["tool_choice"] = required.RequiredFunctionName is { } name
                    ? new JsonObject { ["type"] = "tool", ["name"] = name }
                    : new JsonObject { ["type"] = "any" };
            }
        }

        var request = new HttpRequestMessage(HttpMethod.Post, messagesUri)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(stream ? "text/event-stream" : "application/json"));
        return request;
    }

    /// <summary>Maps chat messages to Anthropic turns. Tool results become user turns, and consecutive same-role turns are merged.</summary>
    internal static JsonArray BuildMessages(IEnumerable<ChatMessage> messages)
    {
        var turns = new JsonArray();
        string? lastRole = null;
        JsonArray? lastBlocks = null;

        foreach (var m in messages.Where(m => m.Role != ChatRole.System))
        {
            var role = m.Role == ChatRole.Assistant ? "assistant" : "user";
            var blocks = new JsonArray();
            foreach (var c in m.Contents)
            {
                switch (c)
                {
                    case TextContent { Text.Length: > 0 } text:
                        blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text });
                        break;
                    case FunctionCallContent call when role == "assistant":
                        blocks.Add(new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = call.CallId,
                            ["name"] = call.Name,
                            ["input"] = JsonSerializer.SerializeToNode(call.Arguments ?? new Dictionary<string, object?>(), AIJsonUtilities.DefaultOptions)
                        });
                        break;
                    case FunctionResultContent result:
                        var block = new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = result.CallId,
                            ["content"] = ResultText(result)
                        };
                        if (result.Exception is not null) block["is_error"] = true;
                        blocks.Add(block);
                        break;
                }
            }
            if (blocks.Count == 0) continue;

            if (role == lastRole && lastBlocks is not null)
            {
                foreach (var b in blocks.ToList())
                {
                    blocks.Remove(b);
                    lastBlocks.Add(b);
                }
                continue;
            }

            turns.Add(new JsonObject { ["role"] = role, ["content"] = blocks });
            lastRole = role;
            lastBlocks = blocks;
        }
        return turns;
    }

    private static ChatFinishReason? MapStopReason(string? reason) => reason switch
    {
        "end_turn" or "stop_sequence" or "pause_turn" => ChatFinishReason.Stop,
        "max_tokens" => ChatFinishReason.Length,
        "tool_use" => ChatFinishReason.ToolCalls,
        "refusal" => ChatFinishReason.ContentFilter,
        null => null,
        var other => new ChatFinishReason(other)
    };

    private static UsageDetails ParseUsage(JsonElement usage) => new()
    {
        InputTokenCount = usage.TryGetProperty("input_tokens", out var input) && input.ValueKind == JsonValueKind.Number ? input.GetInt64() : null,
        OutputTokenCount = usage.TryGetProperty("output_tokens", out var output) && output.ValueKind == JsonValueKind.Number ? output.GetInt64() : null
    };
}

/// <summary><see cref="IChatClient"/> over the Google Generative Language REST API, including function calling.</summary>
public sealed class GoogleAIChatClient(HttpClient http, string apiKey, string model, int defaultMaxTokens) : IChatClient
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";

    /// <summary>Gemini thought signature attached to a function call; it must be sent back with the call.</summary>
    internal const string ThoughtSignatureKey = "gemini.thoughtSignature";

    // Marks call ids created locally because Gemini did not return one; they are not sent back to the API.
    private const string LocalIdPrefix = "local-call-";

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(messages, options, stream: false);
        using var response = await http.SendAsync(request, cancellationToken);
        await RestChatHelpers.EnsureSuccessAsync(response, "Google AI", cancellationToken);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);

        var (contents, finish) = ParseCandidate(doc.RootElement);
        var responseId = GetString(doc.RootElement, "responseId");
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents) { MessageId = responseId })
        {
            ModelId = GetString(doc.RootElement, "modelVersion") ?? model,
            ResponseId = responseId,
            FinishReason = finish,
            Usage = ParseUsage(doc.RootElement)
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(messages, options, stream: true);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await RestChatHelpers.EnsureSuccessAsync(response, "Google AI", cancellationToken);

        await foreach (var data in RestChatHelpers.ReadSseDataAsync(response, cancellationToken))
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                throw new HttpRequestException($"Google AI stream error: {GetString(error, "status") ?? "unknown"}.");
            }

            var (contents, finish) = ParseCandidate(root);
            if (ParseUsage(root) is { } usage) contents.Add(new UsageContent(usage));
            if (contents.Count == 0 && finish is null) continue;

            var responseId = GetString(root, "responseId");
            yield return new ChatResponseUpdate(ChatRole.Assistant, contents)
            {
                MessageId = responseId,
                ResponseId = responseId,
                ModelId = GetString(root, "modelVersion") ?? model,
                FinishReason = finish
            };
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }

    private HttpRequestMessage CreateRequest(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var list = messages.ToList();
        var system = string.Join("\n\n", list.Where(m => m.Role == ChatRole.System).Select(m => m.Text));

        var generation = new JsonObject { ["maxOutputTokens"] = options?.MaxOutputTokens ?? defaultMaxTokens };
        if (options?.Temperature is { } temperature) generation["temperature"] = temperature;
        var body = new JsonObject { ["contents"] = BuildContents(list), ["generationConfig"] = generation };
        if (system.Length > 0) body["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) };

        var tools = options?.ToolMode is NoneChatToolMode ? [] : options?.Tools?.OfType<AIFunctionDeclaration>().ToList() ?? [];
        if (tools.Count > 0)
        {
            var declarations = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parametersJsonSchema"] = ToObjectSchema(t.JsonSchema)
            }).ToArray());
            body["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = declarations });

            if (options?.ToolMode is RequiredChatToolMode required)
            {
                var config = new JsonObject { ["mode"] = "ANY" };
                if (required.RequiredFunctionName is { } name) config["allowedFunctionNames"] = new JsonArray(name);
                body["toolConfig"] = new JsonObject { ["functionCallingConfig"] = config };
            }
        }

        var modelId = Uri.EscapeDataString(options?.ModelId ?? model);
        var url = BaseUrl + modelId + (stream ? ":streamGenerateContent?alt=sse" : ":generateContent");
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-goog-api-key", apiKey);
        return request;
    }

    /// <summary>Maps chat messages to Gemini contents. Function results become user turns, and consecutive same-role turns are merged.</summary>
    internal static JsonArray BuildContents(IReadOnlyList<ChatMessage> messages)
    {
        // Gemini function responses must carry the function name, which only the original call has.
        var callNames = messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>()
            .GroupBy(c => c.CallId).ToDictionary(g => g.Key, g => g.First().Name);

        var contents = new JsonArray();
        string? lastRole = null;
        JsonArray? lastParts = null;

        foreach (var m in messages.Where(m => m.Role != ChatRole.System))
        {
            var role = m.Role == ChatRole.Assistant ? "model" : "user";
            var parts = new JsonArray();
            foreach (var c in m.Contents)
            {
                switch (c)
                {
                    case TextContent { Text.Length: > 0 } text:
                        parts.Add(new JsonObject { ["text"] = text.Text });
                        break;
                    case FunctionCallContent call when role == "model":
                    {
                        var functionCall = new JsonObject
                        {
                            ["name"] = call.Name,
                            ["args"] = JsonSerializer.SerializeToNode(call.Arguments ?? new Dictionary<string, object?>(), AIJsonUtilities.DefaultOptions)
                        };
                        if (!call.CallId.StartsWith(LocalIdPrefix, StringComparison.Ordinal)) functionCall["id"] = call.CallId;
                        var part = new JsonObject { ["functionCall"] = functionCall };
                        if (call.AdditionalProperties?.TryGetValue(ThoughtSignatureKey, out var signature) == true && signature is string s)
                        {
                            part["thoughtSignature"] = s;
                        }
                        parts.Add(part);
                        break;
                    }
                    case FunctionResultContent result:
                    {
                        var functionResponse = new JsonObject
                        {
                            ["name"] = callNames.GetValueOrDefault(result.CallId) ?? result.CallId,
                            ["response"] = result.Exception is null
                                ? new JsonObject { ["result"] = ResultNode(result.Result) }
                                : new JsonObject { ["error"] = ResultText(result) }
                        };
                        if (!result.CallId.StartsWith(LocalIdPrefix, StringComparison.Ordinal)) functionResponse["id"] = result.CallId;
                        parts.Add(new JsonObject { ["functionResponse"] = functionResponse });
                        break;
                    }
                }
            }
            if (parts.Count == 0) continue;

            if (role == lastRole && lastParts is not null)
            {
                foreach (var p in parts.ToList())
                {
                    parts.Remove(p);
                    lastParts.Add(p);
                }
                continue;
            }

            contents.Add(new JsonObject { ["role"] = role, ["parts"] = parts });
            lastRole = role;
            lastParts = parts;
        }
        return contents;
    }

    private static JsonNode? ResultNode(object? result) => result switch
    {
        null => null,
        JsonElement e => JsonNode.Parse(e.GetRawText()),
        JsonNode n => n.DeepClone(),
        var o => JsonSerializer.SerializeToNode(o, AIJsonUtilities.DefaultOptions)
    };

    private static (List<AIContent> Contents, ChatFinishReason? Finish) ParseCandidate(JsonElement root)
    {
        var contents = new List<AIContent>();
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array
            || candidates.GetArrayLength() == 0)
        {
            return (contents, null);
        }

        var candidate = candidates[0];
        if (candidate.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts)
            && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True) continue;

                if (part.TryGetProperty("functionCall", out var fc))
                {
                    var call = new FunctionCallContent(
                        GetString(fc, "id") ?? LocalIdPrefix + Guid.NewGuid().ToString("N"),
                        GetString(fc, "name") ?? "",
                        fc.TryGetProperty("args", out var args) ? ParseArguments(args.GetRawText()) : new Dictionary<string, object?>());
                    if (GetString(part, "thoughtSignature") is { } signature)
                    {
                        call.AdditionalProperties = new() { [ThoughtSignatureKey] = signature };
                    }
                    contents.Add(call);
                }
                else if (GetString(part, "text") is { Length: > 0 } text)
                {
                    contents.Add(new TextContent(text));
                }
            }
        }

        var finish = GetString(candidate, "finishReason") switch
        {
            null => (ChatFinishReason?)null,
            _ when contents.OfType<FunctionCallContent>().Any() => ChatFinishReason.ToolCalls,
            "STOP" => ChatFinishReason.Stop,
            "MAX_TOKENS" => ChatFinishReason.Length,
            "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII" => ChatFinishReason.ContentFilter,
            var other => new ChatFinishReason(other)
        };
        return (contents, finish);
    }

    private static UsageDetails? ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usageMetadata", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
        long? Count(string name) => usage.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
        return new UsageDetails
        {
            InputTokenCount = Count("promptTokenCount"),
            OutputTokenCount = Count("candidatesTokenCount"),
            TotalTokenCount = Count("totalTokenCount")
        };
    }
}

internal static class RestChatHelpers
{
    /// <summary>Throws with the status code only; provider bodies are not echoed to avoid leaking prompt content.</summary>
    public static Task EnsureSuccessAsync(HttpResponseMessage response, string provider, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return Task.CompletedTask;
        throw new HttpRequestException($"{provider} returned {(int)response.StatusCode} {response.ReasonPhrase}.", null, response.StatusCode);
    }

    public static async IAsyncEnumerable<string> ReadSseDataAsync(HttpResponseMessage response, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var data = line[5..].Trim();
                if (data.Length > 0 && data != "[DONE]") yield return data;
            }
        }
    }

    public static string ResultText(FunctionResultContent result) => result.Result switch
    {
        null when result.Exception is not null => "Error: the tool call failed.",
        null => "",
        string s => s,
        JsonElement e when e.ValueKind == JsonValueKind.String => e.GetString() ?? "",
        var o => JsonSerializer.Serialize(o, AIJsonUtilities.DefaultOptions)
    };

    /// <summary>Both providers require an object schema, even for tools without parameters.</summary>
    public static JsonNode ToObjectSchema(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.Object && JsonNode.Parse(schema.GetRawText()) is JsonObject obj)
        {
            obj.Remove("$schema");
            obj["type"] ??= "object";
            return obj;
        }
        return new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
    }

    public static Dictionary<string, object?> ParseArguments(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json, AIJsonUtilities.DefaultOptions) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
