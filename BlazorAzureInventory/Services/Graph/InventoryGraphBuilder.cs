using System.Security.Cryptography;
using System.Text;
using BlazorAzureInventory.Models.Configuration;
using BlazorAzureInventory.Models.Graph;
using BlazorAzureInventory.Models.Inventory;
using BlazorAzureInventory.Models.Options;
using BlazorAzureInventory.Services.Analysis;

namespace BlazorAzureInventory.Services.Graph;

public sealed record GraphBuildOptions(int MaxBlobContainersPerAccount = 200, int MaxQueuesPerAccount = 200)
{
    public static GraphBuildOptions From(InventoryOptions o) => new(o.MaxBlobContainersPerAccount, o.MaxQueuesPerAccount);
}

/// <summary>Builds a directed, labeled property graph from an inventory snapshot. Deterministic for the same input.</summary>
public sealed class InventoryGraphBuilder(
    ResourceMatcher matcher, ILogger<InventoryGraphBuilder> logger, ExternalServiceCatalog? catalog = null)
{
    private readonly ExternalServiceCatalog _catalog = catalog ?? ExternalServiceCatalog.Default;
    public GraphDocument Build(InventorySnapshot snapshot, GraphBuildOptions options)
    {
        var nodes = new Dictionary<string, GraphNode>(StringComparer.OrdinalIgnoreCase);
        var edges = new Dictionary<string, GraphEdge>(StringComparer.Ordinal);

        AddScopeNodes(snapshot, nodes, edges);

        var analysesByApp = snapshot.AppConfigs.ToDictionary(a => a.AppId, StringComparer.OrdinalIgnoreCase);
        var referencedIds = snapshot.AppConfigs
            .SelectMany(a => a.Entries.Where(e => !e.IsOverridden).SelectMany(e => e.References))
            .Select(r => r.ResolvedResourceId)
            .Where(id => id is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var included = ApplyChildCap(snapshot.Resources, referencedIds, nodes, edges,
            (AzureResourceKind.BlobContainer, EdgeTypes.HasContainer, "blobservices/default/containers", "containers", options.MaxBlobContainersPerAccount),
            (AzureResourceKind.StorageQueue, EdgeTypes.HasQueue, "queueservices/default/queues", "queues", options.MaxQueuesPerAccount));

        foreach (var r in included)
        {
            nodes[r.Id] = CreateResourceNode(r, analysesByApp.GetValueOrDefault(r.Id));
            switch (r.Kind)
            {
                case AzureResourceKind.WebAppSlot when r.ParentId is not null:
                    AddEdge(edges, r.ParentId, EdgeTypes.HasSlot, r.Id);
                    break;
                case AzureResourceKind.SqlDatabase when r.ParentId is not null:
                    AddEdge(edges, r.ParentId, EdgeTypes.HostsDatabase, r.Id);
                    break;
                case AzureResourceKind.BlobContainer when r.ParentId is not null:
                    AddEdge(edges, r.ParentId, EdgeTypes.HasContainer, r.Id);
                    break;
                case AzureResourceKind.StorageQueue when r.ParentId is not null:
                    AddEdge(edges, r.ParentId, EdgeTypes.HasQueue, r.Id);
                    break;
                default:
                    AddEdge(edges, ResourceGroupId(r.SubscriptionId, r.ResourceGroup), EdgeTypes.Contains, r.Id);
                    break;
            }
        }

        foreach (var analysis in snapshot.AppConfigs.OrderBy(a => a.AppId, StringComparer.Ordinal))
        {
            if (!nodes.ContainsKey(analysis.AppId)) continue;
            AddConfigEdges(analysis, nodes, edges);
            AddImageEdges(analysis, nodes, edges);
        }

        // Integrity pass: any dangling edge is a bug, so log it with IDs only.
        foreach (var edge in edges.Values.ToList())
        {
            if (!nodes.ContainsKey(edge.Source) || !nodes.ContainsKey(edge.Target))
            {
                logger.LogWarning("Removing dangling edge {EdgeId} ({Source} -> {Target})", edge.Id, edge.Source, edge.Target);
                edges.Remove(edge.Id);
            }
        }

        return new GraphDocument
        {
            GeneratedUtc = snapshot.ScannedUtc.UtcDateTime,
            ScopeDescription = snapshot.ScopeDescription,
            Nodes = nodes.Values.OrderBy(n => n.Id, StringComparer.Ordinal).ToList(),
            Edges = edges.Values.OrderBy(e => e.Id, StringComparer.Ordinal).ToList()
        };
    }

    public static string ResourceGroupId(string subscriptionId, string resourceGroup) =>
        $"/subscriptions/{subscriptionId}/resourcegroups/{resourceGroup}".ToLowerInvariant();

    public static string EdgeId(string source, string type, string target)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{source.ToLowerInvariant()}|{type}|{target.ToLowerInvariant()}"));
        return "e:" + Convert.ToHexStringLower(hash)[..16];
    }

    private static void AddScopeNodes(InventorySnapshot snapshot, Dictionary<string, GraphNode> nodes, Dictionary<string, GraphEdge> edges)
    {
        foreach (var t in snapshot.Tenants)
        {
            var id = $"tenant:{t.TenantId}".ToLowerInvariant();
            nodes[id] = new GraphNode
            {
                Id = id, Type = nameof(AzureResourceKind.Tenant), Label = t.DisplayName,
                Data = new() { ["tenantId"] = t.TenantId, ["defaultDomain"] = t.DefaultDomain }
            };
        }

        foreach (var s in snapshot.Subscriptions)
        {
            var id = $"/subscriptions/{s.SubscriptionId}".ToLowerInvariant();
            nodes[id] = new GraphNode
            {
                Id = id, Type = nameof(AzureResourceKind.Subscription), Label = s.DisplayName,
                Data = new()
                {
                    ["subscriptionId"] = s.SubscriptionId, ["state"] = s.State, ["tenantId"] = s.TenantId,
                    [TagKeys.Tags] = CopyTags(s.Tags), [TagKeys.Taggable] = true
                }
            };
            AddEdge(edges, $"tenant:{s.TenantId}", EdgeTypes.Contains, id);
        }

        var groups = snapshot.ResourceGroups
            .Select(g => (Id: g.Id.ToLowerInvariant(), g.Name, g.SubscriptionId, g.Location, g.TenantId, g.Tags))
            .Concat(snapshot.Resources.Select(r =>
                (Id: ResourceGroupId(r.SubscriptionId, r.ResourceGroup), Name: r.ResourceGroup, r.SubscriptionId, Location: "", r.TenantId,
                 Tags: AzureResourceInfo.EmptyTags)))
            .GroupBy(g => g.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.Location.Length).First());

        foreach (var g in groups)
        {
            nodes[g.Id] = new GraphNode
            {
                Id = g.Id, Type = nameof(AzureResourceKind.ResourceGroup), Label = g.Name,
                Data = new()
                {
                    ["location"] = g.Location, ["subscriptionId"] = g.SubscriptionId, ["resourceGroup"] = g.Name, ["tenantId"] = g.TenantId,
                    [TagKeys.Tags] = CopyTags(g.Tags), [TagKeys.Taggable] = true
                }
            };
            AddEdge(edges, $"/subscriptions/{g.SubscriptionId}".ToLowerInvariant(), EdgeTypes.Contains, g.Id);
        }
    }

    public static string? ConfigAccessHint(ConfigAccessReason reason) => reason switch
    {
        ConfigAccessReason.MissingRole => $"Assign the {AzureRoleRequirements.WebsiteContributor} role on the app (or its resource group) to read config files.",
        ConfigAccessReason.ScmAccessRestricted => "The SCM site's access restrictions block the scanner. Allow its outbound IP or VNet, or keep the ARM fallback enabled.",
        ConfigAccessReason.ScmUnreachable => "The SCM site is not reachable from the scanner (private endpoint or DNS). Link the scanner VNet to the private DNS zone.",
        ConfigAccessReason.AuthRejected => "The SCM site rejected the token although the role is present, for example because of Conditional Access.",
        ConfigAccessReason.Unknown => "Config files could not be read and the cause could not be determined.",
        _ => null
    };

    private static Dictionary<string, string> CopyTags(IReadOnlyDictionary<string, string> tags) =>
        new(tags, StringComparer.OrdinalIgnoreCase);

    private static List<AzureResourceInfo> ApplyChildCap(
        IReadOnlyList<AzureResourceInfo> resources, HashSet<string?> referencedIds,
        Dictionary<string, GraphNode> nodes, Dictionary<string, GraphEdge> edges,
        params (AzureResourceKind Kind, string EdgeType, string Path, string Noun, int Cap)[] children)
    {
        var childKinds = children.Select(c => c.Kind).ToHashSet();
        var result = resources.Where(r => !childKinds.Contains(r.Kind)).ToList();

        foreach (var child in children)
        {
            foreach (var group in resources.Where(r => r.Kind == child.Kind && r.ParentId is not null)
                         .GroupBy(r => r.ParentId!, StringComparer.OrdinalIgnoreCase))
            {
                var ordered = group
                    .OrderByDescending(c => referencedIds.Contains(c.Id))
                    .ThenBy(c => c.Name, StringComparer.Ordinal)
                    .ToList();
                var keep = ordered.Where((c, i) => i < child.Cap || referencedIds.Contains(c.Id)).ToList();
                result.AddRange(keep);

                var remaining = ordered.Count - keep.Count;
                if (remaining > 0)
                {
                    var first = group.First();
                    var id = $"{group.Key}/{child.Path}/__more".ToLowerInvariant();
                    nodes[id] = new GraphNode
                    {
                        Id = id, Type = child.Kind.ToString(), Label = $"+{remaining} more {child.Noun}",
                        Data = new() { ["isAggregate"] = true, ["subscriptionId"] = first.SubscriptionId, ["resourceGroup"] = first.ResourceGroup }
                    };
                    AddEdge(edges, group.Key, child.EdgeType, id);
                }
            }
        }

        return result;
    }

    private static GraphNode CreateResourceNode(AzureResourceInfo r, AppConfigAnalysis? analysis)
    {
        var data = new Dictionary<string, object?>
        {
            ["armType"] = r.ArmType,
            ["tenantId"] = r.TenantId,
            ["subscriptionId"] = r.SubscriptionId,
            ["resourceGroup"] = r.ResourceGroup,
            ["location"] = r.Location
        };
        foreach (var (k, v) in r.Properties) data[k] = v;
        data[TagKeys.Tags] = CopyTags(r.Tags);
        data[TagKeys.Taggable] = r.Kind is not (AzureResourceKind.BlobContainer or AzureResourceKind.StorageQueue);

        var host = ResourceMatcher.HostOf(r.Endpoint);
        switch (r.Kind)
        {
            case AzureResourceKind.WebApp or AzureResourceKind.FunctionApp or AzureResourceKind.WebAppSlot:
                data["defaultHost"] = host;
                if (analysis is not null)
                {
                    data["environment"] = analysis.Environment;
                    data["settingsStatus"] = analysis.SettingsStatus.ToString();
                    data["configFilesStatus"] = analysis.ConfigFilesStatus.ToString();
                    if (analysis.ConfigFilesReason != ConfigAccessReason.None) data["configFilesReason"] = analysis.ConfigFilesReason.ToString();
                    if (analysis.ConfigFilesStatus != ConfigReadStatus.Read && ConfigAccessHint(analysis.ConfigFilesReason) is { } hint)
                        data["configFilesHint"] = hint;
                    if (analysis.ConfigFilesVia != ConfigFilesRoute.None) data["configFilesVia"] = analysis.ConfigFilesVia.ToString();
                    if (analysis.ConfigFilesRead.Count > 0) data["configFilesRead"] = string.Join(", ", analysis.ConfigFilesRead);
                    if (analysis.ConfigFilesSkipped.Count > 0) data["configFilesSkipped"] = string.Join(", ", analysis.ConfigFilesSkipped);
                }
                break;
            case AzureResourceKind.ContainerApp:
                data["fqdn"] = host;
                break;
            case AzureResourceKind.SqlServer:
                data["fqdn"] = host;
                break;
            case AzureResourceKind.StorageAccount:
                data["blobEndpoint"] = r.Endpoint;
                break;
            case AzureResourceKind.ContainerRegistry:
                data["loginServer"] = host;
                break;
            case AzureResourceKind.CosmosAccount or AzureResourceKind.RedisCache
                or AzureResourceKind.ServiceBusNamespace or AzureResourceKind.KeyVault:
                data["endpointHost"] = host;
                break;
        }

        var label = r.Kind == AzureResourceKind.WebAppSlot && r.Prop("siteName") is { } site ? $"{site}/{r.Name}" : r.Name;
        return new GraphNode { Id = r.Id, Type = r.Kind.ToString(), Label = label, Data = data };
    }

    private void AddConfigEdges(AppConfigAnalysis analysis, Dictionary<string, GraphNode> nodes, Dictionary<string, GraphEdge> edges)
    {
        foreach (var entry in analysis.Entries.Where(e => !e.IsOverridden))
        {
            foreach (var reference in entry.References)
            {
                var targetId = reference.ResolvedResourceId
                    ?? (reference.Kind == ConnectionTargetKind.ExternalService
                        ? EnsureExternalServiceNode(reference, nodes)
                        : EnsureExternalNode(reference, nodes));
                var isSecret = reference.Kind == ConnectionTargetKind.KeyVault;
                var edge = AddEdge(edges, analysis.AppId, isSecret ? EdgeTypes.ReferencesSecret : EdgeTypes.ConnectsTo, targetId);

                if (!edge.Data.TryGetValue("references", out var existing) || existing is not List<EdgeReference> refs)
                {
                    refs = new List<EdgeReference>();
                    edge.Data["references"] = refs;
                }

                var unresolvedDb = reference.Kind == ConnectionTargetKind.SqlDatabase
                    && reference.ResolvedResourceId is not null
                    && nodes.TryGetValue(reference.ResolvedResourceId, out var resolvedNode)
                    && resolvedNode.Type == nameof(AzureResourceKind.SqlServer)
                        ? reference.DatabaseName : null;

                refs.Add(isSecret
                    ? new EdgeReference(entry.Key, SecretName: reference.SecretName, Confidence: reference.Confidence.ToString())
                    : new EdgeReference(entry.Key, entry.Source.ToString(), entry.FileName, entry.Environment,
                        reference.Confidence.ToString(), ConnectionClassifier.IsIdentityBasedKey(entry.Key), unresolvedDb));

                var max = refs.Max(x => Enum.TryParse<Confidence>(x.Confidence, out var c) ? c : Confidence.High);
                edge.Data["confidence"] = max.ToString();
            }
        }
    }

    private static void AddImageEdges(AppConfigAnalysis analysis, Dictionary<string, GraphNode> nodes, Dictionary<string, GraphEdge> edges)
    {
        foreach (var group in analysis.Images.GroupBy(i => i.ResolvedResourceId ?? $"external:registry:{i.RegistryHost}"))
        {
            var first = group.First();
            if (first.ResolvedResourceId is null)
            {
                // Only private Azure registries the user cannot see become external nodes; public registries are skipped.
                if (!first.RegistryHost.Contains(".azurecr.", StringComparison.OrdinalIgnoreCase)) continue;
                nodes.TryAdd(group.Key, new GraphNode
                {
                    Id = group.Key, Type = nameof(AzureResourceKind.External), Label = first.RegistryHost,
                    Data = new() { ["kind"] = nameof(ConnectionTargetKind.ContainerRegistry), ["isLocalEmulator"] = false, ["reason"] = "NotVisible" }
                });
            }

            var edge = AddEdge(edges, analysis.AppId, EdgeTypes.PullsImageFrom, group.Key);
            edge.Data["repositories"] = group.Select(i => i.Repository).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r).ToList();
        }
    }

    /// <summary>One shared node per external service, so every app that uses it connects to the same hub.</summary>
    private string EnsureExternalServiceNode(ConnectionReference reference, Dictionary<string, GraphNode> nodes)
    {
        var id = ResourceMatcher.ExternalNodeId(reference);
        if (!nodes.TryGetValue(id, out var node))
        {
            var def = _catalog.ById(reference.AccountOrServerName);
            node = new GraphNode
            {
                Id = id,
                Type = nameof(AzureResourceKind.ExternalService),
                Label = def?.DisplayName ?? reference.AccountOrServerName ?? "External service",
                Data = new()
                {
                    ["serviceId"] = reference.AccountOrServerName,
                    ["category"] = def?.Category ?? "",
                    ["color"] = def?.Color,
                    ["matchedHosts"] = new List<string>(),
                    [TagKeys.Taggable] = false
                }
            };
            nodes[id] = node;
        }

        if (reference.Host is { } host && node.Data["matchedHosts"] is List<string> hosts && !hosts.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            hosts.Add(host);
            hosts.Sort(StringComparer.Ordinal);
        }
        return id;
    }

    private string EnsureExternalNode(ConnectionReference reference, Dictionary<string, GraphNode> nodes)
    {
        var id = ResourceMatcher.ExternalNodeId(reference);
        nodes.TryAdd(id, new GraphNode
        {
            Id = id,
            Type = nameof(AzureResourceKind.External),
            Label = ResourceMatcher.ExternalLabel(reference),
            Data = new()
            {
                ["kind"] = reference.Kind.ToString(),
                ["isLocalEmulator"] = reference.IsLocalEmulator,
                ["reason"] = matcher.ExternalReason(reference)
            }
        });
        return id;
    }

    private static GraphEdge AddEdge(Dictionary<string, GraphEdge> edges, string source, string type, string target)
    {
        var id = EdgeId(source, type, target);
        if (!edges.TryGetValue(id, out var edge))
        {
            edge = new GraphEdge { Id = id, Source = source.ToLowerInvariant(), Target = target.ToLowerInvariant(), Type = type };
            edges[id] = edge;
        }
        return edge;
    }
}
