using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlazorAzureInventory.Models.AI;

namespace BlazorAzureInventory.Services.AI;

/// <summary>
/// Lists models per provider for the settings editor. Scoped per user; cache keys use a SHA-256 hash of the key.
/// </summary>
public sealed class AIModelCacheService(SessionAISettingsService settings, IHttpClientFactory httpClientFactory)
{
    private readonly ConcurrentDictionary<string, ModelListResult> _cache = new();

    public async Task<ModelListResult> GetModelsAsync(AIProviderSettings provider, bool forceRefresh, CancellationToken ct)
    {
        var key = string.IsNullOrWhiteSpace(provider.ApiKey) ? await settings.GetApiKeyAsync(provider) : provider.ApiKey.Trim();
        if (string.IsNullOrEmpty(key)) return new ModelListResult(ModelListStatus.Unauthorized, [], "Enter an API key to load models.");

        var cacheKey = $"{provider.ServiceType}|{provider.Endpoint}|{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))}";
        if (!forceRefresh && _cache.TryGetValue(cacheKey, out var cached)) return cached;

        var result = provider.ServiceType switch
        {
            AIServiceType.OpenAI => await ListAsync("https://api.openai.com/v1/models",
                r => r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key),
                root => Ids(root, "data", "id").Where(id => id.StartsWith("gpt", StringComparison.OrdinalIgnoreCase)
                    || id.StartsWith("o3", StringComparison.OrdinalIgnoreCase) || id.StartsWith("o4", StringComparison.OrdinalIgnoreCase)
                    || id.StartsWith("chatgpt", StringComparison.OrdinalIgnoreCase)), ct),
            AIServiceType.Anthropic => await ListAsync("https://api.anthropic.com/v1/models",
                r => { r.Headers.Add("x-api-key", key); r.Headers.Add("anthropic-version", "2023-06-01"); },
                root => Ids(root, "data", "id"), ct),
            AIServiceType.GoogleAI => await ListAsync("https://generativelanguage.googleapis.com/v1beta/models",
                r => r.Headers.Add("x-goog-api-key", key),
                root => Ids(root, "models", "name")
                    .Select(n => n.StartsWith("models/", StringComparison.Ordinal) ? n[7..] : n)
                    .Where(n => n.StartsWith("gemini", StringComparison.OrdinalIgnoreCase)), ct),
            // Azure deployments are listed through the management plane, so the deployment name is entered manually.
            _ => new ModelListResult(ModelListStatus.Empty, [], "Deployments cannot be listed with an API key.")
        };

        if (result.Status == ModelListStatus.Success) _cache[cacheKey] = result;
        return result;
    }

    private async Task<ModelListResult> ListAsync(
        string url, Action<HttpRequestMessage> authorize, Func<JsonElement, IEnumerable<string>> select, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            authorize(request);
            using var response = await httpClientFactory.CreateClient(ChatClientFactory.HttpClientName).SendAsync(request, ct);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                return new ModelListResult(ModelListStatus.Unauthorized, [], $"The provider rejected the API key ({(int)response.StatusCode}).");
            }
            if (!response.IsSuccessStatusCode)
            {
                return new ModelListResult(ModelListStatus.Unreachable, [], $"Model list request failed ({(int)response.StatusCode}).");
            }

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var models = select(doc.RootElement).Distinct().OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
            return models.Count == 0
                ? new ModelListResult(ModelListStatus.Empty, [], "The provider returned no models.")
                : new ModelListResult(ModelListStatus.Success, models, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new ModelListResult(ModelListStatus.Unreachable, [], "Could not reach the provider to list models.");
        }
    }

    private static IEnumerable<string> Ids(JsonElement root, string arrayName, string idName)
    {
        if (!root.TryGetProperty(arrayName, out var array) || array.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in array.EnumerateArray())
        {
            if (item.TryGetProperty(idName, out var id) && id.GetString() is { Length: > 0 } s) yield return s;
        }
    }
}
