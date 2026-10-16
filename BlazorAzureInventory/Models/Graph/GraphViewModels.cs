using System.Text.Json.Serialization;

namespace BlazorAzureInventory.Models.Graph;

public enum GraphViewMode { Overview, Focus }

/// <summary>What the browser receives. Node and edge IDs stay on the server (see NodeIds / EdgeSources).</summary>
public sealed record GraphViewPayload(
    [property: JsonPropertyName("mode")] GraphViewMode Mode,
    [property: JsonPropertyName("focus")] int? FocusIndex,
    [property: JsonPropertyName("subs")] IReadOnlyList<string> Subscriptions,   // subscription IDs, indexed by ViewNode.Sub
    [property: JsonPropertyName("rgs")] IReadOnlyList<string> ResourceGroups,   // lower-case RG names, indexed by ViewNode.Rg
    [property: JsonPropertyName("nodes")] IReadOnlyList<ViewNode> Nodes,
    [property: JsonPropertyName("edges")] IReadOnlyList<ViewEdge> Edges,
    [property: JsonPropertyName("totalNodes")] int TotalNodes,
    [property: JsonPropertyName("totalEdges")] int TotalEdges,
    [property: JsonPropertyName("fallbackLayout")] bool FallbackLayout)
{
    // Not 'required': System.Text.Json rejects required members that are also [JsonIgnore].
    [JsonIgnore] public IReadOnlyList<string> NodeIds { get; init; } = [];                     // index -> GraphNode.Id
    [JsonIgnore] public IReadOnlyList<IReadOnlyList<string>> EdgeSources { get; init; } = [];  // index -> GraphEdge.Id list
    // Focus only: payload index of each "+N more" node -> the resources it hides. Server only, like NodeIds.
    [JsonIgnore] public IReadOnlyDictionary<int, AggregateMembership> Aggregates { get; init; } =
        new Dictionary<int, AggregateMembership>();
}

/// <summary>One hidden neighbour and the edge that made it a candidate. Server only.</summary>
public sealed record AggregateMember(string NodeId, GraphEdge Edge);

/// <summary>Hidden neighbours behind one "+N more" node, reconciled against the final payload. Server only.</summary>
public sealed record AggregateMembership(
    string CenterId,                         // focus centre or expanded node the aggregate hangs off
    string Category,                         // NodeCategories value, e.g. "Apps"
    IReadOnlyList<AggregateMember> Members); // ordered by label then ID; never in the payload

public sealed record ViewNode(
    [property: JsonPropertyName("t")] string Type,
    [property: JsonPropertyName("l")] string Label,
    [property: JsonPropertyName("s")] int? Sub = null,
    [property: JsonPropertyName("g")] int? Rg = null,
    [property: JsonPropertyName("x")] float? X = null,
    [property: JsonPropertyName("y")] float? Y = null,
    [property: JsonPropertyName("d")] int Degree = 0,
    [property: JsonPropertyName("p")] int? Parent = null,              // Focus only: compound parent index
    [property: JsonPropertyName("k")] int? CollapsedChildren = null,   // Overview only
    [property: JsonPropertyName("m")] int? HiddenNeighbours = null,    // Focus only: "+48 apps"
    [property: JsonPropertyName("c")] string? Color = null,            // validated #RRGGBB only
    [property: JsonPropertyName("cat")] string? Category = null,       // ExternalService category, e.g. "Email"
    [property: JsonPropertyName("emu")] bool? Emulator = null,
    [property: JsonPropertyName("agg")] bool? Aggregate = null);       // "+N more" nodes

public sealed record ViewEdge(
    [property: JsonPropertyName("s")] int Source,
    [property: JsonPropertyName("t")] int Target,
    [property: JsonPropertyName("y")] string Type,
    [property: JsonPropertyName("lo")] bool? Low = null,
    [property: JsonPropertyName("n")] int? Count = null);              // merged parallel edges in Overview

public sealed record DependencyItem(string NodeId, string Label, string Type, string EdgeType, string SettingKeys, string? Confidence);

public sealed record DependencyGroup(string Category, IReadOnlyList<DependencyItem> Items);

public sealed record GraphLayout(
    GraphDocument Graph,                                        // reference identity is the cache key
    IReadOnlyDictionary<string, (float X, float Y)> Positions,  // by GraphNode.Id
    bool IsFallback,
    TimeSpan Elapsed);
