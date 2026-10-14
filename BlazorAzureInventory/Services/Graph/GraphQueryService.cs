using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlazorAzureInventory.Models.Graph;

namespace BlazorAzureInventory.Services.Graph;

public static class GraphJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
}

/// <summary>Serializer for <see cref="GraphViewPayload"/>. Separate from <see cref="GraphJson"/> so Export JSON is unaffected.</summary>
public static class GraphViewJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
}

/// <summary>Read-only queries over a session's <see cref="GraphIndex"/>.</summary>
public sealed class GraphQueryService
{
    public GraphFacets GetFacets(GraphIndex index)
    {
        var counts = index.Graph.Nodes
            .GroupBy(n => n.Type)
            .ToDictionary(g => g.Key, g => g.Count());

        var subscriptions = index.Graph.Nodes
            .Where(n => n.Type == "Subscription")
            .Select(n => new KeyValuePair<string, string>(n.Data.GetValueOrDefault("subscriptionId")?.ToString() ?? n.Id, n.Label))
            .OrderBy(kv => kv.Value)
            .ToList();

        var groups = index.Graph.Nodes
            .Where(n => n.Type == "ResourceGroup")
            .Select(n => n.Label)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n)
            .Select(n => new KeyValuePair<string, string>(n.ToLowerInvariant(), n))
            .ToList();

        return new GraphFacets(counts, subscriptions, groups);
    }

    /// <summary>A keyword-matched 1-hop subgraph excerpt used to ground models without tool support.</summary>
    public string BuildContext(GraphIndex index, string prompt, int maxNodes = 30)
    {
        var keywords = prompt
            .Split([' ', ',', '.', '?', '!', ';', ':', '"', '\'', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3)
            .Select(w => w.ToLowerInvariant())
            .Distinct()
            .ToList();
        if (keywords.Count == 0) return "";

        var seeds = index.Graph.Nodes
            .Where(n => !NodeTypeStyles.HiddenByDefault.Contains(n.Type) && keywords.Any(k => n.Label.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .Take(maxNodes)
            .ToList();

        var sb = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in seeds)
        {
            if (seen.Count >= maxNodes) break;
            if (!seen.Add(node.Id)) continue;
            sb.AppendLine($"- {node.Type} \"{node.Label}\"");
            foreach (var e in index.Out(node.Id).Concat(index.In(node.Id)).Where(e => !EdgeTypes.Structural.Contains(e.Type) || e.Type == EdgeTypes.HostsDatabase))
            {
                var outgoing = e.Source.Equals(node.Id, StringComparison.OrdinalIgnoreCase);
                var otherId = outgoing ? e.Target : e.Source;
                if (!index.NodesById.TryGetValue(otherId, out var other)) continue;
                var keys = e.Data.TryGetValue("references", out var r) && r is List<EdgeReference> refs
                    ? $" via {string.Join(", ", refs.Select(x => x.SettingKey).Distinct())}"
                    : "";
                sb.AppendLine(outgoing
                    ? $"  -[{e.Type}]-> {other.Type} \"{other.Label}\"{keys}"
                    : $"  <-[{e.Type}]- {other.Type} \"{other.Label}\"{keys}");
            }
        }
        return sb.ToString();
    }
}
