namespace BlazorAzureInventory.Services.AI.GraphTools;

/// <summary>Read-only queries over the user's last scan. Results never contain setting values.</summary>
public interface IInventoryGraphTools
{
    GraphStats Stats();
    NodeSummary[] SearchNodes(string query, string? type, int max);
    NodeDetail? GetNode(string id);
    Neighbor[] GetNeighbors(string id, string? edgeType, string? direction, int max);
    AppSummary[] ListApps(string? subscription, string? resourceGroup, string? kind, int max);
    AppDependencies? GetAppDependencies(string appIdOrName);
    Consumer[] FindConsumers(string resourceIdOrName, int max);
    SharedResource[] ListSharedResources(int minConsumers, int max);
    UnusedResource[] ListUnreferencedResources(string? type, int max);
    UnresolvedConnection[] ListUnresolvedConnections(int max);
    SecretHygieneFinding[] ListSecretLikePlainSettings(int max);
    ScanIssueSummary[] ListScanIssues(int max);
}
