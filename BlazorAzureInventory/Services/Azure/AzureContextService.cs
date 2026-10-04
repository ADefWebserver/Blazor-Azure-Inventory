using BlazorAzureInventory.Models.Inventory;
using BlazorAzureInventory.Services.Auth;
using Microsoft.Identity.Web;

namespace BlazorAzureInventory.Services.Azure;

public sealed record AzureContext(IReadOnlyList<TenantInfo> Tenants, IReadOnlyList<SubscriptionInfo> Subscriptions);

/// <summary>Discovers tenants and subscriptions visible to the signed-in user. Cached per circuit.</summary>
public sealed class AzureContextService(ArmClientFactory armFactory, UserContextAccessor userContext, ILogger<AzureContextService> logger)
{
    private AzureContext? _cached;
    private readonly Dictionary<string, Exception> _consentErrors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The exception that blocked a tenant, so the UI can hand it to the consent handler.</summary>
    public Exception? GetConsentError(string tenantId) => _consentErrors.GetValueOrDefault(tenantId);

    public async Task<AzureContext> GetContextAsync(bool refresh = false, CancellationToken ct = default)
    {
        if (_cached is not null && !refresh) return _cached;

        _consentErrors.Clear();
        var user = await userContext.GetUserAsync();
        var homeClient = await armFactory.GetAsync();
        var tenants = new List<TenantInfo>();
        var subscriptions = new List<SubscriptionInfo>();

        await foreach (var tenant in homeClient.GetTenants().GetAllAsync(ct))
        {
            var tenantId = tenant.Data.TenantId?.ToString();
            if (tenantId is null) continue;

            var displayName = tenant.Data.DisplayName ?? tenant.Data.DefaultDomain ?? tenantId;
            try
            {
                var client = await armFactory.GetAsync(tenantId);
                await foreach (var sub in client.GetSubscriptions().GetAllAsync(ct))
                {
                    subscriptions.Add(new SubscriptionInfo(
                        sub.Data.SubscriptionId, sub.Data.DisplayName ?? sub.Data.SubscriptionId,
                        sub.Data.TenantId?.ToString() ?? tenantId, sub.Data.State?.ToString() ?? "Unknown"));
                }
                tenants.Add(new TenantInfo(tenantId, displayName, tenant.Data.DefaultDomain));
            }
            catch (Exception ex) when (ScanErrors.IsConsentRequired(ex))
            {
                logger.LogInformation("Tenant {TenantId} requires interaction for user {ObjectId}", tenantId, user.GetObjectId());
                _consentErrors[tenantId] = ex;
                tenants.Add(new TenantInfo(tenantId, displayName, tenant.Data.DefaultDomain, RequiresInteraction: true));
            }
            catch (global::Azure.RequestFailedException ex) when (ex.Status is 401 or 403)
            {
                logger.LogInformation("Tenant {TenantId} returned {Status} when listing subscriptions", tenantId, ex.Status);
                tenants.Add(new TenantInfo(tenantId, displayName, tenant.Data.DefaultDomain, RequiresInteraction: true));
            }
        }

        _cached = new AzureContext(
            tenants.OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(),
            subscriptions
                .GroupBy(s => s.SubscriptionId, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToList());
        return _cached;
    }
}
