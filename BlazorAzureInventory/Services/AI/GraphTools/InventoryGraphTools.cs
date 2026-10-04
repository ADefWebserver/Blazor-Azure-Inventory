using BlazorAzureInventory.Models.Configuration;
using BlazorAzureInventory.Models.Graph;
using BlazorAzureInventory.Models.Inventory;

namespace BlazorAzureInventory.Services.AI.GraphTools;

/// <summary>Graph tools bound to one user's session. Every list result is capped by the hard cap.</summary>
public sealed class InventoryGraphTools(InventorySnapshot snapshot, GraphIndex index, int hardCap) : IInventoryGraphTools
{
    private static readonly HashSet<string> AppTypes = ["WebApp", "FunctionApp", "WebAppSlot", "ContainerApp"];
    private static readonly HashSet<string> DataTypes =
        ["SqlServer", "SqlDatabase", "StorageAccount", "BlobContainer", "CosmosAccount", "RedisCache", "ServiceBusNamespace", "KeyVault", "ContainerRegistry"];
    private static readonly HashSet<string> DependencyEdges =
        [EdgeTypes.ConnectsTo, EdgeTypes.ReferencesSecret, EdgeTypes.PullsImageFrom];
    private static readonly HashSet<string> ChildEdges = [EdgeTypes.HostsDatabase, EdgeTypes.HasContainer];

    public GraphStats Stats() => new(
        index.Graph.Nodes.Count,
        index.Graph.Edges.Count,
        index.Graph.Nodes.GroupBy(n => n.Type).ToDictionary(g => g.Key, g => g.Count()),
        index.Graph.Edges.GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.Count()),
        snapshot.ScannedUtc.ToString("u"),
        snapshot.ScopeDescription,
        snapshot.Issues.Count);

    public NodeSummary[] SearchNodes(string query, string? type, int max) =>
        index.Graph.Nodes
            .Where(n => string.IsNullOrWhiteSpace(type) || n.Type.Equals(type, StringComparison.OrdinalIgnoreCase))
            .Where(n => string.IsNullOrWhiteSpace(query) || n.Label.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.Label.Equals(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(n => n.Label)
            .Take(Cap(max))
            .Select(Summary)
            .ToArray();

    public NodeDetail? GetNode(string id)
    {
        var node = Resolve(id).FirstOrDefault();
        if (node is null) return null;
        var props = node.Data.Where(kv => kv.Key != "references")
            .ToDictionary(kv => kv.Key, kv => kv.Value?.ToString());
        return new NodeDetail(node.Id, node.Type, node.Label, props, index.In(node.Id).Count, index.Out(node.Id).Count);
    }

    public Neighbor[] GetNeighbors(string id, string? edgeType, string? direction, int max)
    {
        var node = Resolve(id).FirstOrDefault();
        if (node is null) return [];
        var dir = direction?.ToLowerInvariant();
        IEnumerable<(string Dir, GraphEdge Edge, string Other)> outgoing = dir is null or "out" or "both"
            ? index.Out(node.Id).Select(e => ("Out", e, e.Target)) : [];
        IEnumerable<(string Dir, GraphEdge Edge, string Other)> incoming = dir is null or "in" or "both"
            ? index.In(node.Id).Select(e => ("In", e, e.Source)) : [];
        return outgoing.Concat(incoming)
            .Where(x => string.IsNullOrWhiteSpace(edgeType) || x.Edge.Type.Equals(edgeType, StringComparison.OrdinalIgnoreCase))
            .Where(x => index.NodesById.ContainsKey(x.Other))
            .Take(Cap(max))
            .Select(x =>
            {
                var other = index.NodesById[x.Other];
                return new Neighbor(x.Dir, x.Edge.Type, other.Id, other.Type, other.Label, SettingKeys(x.Edge));
            })
            .ToArray();
    }

    public AppSummary[] ListApps(string? subscription, string? resourceGroup, string? kind, int max)
    {
        var subscriptionIds = string.IsNullOrWhiteSpace(subscription)
            ? null
            : snapshot.Subscriptions
                .Where(s => s.SubscriptionId.Equals(subscription, StringComparison.OrdinalIgnoreCase)
                            || s.DisplayName.Contains(subscription, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.SubscriptionId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return index.Graph.Nodes
            .Where(n => AppTypes.Contains(n.Type))
            .Where(n => string.IsNullOrWhiteSpace(kind) || n.Type.Equals(kind, StringComparison.OrdinalIgnoreCase))
            .Where(n => subscriptionIds is null || subscriptionIds.Contains(Data(n, "subscriptionId") ?? ""))
            .Where(n => string.IsNullOrWhiteSpace(resourceGroup) || string.Equals(Data(n, "resourceGroup"), resourceGroup, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.Label)
            .Take(Cap(max))
            .Select(n => new AppSummary(n.Id, n.Label, n.Type, Data(n, "subscriptionId") ?? "", Data(n, "resourceGroup") ?? "",
                index.Out(n.Id).Count(e => DependencyEdges.Contains(e.Type)), Data(n, "settingsStatus")))
            .ToArray();
    }

    public AppDependencies? GetAppDependencies(string appIdOrName)
    {
        var app = Resolve(appIdOrName).FirstOrDefault(n => AppTypes.Contains(n.Type));
        if (app is null) return null;

        var deps = index.Out(app.Id)
            .Where(e => DependencyEdges.Contains(e.Type) && index.NodesById.ContainsKey(e.Target))
            .Take(hardCap)
            .Select(e =>
            {
                var target = index.NodesById[e.Target];
                return new Dependency(e.Type, target.Type, QualifiedLabel(target), SettingKeys(e), e.Data.GetValueOrDefault("confidence")?.ToString());
            })
            .ToList();

        return new AppDependencies(app.Label, app.Type, Data(app, "environment"), Data(app, "settingsStatus"), Data(app, "configFilesStatus"), deps);
    }

    public Consumer[] FindConsumers(string resourceIdOrName, int max)
    {
        var results = new List<Consumer>();
        foreach (var node in Resolve(resourceIdOrName).Where(n => !AppTypes.Contains(n.Type)))
        {
            var targets = new List<GraphNode> { node };
            targets.AddRange(index.Out(node.Id).Where(e => ChildEdges.Contains(e.Type))
                .Select(e => index.NodesById.GetValueOrDefault(e.Target)).OfType<GraphNode>());

            foreach (var target in targets)
            {
                foreach (var edge in index.In(target.Id).Where(e => DependencyEdges.Contains(e.Type)))
                {
                    if (!index.NodesById.TryGetValue(edge.Source, out var app)) continue;
                    results.Add(new Consumer(app.Label, app.Type, edge.Type, QualifiedLabel(target), SettingKeys(edge)));
                }
            }
        }
        return results.DistinctBy(c => (c.App, c.EdgeType, c.Target)).Take(Cap(max)).ToArray();
    }

    public SharedResource[] ListSharedResources(int minConsumers, int max)
    {
        var min = Math.Max(2, minConsumers);
        var consumers = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var edge in index.Graph.Edges.Where(e => DependencyEdges.Contains(e.Type)))
        {
            if (!index.NodesById.TryGetValue(edge.Source, out var app)) continue;
            foreach (var targetId in WithParents(edge.Target))
            {
                if (!consumers.TryGetValue(targetId, out var set)) consumers[targetId] = set = new(StringComparer.OrdinalIgnoreCase);
                set.Add(app.Label);
            }
        }

        return consumers
            .Where(kv => kv.Value.Count >= min && index.NodesById.ContainsKey(kv.Key))
            .OrderByDescending(kv => kv.Value.Count)
            .Take(Cap(max))
            .Select(kv =>
            {
                var node = index.NodesById[kv.Key];
                return new SharedResource(QualifiedLabel(node), node.Type, kv.Value.Count, kv.Value.OrderBy(v => v).ToList());
            })
            .ToArray();
    }

    public UnusedResource[] ListUnreferencedResources(string? type, int max)
    {
        var referenced = index.Graph.Edges
            .Where(e => DependencyEdges.Contains(e.Type))
            .SelectMany(e => WithParents(e.Target))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return index.Graph.Nodes
            .Where(n => DataTypes.Contains(n.Type) && !n.Data.ContainsKey("isAggregate"))
            .Where(n => string.IsNullOrWhiteSpace(type) || n.Type.Equals(type, StringComparison.OrdinalIgnoreCase))
            .Where(n => !referenced.Contains(n.Id))
            .OrderBy(n => n.Type).ThenBy(n => n.Label)
            .Take(Cap(max))
            .Select(n => new UnusedResource(QualifiedLabel(n), n.Type, Data(n, "resourceGroup")))
            .ToArray();
    }

    public UnresolvedConnection[] ListUnresolvedConnections(int max)
    {
        var results = new List<UnresolvedConnection>();
        foreach (var edge in index.Graph.Edges.Where(e => DependencyEdges.Contains(e.Type)))
        {
            if (!index.NodesById.TryGetValue(edge.Source, out var app) || !index.NodesById.TryGetValue(edge.Target, out var target)) continue;
            if (target.Type == nameof(AzureResourceKind.External))
            {
                results.Add(new UnresolvedConnection(app.Label, target.Label, Data(target, "kind") ?? "External", Data(target, "reason"), SettingKeys(edge)));
            }
            else if (edge.Data.GetValueOrDefault("references") is List<EdgeReference> refs && refs.FirstOrDefault(r => r.UnresolvedDatabase is not null) is { } r)
            {
                results.Add(new UnresolvedConnection(app.Label, $"{target.Label}/{r.UnresolvedDatabase}", "SqlDatabase", "DatabaseNotVisible", SettingKeys(edge)));
            }
        }
        return results.Take(Cap(max)).ToArray();
    }

    public SecretHygieneFinding[] ListSecretLikePlainSettings(int max) =>
        snapshot.AppConfigs
            .SelectMany(a => a.Entries)
            .Where(e => e.IsSecretLike && !e.IsOverridden && !e.IsKeyVaultReference)
            .Select(e => new SecretHygieneFinding(
                index.NodesById.TryGetValue(e.AppId, out var n) ? n.Label : e.AppId.Split('/')[^1],
                e.Key, ConfigSourceNames.ToDisplay(e.Source, e.FileName)))
            .Take(Cap(max))
            .ToArray();

    public ScanIssueSummary[] ListScanIssues(int max) =>
        snapshot.Issues
            .Take(Cap(max))
            .Select(i => new ScanIssueSummary(i.ResourceName, i.Stage, i.Kind.ToString(), i.Message))
            .ToArray();

    private int Cap(int max) => Math.Clamp(max <= 0 ? 20 : max, 1, hardCap);

    /// <summary>Resolves by ID, "parent/child" label, exact label, then partial label.</summary>
    private IEnumerable<GraphNode> Resolve(string idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName)) return [];
        var q = idOrName.Trim();
        if (index.NodesById.TryGetValue(q, out var byId)) return [byId];

        var parts = q.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && !q.StartsWith('/'))
        {
            var parentLabel = parts[0];
            var childLabel = parts[^1];
            var children = index.Graph.Nodes.Where(n => n.Label.Equals(childLabel, StringComparison.OrdinalIgnoreCase)
                && index.In(n.Id).Any(e => ChildEdges.Contains(e.Type)
                    && index.NodesById.TryGetValue(e.Source, out var p) && p.Label.Equals(parentLabel, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (children.Count > 0) return children;
        }

        var exact = index.Graph.Nodes.Where(n => n.Label.Equals(q, StringComparison.OrdinalIgnoreCase)).ToList();
        return exact.Count > 0 ? exact : index.Graph.Nodes.Where(n => n.Label.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(10).ToList();
    }

    private IEnumerable<string> WithParents(string nodeId)
    {
        yield return nodeId;
        foreach (var e in index.In(nodeId).Where(e => ChildEdges.Contains(e.Type))) yield return e.Source;
    }

    private string QualifiedLabel(GraphNode node)
    {
        var parent = index.In(node.Id).FirstOrDefault(e => ChildEdges.Contains(e.Type));
        return parent is not null && index.NodesById.TryGetValue(parent.Source, out var p) ? $"{p.Label}/{node.Label}" : node.Label;
    }

    private static List<string> SettingKeys(GraphEdge edge) => edge.Data.GetValueOrDefault("references") switch
    {
        List<EdgeReference> refs => refs.Select(r => r.SettingKey).Distinct().ToList(),
        _ when edge.Data.GetValueOrDefault("repositories") is List<string> repos => repos,
        _ => []
    };

    private static NodeSummary Summary(GraphNode n) => new(n.Id, n.Type, n.Label);

    private static string? Data(GraphNode n, string key) => n.Data.GetValueOrDefault(key)?.ToString();
}
