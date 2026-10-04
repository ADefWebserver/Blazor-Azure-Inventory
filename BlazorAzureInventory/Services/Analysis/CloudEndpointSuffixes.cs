using BlazorAzureInventory.Models.Options;
using Microsoft.Extensions.Options;

namespace BlazorAzureInventory.Services.Analysis;

/// <summary>Configurable cloud DNS suffixes so sovereign clouds work. Empty options fall back to public and sovereign defaults.</summary>
public sealed class CloudEndpointSuffixes
{
    public CloudEndpointSuffixes(IOptions<InventoryOptions> options) : this(options.Value.CloudEndpointSuffixes) { }

    public CloudEndpointSuffixes(CloudEndpointSuffixOptions o)
    {
        Sql = Pick(o.Sql, ".database.windows.net", ".database.usgovcloudapi.net", ".database.chinacloudapi.cn");
        Storage = Pick(o.Storage, ".core.windows.net", ".core.usgovcloudapi.net", ".core.chinacloudapi.cn");
        Cosmos = Pick(o.Cosmos, ".documents.azure.com", ".documents.azure.us", ".documents.azure.cn");
        Redis = Pick(o.Redis, ".redis.cache.windows.net", ".redis.azure.net", ".redisenterprise.cache.azure.net",
            ".redis.cache.usgovcloudapi.net", ".redis.cache.chinacloudapi.cn");
        ServiceBus = Pick(o.ServiceBus, ".servicebus.windows.net", ".servicebus.usgovcloudapi.net", ".servicebus.chinacloudapi.cn");
        KeyVault = Pick(o.KeyVault, ".vault.azure.net", ".vault.usgovcloudapi.net", ".vault.azure.cn");
        ContainerRegistry = Pick(o.ContainerRegistry, ".azurecr.io", ".azurecr.us", ".azurecr.cn");
    }

    public IReadOnlyList<string> Sql { get; }
    public IReadOnlyList<string> Storage { get; }
    public IReadOnlyList<string> Cosmos { get; }
    public IReadOnlyList<string> Redis { get; }
    public IReadOnlyList<string> ServiceBus { get; }
    public IReadOnlyList<string> KeyVault { get; }
    public IReadOnlyList<string> ContainerRegistry { get; }

    public IEnumerable<string> AllAzure => Sql.Concat(Storage).Concat(Cosmos).Concat(Redis)
        .Concat(ServiceBus).Concat(KeyVault).Concat(ContainerRegistry);

    public static bool EndsWithAny(string? host, IEnumerable<string> suffixes) =>
        host is not null && suffixes.Any(s => host.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    private static string[] Pick(string[] configured, params string[] defaults) =>
        configured is { Length: > 0 } ? configured : defaults;
}
