using System.Net;
using BlazorAzureInventory.Models.Configuration;
using BlazorAzureInventory.Models.Inventory;

namespace BlazorAzureInventory.Services.Analysis;

/// <summary>Lookup indexes over discovered resources. Every key is lower-cased with trailing dots and ports trimmed.</summary>
public sealed class ResourceIndex
{
    internal Dictionary<string, AzureResourceInfo> SqlServers { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, AzureResourceInfo> SqlDatabases { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, AzureResourceInfo> StorageAccounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, AzureResourceInfo> BlobContainers { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, AzureResourceInfo> StorageQueues { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, AzureResourceInfo> Cosmos { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, AzureResourceInfo> Redis { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, AzureResourceInfo> ServiceBus { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, AzureResourceInfo> KeyVaults { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, AzureResourceInfo> Registries { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, AzureResourceInfo> ById { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ResourceMatcher(CloudEndpointSuffixes suffixes)
{
    public const string EmulatorNodeId = "external:emulator";

    public ResourceIndex BuildIndex(IEnumerable<AzureResourceInfo> resources)
    {
        var index = new ResourceIndex();
        var list = resources.ToList();
        foreach (var r in list) index.ById[r.Id] = r;

        foreach (var r in list)
        {
            switch (r.Kind)
            {
                case AzureResourceKind.SqlServer:
                    index.SqlServers[r.Name] = r;
                    if (HostOf(r.Endpoint) is { } fqdn) index.SqlServers[fqdn] = r;
                    break;
                case AzureResourceKind.SqlDatabase when r.ParentId is not null && index.ById.TryGetValue(r.ParentId, out var server):
                    index.SqlDatabases[$"{server.Name}/{r.Name}"] = r;
                    if (HostOf(server.Endpoint) is { } serverFqdn) index.SqlDatabases[$"{serverFqdn}/{r.Name}"] = r;
                    break;
                case AzureResourceKind.StorageAccount:
                    index.StorageAccounts[r.Name] = r;
                    break;
                case AzureResourceKind.BlobContainer when r.ParentId is not null && index.ById.TryGetValue(r.ParentId, out var account):
                    index.BlobContainers[$"{account.Name}/{r.Name}"] = r;
                    break;
                case AzureResourceKind.StorageQueue when r.ParentId is not null && index.ById.TryGetValue(r.ParentId, out var queueAccount):
                    index.StorageQueues[$"{queueAccount.Name}/{r.Name}"] = r;
                    break;
                case AzureResourceKind.CosmosAccount:
                    AddHost(index.Cosmos, r);
                    break;
                case AzureResourceKind.RedisCache:
                    AddHost(index.Redis, r);
                    break;
                case AzureResourceKind.ServiceBusNamespace:
                    AddHost(index.ServiceBus, r);
                    break;
                case AzureResourceKind.KeyVault:
                    index.KeyVaults[r.Name] = r;
                    if (HostOf(r.Endpoint) is { } vaultHost) index.KeyVaults[vaultHost.Split('.')[0]] = r;
                    break;
                case AzureResourceKind.ContainerRegistry:
                    AddHost(index.Registries, r);
                    break;
            }
        }
        return index;
    }

    /// <summary>Returns the reference with <see cref="ConnectionReference.ResolvedResourceId"/> filled in when a match exists.</summary>
    public ConnectionReference Match(ConnectionReference reference, ResourceIndex index)
    {
        if (reference.IsLocalEmulator) return reference;

        var host = Normalize(reference.Host);
        var name = Normalize(reference.AccountOrServerName);
        AzureResourceInfo? match = null;

        switch (reference.Kind)
        {
            case ConnectionTargetKind.SqlDatabase:
                if (reference.DatabaseName is { } db)
                {
                    match = Lookup(index.SqlDatabases, $"{host}/{db}") ?? Lookup(index.SqlDatabases, $"{name}/{db}");
                }
                match ??= Lookup(index.SqlServers, host) ?? Lookup(index.SqlServers, name);
                break;
            case ConnectionTargetKind.SqlServer:
                match = Lookup(index.SqlServers, host) ?? Lookup(index.SqlServers, name);
                break;
            case ConnectionTargetKind.StorageAccount:
                match = Lookup(index.StorageAccounts, name);
                break;
            case ConnectionTargetKind.BlobContainer:
                if (reference.ContainerName is { } container)
                {
                    match = Lookup(index.BlobContainers, $"{name}/{container}");
                }
                match ??= Lookup(index.StorageAccounts, name);
                break;
            case ConnectionTargetKind.StorageQueue:
                if (reference.ContainerName is { } queue)
                {
                    match = Lookup(index.StorageQueues, $"{name}/{queue}");
                }
                match ??= Lookup(index.StorageAccounts, name);
                break;
            case ConnectionTargetKind.CosmosAccount:
                match = Lookup(index.Cosmos, host);
                break;
            case ConnectionTargetKind.RedisCache:
                match = Lookup(index.Redis, host);
                break;
            case ConnectionTargetKind.ServiceBusNamespace:
                match = Lookup(index.ServiceBus, host);
                break;
            case ConnectionTargetKind.KeyVault:
                match = Lookup(index.KeyVaults, name);
                break;
            case ConnectionTargetKind.ContainerRegistry:
                match = Lookup(index.Registries, host);
                break;
        }

        return match is null ? reference : reference with { ResolvedResourceId = match.Id };
    }

    public ImageReference Match(ImageReference image, ResourceIndex index) =>
        Lookup(index.Registries, Normalize(image.RegistryHost)) is { } registry
            ? image with { ResolvedResourceId = registry.Id }
            : image;

    /// <summary>Resolves every connection and image reference of one app.</summary>
    public AppConfigAnalysis Match(AppConfigAnalysis analysis, ResourceIndex index)
    {
        var accounts = analysis.Entries
            .Where(e => !e.IsOverridden)
            .SelectMany(e => e.References)
            .Where(r => r.Kind is ConnectionTargetKind.StorageAccount or ConnectionTargetKind.BlobContainer or ConnectionTargetKind.StorageQueue
                        && r.Confidence != Confidence.Low && r.AccountOrServerName is not null)
            .Select(r => r.AccountOrServerName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return analysis with
        {
            Entries = analysis.Entries
                .Select(e => e with { References = e.References.Select(r => IsHint(r) ? ConfirmHint(r, accounts, index) : Match(r, index)).ToList() })
                .ToList(),
            Images = analysis.Images.Select(i => Match(i, index)).ToList()
        };
    }

    /// <summary>True when the index knows the container or queue <paramref name="name"/> in <paramref name="account"/>.</summary>
    public static bool HasChild(ResourceIndex index, ConnectionTargetKind kind, string account, string name) =>
        (kind == ConnectionTargetKind.StorageQueue ? index.StorageQueues : index.BlobContainers).ContainsKey($"{account}/{name}");

    public static bool IsHint(ConnectionReference r) =>
        r.Kind is ConnectionTargetKind.BlobContainer or ConnectionTargetKind.StorageQueue
        && r.Confidence == Confidence.Low && r.ContainerName is not null;

    /// <summary>
    /// A name hint that matches a discovered container or queue (in the assigned account or another storage account of the same app)
    /// is promoted to Medium. Otherwise it stays Low and points at the assigned storage account.
    /// </summary>
    private ConnectionReference ConfirmHint(ConnectionReference hint, IReadOnlyList<string> appAccounts, ResourceIndex index)
    {
        var map = hint.Kind == ConnectionTargetKind.StorageQueue ? index.StorageQueues : index.BlobContainers;
        var assigned = Normalize(hint.AccountOrServerName);
        var ordered = (assigned is null ? appAccounts : appAccounts.Prepend(assigned))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var account in ordered)
        {
            if (Lookup(map, $"{Normalize(account)}/{hint.ContainerName}") is { } child)
            {
                return hint with { AccountOrServerName = account, ResolvedResourceId = child.Id, Confidence = Confidence.Medium };
            }
        }
        return Match(hint, index);
    }

    /// <summary>Stable ID for an unmatched reference, e.g. <c>external:sql:10.0.0.5/hr</c>.</summary>
    public static string ExternalNodeId(ConnectionReference r)
    {
        if (r.IsLocalEmulator) return EmulatorNodeId;
        var slug = r.Kind switch
        {
            ConnectionTargetKind.SqlServer or ConnectionTargetKind.SqlDatabase => "sql",
            ConnectionTargetKind.StorageAccount or ConnectionTargetKind.BlobContainer or ConnectionTargetKind.StorageQueue => "storage",
            ConnectionTargetKind.CosmosAccount => "cosmos",
            ConnectionTargetKind.RedisCache => "redis",
            ConnectionTargetKind.ServiceBusNamespace => "servicebus",
            ConnectionTargetKind.KeyVault => "keyvault",
            ConnectionTargetKind.ContainerRegistry => "registry",
            ConnectionTargetKind.ExternalService => "service",
            _ => "other"
        };
        var main = Normalize(r.Kind is ConnectionTargetKind.StorageAccount or ConnectionTargetKind.BlobContainer or ConnectionTargetKind.StorageQueue
                or ConnectionTargetKind.KeyVault or ConnectionTargetKind.ExternalService
            ? r.AccountOrServerName ?? r.Host
            : r.Host ?? r.AccountOrServerName) ?? "unknown";
        if (r.Kind == ConnectionTargetKind.ExternalService) return $"external:service:{main}";
        var child = r.DatabaseName ?? r.ContainerName;
        return string.IsNullOrEmpty(child) ? $"external:{slug}:{main}" : $"external:{slug}:{main}/{child.ToLowerInvariant()}";
    }

    public static string ExternalLabel(ConnectionReference r)
    {
        if (r.IsLocalEmulator) return "Local emulator";
        var id = ExternalNodeId(r);
        return id[(id.IndexOf(':', "external:".Length) + 1)..];
    }

    /// <summary>NotVisible for Azure hosts that are not in the inventory, NotAzure for IPs and other hosts.</summary>
    public string ExternalReason(ConnectionReference r)
    {
        if (r.IsLocalEmulator) return "NotAzure";
        var host = Normalize(r.Host);
        if (host is null) return r.AccountOrServerName is null ? "Unknown" : "NotVisible";
        if (IPAddress.TryParse(host, out _) || host == "localhost") return "NotAzure";
        return CloudEndpointSuffixes.EndsWithAny(host, suffixes.AllAzure) ? "NotVisible" : "NotAzure";
    }

    private static AzureResourceInfo? Lookup(Dictionary<string, AzureResourceInfo> map, string? key) =>
        key is not null && map.TryGetValue(key, out var r) ? r : null;

    private static void AddHost(Dictionary<string, AzureResourceInfo> map, AzureResourceInfo r)
    {
        if (HostOf(r.Endpoint) is { } host) map[host] = r;
    }

    internal static string? HostOf(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return null;
        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? Normalize(uri.Host)
            : Normalize(endpoint);
    }

    internal static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim().ToLowerInvariant();
        var slash = v.IndexOf('/');
        if (slash >= 0) v = v[..slash];
        var colon = v.LastIndexOf(':');
        if (colon > 0 && v.IndexOf(':') == colon) v = v[..colon];
        return v.TrimEnd('.');
    }
}
