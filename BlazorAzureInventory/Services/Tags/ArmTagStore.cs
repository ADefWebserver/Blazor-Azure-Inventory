using Azure;
using Azure.Core;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;
using BlazorAzureInventory.Services.Azure;

namespace BlazorAzureInventory.Services.Tags;

/// <summary>Reads and patches the tags of an ARM scope (resource, resource group, or subscription).</summary>
public interface ITagStore
{
    Task<IReadOnlyDictionary<string, string>> GetAsync(string tenantId, string resourceId, CancellationToken ct);

    /// <summary>
    /// Deletes <paramref name="delete"/> (name and current value), then merges <paramref name="merge"/>. Untouched tags are
    /// never re-sent, so tags Azure created with otherwise invalid names survive. Returns the tags Azure stored (policy
    /// <c>modify</c> effects may change them).
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> PatchAsync(string tenantId, string resourceId,
        IReadOnlyDictionary<string, string> merge, IReadOnlyDictionary<string, string> delete, CancellationToken ct);
}

/// <summary>Uses the <c>Microsoft.Resources/tags/default</c> extension resource as the signed-in user.</summary>
public sealed class ArmTagStore(ArmClientFactory armFactory) : ITagStore
{
    public async Task<IReadOnlyDictionary<string, string>> GetAsync(string tenantId, string resourceId, CancellationToken ct)
    {
        var tagResource = await GetTagResourceAsync(tenantId, resourceId);
        var response = await tagResource.GetAsync(ct);
        return ToDictionary(response.Value.Data.TagValues);
    }

    public async Task<IReadOnlyDictionary<string, string>> PatchAsync(string tenantId, string resourceId,
        IReadOnlyDictionary<string, string> merge, IReadOnlyDictionary<string, string> delete, CancellationToken ct)
    {
        var tagResource = await GetTagResourceAsync(tenantId, resourceId);
        IDictionary<string, string>? result = null;
        if (delete.Count > 0)
        {
            var patch = new TagResourcePatch { PatchMode = TagPatchMode.Delete };
            foreach (var (k, v) in delete) patch.TagValues[k] = v;
            result = (await tagResource.UpdateAsync(WaitUntil.Completed, patch, ct)).Value.Data.TagValues;
        }
        if (merge.Count > 0)
        {
            var patch = new TagResourcePatch { PatchMode = TagPatchMode.Merge };
            foreach (var (k, v) in merge) patch.TagValues[k] = v;
            result = (await tagResource.UpdateAsync(WaitUntil.Completed, patch, ct)).Value.Data.TagValues;
        }
        return result is null ? await GetAsync(tenantId, resourceId, ct) : ToDictionary(result);
    }

    private async Task<TagResource> GetTagResourceAsync(string tenantId, string resourceId)
    {
        var client = await armFactory.GetAsync(string.IsNullOrEmpty(tenantId) ? null : tenantId);
        return client.GetTagResource(TagResource.CreateResourceIdentifier(new ResourceIdentifier(resourceId)));
    }

    private static Dictionary<string, string> ToDictionary(IDictionary<string, string>? tags)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (tags is null) return result;
        foreach (var (k, v) in tags) result.TryAdd(k, v ?? "");
        return result;
    }
}
