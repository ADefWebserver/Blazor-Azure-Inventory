namespace BlazorAzureInventory.Services.AI.TagTools;

/// <summary>
/// Tag tools for the AI chat. The model can read tags and PROPOSE changes; it can never apply them. Only a user click on
/// Apply in the UI calls <see cref="Tags.TagWriteService"/>. Secret-like tag values are masked in every result.
/// </summary>
public interface IInventoryTagTools
{
    TagSet? GetTags(string idOrName);
    TaggedResource[] FindByTag(string name, string? value, string? valueMatch, int max);
    TagUsage[] ListTagNames(string? type, int max);

    TagProposalSummary ProposeTagChanges(TagChangeRequest[] changes, string reason);
    TagProposalSummary ProposeConnectionTags(string? scope, string[]? resourceIdsOrNames, string? types, string reason);
    TagProposalSummary? GetProposal(string proposalId);
}
