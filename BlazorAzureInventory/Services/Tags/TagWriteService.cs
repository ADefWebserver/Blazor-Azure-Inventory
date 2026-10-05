using Azure;
using BlazorAzureInventory.Models.Graph;
using BlazorAzureInventory.Models.Options;
using BlazorAzureInventory.Models.Tags;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;

namespace BlazorAzureInventory.Services.Tags;

/// <summary>
/// The only code path that changes Azure state. Re-reads tags, rejects writes whose baseline is stale, merges, writes the
/// full set, and syncs the result into the session. Never reachable from an AI function.
/// </summary>
public sealed class TagWriteService(
    ITagStore tagStore,
    TagValidator validator,
    TagSyncCoordinator sync,
    IOptions<TaggingOptions> options,
    ILogger<TagWriteService> logger)
{
    private const int MaxRetries = 3;

    /// <summary>Delay used between throttling retries. Replaceable in tests.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    public bool IsEnabled => options.Value.Enabled;

    public Task<IReadOnlyDictionary<string, string>> ReadCurrentAsync(string tenantId, string resourceId, CancellationToken ct) =>
        tagStore.GetAsync(tenantId, resourceId, ct);

    public async Task<TagWriteResult> ApplyAsync(ResourceTagDiff diff, CancellationToken ct)
    {
        if (!options.Value.Enabled) return Result(diff, TagWriteOutcome.Failed, "Tag editing is disabled.");

        // Only resources from the user's own last scan can be written; IDs from the client or the model are never trusted.
        var session = await sync.GetSessionAsync();
        GraphNode? node = null;
        if (session?.Index?.NodesById.TryGetValue(diff.ResourceId, out var n) == true) node = n;
        if (node is null) return Result(diff, TagWriteOutcome.ValidationFailed, "This resource is not in your last scan.");
        if (!node.IsTaggable()) return Result(diff, TagWriteOutcome.ValidationFailed, "This resource does not support tags.");

        var tenantId = node.Data.GetValueOrDefault("tenantId")?.ToString() ?? "";
        var armType = node.Data.GetValueOrDefault("armType")?.ToString();
        if (diff.IsEmpty) return new TagWriteResult(node.Id, TagWriteOutcome.Succeeded, node.GetTags(), "No changes.");

        var changeCheck = validator.ValidateChanges(diff.Changes, armType);
        if (!changeCheck.IsValid) return Result(diff, TagWriteOutcome.ValidationFailed, string.Join(" ", changeCheck.Errors.Select(e => e.Message).Distinct()));

        var retries = new RetryCounter();
        TagWriteResult result;
        try
        {
            var current = await WithRetryAsync(() => tagStore.GetAsync(tenantId, node.Id, ct), retries, ct);
            var conflicts = TagDiffBuilder.FindConflicts(diff.BaselineTags, current, diff.Changes);
            if (conflicts.Count > 0)
            {
                result = new TagWriteResult(node.Id, TagWriteOutcome.Conflict, current,
                    $"Tags changed in Azure since you loaded them: {string.Join(", ", conflicts.Select(c => c.Name))}.")
                {
                    Conflicts = conflicts,
                    Retries = retries.Count
                };
            }
            else
            {
                var merged = TagDiffBuilder.Apply(current, diff.Changes);
                var setCheck = validator.ValidateSet(merged, armType, current);
                if (!setCheck.IsValid)
                {
                    result = Result(diff, TagWriteOutcome.ValidationFailed, string.Join(" ", setCheck.Errors.Select(e => e.Message).Distinct()));
                }
                else
                {
                    var (merge, delete) = ToPatch(current, diff.Changes);
                    var saved = await WithRetryAsync(() => tagStore.PatchAsync(tenantId, node.Id, merge, delete, ct), retries, ct);
                    await sync.ApplyAsync(node.Id, saved);
                    result = new TagWriteResult(node.Id, TagWriteOutcome.Succeeded, saved, null) { Retries = retries.Count };
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var (outcome, message) = TagErrorMapper.Map(ex);
            result = new TagWriteResult(node.Id, outcome, null, message) { Retries = retries.Count };
        }

        // Tag names and IDs only; values stay out of logs.
        var user = await sync.GetUserAsync();
        logger.LogInformation("Tag write {Outcome} for {ResourceId} by {UserObjectId}; tags: {TagNames}",
            result.Outcome, node.Id, user.GetObjectId(), string.Join(",", diff.Changes.Select(c => $"{c.Kind}:{c.Name}")));
        return result;
    }

    /// <summary>Applies diffs with bounded parallelism. Each resource succeeds or fails on its own.</summary>
    public async Task<IReadOnlyList<TagWriteResult>> ApplyManyAsync(
        IReadOnlyList<ResourceTagDiff> diffs, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var opts = options.Value;
        if (diffs.Count > opts.MaxResourcesPerBatch)
        {
            throw new InvalidOperationException($"A batch can contain at most {opts.MaxResourcesPerBatch} resources.");
        }

        var results = new TagWriteResult?[diffs.Count];
        var done = 0;
        using var gate = new SemaphoreSlim(opts.MaxParallelWrites);
        var tasks = diffs.Select(async (diff, i) =>
        {
            try
            {
                await gate.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                results[i] = Result(diff, TagWriteOutcome.Failed, "Cancelled before it started.");
                return;
            }

            try
            {
                results[i] = await ApplyAsync(diff, ct);
            }
            catch (OperationCanceledException)
            {
                results[i] = Result(diff, TagWriteOutcome.Failed, "Cancelled.");
            }
            finally
            {
                gate.Release();
                progress?.Report((Interlocked.Increment(ref done), diffs.Count));
            }
        }).ToList();

        await Task.WhenAll(tasks);
        return results.Select((r, i) => r ?? Result(diffs[i], TagWriteOutcome.Failed, "Not attempted.")).ToList();
    }

    private async Task<T> WithRetryAsync<T>(Func<Task<T>> action, RetryCounter retries, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (RequestFailedException ex) when (ex.Status == 429 && attempt < MaxRetries)
            {
                retries.Count++;
                await Delay(TagErrorMapper.RetryAfter(ex) ?? TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
        }
    }

    /// <summary>
    /// Splits changes into a Merge set (adds and updates) and a Delete set (removed names with their current values). A
    /// case-only rename deletes the old casing first so the new casing is stored.
    /// </summary>
    public static (Dictionary<string, string> Merge, Dictionary<string, string> Delete) ToPatch(
        IReadOnlyDictionary<string, string> current, IEnumerable<TagChange> changes)
    {
        var merge = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var delete = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in changes)
        {
            var existing = current.FirstOrDefault(kv => string.Equals(kv.Key, c.Name, StringComparison.OrdinalIgnoreCase));
            if (c.Kind == TagChangeKind.Remove)
            {
                if (existing.Key is not null) delete[existing.Key] = existing.Value;
                continue;
            }
            if (existing.Key is not null && !string.Equals(existing.Key, c.Name, StringComparison.Ordinal)) delete[existing.Key] = existing.Value;
            merge[c.Name] = c.NewValue ?? "";
        }
        return (merge, delete);
    }

    private static TagWriteResult Result(ResourceTagDiff diff, TagWriteOutcome outcome, string message) =>
        new(diff.ResourceId, outcome, null, message);

    private sealed class RetryCounter
    {
        public int Count { get; set; }
    }
}
