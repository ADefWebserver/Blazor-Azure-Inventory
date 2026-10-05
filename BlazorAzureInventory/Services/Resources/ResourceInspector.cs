using BlazorAzureInventory.Models.Graph;
using BlazorAzureInventory.Models.Inventory;
using BlazorAzureInventory.Services.Auth;
using BlazorAzureInventory.Services.Graph;
using BlazorAzureInventory.Services.Tags;

namespace BlazorAzureInventory.Services.Resources;

public sealed record ConnectionRow(string Direction, string EdgeType, string OtherId, string Other, string OtherType, string SettingKeys, bool OtherIsResource);

public sealed record ResourceDetails(
    string Id,
    string Label,
    string Type,
    string? ArmType,
    string? TenantId,
    string? SubscriptionId,
    string? SubscriptionName,
    string? ResourceGroup,
    string? Location,
    string? Endpoint,
    string? PortalUrl,
    IReadOnlyList<KeyValuePair<string, string>> Properties,
    IReadOnlyList<ConnectionRow> Connections,
    IReadOnlyDictionary<string, string> Tags,
    bool IsTaggable,
    string? NotTaggableReason,
    DateTimeOffset ScannedUtc);

/// <summary>Builds read-only view models for the Resource Properties popup from the user's session.</summary>
public sealed class ResourceInspector(InventorySessionStore store, UserContextAccessor userContext, ITagPermissionChecker permissions)
{
    private static readonly HashSet<string> HiddenProperties = new(StringComparer.OrdinalIgnoreCase) { TagKeys.Taggable };

    public async Task<ResourceDetails?> GetAsync(string? resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceId)) return null;
        var session = store.Get(await userContext.GetUserAsync());
        if (session?.Index is null || session.Snapshot is null) return null;
        lock (session.SyncRoot)
        {
            return Build(session.Snapshot, session.Index, resourceId);
        }
    }

    /// <summary>Returns null when the ID is not in the scan; client-supplied IDs are never loaded from Azure.</summary>
    public static ResourceDetails? Build(InventorySnapshot snapshot, GraphIndex index, string resourceId)
    {
        if (!index.NodesById.TryGetValue(resourceId, out var node)) return null;

        string? D(string key) => node.Data.GetValueOrDefault(key)?.ToString() is { Length: > 0 } v ? v : null;
        var subscriptionId = D("subscriptionId");
        var taggable = node.IsTaggable();
        var reason = taggable ? null : node.Type switch
        {
            nameof(AzureResourceKind.BlobContainer) => "Blob containers do not support Azure tags (they use container metadata instead).",
            nameof(AzureResourceKind.External) => "This target is outside the scanned subscriptions.",
            nameof(AzureResourceKind.Tenant) => "Tenants cannot be tagged.",
            _ => "This item cannot be tagged."
        };

        return new ResourceDetails(
            node.Id,
            node.Label,
            node.Type,
            D("armType"),
            D("tenantId"),
            subscriptionId,
            snapshot.Subscriptions.FirstOrDefault(s => s.SubscriptionId.Equals(subscriptionId, StringComparison.OrdinalIgnoreCase))?.DisplayName,
            D("resourceGroup"),
            D("location"),
            D("defaultHost") ?? D("fqdn") ?? D("loginServer") ?? D("endpointHost") ?? D("blobEndpoint"),
            PortalUrl(node),
            BuildProperties(node),
            BuildConnections(index, node),
            new Dictionary<string, string>(node.GetTags(), StringComparer.OrdinalIgnoreCase),
            taggable,
            reason,
            snapshot.ScannedUtc);
    }

    public Task<bool> CanWriteTagsAsync(ResourceDetails details, CancellationToken ct) =>
        details.IsTaggable ? permissions.CanWriteTagsAsync(details.Id, details.TenantId, ct) : Task.FromResult(false);

    public static string? PortalUrl(GraphNode node) =>
        node.Id.StartsWith("/subscriptions/", StringComparison.Ordinal)
            ? $"https://portal.azure.com/#@{node.Data.GetValueOrDefault("tenantId")}/resource{node.Id}"
            : null;

    /// <summary>Scalar node data, sorted by key. Tags and internal flags are excluded.</summary>
    public static List<KeyValuePair<string, string>> BuildProperties(GraphNode node) =>
        node.Data
            .Where(kv => !HiddenProperties.Contains(kv.Key))
            .Where(kv => kv.Value is string or bool or int or long or double)
            .Where(kv => kv.Value is not string s || s.Length > 0)
            .Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value!.ToString()!))
            .OrderBy(kv => kv.Key)
            .ToList();

    public static List<ConnectionRow> BuildConnections(GraphIndex index, GraphNode node) =>
        index.Out(node.Id).Select(e => Row(index, "Out", e, e.Target))
            .Concat(index.In(node.Id).Select(e => Row(index, "In", e, e.Source)))
            .ToList();

    private static ConnectionRow Row(GraphIndex index, string direction, GraphEdge edge, string otherId)
    {
        var other = index.NodesById.TryGetValue(otherId, out var n) ? n : null;
        var keys = edge.Data.GetValueOrDefault("references") is List<EdgeReference> refs
            ? string.Join(", ", refs.Select(r => r.SettingKey).Distinct())
            : edge.Data.GetValueOrDefault("repositories") is List<string> repos ? string.Join(", ", repos) : "";
        return new ConnectionRow(direction, edge.Type, otherId, other?.Label ?? otherId, other?.Type ?? "", keys,
            other is not null && other.Type != nameof(AzureResourceKind.Tenant));
    }
}
