using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BlazorAzureInventory.Services.Auth;
using Microsoft.Identity.Web;

namespace BlazorAzureInventory.Services.Azure;

/// <summary>A user in a Microsoft Entra directory. <see cref="ObjectId"/> is specific to that directory.</summary>
public sealed record DirectoryUser(string ObjectId, string DisplayName, string? UserPrincipalName, string? Mail)
{
    public string Label => string.IsNullOrEmpty(Mail ?? UserPrincipalName) ? DisplayName : $"{DisplayName} ({Mail ?? UserPrincipalName})";
}

/// <summary>
/// Resolves an email, user principal name, or object ID to a user in a specific tenant through Microsoft Graph,
/// using the signed-in user's delegated <c>User.ReadBasic.All</c> permission (requested on first use).
/// </summary>
public sealed class DirectoryUserLookup(
    ITokenAcquisition tokens,
    UserContextAccessor userContext,
    IHttpClientFactory httpFactory)
{
    public const string HttpClientName = "graph-users";
    public const string Scope = "https://graph.microsoft.com/User.ReadBasic.All";
    public static IReadOnlyList<string> Scopes { get; } = [Scope];
    private const string GraphEndpoint = "https://graph.microsoft.com/v1.0";
    private const string Select = "$select=id,displayName,userPrincipalName,mail";

    /// <summary>The user in <paramref name="tenantId"/>, or null if none matches. Consent exceptions propagate to the caller.</summary>
    public async Task<DirectoryUser?> FindAsync(string tenantId, string identifier, CancellationToken ct)
    {
        var user = await userContext.GetUserAsync();
        var token = await tokens.GetAccessTokenForUserAsync(Scopes, tenantId: tenantId, user: user,
            tokenAcquisitionOptions: new TokenAcquisitionOptions { CancellationToken = ct });
        return await FindAsync(httpFactory.CreateClient(HttpClientName), token, identifier, ct);
    }

    public static async Task<DirectoryUser?> FindAsync(HttpClient client, string token, string identifier, CancellationToken ct)
    {
        identifier = Normalize(identifier) ?? throw new ArgumentException("Enter an email address, user principal name, or object ID.", nameof(identifier));

        // Object IDs and member UPNs resolve directly.
        using (var response = await SendAsync(client, token, $"{GraphEndpoint}/users/{Uri.EscapeDataString(identifier)}?{Select}", ct))
        {
            if (response.IsSuccessStatusCode) return Parse(await ReadAsync(response, ct));
            if (response.StatusCode != HttpStatusCode.NotFound) throw await ErrorAsync(response, ct);
        }
        if (Guid.TryParse(identifier, out _)) return null;

        // Guests have a #EXT# UPN, so match their email address too.
        var literal = identifier.Replace("'", "''");
        var filter = Uri.EscapeDataString($"mail eq '{literal}' or userPrincipalName eq '{literal}'");
        using (var response = await SendAsync(client, token, $"{GraphEndpoint}/users?$filter={filter}&{Select}&$top=2", ct))
        {
            if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, ct);
            using var doc = await ReadAsync(response, ct);
            var matches = doc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().ToList()
                : [];
            return matches.Count switch
            {
                0 => null,
                1 => Parse(matches[0]),
                _ => throw new InvalidOperationException($"More than one user matches '{identifier}'. Use the user principal name or object ID.")
            };
        }
    }

    /// <summary>Trimmed identifier, or null when it is empty, too long, or contains control characters.</summary>
    public static string? Normalize(string? identifier)
    {
        var value = identifier?.Trim();
        return string.IsNullOrEmpty(value) || value.Length > 256 || value.Any(char.IsControl) ? null : value;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, ct);
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static DirectoryUser? Parse(JsonDocument doc)
    {
        using (doc) return Parse(doc.RootElement);
    }

    private static DirectoryUser? Parse(JsonElement user)
    {
        var id = Text(user, "id");
        return id is null ? null : new DirectoryUser(id, Text(user, "displayName") ?? id, Text(user, "userPrincipalName"), Text(user, "mail"));
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } s ? s : null;

    private static async Task<HttpRequestException> ErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? code = null;
        try
        {
            using var doc = await ReadAsync(response, ct);
            code = doc.RootElement.TryGetProperty("error", out var error) ? Text(error, "code") : null;
        }
        catch (JsonException)
        {
        }
        var message = response.StatusCode == HttpStatusCode.Forbidden
            ? "403: you are not allowed to look up users in this directory."
            : $"{(int)response.StatusCode}: {code ?? response.ReasonPhrase}";
        return new HttpRequestException(message, null, response.StatusCode);
    }
}
