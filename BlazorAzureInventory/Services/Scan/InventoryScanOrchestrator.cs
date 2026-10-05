using System.Collections.Concurrent;
using BlazorAzureInventory.Models.Configuration;
using BlazorAzureInventory.Models.Graph;
using BlazorAzureInventory.Models.Inventory;
using BlazorAzureInventory.Models.Options;
using BlazorAzureInventory.Services.Analysis;
using BlazorAzureInventory.Services.Auth;
using BlazorAzureInventory.Services.Azure;
using BlazorAzureInventory.Services.Graph;
using Microsoft.Extensions.Options;

namespace BlazorAzureInventory.Services.Scan;

/// <summary>Runs discovery, enrichment, configuration analysis, matching, and graph building for the signed-in user.</summary>
public sealed class InventoryScanOrchestrator(
    ResourceGraphInventoryService resourceGraph,
    ResourceEnrichmentService enrichment,
    AppConfigurationReader configReader,
    ResourceMatcher matcher,
    InventoryGraphBuilder graphBuilder,
    InventorySessionStore store,
    UserContextAccessor userContext,
    IOptions<InventoryOptions> options,
    ILogger<InventoryScanOrchestrator> logger)
{
    public async Task<InventorySnapshot> ScanAsync(ScanRequest request, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        var opts = options.Value;
        var issues = new ConcurrentBag<ScanIssue>();
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = opts.MaxParallelism, CancellationToken = ct };
        var user = await userContext.GetUserAsync();
        logger.LogInformation("Scan started for {SubscriptionCount} subscriptions in {TenantCount} tenants",
            request.Subscriptions.Count, request.Tenants.Count);

        // 1. Resolve subscriptions per tenant
        var stage = ScanProgress.Stages[0];
        progress.Report(new ScanProgress(stage, 0, 1, null));
        var byTenant = request.Subscriptions
            .GroupBy(s => s.TenantId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        progress.Report(new ScanProgress(stage, 1, 1, null));

        // 2. Resource Graph per tenant
        stage = ScanProgress.Stages[1];
        var resources = new List<AzureResourceInfo>();
        var groups = new List<ResourceGroupInfo>();
        var done = 0;
        foreach (var tenantGroup in byTenant)
        {
            var tenantName = request.Tenants.FirstOrDefault(t => t.TenantId == tenantGroup.Key)?.DisplayName ?? tenantGroup.Key;
            progress.Report(new ScanProgress(stage, done, byTenant.Count, tenantName));
            try
            {
                var (found, rgs) = await resourceGraph.QueryAsync(tenantGroup.Key, tenantGroup.Select(s => s.SubscriptionId).ToList(), ct);
                resources.AddRange(found);
                groups.AddRange(rgs);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var (kind, message) = ScanErrors.Classify(ex, AzureRoleRequirements.ResourceGraph.Hint);
                issues.Add(new ScanIssue($"tenant:{tenantGroup.Key}", stage, kind, message));
            }
            progress.Report(new ScanProgress(stage, ++done, byTenant.Count, tenantName));
        }

        // 3. Blob containers
        stage = ScanProgress.Stages[2];
        var accounts = resources.Where(r => r.Kind == AzureResourceKind.StorageAccount).ToList();
        var containers = new ConcurrentBag<AzureResourceInfo>();
        var accountExtras = new ConcurrentDictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        done = 0;
        await Parallel.ForEachAsync(accounts, parallel, async (account, token) =>
        {
            try
            {
                var (list, truncated) = await enrichment.ListBlobContainersAsync(account, opts.MaxBlobContainersPerAccount, token);
                foreach (var c in list) containers.Add(c);
                accountExtras[account.Id] = new() { ["containerCount"] = list.Count.ToString(), ["containersTruncated"] = truncated ? "true" : "false" };
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                var (kind, message) = ScanErrors.Classify(ex, AzureRoleRequirements.BlobContainers.Hint);
                issues.Add(new ScanIssue(account.Id, stage, kind, message));
            }
            progress.Report(new ScanProgress(stage, Interlocked.Increment(ref done), accounts.Count, account.Name));
        });
        resources.AddRange(containers);

        // 4/5. App Service settings and config files (processed per app so raw values stay short-lived)
        var analyses = new ConcurrentBag<AppConfigAnalysis>();
        var appExtras = new ConcurrentDictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var webApps = resources.Where(r => r.Kind is AzureResourceKind.WebApp or AzureResourceKind.FunctionApp or AzureResourceKind.WebAppSlot).ToList();
        done = 0;
        await Parallel.ForEachAsync(webApps, parallel, async (app, token) =>
        {
            progress.Report(new ScanProgress(ScanProgress.Stages[3], done, webApps.Count, app.Name));
            var result = await configReader.ReadAppServiceAsync(app,
                s => progress.Report(new ScanProgress(s, done, webApps.Count, app.Name)), token);
            analyses.Add(result.Analysis);
            appExtras[app.Id] = result.ExtraProperties;
            foreach (var i in result.Issues) issues.Add(i);
            progress.Report(new ScanProgress(ScanProgress.Stages[3], Interlocked.Increment(ref done), webApps.Count, app.Name));
        });

        // 6. Container Apps
        stage = ScanProgress.Stages[5];
        var containerApps = resources.Where(r => r.Kind == AzureResourceKind.ContainerApp).ToList();
        done = 0;
        await Parallel.ForEachAsync(containerApps, parallel, async (app, token) =>
        {
            var result = await configReader.ReadContainerAppAsync(app, token);
            analyses.Add(result.Analysis);
            appExtras[app.Id] = result.ExtraProperties;
            foreach (var i in result.Issues) issues.Add(i);
            progress.Report(new ScanProgress(stage, Interlocked.Increment(ref done), containerApps.Count, app.Name));
        });

        ct.ThrowIfCancellationRequested();

        // 7. Merge extra properties and match references to resources
        stage = ScanProgress.Stages[6];
        progress.Report(new ScanProgress(stage, 0, 1, null));
        resources = resources.Select(r =>
        {
            IReadOnlyDictionary<string, string>? extra = appExtras.TryGetValue(r.Id, out var e) ? e
                : accountExtras.TryGetValue(r.Id, out var a) ? a : null;
            if (extra is null || extra.Count == 0) return r;
            var merged = new Dictionary<string, string>(r.Properties, StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in extra) merged[k] = v;
            return r with { Properties = merged };
        }).ToList();

        var index = matcher.BuildIndex(resources);
        var matched = analyses
            .OrderBy(a => a.AppId, StringComparer.Ordinal)
            .Select(a => matcher.Match(a, index))
            .ToList();
        progress.Report(new ScanProgress(stage, 1, 1, null));

        var tenantIds = byTenant.Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var snapshot = new InventorySnapshot(
            DateTimeOffset.UtcNow,
            request.Tenants.Where(t => tenantIds.Contains(t.TenantId)).ToList(),
            request.Subscriptions,
            groups.DistinctBy(g => g.Id).ToList(),
            resources.OrderBy(r => r.Id, StringComparer.Ordinal).ToList(),
            matched,
            issues.OrderBy(i => i.Stage).ThenBy(i => i.ResourceId).ToList());

        // 8. Graph
        stage = ScanProgress.Stages[7];
        progress.Report(new ScanProgress(stage, 0, 1, null));
        var graph = graphBuilder.Build(snapshot, new GraphBuildOptions(opts.MaxBlobContainersPerAccount));
        ct.ThrowIfCancellationRequested();

        var session = store.Get(user) ?? new InventorySession();
        session.Snapshot = snapshot;
        session.Graph = graph;
        session.Index = new GraphIndex(graph);
        store.Set(user, session);
        progress.Report(new ScanProgress(stage, 1, 1, null));

        logger.LogInformation("Scan completed: {ResourceCount} resources, {AppCount} apps analyzed, {IssueCount} issues, {NodeCount} nodes, {EdgeCount} edges",
            snapshot.Resources.Count, snapshot.AppConfigs.Count, snapshot.Issues.Count, graph.Nodes.Count, graph.Edges.Count);
        return snapshot;
    }

    /// <summary>Re-runs the graph builder from the cached snapshot without rescanning.</summary>
    public async Task<GraphDocument?> RebuildGraphAsync()
    {
        var user = await userContext.GetUserAsync();
        var session = store.Get(user);
        if (session?.Snapshot is null) return null;
        var graph = graphBuilder.Build(session.Snapshot, new GraphBuildOptions(options.Value.MaxBlobContainersPerAccount));
        session.Graph = graph;
        session.Index = new GraphIndex(graph);
        store.Set(user, session);
        return graph;
    }
}
