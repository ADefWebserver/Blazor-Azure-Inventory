using System.Collections.Concurrent;
using BlazorAzureInventory.Models.Configuration;
using BlazorAzureInventory.Models.Graph;
using BlazorAzureInventory.Models.Inventory;
using BlazorAzureInventory.Models.Options;
using BlazorAzureInventory.Services.Analysis;
using BlazorAzureInventory.Services.Auth;
using BlazorAzureInventory.Services.Azure;
using BlazorAzureInventory.Services.Graph;
using BlazorAzureInventory.Services.Persistence;
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
    GraphLayoutService layouts,
    UserContextAccessor userContext,
    IUserDataStore dataStore,
    Quotas.UserQuotaService quotas,
    IOptions<InventoryOptions> options,
    ILogger<InventoryScanOrchestrator> logger)
{
    private static readonly TimeSpan SaveTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Why the last scan could not be saved, or null when it was saved (or saving is unavailable).</summary>
    public string? LastSaveError { get; private set; }

    public async Task<InventorySnapshot> ScanAsync(ScanRequest request, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        var opts = options.Value;
        var issues = new ConcurrentBag<ScanIssue>();
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = opts.MaxParallelism, CancellationToken = ct };
        var user = await userContext.GetUserAsync();
        using var slot = quotas.TryStartScan(user, out var denied) ?? throw new Quotas.QuotaExceededException(denied.Message!);
        logger.LogInformation("Scan started for {SubscriptionCount} subscriptions in {TenantCount} tenants",
            request.Subscriptions.Count, request.Tenants.Count);

        // 1. Resolve subscriptions per tenant
        var stage = ScanProgress.ResolvingSubscriptions;
        progress.Report(new ScanProgress(stage, 0, 1, null));
        var byTenant = request.Subscriptions
            .GroupBy(s => s.TenantId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        progress.Report(new ScanProgress(stage, 1, 1, null));

        // 2. Resource Graph per tenant
        stage = ScanProgress.QueryingResourceGraph;
        var resources = new List<AzureResourceInfo>();
        var groups = new List<ResourceGroupInfo>();
        var subscriptionTags = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var done = 0;
        foreach (var tenantGroup in byTenant)
        {
            var tenantName = request.Tenants.FirstOrDefault(t => t.TenantId == tenantGroup.Key)?.DisplayName ?? tenantGroup.Key;
            progress.Report(new ScanProgress(stage, done, byTenant.Count, tenantName));
            try
            {
                var (found, rgs, subTags) = await resourceGraph.QueryWithSubscriptionTagsAsync(
                    tenantGroup.Key, tenantGroup.Select(s => s.SubscriptionId).ToList(), ct);
                resources.AddRange(found);
                groups.AddRange(rgs);
                foreach (var (id, tags) in subTags) subscriptionTags[id] = tags;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var (kind, message) = ScanErrors.Classify(ex, AzureRoleRequirements.ResourceGraph.Hint);
                issues.Add(new ScanIssue($"tenant:{tenantGroup.Key}", stage, kind, message));
            }
            progress.Report(new ScanProgress(stage, ++done, byTenant.Count, tenantName));
        }

        // 3. Blob containers
        stage = ScanProgress.ListingBlobContainers;
        var accounts = resources.Where(r => r.Kind == AzureResourceKind.StorageAccount).ToList();
        var containers = new ConcurrentBag<AzureResourceInfo>();
        var accountExtras = new ConcurrentDictionary<string, ConcurrentDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        done = 0;
        await Parallel.ForEachAsync(accounts, parallel, async (account, token) =>
        {
            try
            {
                var (list, truncated) = await enrichment.ListBlobContainersAsync(account, opts.MaxBlobContainersPerAccount, token);
                foreach (var c in list) containers.Add(c);
                var extra = accountExtras.GetOrAdd(account.Id, _ => new(StringComparer.OrdinalIgnoreCase));
                extra["containerCount"] = list.Count.ToString();
                extra["containersTruncated"] = truncated ? "true" : "false";
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                var (kind, message) = ScanErrors.Classify(ex, AzureRoleRequirements.BlobContainers.Hint);
                issues.Add(new ScanIssue(account.Id, stage, kind, message));
            }
            progress.Report(new ScanProgress(stage, Interlocked.Increment(ref done), accounts.Count, account.Name));
        });
        resources.AddRange(containers);

        // 3b. Storage queues
        stage = ScanProgress.ListingStorageQueues;
        var queueAccounts = accounts.Where(ResourceEnrichmentService.SupportsQueues).ToList();
        var queues = new ConcurrentBag<AzureResourceInfo>();
        done = 0;
        await Parallel.ForEachAsync(queueAccounts, parallel, async (account, token) =>
        {
            try
            {
                var (list, truncated) = await enrichment.ListQueuesAsync(account, opts.MaxQueuesPerAccount, token);
                foreach (var q in list) queues.Add(q);
                var extra = accountExtras.GetOrAdd(account.Id, _ => new(StringComparer.OrdinalIgnoreCase));
                extra["queueCount"] = list.Count.ToString();
                extra["queuesTruncated"] = truncated ? "true" : "false";
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                var (kind, message) = ScanErrors.Classify(ex, AzureRoleRequirements.StorageQueues.Hint);
                issues.Add(new ScanIssue(account.Id, ScanProgress.ListingStorageQueues, kind, message));
            }
            progress.Report(new ScanProgress(ScanProgress.ListingStorageQueues, Interlocked.Increment(ref done), queueAccounts.Count, account.Name));
        });
        resources.AddRange(queues);

        // 4/5. App Service settings and config files (processed per app so raw values stay short-lived)
        var analyses = new ConcurrentBag<AppConfigAnalysis>();
        var appExtras = new ConcurrentDictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var webApps = resources.Where(r => r.Kind is AzureResourceKind.WebApp or AzureResourceKind.FunctionApp or AzureResourceKind.WebAppSlot).ToList();
        done = 0;
        await Parallel.ForEachAsync(webApps, parallel, async (app, token) =>
        {
            progress.Report(new ScanProgress(ScanProgress.ReadingAppSettings, done, webApps.Count, app.Name));
            var result = await configReader.ReadAppServiceAsync(app,
                s => progress.Report(new ScanProgress(s, done, webApps.Count, app.Name)), token);
            analyses.Add(result.Analysis);
            appExtras[app.Id] = result.ExtraProperties;
            foreach (var i in result.Issues) issues.Add(i);
            progress.Report(new ScanProgress(ScanProgress.ReadingAppSettings, Interlocked.Increment(ref done), webApps.Count, app.Name));
        });

        // 6. Container Apps
        stage = ScanProgress.ReadingContainerApps;
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
        stage = ScanProgress.AnalyzingConnections;
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

        var confirmed = await FindTruncatedHintTargetsAsync(matched, index, parallel, ct);
        if (confirmed.Count > 0)
        {
            resources.AddRange(confirmed.Where(c => !index.ById.ContainsKey(c.Id)));
            index = matcher.BuildIndex(resources);
            matched = analyses
                .OrderBy(a => a.AppId, StringComparer.Ordinal)
                .Select(a => matcher.Match(a, index))
                .ToList();
        }
        progress.Report(new ScanProgress(stage, 1, 1, null));

        var tenantIds = byTenant.Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var snapshot = new InventorySnapshot(
            DateTimeOffset.UtcNow,
            request.Tenants.Where(t => tenantIds.Contains(t.TenantId)).ToList(),
            request.Subscriptions
                .Select(s => subscriptionTags.TryGetValue(s.SubscriptionId, out var tags) ? s with { Tags = tags } : s)
                .ToList(),
            groups.DistinctBy(g => g.Id).ToList(),
            resources.OrderBy(r => r.Id, StringComparer.Ordinal).ToList(),
            matched,
            issues.OrderBy(i => i.Stage).ThenBy(i => i.ResourceId).ToList());

        // 8. Graph
        stage = ScanProgress.BuildingGraph;
        progress.Report(new ScanProgress(stage, 0, 1, null));
        var graph = graphBuilder.Build(snapshot, GraphBuildOptions.From(opts));
        ct.ThrowIfCancellationRequested();

        var session = store.Get(user) ?? new InventorySession();
        session.Snapshot = snapshot;
        session.Graph = graph;
        session.Index = new GraphIndex(graph);
        session.Source = SessionDataSource.Scan;
        session.HiddenSubscriptions = 0;
        store.Set(user, session);
        layouts.WarmUp(session);
        progress.Report(new ScanProgress(stage, 1, 1, null));

        logger.LogInformation("Scan completed: {ResourceCount} resources, {AppCount} apps analyzed, {IssueCount} issues, {NodeCount} nodes, {EdgeCount} edges",
            snapshot.Resources.Count, snapshot.AppConfigs.Count, snapshot.Issues.Count, graph.Nodes.Count, graph.Edges.Count);
        await SaveAsync(user, snapshot);
        return snapshot;
    }

    private async Task SaveAsync(System.Security.Claims.ClaimsPrincipal user, InventorySnapshot snapshot)
    {
        LastSaveError = null;
        if (!dataStore.IsPersistent || UserKey.FromPrincipal(user) is not { } key) return;
        using var cts = new CancellationTokenSource(SaveTimeout);
        try
        {
            await dataStore.SaveSnapshotAsync(key, snapshot, rotate: true, cts.Token);
        }
        catch (Exception ex)
        {
            // A failed save never fails the scan; the result stays in memory for this session.
            logger.LogWarning("Saving the scan for {UserKey} failed ({ExceptionType})", key, ex.GetType().Name);
            LastSaveError = ex is SnapshotTooLargeException ? ex.Message : "The scan could not be saved and is kept for this session only.";
        }
    }

    private const int MaxTargetedLookupsPerAccount = 50;

    /// <summary>
    /// Hints that fell back to a storage account whose container or queue listing was truncated may name a child past the cap.
    /// Those names are looked up directly so the hint can be confirmed.
    /// </summary>
    private async Task<IReadOnlyList<AzureResourceInfo>> FindTruncatedHintTargetsAsync(
        IReadOnlyList<AppConfigAnalysis> matched, ResourceIndex index, ParallelOptions parallel, CancellationToken ct)
    {
        var lookups = matched
            .SelectMany(a => a.Entries.Where(e => !e.IsOverridden).SelectMany(e => e.References))
            .Where(r => ResourceMatcher.IsHint(r) && r.ResolvedResourceId is not null)
            .Select(r => (Account: index.ById.GetValueOrDefault(r.ResolvedResourceId!), r.Kind, Name: r.ContainerName!))
            .Where(x => x.Account is { Kind: AzureResourceKind.StorageAccount } account
                        && account.Prop(x.Kind == ConnectionTargetKind.StorageQueue ? "queuesTruncated" : "containersTruncated") == "true")
            .Distinct()
            .GroupBy(x => x.Account!.Id, StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => g.Take(MaxTargetedLookupsPerAccount))
            .ToList();
        if (lookups.Count == 0) return [];

        var found = new ConcurrentBag<AzureResourceInfo>();
        await Parallel.ForEachAsync(lookups, parallel, async (x, token) =>
        {
            try
            {
                var kind = x.Kind == ConnectionTargetKind.StorageQueue ? AzureResourceKind.StorageQueue : AzureResourceKind.BlobContainer;
                if (await enrichment.FindChildAsync(x.Account!, kind, x.Name, token) is { } child) found.Add(child);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                logger.LogDebug("Targeted lookup in {AccountId} failed ({ExceptionType})", x.Account!.Id, ex.GetType().Name);
            }
        });
        return found.ToList();
    }

    /// <summary>Re-runs the graph builder from the cached snapshot without rescanning.</summary>
    public async Task<GraphDocument?> RebuildGraphAsync()
    {
        var user = await userContext.GetUserAsync();
        var session = store.Get(user);
        if (session?.Snapshot is null) return null;
        var graph = graphBuilder.Build(session.Snapshot, GraphBuildOptions.From(options.Value));
        session.Graph = graph;
        session.Index = new GraphIndex(graph);
        store.Set(user, session);
        layouts.WarmUp(session);
        return graph;
    }
}
