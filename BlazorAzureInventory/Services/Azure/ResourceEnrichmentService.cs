using Azure;
using Azure.Core;
using Azure.ResourceManager.Storage;
using BlazorAzureInventory.Models.Inventory;

namespace BlazorAzureInventory.Services.Azure;

/// <summary>Management-plane enrichment that does not involve configuration values.</summary>
public sealed class ResourceEnrichmentService(ArmClientFactory armFactory)
{
    public async Task<(IReadOnlyList<AzureResourceInfo> Containers, bool Truncated)> ListBlobContainersAsync(
        AzureResourceInfo account, int maxContainers, CancellationToken ct)
    {
        var client = await armFactory.GetAsync(account.TenantId);
        var storage = client.GetStorageAccountResource(new ResourceIdentifier(account.Id));
        var containers = new List<AzureResourceInfo>();

        await foreach (var container in storage.GetBlobService().GetBlobContainers().GetAllAsync(cancellationToken: ct))
        {
            if (containers.Count >= maxContainers)
            {
                return (containers, true);
            }

            containers.Add(ContainerInfo(account, container.Data));
        }

        return (containers, false);
    }

    private static AzureResourceInfo ContainerInfo(AzureResourceInfo account, BlobContainerData data)
    {
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["publicAccess"] = data.PublicAccess?.ToString() ?? "None",
            ["lastModified"] = data.LastModifiedOn?.UtcDateTime.ToString("yyyy-MM-dd HH:mm") ?? "",
            ["leaseState"] = data.LeaseState?.ToString() ?? ""
        };
        return new AzureResourceInfo(
            $"{account.Id}/blobservices/default/containers/{data.Name}".ToLowerInvariant(),
            data.Name, AzureResourceKind.BlobContainer, "microsoft.storage/storageaccounts/blobservices/containers",
            account.TenantId, account.SubscriptionId, account.ResourceGroup, account.Location,
            account.Id, null, props);
    }

    /// <summary>Premium block blob and file-only accounts have no queue service.</summary>
    public static bool SupportsQueues(AzureResourceInfo account) =>
        account.Prop("kind") is not { } kind
        || !(kind.Equals("BlockBlobStorage", StringComparison.OrdinalIgnoreCase) || kind.Equals("FileStorage", StringComparison.OrdinalIgnoreCase));

    public async Task<(IReadOnlyList<AzureResourceInfo> Queues, bool Truncated)> ListQueuesAsync(
        AzureResourceInfo account, int maxQueues, CancellationToken ct)
    {
        var client = await armFactory.GetAsync(account.TenantId);
        var storage = client.GetStorageAccountResource(new ResourceIdentifier(account.Id));
        var queues = new List<AzureResourceInfo>();

        await foreach (var queue in storage.GetQueueService().GetStorageQueues().GetAllAsync(cancellationToken: ct))
        {
            if (queues.Count >= maxQueues)
            {
                return (queues, true);
            }
            queues.Add(QueueInfo(account, queue.Data.Name));
        }

        return (queues, false);
    }

    /// <summary>Looks up one container or queue by name; null when it does not exist.</summary>
    public async Task<AzureResourceInfo?> FindChildAsync(AzureResourceInfo account, AzureResourceKind kind, string name, CancellationToken ct)
    {
        var client = await armFactory.GetAsync(account.TenantId);
        var storage = client.GetStorageAccountResource(new ResourceIdentifier(account.Id));
        try
        {
            if (kind == AzureResourceKind.StorageQueue)
            {
                var queue = await storage.GetQueueService().GetStorageQueues().GetAsync(name, ct);
                return QueueInfo(account, queue.Value.Data.Name);
            }
            var container = await storage.GetBlobService().GetBlobContainers().GetAsync(name, ct);
            return ContainerInfo(account, container.Value.Data);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    private static AzureResourceInfo QueueInfo(AzureResourceInfo account, string name) => new(
        $"{account.Id}/queueservices/default/queues/{name}".ToLowerInvariant(),
        name, AzureResourceKind.StorageQueue, "microsoft.storage/storageaccounts/queueservices/queues",
        account.TenantId, account.SubscriptionId, account.ResourceGroup, account.Location,
        account.Id, null, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
}
