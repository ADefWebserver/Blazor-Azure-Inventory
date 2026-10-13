using System.Text;
using BlazorAzureInventory.Models.Graph;
using BlazorAzureInventory.Models.Inventory;
using BlazorAzureInventory.Models.Options;
using BlazorAzureInventory.Models.Tags;
using Microsoft.Extensions.Options;

namespace BlazorAzureInventory.Services.Tags;

/// <summary>
/// Builds <c>conn-out-&lt;Type&gt;</c> / <c>conn-in-&lt;Type&gt;</c> tags from the graph. Deterministic and does no I/O, so the
/// Tags tab, the bulk dialog, and the AI proposal tool all produce identical results.
/// </summary>
public sealed class ConnectionTagGenerator(IOptions<TaggingOptions> options)
{
    private static readonly HashSet<string> ChildEdges = [EdgeTypes.HostsDatabase, EdgeTypes.HasContainer, EdgeTypes.HasQueue, EdgeTypes.HasSlot];
    private static readonly HashSet<string> ScopeTypes =
        [nameof(AzureResourceKind.Tenant), nameof(AzureResourceKind.Subscription), nameof(AzureResourceKind.ResourceGroup)];

    private ConnectionTagOptions Opts => options.Value.Connections;

    /// <summary>True for tags this generator owns and may remove on regeneration.</summary>
    public bool IsOwned(string tagName)
    {
        var o = Opts;
        return tagName.StartsWith(o.OutPrefix, StringComparison.OrdinalIgnoreCase)
               || tagName.StartsWith(o.InPrefix, StringComparison.OrdinalIgnoreCase)
               || (!string.IsNullOrEmpty(o.UpdatedTagName) && tagName.Equals(o.UpdatedTagName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Resource nodes that can receive connection tags (not scope, external, aggregate, or non-taggable nodes).</summary>
    public static bool IsCandidate(GraphNode node) =>
        node.IsTaggable() && !ScopeTypes.Contains(node.Type) && node.Type != nameof(AzureResourceKind.External);

    /// <summary>The generated connection tags (owned tags only) for one node, before budget trimming.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> GenerateTags(GraphIndex index, GraphNode node, DateTimeOffset scannedUtc)
    {
        var o = Opts;
        var edgeTypes = new HashSet<string>(o.EffectiveEdgeTypes, StringComparer.OrdinalIgnoreCase);
        if (o.IncludeStructural) edgeTypes.UnionWith(EdgeTypes.Structural);

        var groups = new SortedDictionary<(int Order, string Type), SortedSet<string>>(
            Comparer<(int Order, string Type)>.Create((a, b) =>
                a.Order != b.Order ? a.Order.CompareTo(b.Order) : string.Compare(a.Type, b.Type, StringComparison.OrdinalIgnoreCase)));

        void Collect(int order, IEnumerable<GraphEdge> edges, Func<GraphEdge, string> other)
        {
            foreach (var edge in edges)
            {
                if (!edgeTypes.Contains(edge.Type)) continue;
                if (!o.IncludeLowConfidence && string.Equals(edge.Data.GetValueOrDefault("confidence")?.ToString(), "Low", StringComparison.OrdinalIgnoreCase)) continue;
                if (!index.NodesById.TryGetValue(other(edge), out var target)) continue;
                if (ScopeTypes.Contains(target.Type) || target.Data.ContainsKey("isAggregate")) continue;
                if (!o.IncludeExternal && target.Type is nameof(AzureResourceKind.External) or nameof(AzureResourceKind.ExternalService)) continue;

                var key = (order, target.Type);
                if (!groups.TryGetValue(key, out var names)) groups[key] = names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                names.Add(DisplayName(index, target).Replace(o.Separator, "_", StringComparison.Ordinal));
            }
        }

        Collect(0, index.Out(node.Id), e => e.Target);
        Collect(1, index.In(node.Id), e => e.Source);

        var result = new List<KeyValuePair<string, string>>();
        if (groups.Count == 0 && !o.WriteWhenEmpty) return result;

        if (!string.IsNullOrEmpty(o.UpdatedTagName))
        {
            result.Add(new(o.UpdatedTagName, scannedUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")));
        }

        foreach (var ((order, type), names) in groups)
        {
            var prefix = order == 0 ? o.OutPrefix : o.InPrefix;
            var chunks = Chunk(names, o.Separator, o.MaxValueLength);
            for (var i = 0; i < chunks.Count; i++)
            {
                result.Add(new(i == 0 ? $"{prefix}{type}" : $"{prefix}{type}-{i + 1}", chunks[i]));
            }
        }
        return result;
    }

    /// <summary>Builds the diff that brings a node's owned connection tags in line with the graph.</summary>
    public ResourceTagDiff Generate(GraphIndex index, string resourceId, DateTimeOffset scannedUtc)
    {
        if (!index.NodesById.TryGetValue(resourceId, out var node))
        {
            return new ResourceTagDiff(resourceId, resourceId, "", null, "", AzureResourceInfo.EmptyTags, [])
            {
                Errors = ["The resource is not in the last scan."]
            };
        }

        var current = node.GetTags();
        var armType = node.Data.GetValueOrDefault("armType")?.ToString();
        var tenantId = node.Data.GetValueOrDefault("tenantId")?.ToString() ?? "";
        if (!IsCandidate(node))
        {
            return new ResourceTagDiff(node.Id, node.Label, node.Type, armType, tenantId, current, [])
            {
                Errors = ["This resource cannot receive connection tags."]
            };
        }

        var generated = GenerateTags(index, node, scannedUtc);
        var nonOwned = current.Where(t => !IsOwned(t.Key)).ToList();
        var budget = Math.Max(0, TagValidator.MaxTagsPerResource - nonOwned.Count);
        var warnings = new List<string>();
        var kept = generated.Take(budget).ToList();
        if (generated.Count > budget)
        {
            warnings.Add($"{generated.Count - budget} connection tag(s) were omitted because of the {TagValidator.MaxTagsPerResource}-tag limit.");
        }

        var desired = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in nonOwned) desired[k] = v;
        foreach (var (k, v) in kept) desired[k] = v;

        return new ResourceTagDiff(node.Id, node.Label, node.Type, armType, tenantId, current, TagDiffBuilder.Diff(current, desired))
        {
            Warnings = warnings
        };
    }

    public IReadOnlyList<ResourceTagDiff> GenerateMany(GraphIndex index, IEnumerable<string> resourceIds, DateTimeOffset scannedUtc) =>
        resourceIds.Distinct(StringComparer.OrdinalIgnoreCase).Select(id => Generate(index, id, scannedUtc)).ToList();

    /// <summary>Candidate nodes filtered by subscription ID, resource group name, and node types (all optional).</summary>
    public static List<GraphNode> Candidates(GraphIndex index, string? subscriptionId, string? resourceGroup, IReadOnlyCollection<string>? types) =>
        index.Graph.Nodes
            .Where(IsCandidate)
            .Where(n => string.IsNullOrWhiteSpace(subscriptionId)
                        || string.Equals(n.Data.GetValueOrDefault("subscriptionId")?.ToString(), subscriptionId, StringComparison.OrdinalIgnoreCase))
            .Where(n => string.IsNullOrWhiteSpace(resourceGroup)
                        || string.Equals(n.Data.GetValueOrDefault("resourceGroup")?.ToString(), resourceGroup, StringComparison.OrdinalIgnoreCase))
            .Where(n => types is not { Count: > 0 } || types.Contains(n.Type, StringComparer.OrdinalIgnoreCase))
            .OrderBy(n => n.Type, StringComparer.Ordinal).ThenBy(n => n.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Diffs for every node that needs changes, plus how many are already up to date.</summary>
    public (List<ResourceTagDiff> Diffs, int UpToDate) BuildProposalDiffs(GraphIndex index, IEnumerable<GraphNode> nodes, DateTimeOffset scannedUtc)
    {
        var diffs = new List<ResourceTagDiff>();
        var upToDate = 0;
        foreach (var node in nodes)
        {
            var diff = Generate(index, node.Id, scannedUtc);
            if (diff.IsEmpty && diff.IsValid) upToDate++;
            else diffs.Add(diff);
        }
        return (diffs, upToDate);
    }

    /// <summary>
    /// Unique display form: <c>server/database</c>, <c>account/container</c>, <c>site/slot</c> (the slot label), or the label.
    /// </summary>
    public static string DisplayName(GraphIndex index, GraphNode node)
    {
        if (node.Type is nameof(AzureResourceKind.SqlDatabase) or nameof(AzureResourceKind.BlobContainer) or nameof(AzureResourceKind.StorageQueue))
        {
            var parent = index.In(node.Id).FirstOrDefault(e => ChildEdges.Contains(e.Type));
            if (parent is not null && index.NodesById.TryGetValue(parent.Source, out var p)) return $"{p.Label}/{node.Label}";
        }
        return node.Label;
    }

    /// <summary>Packs names into values no longer than <paramref name="max"/> without splitting a name.</summary>
    public static List<string> Chunk(IEnumerable<string> names, string separator, int max)
    {
        var chunks = new List<string>();
        var sb = new StringBuilder();
        foreach (var raw in names)
        {
            var name = raw.Length > max ? raw[..(max - 1)] + "…" : raw;
            var needed = sb.Length == 0 ? name.Length : sb.Length + separator.Length + name.Length;
            if (needed > max && sb.Length > 0)
            {
                chunks.Add(sb.ToString());
                sb.Clear();
            }
            if (sb.Length > 0) sb.Append(separator);
            sb.Append(name);
        }
        if (sb.Length > 0) chunks.Add(sb.ToString());
        return chunks;
    }
}
