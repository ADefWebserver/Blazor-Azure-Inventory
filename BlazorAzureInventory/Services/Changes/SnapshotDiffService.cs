using BlazorAzureInventory.Models.Graph;
using BlazorAzureInventory.Models.Inventory;
using BlazorAzureInventory.Models.Options;
using BlazorAzureInventory.Services.Auth;
using BlazorAzureInventory.Services.Graph;
using BlazorAzureInventory.Services.Persistence;
using Microsoft.Extensions.Options;

namespace BlazorAzureInventory.Services.Changes;

/// <summary>Compares two scans. Values are limited to non-secret resource metadata, tags, and graph edges.</summary>
public sealed class SnapshotDiffService(InventoryGraphBuilder graphBuilder, IOptions<ChangeTrackingOptions> options, IOptions<InventoryOptions> inventoryOptions)
{
    private const int MaxValueLength = 120;
    private static readonly HashSet<string> ConnectionEdgeTypes =
        new(StringComparer.Ordinal) { EdgeTypes.ConnectsTo, EdgeTypes.ReferencesSecret, EdgeTypes.PullsImageFrom };

    public SnapshotDiff Compare(InventorySnapshot previous, InventorySnapshot current, GraphDocument? currentGraph = null)
    {
        var shared = previous.Subscriptions.Select(s => s.SubscriptionId)
            .Intersect(current.Subscriptions.Select(s => s.SubscriptionId), StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var before = InScope(previous.Resources, shared).ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        var after = InScope(current.Resources, shared).ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        var ignored = options.Value.EffectiveIgnoredProperties.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = after.Values.Where(r => !before.ContainsKey(r.Id)).OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        var removed = before.Values.Where(r => !after.ContainsKey(r.Id)).OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        var changed = after.Values
            .Where(r => before.ContainsKey(r.Id))
            .Select(r => new ResourceChange(r.Id, r.Name, r.Kind, Describe(before[r.Id], r, ignored)))
            .Where(c => c.Changes.Count > 0)
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .ToList();

        var buildOptions = GraphBuildOptions.From(inventoryOptions.Value);
        var oldEdges = Connections(graphBuilder.Build(previous with { Resources = before.Values.ToList() }, buildOptions), shared);
        var newEdges = Connections(currentGraph ?? graphBuilder.Build(current, buildOptions), shared);
        return new SnapshotDiff(previous.ScannedUtc, current.ScannedUtc, added, removed, changed,
            newEdges.Where(e => !oldEdges.ContainsKey(e.Key)).Select(e => e.Value).ToList(),
            oldEdges.Where(e => !newEdges.ContainsKey(e.Key)).Select(e => e.Value).ToList());
    }

    private static IEnumerable<AzureResourceInfo> InScope(IEnumerable<AzureResourceInfo> resources, HashSet<string> shared) =>
        resources.Where(r => r.Kind == AzureResourceKind.External || shared.Contains(r.SubscriptionId));

    private static List<string> Describe(AzureResourceInfo old, AzureResourceInfo now, HashSet<string> ignored)
    {
        var changes = new List<string>();
        if (!string.Equals(old.Location, now.Location, StringComparison.OrdinalIgnoreCase))
            changes.Add($"Location: {old.Location} -> {now.Location}");
        if (!string.Equals(old.Endpoint, now.Endpoint, StringComparison.OrdinalIgnoreCase))
            changes.Add($"Endpoint: {Trim(old.Endpoint)} -> {Trim(now.Endpoint)}");
        Compare(old.Tags, now.Tags, "Tag", changes, null);
        Compare(old.Properties, now.Properties, "Property", changes, ignored);
        return changes;
    }

    private static void Compare(IReadOnlyDictionary<string, string> old, IReadOnlyDictionary<string, string> now, string label,
        List<string> changes, HashSet<string>? ignored)
    {
        foreach (var key in old.Keys.Union(now.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (ignored?.Contains(key) == true) continue;
            var had = old.TryGetValue(key, out var a);
            var has = now.TryGetValue(key, out var b);
            if (had && !has) changes.Add($"{label} removed: {key}");
            else if (!had && has) changes.Add($"{label} added: {key} = {Trim(b)}");
            else if (!string.Equals(a, b, StringComparison.Ordinal)) changes.Add($"{label} {key}: {Trim(a)} -> {Trim(b)}");
        }
    }

    private static Dictionary<(string, string, string), ConnectionChange> Connections(GraphDocument graph, HashSet<string> shared)
    {
        var labels = graph.Nodes.ToDictionary(n => n.Id, n => n.Label, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<(string, string, string), ConnectionChange>();
        foreach (var edge in graph.Edges.Where(e => ConnectionEdgeTypes.Contains(e.Type) && IsInScope(e.Source, shared)))
        {
            var key = (edge.Source.ToLowerInvariant(), edge.Target.ToLowerInvariant(), edge.Type);
            result.TryAdd(key, new ConnectionChange(edge.Source, labels.GetValueOrDefault(edge.Source, edge.Source),
                edge.Target, labels.GetValueOrDefault(edge.Target, edge.Target), edge.Type));
        }
        return result;
    }

    private static bool IsInScope(string nodeId, HashSet<string> shared)
    {
        var parts = nodeId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length < 2 || !parts[0].Equals("subscriptions", StringComparison.OrdinalIgnoreCase) || shared.Contains(parts[1]);
    }

    private static string Trim(string? value) =>
        value is null ? "-" : value.Length <= MaxValueLength ? value : value[..MaxValueLength] + "...";
}

/// <summary>Loads the previous saved scan for the signed-in user and caches the comparison on the session.</summary>
public sealed class ChangeTrackingService(
    SnapshotDiffService diffService,
    IUserDataStore dataStore,
    InventorySessionLoader loader,
    UserContextAccessor userContext,
    ILogger<ChangeTrackingService> logger)
{
    public async Task<SnapshotDiff?> GetChangesAsync(CancellationToken ct = default)
    {
        var session = await loader.EnsureLoadedAsync(ct);
        if (session?.Snapshot is not { } current) return null;
        if (session.Changes is { } cached && cached.CurrentScannedUtc == current.ScannedUtc) return cached;
        if (UserKey.FromPrincipal(await userContext.GetUserAsync()) is not { } key) return null;

        var previous = await dataStore.LoadPreviousSnapshotAsync(key, ct);
        if (previous is null || previous.ScannedUtc >= current.ScannedUtc) return null;
        try
        {
            var diff = diffService.Compare(previous, current, session.Graph);
            session.Changes = diff;
            return diff;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Comparing scans failed ({ExceptionType})", ex.GetType().Name);
            return null;
        }
    }
}
