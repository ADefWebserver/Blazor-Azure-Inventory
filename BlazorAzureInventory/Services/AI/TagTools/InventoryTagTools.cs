using BlazorAzureInventory.Models.Graph;
using BlazorAzureInventory.Models.Options;
using BlazorAzureInventory.Models.Tags;
using BlazorAzureInventory.Services.AI.GraphTools;
using BlazorAzureInventory.Services.Graph;
using BlazorAzureInventory.Services.Tags;

namespace BlazorAzureInventory.Services.AI.TagTools;

/// <summary>Tag tools bound to one user's session. Proposals are stored in the session and never written to Azure here.</summary>
public sealed class InventoryTagTools(
    InventorySession session,
    GraphIndex index,
    ConnectionTagGenerator generator,
    TagValidator validator,
    TagProposalStore proposals,
    TaggingOptions options,
    int hardCap) : IInventoryTagTools
{
    public const string MaskedValue = "[masked: looks like a secret]";
    private const int MaxMessages = 10;

    public TagSet? GetTags(string idOrName)
    {
        var node = GraphNodeResolver.Resolve(index, idOrName).FirstOrDefault();
        if (node is null) return null;
        lock (session.SyncRoot)
        {
            return new TagSet(node.Id, node.Label, node.Type, node.IsTaggable(), Masked(node.GetTags()));
        }
    }

    public TaggedResource[] FindByTag(string name, string? value, string? valueMatch, int max)
    {
        if (string.IsNullOrWhiteSpace(name)) return [];
        var wanted = name.Trim();
        var match = valueMatch?.Trim().ToLowerInvariant();
        var results = new List<TaggedResource>();
        lock (session.SyncRoot)
        {
            foreach (var node in index.Graph.Nodes)
            {
                foreach (var (tagName, tagValue) in node.GetTags())
                {
                    if (!NameMatches(tagName, wanted)) continue;
                    var secret = validator.IsSecretLike(tagName, tagValue);
                    if (!string.IsNullOrEmpty(value))
                    {
                        // Secret-like values never match a value filter, so they cannot be probed character by character.
                        if (secret) continue;
                        var ok = match switch
                        {
                            "contains" => tagValue.Contains(value, StringComparison.OrdinalIgnoreCase),
                            "prefix" => tagValue.StartsWith(value, StringComparison.OrdinalIgnoreCase),
                            _ => tagValue.Equals(value, StringComparison.OrdinalIgnoreCase)
                        };
                        if (!ok) continue;
                    }
                    results.Add(new TaggedResource(node.Id, node.Label, node.Type, tagName, secret ? MaskedValue : tagValue));
                }
                if (results.Count >= Cap(max)) break;
            }
        }
        return results.Take(Cap(max)).ToArray();
    }

    public TagUsage[] ListTagNames(string? type, int max)
    {
        lock (session.SyncRoot)
        {
            return index.Graph.Nodes
                .Where(n => string.IsNullOrWhiteSpace(type) || n.Type.Equals(type, StringComparison.OrdinalIgnoreCase))
                .SelectMany(n => n.GetTags())
                .GroupBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Take(Cap(max))
                .Select(g => new TagUsage(g.Key, g.Count(),
                    g.Select(t => validator.IsSecretLike(t.Key, t.Value) ? MaskedValue : t.Value).Distinct().Take(3).ToList()))
                .ToArray();
        }
    }

    public TagProposalSummary ProposeTagChanges(TagChangeRequest[] changes, string reason)
    {
        var warnings = new List<string>();
        var errors = new List<string>();
        var desiredByNode = new Dictionary<string, (GraphNode Node, Dictionary<string, string> Desired)>(StringComparer.OrdinalIgnoreCase);

        lock (session.SyncRoot)
        {
            foreach (var group in (changes ?? []).Where(c => c is not null).GroupBy(c => c.Resource?.Trim() ?? "", StringComparer.OrdinalIgnoreCase))
            {
                var matches = GraphNodeResolver.ResolveExact(index, group.Key);
                if (matches.Count == 0) { errors.Add($"Not found: '{group.Key}'."); continue; }
                if (matches.Count > 1) { errors.Add($"Ambiguous: '{group.Key}' matches {matches.Count} resources; use the node ID."); continue; }
                var node = matches[0];
                if (!node.IsTaggable()) { warnings.Add($"Skipped '{node.Label}': it cannot be tagged."); continue; }

                if (!desiredByNode.TryGetValue(node.Id, out var entry))
                {
                    entry = (node, new Dictionary<string, string>(node.GetTags(), StringComparer.OrdinalIgnoreCase));
                    desiredByNode[node.Id] = entry;
                }
                foreach (var c in group)
                {
                    if (string.IsNullOrWhiteSpace(c.Name)) { errors.Add($"A change for '{node.Label}' has no tag name."); continue; }
                    if (c.Remove) entry.Desired.Remove(c.Name.Trim());
                    else
                    {
                        entry.Desired.Remove(c.Name.Trim());
                        entry.Desired[c.Name.Trim()] = (c.Value ?? "").Trim();
                    }
                }
            }

            if (desiredByNode.Count > options.MaxResourcesPerProposal)
            {
                return Summary(null, [], 0, warnings, [.. errors, TooMany(desiredByNode.Count)], "Rejected");
            }

            var diffs = desiredByNode.Values.Select(e =>
            {
                var current = e.Node.GetTags();
                var armType = e.Node.Data.GetValueOrDefault("armType")?.ToString();
                var check = validator.ValidateSet(e.Desired, armType, current);
                return new ResourceTagDiff(e.Node.Id, e.Node.Label, e.Node.Type, armType,
                    e.Node.Data.GetValueOrDefault("tenantId")?.ToString() ?? "", current, TagDiffBuilder.Diff(current, e.Desired))
                {
                    Errors = check.Errors.Select(x => x.Message).Distinct().ToList(),
                    Warnings = check.Warnings.Where(w => !w.Message.Contains("secret", StringComparison.OrdinalIgnoreCase))
                        .Select(x => x.Message).Distinct().ToList()
                };
            }).ToList();

            return CreateProposal($"Tag changes for {diffs.Count(d => !d.IsEmpty)} resource(s)", reason, diffs, 0, warnings, errors);
        }
    }

    public TagProposalSummary ProposeConnectionTags(string? scope, string[]? resourceIdsOrNames, string? types, string reason)
    {
        var warnings = new List<string>();
        var errors = new List<string>();
        var typeSet = (types ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

        lock (session.SyncRoot)
        {
            List<GraphNode> nodes;
            if (resourceIdsOrNames is { Length: > 0 })
            {
                nodes = new();
                foreach (var r in resourceIdsOrNames)
                {
                    var matches = GraphNodeResolver.ResolveExact(index, r);
                    if (matches.Count == 0) errors.Add($"Not found: '{r}'.");
                    else if (matches.Count > 1) errors.Add($"Ambiguous: '{r}' matches {matches.Count} resources; use the node ID.");
                    else if (!ConnectionTagGenerator.IsCandidate(matches[0])) warnings.Add($"Skipped '{matches[0].Label}': it cannot receive connection tags.");
                    else nodes.Add(matches[0]);
                }
                if (typeSet.Count > 0) nodes = nodes.Where(n => typeSet.Contains(n.Type)).ToList();
                nodes = nodes.DistinctBy(n => n.Id, StringComparer.OrdinalIgnoreCase).ToList();
            }
            else
            {
                if (!TryResolveScope(scope, out var subscriptionId, out var resourceGroup))
                {
                    return Summary(null, [], 0, warnings, [$"Unknown scope '{scope}'. Use 'all', a subscription name or ID, or a resource group name."], "Rejected");
                }
                nodes = ConnectionTagGenerator.Candidates(index, subscriptionId, resourceGroup, typeSet);
            }

            if (nodes.Count > options.MaxResourcesPerProposal)
            {
                return Summary(null, [], 0, warnings, [.. errors, TooMany(nodes.Count)], "Rejected");
            }

            var scanned = session.Snapshot?.ScannedUtc ?? DateTimeOffset.UtcNow;
            var (diffs, upToDate) = generator.BuildProposalDiffs(index, nodes, scanned);
            return CreateProposal($"Connection tags for {diffs.Count(d => !d.IsEmpty)} resource(s)", reason, diffs, upToDate, warnings, errors);
        }
    }

    public TagProposalSummary? GetProposal(string proposalId)
    {
        if (string.IsNullOrWhiteSpace(proposalId)) return null;
        var p = proposals.Get(session, proposalId);
        if (p is null) return null;
        return Summary(p.Id, p.Diffs, 0, p.Warnings, p.Errors, p.Status.ToString(),
            p.Status is TagProposalStatus.Applied or TagProposalStatus.PartiallyApplied ? p.OutcomeSummary : null);
    }

    private TagProposalSummary CreateProposal(string title, string reason, List<ResourceTagDiff> diffs, int upToDate, List<string> warnings, List<string> errors)
    {
        if (diffs.All(d => d.IsEmpty))
        {
            return Summary(null, [], upToDate, warnings, errors, diffs.Count == 0 && upToDate > 0 ? "NothingToChange" : "NoChanges");
        }
        var proposal = proposals.Create(session, title, string.IsNullOrWhiteSpace(reason) ? "Requested in chat." : reason.Trim(), diffs, warnings, errors);
        return Summary(proposal.Id, proposal.Diffs, upToDate, proposal.Warnings, proposal.Errors, proposal.Status.ToString());
    }

    private static TagProposalSummary Summary(string? id, IReadOnlyList<ResourceTagDiff> diffs, int upToDate,
        IEnumerable<string> warnings, IEnumerable<string> errors, string status, string? outcome = null)
    {
        var allWarnings = warnings.Concat(diffs.SelectMany(d => d.Warnings.Select(w => $"{d.ResourceLabel}: {w}"))).ToList();
        var allErrors = errors.Concat(diffs.SelectMany(d => d.Errors.Select(e => $"{d.ResourceLabel}: {e}"))).ToList();
        return new TagProposalSummary(id, diffs.Count(d => !d.IsEmpty), diffs.Sum(d => d.Adds), diffs.Sum(d => d.Updates), diffs.Sum(d => d.Removes),
            upToDate, Limit(allWarnings), Limit(allErrors), status, outcome);
    }

    private static List<string> Limit(List<string> messages) =>
        messages.Count <= MaxMessages ? messages : [.. messages.Take(MaxMessages), $"...and {messages.Count - MaxMessages} more."];

    private string TooMany(int count) =>
        $"{count} resources exceed the limit of {options.MaxResourcesPerProposal} per proposal. Narrow the scope by subscription, resource group, or type.";

    private bool TryResolveScope(string? scope, out string? subscriptionId, out string? resourceGroup)
    {
        subscriptionId = null;
        resourceGroup = null;
        if (string.IsNullOrWhiteSpace(scope) || scope.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)) return true;
        var s = scope.Trim();
        var subscription = session.Snapshot?.Subscriptions.FirstOrDefault(x =>
            x.SubscriptionId.Equals(s, StringComparison.OrdinalIgnoreCase) || x.DisplayName.Equals(s, StringComparison.OrdinalIgnoreCase));
        if (subscription is not null)
        {
            subscriptionId = subscription.SubscriptionId;
            return true;
        }
        var group = s.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];
        if (index.Graph.Nodes.Any(n => n.Type == "ResourceGroup" && n.Label.Equals(group, StringComparison.OrdinalIgnoreCase)))
        {
            resourceGroup = group;
            return true;
        }
        return false;
    }

    private Dictionary<string, string> Masked(IReadOnlyDictionary<string, string> tags) =>
        tags.ToDictionary(t => t.Key, t => validator.IsSecretLike(t.Key, t.Value) ? MaskedValue : t.Value, StringComparer.OrdinalIgnoreCase);

    private static bool NameMatches(string tagName, string wanted) =>
        tagName.Equals(wanted, StringComparison.OrdinalIgnoreCase)
        || (tagName.StartsWith(wanted + "-", StringComparison.OrdinalIgnoreCase) && int.TryParse(tagName[(wanted.Length + 1)..], out _));

    private int Cap(int max) => Math.Clamp(max <= 0 ? 20 : max, 1, hardCap);
}
