using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Core;
using BlazorAzureInventory.Models.Options;
using BlazorAzureInventory.Services.Azure;
using Microsoft.Extensions.Options;

namespace BlazorAzureInventory.Services.Tags;

/// <summary>Advisory check of whether the signed-in user can write tags on a resource.</summary>
public interface ITagPermissionChecker
{
    Task<bool> CanWriteTagsAsync(string resourceId, string? tenantId, CancellationToken ct);
}

/// <summary>
/// Advisory check of <c>Microsoft.Resources/tags/write</c> through the ARM permissions API, cached per scope for this
/// circuit. Decides whether editing is enabled in the UI; ARM's own 403 remains the final authority.
/// </summary>
public sealed class TagPermissionChecker(
    ArmClientFactory armFactory,
    IHttpClientFactory httpFactory,
    IOptions<TaggingOptions> options,
    IConfiguration configuration,
    ILogger<TagPermissionChecker> logger) : ITagPermissionChecker
{
    public const string HttpClientName = "arm-permissions";
    public const string TagWriteAction = "Microsoft.Resources/tags/write";

    private readonly ConcurrentDictionary<string, (bool Allowed, DateTimeOffset Expires)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<bool> CanWriteTagsAsync(string resourceId, string? tenantId, CancellationToken ct)
    {
        var scope = ScopeFor(resourceId);
        if (scope is null) return false;
        if (_cache.TryGetValue(scope, out var cached) && cached.Expires > DateTimeOffset.UtcNow) return cached.Allowed;

        try
        {
            var credential = await armFactory.GetCredentialAsync(string.IsNullOrEmpty(tenantId) ? null : tenantId);
            var scopes = configuration.GetSection("AzureResourceManager:Scopes").Get<string[]>() is { Length: > 0 } s
                ? s
                : ["https://management.azure.com/user_impersonation"];
            var token = await credential.GetTokenAsync(new TokenRequestContext(scopes), ct);

            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://management.azure.com{scope}/providers/Microsoft.Authorization/permissions?api-version=2022-04-01");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            using var response = await httpFactory.CreateClient(HttpClientName).SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                // Network or throttling failures fall back to allowing the edit; ARM enforces access on write.
                return (int)response.StatusCode is 401 or 403 ? Cache(scope, false) : true;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return Cache(scope, Evaluate(doc.RootElement, TagWriteAction));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogDebug("Permission check failed for {Scope} ({ExceptionType}); allowing edit", scope, ex.GetType().Name);
            return true;
        }
    }

    private bool Cache(string scope, bool allowed)
    {
        _cache[scope] = (allowed, DateTimeOffset.UtcNow.AddMinutes(options.Value.PermissionCacheMinutes));
        return allowed;
    }

    /// <summary>The resource group (or subscription) scope used for the check and the cache.</summary>
    public static string? ScopeFor(string resourceId)
    {
        var parts = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].Equals("subscriptions", StringComparison.OrdinalIgnoreCase)) return null;
        if (parts.Length >= 4 && parts[2].Equals("resourcegroups", StringComparison.OrdinalIgnoreCase))
        {
            return $"/subscriptions/{parts[1]}/resourceGroups/{parts[3]}";
        }
        return $"/subscriptions/{parts[1]}";
    }

    /// <summary>True if any permission entry allows <paramref name="action"/> and does not exclude it.</summary>
    public static bool Evaluate(JsonElement root, string action)
    {
        if (!root.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array) return false;
        foreach (var entry in value.EnumerateArray())
        {
            var allowed = Patterns(entry, "actions").Any(p => Matches(p, action));
            var denied = Patterns(entry, "notActions").Any(p => Matches(p, action));
            if (allowed && !denied) return true;
        }
        return false;
    }

    public static bool Matches(string pattern, string action) =>
        Regex.IsMatch(action, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase);

    private static IEnumerable<string> Patterns(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)
            : [];
}
