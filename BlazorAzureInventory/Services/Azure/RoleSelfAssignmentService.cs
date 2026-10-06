using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BlazorAzureInventory.Models.Inventory;
using BlazorAzureInventory.Services.Tags;
using Microsoft.IdentityModel.JsonWebTokens;

namespace BlazorAzureInventory.Services.Azure;

/// <summary>Which of the app's roles the signed-in user lacks on a subscription, and whether they can assign them.</summary>
public sealed record SubscriptionRoleStatus(SubscriptionInfo Subscription, IReadOnlyList<string> MissingRoles, bool CanAssign, string? Error = null);

public sealed record RoleAssignmentResult(string Role, bool Succeeded, string? Error = null);

/// <summary>
/// Lets a user who may write role assignments (Owner, User Access Administrator, or Role Based Access Control Administrator)
/// assign the app's required roles to themselves at subscription scope. Permission checks are advisory; ARM is the authority.
/// </summary>
public sealed class RoleSelfAssignmentService(
    ArmClientFactory armFactory,
    IHttpClientFactory httpFactory,
    ILogger<RoleSelfAssignmentService> logger)
{
    public const string HttpClientName = "arm-role-assignments";
    public const string RoleAssignmentWriteAction = "Microsoft.Authorization/roleAssignments/write";
    private const string ArmEndpoint = "https://management.azure.com";
    private const string ApiVersion = "2022-04-01";

    /// <summary>Built-in role definition IDs (the same in every tenant).</summary>
    public static IReadOnlyDictionary<string, string> RoleDefinitionIds { get; } = new Dictionary<string, string>
    {
        [AzureRoleRequirements.Reader] = "acdd72a7-3385-48ef-bd42-f606fba81ae7",
        [AzureRoleRequirements.WebsiteContributor] = "de139f84-1756-47ae-9be6-808fbbe84772",
        [AzureRoleRequirements.TagContributor] = "4a9ae827-6dc8-4573-8ac7-8239d42aa03f",
    };

    public async Task<SubscriptionRoleStatus> GetStatusAsync(SubscriptionInfo subscription, CancellationToken ct)
    {
        try
        {
            var token = await armFactory.GetTokenAsync(subscription.TenantId, ct);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{ArmEndpoint}{Scope(subscription)}/providers/Microsoft.Authorization/permissions?api-version={ApiVersion}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            using var response = await httpFactory.CreateClient(HttpClientName).SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return new SubscriptionRoleStatus(subscription, [], false, await ErrorAsync(response, ct));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return new SubscriptionRoleStatus(subscription, MissingRoles(doc.RootElement), CanAssign(doc.RootElement));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogInformation("Role check failed for subscription {SubscriptionId} ({ExceptionType})", subscription.SubscriptionId, ex.GetType().Name);
            return new SubscriptionRoleStatus(subscription, [], false, Describe(ex));
        }
    }

    /// <summary>Assigns each role to the signed-in user at subscription scope. An existing assignment counts as success.</summary>
    public async Task<IReadOnlyList<RoleAssignmentResult>> AssignToMeAsync(SubscriptionInfo subscription, IEnumerable<string> roles, CancellationToken ct)
    {
        var results = new List<RoleAssignmentResult>();
        string token, principalId;
        try
        {
            token = (await armFactory.GetTokenAsync(subscription.TenantId, ct)).Token;
            principalId = ObjectIdFromToken(token)
                ?? throw new InvalidOperationException("The access token has no object ID (oid) claim.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return roles.Select(r => new RoleAssignmentResult(r, false, Describe(ex))).ToList();
        }

        var client = httpFactory.CreateClient(HttpClientName);
        foreach (var role in roles)
        {
            if (!RoleDefinitionIds.TryGetValue(role, out var definitionId))
            {
                results.Add(new RoleAssignmentResult(role, false, "Unknown role."));
                continue;
            }

            try
            {
                var scope = Scope(subscription);
                using var request = new HttpRequestMessage(HttpMethod.Put,
                    $"{ArmEndpoint}{scope}/providers/Microsoft.Authorization/roleAssignments/{Guid.NewGuid()}?api-version={ApiVersion}")
                {
                    Content = JsonContent.Create(new
                    {
                        properties = new
                        {
                            roleDefinitionId = $"{scope}/providers/Microsoft.Authorization/roleDefinitions/{definitionId}",
                            principalId,
                            principalType = "User"
                        }
                    })
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await client.SendAsync(request, ct);
                var errorCode = response.IsSuccessStatusCode ? null : await ErrorCodeAsync(response, ct);
                if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict && errorCode == "RoleAssignmentExists")
                {
                    logger.LogInformation("Assigned {Role} to {ObjectId} on subscription {SubscriptionId}", role, principalId, subscription.SubscriptionId);
                    results.Add(new RoleAssignmentResult(role, true));
                }
                else
                {
                    results.Add(new RoleAssignmentResult(role, false, $"{(int)response.StatusCode}: {errorCode ?? response.ReasonPhrase}"));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                results.Add(new RoleAssignmentResult(role, false, Describe(ex)));
            }
        }
        return results;
    }

    /// <summary>Roles whose permissions are not all granted by the permissions response, in the order of <see cref="RoleDefinitionIds"/>.</summary>
    public static IReadOnlyList<string> MissingRoles(JsonElement permissions) =>
        AzureRoleRequirements.All
            .GroupBy(r => r.Role)
            .Where(g => g.Any(r => !TagPermissionChecker.Evaluate(permissions, r.Permission)))
            .Select(g => g.Key)
            .OrderBy(r => RoleDefinitionIds.Keys.ToList().IndexOf(r))
            .ToList();

    public static bool CanAssign(JsonElement permissions) => TagPermissionChecker.Evaluate(permissions, RoleAssignmentWriteAction);

    /// <summary>The user's object ID in the token's tenant (differs from the home tenant for guest users).</summary>
    public static string? ObjectIdFromToken(string token) =>
        new JsonWebToken(token).TryGetPayloadValue<string>("oid", out var oid) && !string.IsNullOrEmpty(oid) ? oid : null;

    private static string Scope(SubscriptionInfo subscription) => $"/subscriptions/{subscription.SubscriptionId}";

    private static string Describe(Exception ex) => ScanErrors.IsConsentRequired(ex)
        ? "Sign-in or consent is required for this directory. Open Inventory to grant access."
        : ex.GetType().Name;

    private static async Task<string> ErrorAsync(HttpResponseMessage response, CancellationToken ct) =>
        $"{(int)response.StatusCode}: {await ErrorCodeAsync(response, ct) ?? response.ReasonPhrase}";

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return doc.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var code) ? code.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
