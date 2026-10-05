using System.Security.Claims;
using BlazorAzureInventory.Models.Graph;
using BlazorAzureInventory.Models.Inventory;
using BlazorAzureInventory.Models.Tags;
using BlazorAzureInventory.Services.Auth;
using BlazorAzureInventory.Services.Graph;

namespace BlazorAzureInventory.Services.Tags;

/// <summary>Applies successful tag writes to the user's in-memory snapshot and graph, and raises change events.</summary>
public sealed class TagSyncCoordinator(InventorySessionStore store, UserContextAccessor userContext)
{
    public Task<ClaimsPrincipal> GetUserAsync() => userContext.GetUserAsync();

    public async Task<InventorySession?> GetSessionAsync() => store.Get(await userContext.GetUserAsync());

    public async Task ApplyAsync(string resourceId, IReadOnlyDictionary<string, string> tags)
    {
        var user = await userContext.GetUserAsync();
        var session = store.Get(user);
        if (session is null) return;
        if (Apply(session, resourceId, tags)) store.NotifyTagsChanged(user, resourceId);
    }

    /// <summary>Updates the snapshot record and graph node for <paramref name="resourceId"/>. Returns false if it is unknown.</summary>
    public static bool Apply(InventorySession session, string resourceId, IReadOnlyDictionary<string, string> tags)
    {
        var copy = new Dictionary<string, string>(tags, StringComparer.OrdinalIgnoreCase);
        lock (session.SyncRoot)
        {
            var found = false;
            if (session.Snapshot is { } snapshot)
            {
                var updated = snapshot;
                var rIndex = IndexOf(snapshot.Resources, r => r.Id, resourceId);
                if (rIndex >= 0)
                {
                    var list = snapshot.Resources.ToList();
                    list[rIndex] = list[rIndex] with { Tags = copy };
                    updated = updated with { Resources = list };
                    found = true;
                }

                var gIndex = IndexOf(snapshot.ResourceGroups, g => g.Id, resourceId);
                if (gIndex >= 0)
                {
                    var list = snapshot.ResourceGroups.ToList();
                    list[gIndex] = list[gIndex] with { Tags = copy };
                    updated = updated with { ResourceGroups = list };
                    found = true;
                }

                var sIndex = IndexOf(snapshot.Subscriptions, s => $"/subscriptions/{s.SubscriptionId}", resourceId);
                if (sIndex >= 0)
                {
                    var list = snapshot.Subscriptions.ToList();
                    list[sIndex] = list[sIndex] with { Tags = copy };
                    updated = updated with { Subscriptions = list };
                    found = true;
                }
                session.Snapshot = updated;
            }

            if (session.Index?.NodesById.TryGetValue(resourceId, out var node) == true)
            {
                node.Data[TagKeys.Tags] = copy;
                found = true;
            }

            foreach (var proposal in session.TagProposals.Values.Where(p => p.Status == TagProposalStatus.Pending))
            {
                var diff = proposal.Diffs.FirstOrDefault(d => d.ResourceId.Equals(resourceId, StringComparison.OrdinalIgnoreCase));
                if (diff is not null && !TagDiffBuilder.SameTags(diff.BaselineTags, copy)) proposal.BaselineChanged.Add(resourceId);
            }
            return found;
        }
    }

    private static int IndexOf<T>(IReadOnlyList<T> list, Func<T, string> id, string resourceId)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(id(list[i]), resourceId, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }
}
