using System.Text.Json.Serialization;
using BlazorAzureInventory.Models.Configuration;

namespace BlazorAzureInventory.Models.Inventory;

public enum AzureResourceKind
{
    Tenant, Subscription, ResourceGroup,
    WebApp, FunctionApp, WebAppSlot,
    SqlServer, SqlDatabase,
    StorageAccount, BlobContainer,
    ContainerApp, ContainerRegistry,
    CosmosAccount, RedisCache, ServiceBusNamespace, KeyVault,
    External
}

public sealed record AzureResourceInfo(
    string Id,                       // lower-cased ARM resource ID
    string Name,
    AzureResourceKind Kind,
    string ArmType,
    string TenantId,
    string SubscriptionId,
    string ResourceGroup,
    string Location,
    string? ParentId,
    string? Endpoint,                // FQDN / URI host - never contains credentials
    IReadOnlyDictionary<string, string> Properties)
{
    public static readonly IReadOnlyDictionary<string, string> EmptyTags =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Azure tags. Names are case-insensitive; values are case-sensitive.</summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } = EmptyTags;

    public string? Prop(string key) => Properties.TryGetValue(key, out var v) ? v : null;
}

public enum ScanIssueKind { AccessDenied, NotFound, Throttled, Timeout, ConsentRequired, Unexpected }

public sealed record ScanIssue(string? ResourceId, string Stage, ScanIssueKind Kind, string Message)
{
    [JsonIgnore]
    public string ResourceName => ResourceId is null ? "-" : ResourceId.TrimEnd('/').Split('/')[^1];
}

public sealed record TenantInfo(string TenantId, string DisplayName, string? DefaultDomain, bool RequiresInteraction = false);

public sealed record SubscriptionInfo(string SubscriptionId, string DisplayName, string TenantId, string State)
{
    public IReadOnlyDictionary<string, string> Tags { get; init; } = AzureResourceInfo.EmptyTags;
}

public sealed record ResourceGroupInfo(string Id, string Name, string SubscriptionId, string TenantId, string Location)
{
    public IReadOnlyDictionary<string, string> Tags { get; init; } = AzureResourceInfo.EmptyTags;
}

public sealed record InventorySnapshot(
    DateTimeOffset ScannedUtc,
    IReadOnlyList<TenantInfo> Tenants,
    IReadOnlyList<SubscriptionInfo> Subscriptions,
    IReadOnlyList<ResourceGroupInfo> ResourceGroups,
    IReadOnlyList<AzureResourceInfo> Resources,
    IReadOnlyList<AppConfigAnalysis> AppConfigs,
    IReadOnlyList<ScanIssue> Issues)
{
    [JsonIgnore]
    public string ScopeDescription =>
        $"{Tenants.Count} tenant{(Tenants.Count == 1 ? "" : "s")}, {Subscriptions.Count} subscription{(Subscriptions.Count == 1 ? "" : "s")}";
}

public sealed record ScanRequest(IReadOnlyList<TenantInfo> Tenants, IReadOnlyList<SubscriptionInfo> Subscriptions);

public sealed record ScanProgress(string Stage, int Completed, int Total, string? CurrentItem)
{
    public static readonly string[] Stages =
    [
        "Resolving subscriptions",
        "Querying Resource Graph",
        "Listing blob containers",
        "Reading app settings",
        "Reading config files",
        "Reading Container Apps",
        "Analyzing connections",
        "Building graph"
    ];

    public double OverallPercent
    {
        get
        {
            var index = Array.IndexOf(Stages, Stage);
            if (index < 0) return 0;
            var within = Total > 0 ? Math.Clamp((double)Completed / Total, 0, 1) : 0;
            return Math.Round((index + within) / Stages.Length * 100, 0);
        }
    }
}
