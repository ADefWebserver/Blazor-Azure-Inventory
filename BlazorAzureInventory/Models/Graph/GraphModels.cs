namespace BlazorAzureInventory.Models.Graph;

public sealed class GraphDocument
{
    public int Version { get; set; } = 1;
    public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;
    public string ScopeDescription { get; set; } = "";
    public List<GraphNode> Nodes { get; set; } = new();
    public List<GraphEdge> Edges { get; set; } = new();
}

public sealed class GraphNode
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Label { get; set; } = "";
    public Dictionary<string, object?> Data { get; set; } = new();
}

public sealed class GraphEdge
{
    public string Id { get; set; } = "";
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public string Type { get; set; } = "";
    public Dictionary<string, object?> Data { get; set; } = new();
}

public static class EdgeTypes
{
    public const string Contains = "CONTAINS";
    public const string HasSlot = "HAS_SLOT";
    public const string HostsDatabase = "HOSTS_DATABASE";
    public const string HasContainer = "HAS_CONTAINER";
    public const string ConnectsTo = "CONNECTS_TO";
    public const string ReferencesSecret = "REFERENCES_SECRET";
    public const string PullsImageFrom = "PULLS_IMAGE_FROM";

    public static readonly IReadOnlySet<string> Structural =
        new HashSet<string> { Contains, HasSlot, HostsDatabase, HasContainer };
}

/// <summary>A non-secret setting reference attached to a CONNECTS_TO or REFERENCES_SECRET edge.</summary>
public sealed record EdgeReference(
    string SettingKey,
    string? Source = null,
    string? FileName = null,
    string? Environment = null,
    string? Confidence = null,
    bool IsIdentityBased = false,
    string? UnresolvedDatabase = null,
    string? SecretName = null);

/// <summary>Adjacency lists built once per graph for O(1) neighbor lookups.</summary>
public sealed class GraphIndex
{
    public GraphIndex(GraphDocument graph)
    {
        Graph = graph;
        NodesById = graph.Nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var edge in graph.Edges)
        {
            Add(OutEdges, edge.Source, edge);
            Add(InEdges, edge.Target, edge);
        }
    }

    public GraphDocument Graph { get; }
    public IReadOnlyDictionary<string, GraphNode> NodesById { get; }
    public Dictionary<string, List<GraphEdge>> OutEdges { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<GraphEdge>> InEdges { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<GraphEdge> Out(string id) => OutEdges.TryGetValue(id, out var l) ? l : [];
    public IReadOnlyList<GraphEdge> In(string id) => InEdges.TryGetValue(id, out var l) ? l : [];

    private static void Add(Dictionary<string, List<GraphEdge>> map, string key, GraphEdge edge)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<GraphEdge>();
            map[key] = list;
        }
        list.Add(edge);
    }
}

/// <summary>Client-side filter state sent to graph-view.js. Applied without re-sending graph data.</summary>
public sealed class GraphFilterState
{
    public List<string> NodeTypes { get; set; } = new();
    public List<string> Subscriptions { get; set; } = new();
    public List<string> ResourceGroups { get; set; } = new();
    public bool OnlyConnected { get; set; }
    public bool HideExternal { get; set; }
    public bool HideEmulator { get; set; } = true;
    public bool ShowLowConfidence { get; set; }
}

public sealed record GraphFacets(
    IReadOnlyDictionary<string, int> NodeTypeCounts,
    IReadOnlyList<KeyValuePair<string, string>> Subscriptions,
    IReadOnlyList<KeyValuePair<string, string>> ResourceGroups);

public static class NodeTypeStyles
{
    public static readonly IReadOnlyList<(string Type, string Color)> All =
    [
        ("WebApp", "#0078D4"),
        ("FunctionApp", "#F2A900"),
        ("WebAppSlot", "#69AFE5"),
        ("ContainerApp", "#008575"),
        ("ContainerRegistry", "#004E46"),
        ("SqlServer", "#2E7D32"),
        ("SqlDatabase", "#66BB6A"),
        ("StorageAccount", "#7B1FA2"),
        ("BlobContainer", "#CE93D8"),
        ("CosmosAccount", "#5C6BC0"),
        ("RedisCache", "#D32F2F"),
        ("ServiceBusNamespace", "#455A64"),
        ("KeyVault", "#C8A400"),
        ("External", "#9E9E9E"),
        ("Tenant", "#BDBDBD"),
        ("Subscription", "#BDBDBD"),
        ("ResourceGroup", "#BDBDBD"),
    ];

    public static readonly IReadOnlySet<string> HiddenByDefault =
        new HashSet<string> { "Tenant", "Subscription", "ResourceGroup" };

    public static string ColorOf(string type) =>
        All.FirstOrDefault(t => t.Type == type).Color ?? "#9E9E9E";
}
