using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace BlazorAzureInventory.Services.AI;

/// <summary>Text-only <see cref="IChatClient"/> over the Anthropic Messages REST API (Anthropic or Foundry).</summary>
public sealed class AnthropicChatClient(HttpClient http, Uri messagesUri, string apiKey, string model, int defaultMaxTokens) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(messages, options, stream: false);
        using var response = await http.SendAsync(request, cancellationToken);
        await RestChatHelpers.EnsureSuccessAsync(response, "Anthropic", cancellationToken);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var text = new StringBuilder();
        if (doc.RootElement.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t) && t.GetString() == "text" && block.TryGetProperty("text", out var tx))
                {
                    text.Append(tx.GetString());
                }
            }
        }
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text.ToString())) { ModelId = model };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(messages, options, stream: true);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await RestChatHelpers.EnsureSuccessAsync(response, "Anthropic", cancellationToken);

        await foreach (var data in RestChatHelpers.ReadSseDataAsync(response, cancellationToken))
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var type) && type.GetString() == "content_block_delta"
                && root.TryGetProperty("delta", out var delta) && delta.TryGetProperty("text", out var text))
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, text.GetString()) { ModelId = model };
            }
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }

    private HttpRequestMessage CreateRequest(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var list = messages.ToList();
        var system = string.Join("\n\n", list.Where(m => m.Role == ChatRole.System).Select(m => m.Text));
        var turns = new JsonArray();
        foreach (var m in list.Where(m => m.Role == ChatRole.User || m.Role == ChatRole.Assistant))
        {
            turns.Add(new JsonObject { ["role"] = m.Role == ChatRole.User ? "user" : "assistant", ["content"] = m.Text });
        }

        var body = new JsonObject
        {
            ["model"] = options?.ModelId ?? model,
            ["max_tokens"] = options?.MaxOutputTokens ?? defaultMaxTokens,
            ["messages"] = turns,
            ["stream"] = stream
        };
        if (system.Length > 0) body["system"] = system;
        if (options?.Temperature is { } temperature) body["temperature"] = temperature;

        var request = new HttpRequestMessage(HttpMethod.Post, messagesUri)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(stream ? "text/event-stream" : "application/json"));
        return request;
    }
}

/// <summary>Text-only <see cref="IChatClient"/> over the Google Generative Language REST API.</summary>
public sealed class GoogleAIChatClient(HttpClient http, string apiKey, string model, int defaultMaxTokens) : IChatClient
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(messages, options, stream: false);
        using var response = await http.SendAsync(request, cancellationToken);
        await RestChatHelpers.EnsureSuccessAsync(response, "Google AI", cancellationToken);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, ExtractText(doc.RootElement))) { ModelId = model };
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
            var text = ExtractText(doc.RootElement);
            if (text.Length > 0) yield return new ChatResponseUpdate(ChatRole.Assistant, text) { ModelId = model };
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }

    private HttpRequestMessage CreateRequest(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var list = messages.ToList();
        var system = string.Join("\n\n", list.Where(m => m.Role == ChatRole.System).Select(m => m.Text));
        var contents = new JsonArray();
        foreach (var m in list.Where(m => m.Role == ChatRole.User || m.Role == ChatRole.Assistant))
        {
            contents.Add(new JsonObject
            {
                ["role"] = m.Role == ChatRole.User ? "user" : "model",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = m.Text })
            });
        }

        var generation = new JsonObject { ["maxOutputTokens"] = options?.MaxOutputTokens ?? defaultMaxTokens };
        if (options?.Temperature is { } temperature) generation["temperature"] = temperature;
        var body = new JsonObject { ["contents"] = contents, ["generationConfig"] = generation };
        if (system.Length > 0) body["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) };

        var modelId = Uri.EscapeDataString(options?.ModelId ?? model);
        var url = BaseUrl + modelId + (stream ? ":streamGenerateContent?alt=sse" : ":generateContent");
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-goog-api-key", apiKey);
        return request;
    }

    private static string ExtractText(JsonElement root)
    {
        var sb = new StringBuilder();
        if (root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in candidates.EnumerateArray())
            {
                if (c.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts))
                {
                    foreach (var p in parts.EnumerateArray())
                    {
                        if (p.TryGetProperty("text", out var t)) sb.Append(t.GetString());
                    }
                }
            }
        }
        return sb.ToString();
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
}
