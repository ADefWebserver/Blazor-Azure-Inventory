namespace BlazorAzureInventory.Models.Tags;

public enum TagChangeKind { Add, Update, Remove }

public sealed record TagChange(string Name, TagChangeKind Kind, string? OldValue, string? NewValue);

/// <summary>A set of changes for one resource, computed against the tags the user (or AI) last saw.</summary>
public sealed record ResourceTagDiff(
    string ResourceId,
    string ResourceLabel,
    string ResourceType,
    string? ArmType,
    string TenantId,
    IReadOnlyDictionary<string, string> BaselineTags,
    IReadOnlyList<TagChange> Changes)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];

    public bool IsEmpty => Changes.Count == 0;
    public bool IsValid => Errors.Count == 0;
    public int Adds => Changes.Count(c => c.Kind == TagChangeKind.Add);
    public int Updates => Changes.Count(c => c.Kind == TagChangeKind.Update);
    public int Removes => Changes.Count(c => c.Kind == TagChangeKind.Remove);
}

public enum TagWriteOutcome { Succeeded, Conflict, Forbidden, PolicyDenied, NotFound, Throttled, ValidationFailed, Failed }

public sealed record TagWriteResult(
    string ResourceId,
    TagWriteOutcome Outcome,
    IReadOnlyDictionary<string, string>? NewTags,
    string? Message)
{
    /// <summary>Tags that changed in Azure since the baseline (only for <see cref="TagWriteOutcome.Conflict"/>).</summary>
    public IReadOnlyList<TagConflict> Conflicts { get; init; } = [];
    public int Retries { get; init; }
}

public sealed record TagConflict(string Name, string? BaselineValue, string? CurrentValue, string? YourValue);

public sealed record TagValidationError(string? TagName, string Message);

public sealed record TagValidationResult(IReadOnlyList<TagValidationError> Errors, IReadOnlyList<TagValidationError> Warnings)
{
    public static readonly TagValidationResult Ok = new([], []);
    public bool IsValid => Errors.Count == 0;
}

public enum TagProposalStatus { Pending, Applying, Applied, PartiallyApplied, Discarded, Expired }

public sealed class TagProposal
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Reason { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
    public required DateTimeOffset ScanUtc { get; init; }
    public required List<ResourceTagDiff> Diffs { get; set; }
    public List<string> Warnings { get; init; } = new();
    public List<string> Errors { get; init; } = new();
    public HashSet<string> SelectedResourceIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public TagProposalStatus Status { get; set; } = TagProposalStatus.Pending;
    public Dictionary<string, TagWriteResult> Results { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resources whose tags changed elsewhere after the proposal was created.</summary>
    public HashSet<string> BaselineChanged { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int Succeeded => Results.Values.Count(r => r.Outcome == TagWriteOutcome.Succeeded);
    public int Failed => Results.Values.Count(r => r.Outcome != TagWriteOutcome.Succeeded);

    public string OutcomeSummary => Status switch
    {
        TagProposalStatus.Applied or TagProposalStatus.PartiallyApplied => $"{Succeeded} succeeded, {Failed} failed",
        _ => Status.ToString()
    };
}
