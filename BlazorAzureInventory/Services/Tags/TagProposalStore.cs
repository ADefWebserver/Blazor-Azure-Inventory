using System.Security.Cryptography;
using BlazorAzureInventory.Models.Options;
using BlazorAzureInventory.Models.Tags;
using BlazorAzureInventory.Services.Graph;
using Microsoft.Extensions.Options;

namespace BlazorAzureInventory.Services.Tags;

/// <summary>Creates, finds, and expires tag proposals stored in a user's session. Never writes to Azure.</summary>
public sealed class TagProposalStore(IOptions<TaggingOptions> options, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public TagProposal Create(InventorySession session, string title, string reason, IEnumerable<ResourceTagDiff> diffs,
        IEnumerable<string>? warnings = null, IEnumerable<string>? errors = null)
    {
        var proposal = new TagProposal
        {
            Id = "tp_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)),
            Title = title,
            Reason = reason,
            CreatedUtc = _clock.GetUtcNow(),
            ScanUtc = session.Snapshot?.ScannedUtc ?? default,
            Diffs = diffs.Where(d => !d.IsEmpty || !d.IsValid).ToList(),
            Warnings = warnings?.ToList() ?? new(),
            Errors = errors?.ToList() ?? new()
        };
        foreach (var d in proposal.Diffs.Where(d => d.IsValid && !d.IsEmpty)) proposal.SelectedResourceIds.Add(d.ResourceId);

        lock (session.SyncRoot)
        {
            foreach (var stale in session.TagProposals.Values.Where(p => IsExpired(session, p)).ToList())
            {
                stale.Status = TagProposalStatus.Expired;
            }
            session.TagProposals[proposal.Id] = proposal;
        }
        return proposal;
    }

    public TagProposal? Get(InventorySession session, string proposalId)
    {
        lock (session.SyncRoot)
        {
            if (!session.TagProposals.TryGetValue(proposalId.Trim(), out var proposal)) return null;
            if (proposal.Status == TagProposalStatus.Pending && IsExpired(session, proposal)) proposal.Status = TagProposalStatus.Expired;
            return proposal;
        }
    }

    public IReadOnlyList<TagProposal> List(InventorySession session)
    {
        lock (session.SyncRoot)
        {
            return session.TagProposals.Values.OrderBy(p => p.CreatedUtc).ToList();
        }
    }

    /// <summary>Proposals expire after the TTL, or when a new scan replaced the snapshot they were built from.</summary>
    public bool IsExpired(InventorySession session, TagProposal proposal) =>
        proposal.Status == TagProposalStatus.Pending
        && (_clock.GetUtcNow() - proposal.CreatedUtc > TimeSpan.FromMinutes(options.Value.ProposalTtlMinutes)
            || session.Snapshot is null
            || session.Snapshot.ScannedUtc != proposal.ScanUtc);
}
