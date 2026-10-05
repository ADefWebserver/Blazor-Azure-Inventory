using System.Text.Json;
using Azure.ResourceManager.ResourceGraph;
using Azure.ResourceManager.ResourceGraph.Models;
using Azure.ResourceManager.Resources;
using BlazorAzureInventory.Models.Inventory;
using BlazorAzureInventory.Models.Options;
using Microsoft.Extensions.Options;

namespace BlazorAzureInventory.Services.Azure;

/// <summary>Discovers resources with Azure Resource Graph. Query text is constant; subscriptions are passed as parameters.</summary>
public sealed class ResourceGraphInventoryService(ArmClientFactory armFactory, IOptions<InventoryOptions> options)
{
    private const string ResourcesQuery = """
        resources
        | where type in~ (
            'microsoft.web/sites', 'microsoft.web/sites/slots',
            'microsoft.sql/servers', 'microsoft.sql/servers/databases',
            'microsoft.storage/storageaccounts',
            'microsoft.app/containerapps', 'microsoft.containerregistry/registries',
            'microsoft.documentdb/databaseaccounts',
            'microsoft.cache/redis', 'microsoft.cache/redisenterprise',
            'microsoft.servicebus/namespaces', 'microsoft.keyvault/vaults')
        | where not(type =~ 'microsoft.sql/servers/databases' and name =~ 'master')
        | extend endpoint = case(
            type =~ 'microsoft.sql/servers', tostring(properties.fullyQualifiedDomainName),
            type =~ 'microsoft.storage/storageaccounts', tostring(properties.primaryEndpoints.blob),
            type =~ 'microsoft.documentdb/databaseaccounts', tostring(properties.documentEndpoint),
            type in~ ('microsoft.cache/redis', 'microsoft.cache/redisenterprise'), tostring(properties.hostName),
            type =~ 'microsoft.servicebus/namespaces', tostring(properties.serviceBusEndpoint),
            type =~ 'microsoft.keyvault/vaults', tostring(properties.vaultUri),
            type =~ 'microsoft.containerregistry/registries', tostring(properties.loginServer),
            type in~ ('microsoft.web/sites', 'microsoft.web/sites/slots'), tostring(properties.defaultHostName),
            type =~ 'microsoft.app/containerapps', tostring(properties.configuration.ingress.fqdn),
            '')
        | project id, name, type, kind, location, resourceGroup, subscriptionId, tenantId,
                  endpoint, state = tostring(properties.state), sku = tostring(sku.name),
                  status = tostring(properties.status), hns = tostring(properties.isHnsEnabled),
                  environmentId = tostring(coalesce(properties.managedEnvironmentId, properties.environmentId)),
                  tags
        """;

    private const string ContainersQuery = """
        resourcecontainers
        | where type in~ ('microsoft.resources/subscriptions', 'microsoft.resources/subscriptions/resourcegroups')
        | project id, name, type, subscriptionId, tenantId, location, tags
        """;

    public async Task<(IReadOnlyList<AzureResourceInfo> Resources, IReadOnlyList<ResourceGroupInfo> Groups)> QueryAsync(
        string tenantId, IReadOnlyList<string> subscriptionIds, CancellationToken ct)
    {
        var (resources, groups, _) = await QueryWithSubscriptionTagsAsync(tenantId, subscriptionIds, ct);
        return (resources, groups);
    }

    /// <summary>Like <see cref="QueryAsync"/>, plus the tags of each subscription keyed by subscription ID.</summary>
    public async Task<(IReadOnlyList<AzureResourceInfo> Resources, IReadOnlyList<ResourceGroupInfo> Groups,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> SubscriptionTags)> QueryWithSubscriptionTagsAsync(
        string tenantId, IReadOnlyList<string> subscriptionIds, CancellationToken ct)
    {
        var subscriptionTags = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var validIds = subscriptionIds.Where(id => Guid.TryParse(id, out _)).ToList();
        if (validIds.Count == 0) return ([], [], subscriptionTags);

        var client = await armFactory.GetAsync(tenantId);
        TenantResource? tenant = null;
        await foreach (var t in client.GetTenants().GetAllAsync(ct))
        {
            tenant = t;
            break;
        }
        if (tenant is null) return ([], [], subscriptionTags);

        var resources = new List<AzureResourceInfo>();
        await foreach (var row in QueryRowsAsync(tenant, ResourcesQuery, validIds, ct))
        {
            if (Map(row, tenantId) is { } info) resources.Add(info);
        }

        var groups = new List<ResourceGroupInfo>();
        await foreach (var row in QueryRowsAsync(tenant, ContainersQuery, validIds, ct))
        {
            var type = Str(row, "type");
            if (type.Equals("microsoft.resources/subscriptions/resourcegroups", StringComparison.OrdinalIgnoreCase))
            {
                groups.Add(new ResourceGroupInfo(Str(row, "id").ToLowerInvariant(), Str(row, "name"),
                    Str(row, "subscriptionId"), Str(row, "tenantId") is { Length: > 0 } t ? t : tenantId, Str(row, "location"))
                {
                    Tags = ReadTags(row)
                });
            }
            else if (type.Equals("microsoft.resources/subscriptions", StringComparison.OrdinalIgnoreCase))
            {
                subscriptionTags[Str(row, "subscriptionId")] = ReadTags(row);
            }
        }

        return (resources, groups, subscriptionTags);
    }

    /// <summary>Reads the <c>tags</c> column. Non-string values are converted to text; case-only duplicate names keep the first.</summary>
    public static IReadOnlyDictionary<string, string> ReadTags(JsonElement row)
    {
        if (!row.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object) return AzureResourceInfo.EmptyTags;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in tags.EnumerateObject())
        {
            var value = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString() ?? "",
                JsonValueKind.Null or JsonValueKind.Undefined => "",
                _ => p.Value.GetRawText()
            };
            result.TryAdd(p.Name, value);
        }
        return result;
    }

    private static async IAsyncEnumerable<JsonElement> QueryRowsAsync(
        TenantResource tenant, string query, IReadOnlyList<string> subscriptionIds,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string? skipToken = null;
        do
        {
            var content = new ResourceQueryContent(query)
            {
                Options = new ResourceQueryRequestOptions { ResultFormat = ResultFormat.ObjectArray, Top = 1000, SkipToken = skipToken }
            };
            foreach (var id in subscriptionIds) content.Subscriptions.Add(id);

            var response = await tenant.GetResourcesAsync(content, ct);
            using var doc = JsonDocument.Parse(response.Value.Data);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in doc.RootElement.EnumerateArray())
                {
                    yield return row.Clone();
                }
            }
            skipToken = response.Value.SkipToken;
        }
        while (!string.IsNullOrEmpty(skipToken));
    }

    private AzureResourceInfo? Map(JsonElement row, string tenantId)
    {
        var id = Str(row, "id").ToLowerInvariant();
        var type = Str(row, "type").ToLowerInvariant();
        var armKind = Str(row, "kind");
        var o = options.Value;

        AzureResourceKind? kind = type switch
        {
            "microsoft.web/sites" when armKind.Contains("functionapp", StringComparison.OrdinalIgnoreCase)
                => o.IncludeFunctionApps ? AzureResourceKind.FunctionApp : null,
            "microsoft.web/sites" => AzureResourceKind.WebApp,
            "microsoft.web/sites/slots" => o.IncludeSlots ? AzureResourceKind.WebAppSlot : null,
            "microsoft.sql/servers" => AzureResourceKind.SqlServer,
            "microsoft.sql/servers/databases" => AzureResourceKind.SqlDatabase,
            "microsoft.storage/storageaccounts" => AzureResourceKind.StorageAccount,
            "microsoft.app/containerapps" => AzureResourceKind.ContainerApp,
            "microsoft.containerregistry/registries" => AzureResourceKind.ContainerRegistry,
            "microsoft.documentdb/databaseaccounts" => AzureResourceKind.CosmosAccount,
            "microsoft.cache/redis" or "microsoft.cache/redisenterprise" => AzureResourceKind.RedisCache,
            "microsoft.servicebus/namespaces" => AzureResourceKind.ServiceBusNamespace,
            "microsoft.keyvault/vaults" => AzureResourceKind.KeyVault,
            _ => null
        };
        if (kind is null) return null;

        var name = Str(row, "name").Split('/')[^1];
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Put(string key, string value) { if (!string.IsNullOrWhiteSpace(value)) props[key] = value; }

        Put("kind", armKind);
        Put("state", Str(row, "state"));
        Put("sku", Str(row, "sku"));
        Put("status", Str(row, "status"));
        if (kind == AzureResourceKind.StorageAccount) props["hnsEnabled"] = Str(row, "hns").Equals("true", StringComparison.OrdinalIgnoreCase) ? "Yes" : "No";
        Put("environmentId", Str(row, "environmentId").ToLowerInvariant());

        string? parentId = null;
        if (kind == AzureResourceKind.WebAppSlot)
        {
            parentId = id[..id.LastIndexOf("/slots/", StringComparison.Ordinal)];
            props["siteName"] = parentId.Split('/')[^1];
            props["slotName"] = name;
        }
        else if (kind == AzureResourceKind.SqlDatabase)
        {
            parentId = id[..id.LastIndexOf("/databases/", StringComparison.Ordinal)];
        }

        var endpoint = Str(row, "endpoint");
        return new AzureResourceInfo(
            id, name, kind.Value, type,
            Str(row, "tenantId") is { Length: > 0 } tid ? tid : tenantId,
            Str(row, "subscriptionId"), Str(row, "resourceGroup"), Str(row, "location"),
            parentId, string.IsNullOrWhiteSpace(endpoint) ? null : endpoint, props)
        {
            Tags = ReadTags(row)
        };
    }

    private static string Str(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
