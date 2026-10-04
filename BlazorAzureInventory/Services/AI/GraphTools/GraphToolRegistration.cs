using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace BlazorAzureInventory.Services.AI.GraphTools;

/// <summary>Exposes <see cref="IInventoryGraphTools"/> as read-only <see cref="AIFunction"/>s.</summary>
public static class GraphToolRegistration
{
    public static IReadOnlyList<AIFunction> Create(IInventoryGraphTools t) =>
    [
        AIFunctionFactory.Create(() => t.Stats(), nameof(IInventoryGraphTools.Stats),
            "Counts of nodes and edges by type, scan time, scope, and number of scan issues."),
        AIFunctionFactory.Create(
            ([Description("Text to find in resource names")] string query,
             [Description("Optional node type, e.g. WebApp, SqlDatabase, StorageAccount")] string? type,
             [Description("Maximum results")] int max) => t.SearchNodes(query, type, max),
            nameof(IInventoryGraphTools.SearchNodes), "Finds graph nodes whose name contains the query."),
        AIFunctionFactory.Create(
            ([Description("Node ID or resource name")] string id) => t.GetNode(id),
            nameof(IInventoryGraphTools.GetNode), "Returns non-secret properties of one node."),
        AIFunctionFactory.Create(
            ([Description("Node ID or resource name")] string id,
             [Description("Optional edge type, e.g. CONNECTS_TO")] string? edgeType,
             [Description("in, out, or both")] string? direction,
             [Description("Maximum results")] int max) => t.GetNeighbors(id, edgeType, direction, max),
            nameof(IInventoryGraphTools.GetNeighbors), "Lists neighbors of a node with the setting keys on each edge."),
        AIFunctionFactory.Create(
            ([Description("Optional subscription name or ID")] string? subscription,
             [Description("Optional resource group name")] string? resourceGroup,
             [Description("Optional kind: WebApp, FunctionApp, WebAppSlot, ContainerApp")] string? kind,
             [Description("Maximum results")] int max) => t.ListApps(subscription, resourceGroup, kind, max),
            nameof(IInventoryGraphTools.ListApps), "Lists Web Apps, Function Apps, slots, and Container Apps."),
        AIFunctionFactory.Create(
            ([Description("App name or ID")] string app) => t.GetAppDependencies(app),
            nameof(IInventoryGraphTools.GetAppDependencies), "Lists what an app connects to, with setting keys."),
        AIFunctionFactory.Create(
            ([Description("Resource name, 'server/database', 'account/container', or ID")] string resource,
             [Description("Maximum results")] int max) => t.FindConsumers(resource, max),
            nameof(IInventoryGraphTools.FindConsumers), "Lists apps that connect to a resource (or its databases/containers), with setting keys."),
        AIFunctionFactory.Create(
            ([Description("Minimum number of consuming apps (at least 2)")] int minConsumers,
             [Description("Maximum results")] int max) => t.ListSharedResources(minConsumers, max),
            nameof(IInventoryGraphTools.ListSharedResources), "Lists resources used by several apps."),
        AIFunctionFactory.Create(
            ([Description("Optional node type, e.g. SqlDatabase")] string? type,
             [Description("Maximum results")] int max) => t.ListUnreferencedResources(type, max),
            nameof(IInventoryGraphTools.ListUnreferencedResources), "Lists data resources that no app references."),
        AIFunctionFactory.Create(
            ([Description("Maximum results")] int max) => t.ListUnresolvedConnections(max),
            nameof(IInventoryGraphTools.ListUnresolvedConnections), "Lists connections to targets outside the scanned subscriptions."),
        AIFunctionFactory.Create(
            ([Description("Maximum results")] int max) => t.ListSecretLikePlainSettings(max),
            nameof(IInventoryGraphTools.ListSecretLikePlainSettings), "Lists setting keys that look like plain-text secrets (keys only, never values)."),
        AIFunctionFactory.Create(
            ([Description("Maximum results")] int max) => t.ListScanIssues(max),
            nameof(IInventoryGraphTools.ListScanIssues), "Lists resources that could not be fully read during the scan and why."),
    ];
}
