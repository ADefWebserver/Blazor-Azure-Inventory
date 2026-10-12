using BlazorAzureInventory.Models.Inventory;

namespace BlazorAzureInventory.Services.Persistence;

public sealed record AccessFilterResult(InventorySnapshot Snapshot, int RemovedSubscriptions);

/// <summary>Hides saved data for subscriptions the user can no longer list, because their Azure access may have been removed since the scan.</summary>
public static class SnapshotAccessFilter
{
    public static AccessFilterResult Filter(InventorySnapshot snapshot, IEnumerable<string> accessibleSubscriptionIds)
    {
        var allowed = accessibleSubscriptionIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = snapshot.Subscriptions.Count(s => !allowed.Contains(s.SubscriptionId));
        if (removed == 0) return new AccessFilterResult(snapshot, 0);

        var resources = snapshot.Resources
            .Where(r => r.Kind == AzureResourceKind.External || string.IsNullOrEmpty(r.SubscriptionId) || allowed.Contains(r.SubscriptionId))
            .ToList();
        var resourceIds = resources.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var filtered = snapshot with
        {
            Subscriptions = snapshot.Subscriptions.Where(s => allowed.Contains(s.SubscriptionId)).ToList(),
            ResourceGroups = snapshot.ResourceGroups.Where(g => allowed.Contains(g.SubscriptionId)).ToList(),
            Resources = resources,
            AppConfigs = snapshot.AppConfigs.Where(a => resourceIds.Contains(a.AppId)).ToList(),
            Issues = snapshot.Issues.Where(i => IsAllowed(i.ResourceId, allowed)).ToList(),
            Tenants = snapshot.Tenants
                .Where(t => snapshot.Subscriptions.Any(s => allowed.Contains(s.SubscriptionId) && s.TenantId.Equals(t.TenantId, StringComparison.OrdinalIgnoreCase)))
                .ToList()
        };
        return new AccessFilterResult(filtered, removed);
    }

    private static bool IsAllowed(string? resourceId, HashSet<string> allowed)
    {
        if (resourceId is null || !resourceId.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase)) return true;
        var parts = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 && allowed.Contains(parts[1]);
    }
}
