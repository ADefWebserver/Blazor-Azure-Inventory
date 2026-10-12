namespace BlazorAzureInventory.Models.Inventory;

public sealed record ResourceChange(string Id, string Name, AzureResourceKind Kind, IReadOnlyList<string> Changes);

public sealed record ConnectionChange(string SourceId, string SourceLabel, string TargetId, string TargetLabel, string Type);

/// <summary>Differences between the previous scan and the current one, limited to subscriptions present in both.</summary>
public sealed record SnapshotDiff(
    DateTimeOffset PreviousScannedUtc,
    DateTimeOffset CurrentScannedUtc,
    IReadOnlyList<AzureResourceInfo> Added,
    IReadOnlyList<AzureResourceInfo> Removed,
    IReadOnlyList<ResourceChange> Changed,
    IReadOnlyList<ConnectionChange> ConnectionsAdded,
    IReadOnlyList<ConnectionChange> ConnectionsRemoved)
{
    public int TotalCount => Added.Count + Removed.Count + Changed.Count + ConnectionsAdded.Count + ConnectionsRemoved.Count;

    public IReadOnlySet<string> AddedIds => Added.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> ChangedIds => Changed.Select(r => r.Id)
        .Concat(ConnectionsAdded.Select(c => c.SourceId)).Concat(ConnectionsRemoved.Select(c => c.SourceId))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
