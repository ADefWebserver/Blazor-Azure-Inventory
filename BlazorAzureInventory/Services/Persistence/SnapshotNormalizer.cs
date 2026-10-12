using BlazorAzureInventory.Models.Configuration;
using BlazorAzureInventory.Models.Inventory;

namespace BlazorAzureInventory.Services.Persistence;

/// <summary>Restores invariants that JSON cannot carry, such as case-insensitive tag and property dictionaries.</summary>
public static class SnapshotNormalizer
{
    public static InventorySnapshot Normalize(InventorySnapshot snapshot) => snapshot with
    {
        Tenants = snapshot.Tenants ?? [],
        Subscriptions = (snapshot.Subscriptions ?? []).Select(s => s with { Tags = Ci(s.Tags) }).ToList(),
        ResourceGroups = (snapshot.ResourceGroups ?? []).Select(g => g with { Tags = Ci(g.Tags) }).ToList(),
        Resources = (snapshot.Resources ?? []).Select(r => r with
        {
            Id = r.Id.ToLowerInvariant(),
            Properties = Ci(r.Properties),
            Tags = Ci(r.Tags)
        }).ToList(),
        AppConfigs = (snapshot.AppConfigs ?? []).Select(a => a with
        {
            ConfigFilesFound = a.ConfigFilesFound ?? [],
            Entries = (a.Entries ?? []).Select(e => e with { References = e.References ?? [] }).ToList(),
            Images = a.Images ?? []
        }).ToList(),
        Issues = snapshot.Issues ?? []
    };

    private static IReadOnlyDictionary<string, string> Ci(IReadOnlyDictionary<string, string>? source)
    {
        if (source is null || source.Count == 0) return AzureResourceInfo.EmptyTags;
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in source) copy[k] = v;
        return copy;
    }
}
