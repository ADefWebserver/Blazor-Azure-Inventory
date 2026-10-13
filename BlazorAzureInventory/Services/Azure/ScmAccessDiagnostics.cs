using System.Net.Http.Headers;
using System.Text.Json;
using BlazorAzureInventory.Models.Configuration;
using BlazorAzureInventory.Models.Inventory;
using BlazorAzureInventory.Services.Tags;

namespace BlazorAzureInventory.Services.Azure;

/// <summary>
/// Explains a failed Kudu call using ARM metadata only (caller permissions and SCM access restrictions).
/// Runs only after a failure; Kudu response bodies are never read.
/// </summary>
public sealed class ScmAccessDiagnostics(ArmClientFactory armFactory, IHttpClientFactory httpFactory, ILogger<ScmAccessDiagnostics> logger)
{
    public const string HttpClientName = "arm-scm-diagnostics";
    public const string PublishAction = "Microsoft.Web/sites/publish/action";
    private const string PermissionsApiVersion = "2022-04-01";
    private const string WebApiVersion = "2023-12-01";

    public async Task<ConfigAccessReason> DiagnoseAsync(AzureResourceInfo app, KuduException failure, CancellationToken ct)
    {
        if (failure.IsNetworkFailure) return ConfigAccessReason.ScmUnreachable;
        if (failure.HttpStatus is not (401 or 403)) return ConfigAccessReason.None;

        try
        {
            using var permissions = await GetJsonAsync(app, $"{app.Id}/providers/Microsoft.Authorization/permissions?api-version={PermissionsApiVersion}", ct);
            if (permissions is null) return ConfigAccessReason.Unknown;
            if (!TagPermissionChecker.Evaluate(permissions.RootElement, PublishAction)) return ConfigAccessReason.MissingRole;

            using var config = await GetJsonAsync(app, $"{app.Id}/config/web?api-version={WebApiVersion}", ct);
            if (config is null) return ConfigAccessReason.Unknown;
            return IsScmRestricted(config.RootElement) ? ConfigAccessReason.ScmAccessRestricted : ConfigAccessReason.AuthRejected;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogDebug("SCM diagnostics failed for {ResourceId} ({ExceptionType})", app.Id, ex.GetType().Name);
            return ConfigAccessReason.Unknown;
        }
    }

    /// <summary>True when public access is disabled or the effective SCM rules do not allow every address.</summary>
    public static bool IsScmRestricted(JsonElement root)
    {
        if (!root.TryGetProperty("properties", out var p) || p.ValueKind != JsonValueKind.Object) return false;
        if (String(p, "publicNetworkAccess") is { } access && access.Equals("Disabled", StringComparison.OrdinalIgnoreCase)) return true;

        var useMain = p.TryGetProperty("scmIpSecurityRestrictionsUseMain", out var u) && u.ValueKind == JsonValueKind.True;
        var defaultAction = String(p, useMain ? "ipSecurityRestrictionsDefaultAction" : "scmIpSecurityRestrictionsDefaultAction");
        if (defaultAction is not null && defaultAction.Equals("Deny", StringComparison.OrdinalIgnoreCase)) return true;

        if (!p.TryGetProperty(useMain ? "ipSecurityRestrictions" : "scmIpSecurityRestrictions", out var rules)
            || rules.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        // Without an explicit default action, any allow rule other than "Any" implicitly denies everyone else.
        return rules.EnumerateArray().Any(r =>
            string.Equals(String(r, "action"), "Allow", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(String(r, "ipAddress"), "Any", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<JsonDocument?> GetJsonAsync(AzureResourceInfo app, string relativeUrl, CancellationToken ct)
    {
        var token = await armFactory.GetTokenAsync(app.TenantId, ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, KuduConfigFileReader.ArmEndpoint + relativeUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var response = await httpFactory.CreateClient(HttpClientName).SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static string? String(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
