namespace BlazorAzureInventory.Services.AI.TagTools;

public sealed record TagChangeRequest(string Resource, string Name, string? Value, bool Remove);

public sealed record TagSet(string Id, string Name, string Type, bool Taggable, IReadOnlyDictionary<string, string> Tags);

public sealed record TaggedResource(string Id, string Name, string Type, string TagName, string Value);

public sealed record TagUsage(string Name, int Count, IReadOnlyList<string> SampleValues);

public sealed record TagProposalSummary(
    string? ProposalId, int Resources, int Adds, int Updates, int Removes, int UpToDate,
    IReadOnlyList<string> Warnings, IReadOnlyList<string> Errors, string Status, string? Outcome = null);
