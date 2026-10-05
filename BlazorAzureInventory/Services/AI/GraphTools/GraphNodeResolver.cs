using BlazorAzureInventory.Models.Graph;

namespace BlazorAzureInventory.Services.AI.GraphTools;

/// <summary>Resolves tool arguments (IDs or names) to graph nodes.</summary>
public static class GraphNodeResolver
{
    private static readonly HashSet<string> ChildEdges = [EdgeTypes.HostsDatabase, EdgeTypes.HasContainer];

    /// <summary>Resolves by ID, "parent/child" label, exact label, then partial label (for read-only lookups).</summary>
    public static IEnumerable<GraphNode> Resolve(GraphIndex index, string idOrName)
    {
        var exact = ResolveExact(index, idOrName);
        if (exact.Count > 0 || string.IsNullOrWhiteSpace(idOrName)) return exact;
        var q = idOrName.Trim();
        return index.Graph.Nodes.Where(n => n.Label.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(10).ToList();
    }

    /// <summary>Resolves by ID, "parent/child" label, or exact label only. Used before proposing changes.</summary>
    public static List<GraphNode> ResolveExact(GraphIndex index, string? idOrName)
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

        return index.Graph.Nodes.Where(n => n.Label.Equals(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}
