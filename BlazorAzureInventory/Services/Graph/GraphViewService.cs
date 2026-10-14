using System.Text.RegularExpressions;
using System.Text.Json;
using BlazorAzureInventory.Models.Graph;
using BlazorAzureInventory.Models.Inventory;
using BlazorAzureInventory.Models.Options;
using Microsoft.Extensions.Options;

namespace BlazorAzureInventory.Services.Graph;

/// <summary>Projects a <see cref="GraphIndex"/> into slim Overview and Focus payloads for the browser.</summary>
public sealed partial class GraphViewService(IOptions<GraphViewOptions> options)
{
    /// <summary>Structural edges from a parent to a child that is folded into the parent when child resources are hidden.</summary>
    private static readonly IReadOnlySet<string> CollapseEdges =
        new HashSet<string> { EdgeTypes.HostsDatabase, EdgeTypes.HasContainer, EdgeTypes.HasQueue, EdgeTypes.HasSlot };

    internal GraphViewOptions Options { get; } = options.Value;

    /// <summary>
    /// Overview projection (plan section 6.4): no scope nodes or structural edges, children optionally folded into their
    /// parents, and parallel edges merged. <see cref="GraphViewPayload.TotalNodes"/> and <see cref="GraphViewPayload.TotalEdges"/>
    /// count every non-scope node and every dependency edge of the graph.
    /// </summary>
    public GraphViewPayload BuildOverview(GraphIndex index, GraphLayout layout, bool showChildResources)
    {
        var graph = index.Graph;
        var (nodes, collapsed, merged) = ProjectOverview(graph, showChildResources);

        var degree = new int[nodes.Count];
        var edges = new List<ViewEdge>(merged.Count);
        var edgeSources = new List<IReadOnlyList<string>>(merged.Count);
        foreach (var edge in merged)
        {
            degree[edge.Source]++;
            degree[edge.Target]++;
            edges.Add(new ViewEdge(edge.Source, edge.Target, edge.Type,
                Low: edge.AllLow ? true : null,
                Count: edge.Ids.Count > 1 ? edge.Ids.Count : null));
            edgeSources.Add(edge.Ids);
        }

        var subscriptions = Lookup(nodes, "subscriptionId");
        var resourceGroups = Lookup(nodes, "resourceGroup");
        var viewNodes = new List<ViewNode>(nodes.Count);
        for (var i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            var hasPosition = layout.Positions.TryGetValue(n.Id, out var position);
            var external = n.Type == nameof(AzureResourceKind.ExternalService);
            viewNodes.Add(new ViewNode(n.Type, n.Label,
                Sub: IndexIn(subscriptions, n, "subscriptionId"),
                Rg: IndexIn(resourceGroups, n, "resourceGroup"),
                X: hasPosition ? position.X : null,
                Y: hasPosition ? position.Y : null,
                Degree: degree[i],
                CollapsedChildren: collapsed.TryGetValue(n, out var children) ? children : null,
                Color: external ? ValidColor(n.Data.GetValueOrDefault("color")?.ToString()) : null,
                Category: external && n.Data.GetValueOrDefault("category")?.ToString() is { Length: > 0 } category ? category : null,
                Emulator: n.Data.GetValueOrDefault("isLocalEmulator") is true ? true : null,
                Aggregate: n.Data.GetValueOrDefault("isAggregate") is true ? true : null));
        }

        return new GraphViewPayload(GraphViewMode.Overview, null,
            subscriptions.Keys.ToList(), resourceGroups.Keys.ToList(), viewNodes, edges,
            graph.Nodes.Count(n => !IsScope(n.Type)),
            graph.Edges.Count(e => !EdgeTypes.Structural.Contains(e.Type)),
            layout.IsFallback)
        {
            NodeIds = nodes.Select(n => n.Id).ToList(),
            EdgeSources = edgeSources
        };
    }

    /// <summary>Projects the focused resource, its one-hop dependencies, and any URL-expanded neighbours.</summary>
    public GraphViewPayload? BuildFocus(
        GraphIndex index,
        string focusId,
        IReadOnlyCollection<string> expandedIds,
        bool hideEmulator,
        bool showLowConfidence)
    {
        if (!index.NodesById.TryGetValue(focusId, out var focus) || IsScope(focus.Type)) return null;

        var nodes = new Dictionary<string, GraphNode>(StringComparer.OrdinalIgnoreCase) { [focus.Id] = focus };
        var edges = new HashSet<GraphEdge>();
        var aggregateEdges = new List<(string Source, string Target, string Type)>();
        var parents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var processedCenters = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { focus.Id };

        var firstHop = CollectFocusHop(index, focus, hideEmulator, showLowConfidence, Options.FocusMaxNeighbours);
        AddInitialHop(firstHop);

        foreach (var expandedId in expandedIds)
        {
            if (!index.NodesById.TryGetValue(expandedId, out var expanded)
                || IsScope(expanded.Type)
                || !nodes.ContainsKey(expanded.Id)
                || !processedCenters.Add(expanded.Id))
                continue;

            var hop = CollectFocusHop(index, expanded, hideEmulator, showLowConfidence, Options.FocusMaxNeighbours);
            var newNodeIds = hop.Nodes.Select(n => n.Id).Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(id => !nodes.ContainsKey(id));
            if (nodes.Count + newNodeIds > Options.FocusMaxNodes) continue;
            AddHop(hop);
        }

        var orderedNodes = new List<GraphNode> { focus };
        orderedNodes.AddRange(nodes.Values.Where(n => !n.Id.Equals(focus.Id, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => NodeCategories.ByType.GetValueOrDefault(n.Type, NodeCategories.External), CategoryComparer)
            .ThenBy(n => n.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(n => n.Id, StringComparer.Ordinal));
        var nodeIndex = new Dictionary<string, int>(orderedNodes.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < orderedNodes.Count; i++) nodeIndex[orderedNodes[i].Id] = i;

        var includedEdges = edges.Where(e => nodeIndex.ContainsKey(e.Source) && nodeIndex.ContainsKey(e.Target))
            .OrderBy(e => nodeIndex[e.Source]).ThenBy(e => nodeIndex[e.Target])
            .ThenBy(e => e.Type, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToList();
        var degree = new int[orderedNodes.Count];
        foreach (var edge in includedEdges)
        {
            degree[nodeIndex[edge.Source]]++;
            degree[nodeIndex[edge.Target]]++;
        }
        foreach (var (source, target, _) in aggregateEdges)
        {
            if (nodeIndex.TryGetValue(source, out var sourceIndex)) degree[sourceIndex]++;
            if (nodeIndex.TryGetValue(target, out var targetIndex)) degree[targetIndex]++;
        }

        var visibleDependencyDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var edge in includedEdges.Where(e => FocusDependencyTypes.Contains(e.Type)))
        {
            visibleDependencyDegree[edge.Source] = visibleDependencyDegree.GetValueOrDefault(edge.Source) + 1;
            visibleDependencyDegree[edge.Target] = visibleDependencyDegree.GetValueOrDefault(edge.Target) + 1;
        }

        var subscriptions = Lookup(orderedNodes, "subscriptionId");
        var resourceGroups = Lookup(orderedNodes, "resourceGroup");
        var viewNodes = new List<ViewNode>(orderedNodes.Count);
        for (var i = 0; i < orderedNodes.Count; i++)
        {
            var node = orderedNodes[i];
            int? parent = parents.TryGetValue(node.Id, out var parentId) && nodeIndex.TryGetValue(parentId, out var parentIndex)
                ? parentIndex
                : null;
            var aggregate = node.Data.GetValueOrDefault("isAggregate") is true;
            int? hidden = node.Id.Equals(focus.Id, StringComparison.OrdinalIgnoreCase) || aggregate
                ? null
                : Math.Max(0, DependencyDegree(index, node.Id) - visibleDependencyDegree.GetValueOrDefault(node.Id));

            viewNodes.Add(new ViewNode(node.Type, node.Label,
                Sub: IndexIn(subscriptions, node, "subscriptionId"),
                Rg: IndexIn(resourceGroups, node, "resourceGroup"),
                Degree: degree[i],
                Parent: parent,
                HiddenNeighbours: hidden is > 0 ? hidden : null,
                Color: node.Type == nameof(AzureResourceKind.ExternalService) ? ValidColor(node.Data.GetValueOrDefault("color")?.ToString()) : null,
                Category: node.Type == nameof(AzureResourceKind.ExternalService)
                    && node.Data.GetValueOrDefault("category")?.ToString() is { Length: > 0 } category ? category : null,
                Emulator: node.Data.GetValueOrDefault("isLocalEmulator") is true ? true : null,
                Aggregate: aggregate ? true : null));
        }

        var viewEdges = new List<ViewEdge>(includedEdges.Count + aggregateEdges.Count);
        var edgeSources = new List<IReadOnlyList<string>>(includedEdges.Count + aggregateEdges.Count);
        foreach (var edge in includedEdges)
        {
            viewEdges.Add(new ViewEdge(nodeIndex[edge.Source], nodeIndex[edge.Target], edge.Type,
                Low: IsLow(edge) ? true : null));
            edgeSources.Add([edge.Id]);
        }
        foreach (var (source, target, type) in aggregateEdges)
        {
            if (!nodeIndex.TryGetValue(source, out var sourceIndex) || !nodeIndex.TryGetValue(target, out var targetIndex)) continue;
            viewEdges.Add(new ViewEdge(sourceIndex, targetIndex, type));
            edgeSources.Add([]);
        }

        return new GraphViewPayload(GraphViewMode.Focus, 0,
            subscriptions.Keys.ToList(), resourceGroups.Keys.ToList(), viewNodes, viewEdges,
            index.Graph.Nodes.Count(n => !IsScope(n.Type)),
            index.Graph.Edges.Count(e => !EdgeTypes.Structural.Contains(e.Type)),
            false)
        {
            NodeIds = orderedNodes.Select(n => n.Id).ToList(),
            EdgeSources = edgeSources
        };

        void AddInitialHop(FocusHop hop)
        {
            // The focused resource must remain present even when an unusually broad estate hits the configured cap.
            var room = Math.Max(0, Options.FocusMaxNodes - nodes.Count);
            AddHop(hop, room);
        }

        void AddHop(FocusHop hop, int? maxNewNodes = null)
        {
            var remainingNewNodes = maxNewNodes;
            foreach (var node in hop.Nodes)
            {
                if (nodes.ContainsKey(node.Id)) continue;
                if (remainingNewNodes is { } remaining)
                {
                    if (remaining <= 0) continue;
                    remainingNewNodes = remaining - 1;
                }
                nodes[node.Id] = node;
            }

            foreach (var edge in hop.Edges)
            {
                if (nodes.ContainsKey(edge.Source) && nodes.ContainsKey(edge.Target)) edges.Add(edge);
            }

            foreach (var (source, target, type) in hop.AggregateEdges)
            {
                if (nodes.ContainsKey(source) && nodes.ContainsKey(target))
                    aggregateEdges.Add((source, target, type));
            }

            foreach (var (child, parent) in hop.Parents)
            {
                if (nodes.ContainsKey(child) && nodes.ContainsKey(parent)) parents.TryAdd(child, parent);
            }
        }
    }

    /// <summary>Returns payload node indexes whose tag name (including overflow keys) and optional value match.</summary>
    public IReadOnlyList<int> MatchTag(GraphViewPayload payload, GraphIndex index, string tagName, string? tagValue)
    {
        if (string.IsNullOrWhiteSpace(tagName)) return [];
        var wanted = tagName.Trim();
        var value = tagValue ?? "";
        var matches = new List<int>();
        for (var i = 0; i < payload.NodeIds.Count && i < payload.Nodes.Count; i++)
        {
            if (!index.NodesById.TryGetValue(payload.NodeIds[i], out var node)) continue;
            if (HasMatchingTag(node, wanted, value)) matches.Add(i);
        }
        return matches;
    }

    /// <summary>Summarizes outgoing dependencies by the target resource category, returning setting keys only.</summary>
    public IReadOnlyList<DependencyGroup> SummarizeDependencies(GraphIndex index, string nodeId)
    {
        if (!index.NodesById.ContainsKey(nodeId)) return [];

        var grouped = new Dictionary<string, List<DependencyItem>>(StringComparer.Ordinal);
        foreach (var edge in index.Out(nodeId).Where(e => !EdgeTypes.Structural.Contains(e.Type)))
        {
            if (!index.NodesById.TryGetValue(edge.Target, out var target)) continue;
            var category = CategoryOf(target.Type);
            if (!grouped.TryGetValue(category, out var items)) grouped[category] = items = [];
            var settingKeys = ReadSettingKeys(edge).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase);
            items.Add(new DependencyItem(target.Id, target.Label, target.Type, edge.Type,
                string.Join(", ", settingKeys), edge.Data.GetValueOrDefault("confidence")?.ToString()));
        }

        return NodeCategories.Order.Where(grouped.ContainsKey)
            .Select(category => new DependencyGroup(category, grouped[category]
                .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.NodeId, StringComparer.Ordinal)
                .ThenBy(item => item.EdgeType, StringComparer.Ordinal)
                .ToList()))
            .ToList();
    }

    public static string CategoryOf(string nodeType) =>
        NodeCategories.ByType.GetValueOrDefault(nodeType, NodeCategories.External);

    private static readonly HashSet<string> FocusDependencyTypes =
        [EdgeTypes.ConnectsTo, EdgeTypes.ReferencesSecret, EdgeTypes.PullsImageFrom];

    private static readonly IReadOnlyDictionary<string, int> CategoryOrder = NodeCategories.Order
        .Select((category, index) => (category, index))
        .ToDictionary(item => item.category, item => item.index, StringComparer.Ordinal);
    private static readonly IComparer<string> CategoryComparer = Comparer<string>.Create((a, b) =>
        CategoryOrder.GetValueOrDefault(a, int.MaxValue).CompareTo(CategoryOrder.GetValueOrDefault(b, int.MaxValue)));

    private sealed record FocusHop(
        IReadOnlyList<GraphNode> Nodes,
        IReadOnlyList<GraphEdge> Edges,
        IReadOnlyList<(string Source, string Target, string Type)> AggregateEdges,
        IReadOnlyList<(string Child, string Parent)> Parents);

    private static FocusHop CollectFocusHop(
        GraphIndex index, GraphNode center, bool hideEmulator, bool showLowConfidence, int maxNeighbours)
    {
        var candidates = new Dictionary<string, (GraphNode Node, List<GraphEdge> Edges)>(StringComparer.OrdinalIgnoreCase);
        var directEdges = new HashSet<GraphEdge>();
        var parents = new List<(string Child, string Parent)>();

        void AddCandidate(GraphNode node, GraphEdge edge)
        {
            if (node.Id.Equals(center.Id, StringComparison.OrdinalIgnoreCase)
                || (hideEmulator && node.Data.GetValueOrDefault("isLocalEmulator") is true))
                return;
            if (!candidates.TryGetValue(node.Id, out var candidate))
                candidates[node.Id] = candidate = (node, []);
            candidate.Edges.Add(edge);
        }

        foreach (var edge in index.Out(center.Id).Concat(index.In(center.Id)))
        {
            if (!FocusDependencyTypes.Contains(edge.Type) || (!showLowConfidence && IsLow(edge))) continue;
            var otherId = edge.Source.Equals(center.Id, StringComparison.OrdinalIgnoreCase) ? edge.Target : edge.Source;
            if (index.NodesById.TryGetValue(otherId, out var other)) AddCandidate(other, edge);
        }
        foreach (var edge in index.Out(center.Id).Where(e =>
                     e.Type == EdgeTypes.HasSlot || EdgeTypes.Children.Contains(e.Type)))
        {
            if (index.NodesById.TryGetValue(edge.Target, out var child)) AddCandidate(child, edge);
        }

        var selected = new List<GraphNode>();
        var aggregateNodes = new List<(GraphNode Node, string Type, bool Incoming)>();
        foreach (var group in candidates.Values
                     .GroupBy(c => CategoryOf(c.Node.Type))
                     .OrderBy(g => g.Key, CategoryComparer))
        {
            var sorted = group.OrderBy(c => c.Node.Label, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Node.Id, StringComparer.Ordinal).ToList();
            var visible = sorted.Take(maxNeighbours).ToList();
            selected.AddRange(visible.Select(c => c.Node));
            foreach (var candidate in visible)
                foreach (var edge in candidate.Edges) directEdges.Add(edge);

            var hiddenCount = sorted.Count - visible.Count;
            if (hiddenCount <= 0) continue;
            var category = group.Key;
            var aggregate = new GraphNode
            {
                Id = $"aggregate:{center.Id}:{category}",
                Type = "Aggregate",
                Label = $"+{hiddenCount} more {category.ToLowerInvariant()}",
                Data = new() { ["isAggregate"] = true }
            };
            selected.Add(aggregate);
            var representative = sorted[visible.Count].Edges[0];
            var incoming = representative.Target.Equals(center.Id, StringComparison.OrdinalIgnoreCase);
            aggregateNodes.Add((aggregate, representative.Type, incoming));
        }

        foreach (var node in selected.Where(n => n.Data.GetValueOrDefault("isAggregate") is not true).ToList())
        {
            foreach (var edge in index.In(node.Id).Where(e => EdgeTypes.Children.Contains(e.Type) || e.Type == EdgeTypes.HasSlot))
            {
                if (!index.NodesById.TryGetValue(edge.Source, out var parent)
                    || (hideEmulator && parent.Data.GetValueOrDefault("isLocalEmulator") is true))
                    continue;
                selected.Add(parent);
                directEdges.Add(edge);
                parents.Add((node.Id, parent.Id));
            }
        }

        var uniqueNodes = new Dictionary<string, GraphNode>(StringComparer.OrdinalIgnoreCase) { [center.Id] = center };
        foreach (var node in selected) uniqueNodes.TryAdd(node.Id, node);
        var aggregateEdges = aggregateNodes.Select(a =>
            a.Incoming ? (a.Node.Id, center.Id, a.Type) : (center.Id, a.Node.Id, a.Type)).ToList();
        return new FocusHop(uniqueNodes.Values.Where(n => !n.Id.Equals(center.Id, StringComparison.OrdinalIgnoreCase)).ToList(),
            directEdges.ToList(), aggregateEdges, parents);
    }

    private static int DependencyDegree(GraphIndex index, string nodeId) =>
        index.Out(nodeId).Concat(index.In(nodeId)).Count(e => FocusDependencyTypes.Contains(e.Type));

    private static bool HasMatchingTag(GraphNode node, string wanted, string value)
    {
        if (!node.Data.TryGetValue(TagKeys.Tags, out var rawTags)) return false;
        IEnumerable<KeyValuePair<string, string>> tags = rawTags switch
        {
            IReadOnlyDictionary<string, string> dictionary => dictionary,
            JsonElement { ValueKind: JsonValueKind.Object } json => json.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.String)
                .Select(p => new KeyValuePair<string, string>(p.Name, p.Value.GetString()!)),
            _ => []
        };
        foreach (var (key, tagValue) in tags)
        {
            var isExact = key.Equals(wanted, StringComparison.OrdinalIgnoreCase);
            var isOverflow = key.Length > wanted.Length + 1
                && key.StartsWith(wanted + "-", StringComparison.OrdinalIgnoreCase)
                && key.AsSpan(wanted.Length + 1).ToString().All(char.IsAsciiDigit);
            if ((isExact || isOverflow)
                && (value.Length == 0 || tagValue.Contains(value, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    private static IEnumerable<string> ReadSettingKeys(GraphEdge edge)
    {
        if (!edge.Data.TryGetValue("references", out var value)) return [];
        if (value is IEnumerable<EdgeReference> references)
            return references.Select(r => r.SettingKey).Where(k => !string.IsNullOrWhiteSpace(k));
        if (value is JsonElement { ValueKind: JsonValueKind.Array } json)
            return json.EnumerateArray().Select(item =>
            {
                if (item.ValueKind != JsonValueKind.Object) return null;
                foreach (var property in item.EnumerateObject())
                    if (property.Name.Equals(nameof(EdgeReference.SettingKey), StringComparison.OrdinalIgnoreCase)
                        && property.Value.ValueKind == JsonValueKind.String)
                        return property.Value.GetString();
                return null;
            }).OfType<string>().Where(k => !string.IsNullOrWhiteSpace(k));
        return [];
    }

    internal sealed record OverviewEdge(int Source, int Target, string Type, IReadOnlyList<string> Ids, bool AllLow);

    internal sealed record OverviewProjection(
        IReadOnlyList<GraphNode> Nodes, IReadOnlyDictionary<GraphNode, int> Collapsed, IReadOnlyList<OverviewEdge> Edges);

    /// <summary>
    /// The nodes and merged edges an Overview draws, in payload order. Shared with <see cref="GraphLayoutService"/> so the
    /// layout covers exactly the nodes and edges that <see cref="BuildOverview"/> sends.
    /// </summary>
    internal static OverviewProjection ProjectOverview(GraphDocument graph, bool showChildResources)
    {
        var drawnAs = new Dictionary<string, GraphNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in graph.Nodes)
        {
            if (!IsScope(node.Type)) drawnAs[node.Id] = node;
        }

        var collapsed = new Dictionary<GraphNode, int>();
        if (!showChildResources)
        {
            foreach (var edge in graph.Edges)
            {
                if (!CollapseEdges.Contains(edge.Type) || !drawnAs.TryGetValue(edge.Source, out var parent) || !drawnAs.ContainsKey(edge.Target)) continue;
                drawnAs[edge.Target] = parent;
                collapsed[parent] = collapsed.GetValueOrDefault(parent) + 1;
            }
        }

        var nodes = graph.Nodes
            .Where(n => drawnAs.TryGetValue(n.Id, out var drawn) && ReferenceEquals(drawn, n))
            .OrderBy(n => n.Id, StringComparer.Ordinal)
            .ToList();
        var indexOf = new Dictionary<GraphNode, int>(nodes.Count);
        for (var i = 0; i < nodes.Count; i++) indexOf[nodes[i]] = i;

        var merged = new Dictionary<(int Source, int Target, string Type), (List<string> Ids, bool AllLow)>();
        foreach (var edge in graph.Edges)
        {
            if (EdgeTypes.Structural.Contains(edge.Type)
                || !drawnAs.TryGetValue(edge.Source, out var source)
                || !drawnAs.TryGetValue(edge.Target, out var target)
                // A child that depends on its own parent folds into a self-loop, which tells the user nothing.
                || ReferenceEquals(source, target)) continue;

            var key = (indexOf[source], indexOf[target], edge.Type);
            var low = IsLow(edge);
            if (merged.TryGetValue(key, out var existing))
            {
                existing.Ids.Add(edge.Id);
                merged[key] = (existing.Ids, existing.AllLow && low);
            }
            else
            {
                merged[key] = ([edge.Id], low);
            }
        }

        var edges = merged
            .OrderBy(m => m.Key.Source).ThenBy(m => m.Key.Target).ThenBy(m => m.Key.Type, StringComparer.Ordinal)
            .Select(m => new OverviewEdge(m.Key.Source, m.Key.Target, m.Key.Type, m.Value.Ids, m.Value.AllLow))
            .ToList();
        return new OverviewProjection(nodes, collapsed, edges);
    }

    private static bool IsScope(string nodeType) =>
        NodeCategories.ByType.GetValueOrDefault(nodeType) == NodeCategories.Scope;

    private static bool IsLow(GraphEdge edge) =>
        string.Equals(edge.Data.GetValueOrDefault("confidence")?.ToString(), "Low", StringComparison.OrdinalIgnoreCase);

    /// <summary>Lower-case distinct values of a node data key, in ordinal order, mapped to their index.</summary>
    private static SortedDictionary<string, int> Lookup(IEnumerable<GraphNode> nodes, string key)
    {
        var values = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var value in nodes.Select(n => Lower(n, key)).OfType<string>()) values.TryAdd(value, 0);
        var i = 0;
        foreach (var value in values.Keys.ToList()) values[value] = i++;
        return values;
    }

    private static int? IndexIn(SortedDictionary<string, int> lookup, GraphNode node, string key) =>
        Lower(node, key) is { } value ? lookup[value] : null;

    private static string? Lower(GraphNode node, string key) =>
        node.Data.GetValueOrDefault(key)?.ToString() is { Length: > 0 } value ? value.ToLowerInvariant() : null;

    internal static string? ValidColor(string? color) => color is not null && HexColor().IsMatch(color) ? color : null;

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();
}
