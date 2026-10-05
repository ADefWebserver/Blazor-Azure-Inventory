using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace BlazorAzureInventory.Services.AI.TagTools;

/// <summary>
/// Exposes <see cref="IInventoryTagTools"/> as <see cref="AIFunction"/>s. There is deliberately no function that applies
/// changes: proposals are surfaced to the user through <paramref name="onProposal"/> and applied only by a user click.
/// </summary>
public static class TagToolRegistration
{
    public static IReadOnlyList<AIFunction> Create(IInventoryTagTools t, Func<string, Task> onProposal) =>
    [
        AIFunctionFactory.Create(
            ([Description("Node ID or resource name")] string id) => t.GetTags(id),
            nameof(IInventoryTagTools.GetTags), "Returns the Azure tags of one resource. Secret-like values are masked."),
        AIFunctionFactory.Create(
            ([Description("Tag name; overflow tags such as name-2 also match")] string name,
             [Description("Optional tag value to match")] string? value,
             [Description("exact (default), contains, or prefix")] string? valueMatch,
             [Description("Maximum results")] int max) => t.FindByTag(name, value, valueMatch, max),
            nameof(IInventoryTagTools.FindByTag), "Finds resources that have a tag, optionally with a matching value."),
        AIFunctionFactory.Create(
            ([Description("Optional node type, e.g. WebApp")] string? type,
             [Description("Maximum results")] int max) => t.ListTagNames(type, max),
            nameof(IInventoryTagTools.ListTagNames), "Lists tag names in use with counts and sample values."),
        AIFunctionFactory.Create(
            async ([Description("Changes: resource (ID or exact name), tag name, value, and remove=true to delete a tag")] TagChangeRequest[] changes,
                   [Description("Short reason shown to the user")] string reason) =>
            {
                var summary = t.ProposeTagChanges(changes, reason);
                if (summary.ProposalId is { } id) await onProposal(id);
                return summary;
            },
            nameof(IInventoryTagTools.ProposeTagChanges),
            "PROPOSES tag changes. Nothing is written: the user reviews the proposal in the chat and clicks Apply."),
        AIFunctionFactory.Create(
            async ([Description("'all', a subscription name or ID, or a resource group name. Ignored when resources are given.")] string? scope,
                   [Description("Optional list of resource IDs or exact names")] string[]? resources,
                   [Description("Optional comma-separated node types, e.g. WebApp,FunctionApp")] string? types,
                   [Description("Short reason shown to the user")] string reason) =>
            {
                var summary = t.ProposeConnectionTags(scope, resources, types, reason);
                if (summary.ProposalId is { } id) await onProposal(id);
                return summary;
            },
            nameof(IInventoryTagTools.ProposeConnectionTags),
            "PROPOSES conn-out-<Type> / conn-in-<Type> tags listing what each resource connects to, grouped by type. Nothing is written until the user clicks Apply."),
        AIFunctionFactory.Create(
            ([Description("Proposal ID, e.g. tp_7f3a9c12")] string proposalId) => t.GetProposal(proposalId),
            nameof(IInventoryTagTools.GetProposal), "Returns the status and outcome of a tag proposal."),
    ];
}
