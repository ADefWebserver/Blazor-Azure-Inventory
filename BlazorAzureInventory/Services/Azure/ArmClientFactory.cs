using System.Collections.Concurrent;
using Azure.Core;
using Azure.ResourceManager;
using BlazorAzureInventory.Services.Auth;
using Microsoft.Identity.Web;

namespace BlazorAzureInventory.Services.Azure;

/// <summary>Creates one <see cref="ArmClient"/> per tenant for the signed-in user (scoped to the circuit).</summary>
public sealed class ArmClientFactory(ITokenAcquisition tokens, UserContextAccessor userContext, IConfiguration configuration)
{
    private readonly ConcurrentDictionary<string, (ArmClient Client, TokenCredential Credential)> _clients = new(StringComparer.OrdinalIgnoreCase);

    private readonly string[] _scopes = configuration.GetSection("AzureResourceManager:Scopes").Get<string[]>() is { Length: > 0 } s
        ? s
        : ["https://management.azure.com/user_impersonation"];

    public async Task<ArmClient> GetAsync(string? tenantId = null) => (await GetEntryAsync(tenantId)).Client;

    public async Task<TokenCredential> GetCredentialAsync(string? tenantId = null) => (await GetEntryAsync(tenantId)).Credential;

    private async Task<(ArmClient Client, TokenCredential Credential)> GetEntryAsync(string? tenantId)
    {
        var user = await userContext.GetUserAsync();
        var key = tenantId ?? user.GetTenantId() ?? "home";
        return _clients.GetOrAdd(key, _ =>
        {
            var credential = new UserTokenCredential(tokens, user, tenantId ?? user.GetTenantId(), _scopes);
            return (new ArmClient(credential), credential);
        });
    }
}
