namespace BlazorAzureInventory.Services.AI.GraphTools;

public sealed record GraphStats(
    int Nodes, int Edges, IReadOnlyDictionary<string, int> NodesByType, IReadOnlyDictionary<string, int> EdgesByType,
    string ScannedUtc, string Scope, int ScanIssues);

public sealed record NodeSummary(string Id, string Type, string Label);

public sealed record NodeDetail(
    string Id, string Type, string Label, IReadOnlyDictionary<string, string?> Properties, int InDegree, int OutDegree);

public sealed record Neighbor(
    string Direction, string EdgeType, string NodeId, string NodeType, string Label, IReadOnlyList<string> SettingKeys);

public sealed record AppSummary(
    string Id, string Name, string Kind, string SubscriptionId, string ResourceGroup, int Connections, string? SettingsStatus);

public sealed record Dependency(
    string EdgeType, string TargetType, string Target, IReadOnlyList<string> SettingKeys, string? Confidence);

public sealed record AppDependencies(
    string App, string Kind, string? Environment, string? SettingsStatus, string? ConfigFilesStatus, IReadOnlyList<Dependency> Dependencies);

public sealed record Consumer(string App, string AppType, string EdgeType, string Target, IReadOnlyList<string> SettingKeys);

public sealed record SharedResource(string Resource, string Type, int ConsumerCount, IReadOnlyList<string> Consumers);

public sealed record UnusedResource(string Resource, string Type, string? ResourceGroup);

public sealed record UnresolvedConnection(string App, string Target, string Kind, string? Reason, IReadOnlyList<string> SettingKeys);

public sealed record SecretHygieneFinding(string App, string SettingKey, string Source);

public sealed record ScanIssueSummary(string Resource, string Stage, string Kind, string Message);
