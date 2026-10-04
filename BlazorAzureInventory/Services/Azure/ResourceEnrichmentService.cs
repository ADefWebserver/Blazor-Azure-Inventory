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

            var data = container.Data;
            var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["publicAccess"] = data.PublicAccess?.ToString() ?? "None",
                ["lastModified"] = data.LastModifiedOn?.UtcDateTime.ToString("yyyy-MM-dd HH:mm") ?? "",
                ["leaseState"] = data.LeaseState?.ToString() ?? ""
            };

            containers.Add(new AzureResourceInfo(
                $"{account.Id}/blobservices/default/containers/{data.Name}".ToLowerInvariant(),
                data.Name, AzureResourceKind.BlobContainer, "microsoft.storage/storageaccounts/blobservices/containers",
                account.TenantId, account.SubscriptionId, account.ResourceGroup, account.Location,
                account.Id, null, props));
        }

        return (containers, false);
    }
}
