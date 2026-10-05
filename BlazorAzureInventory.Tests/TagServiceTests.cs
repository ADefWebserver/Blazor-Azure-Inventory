using System.Text.Json;
using System.Text.RegularExpressions;
using Azure;
using BlazorAzureInventory.Components.Chat;
using BlazorAzureInventory.Models.Graph;
using BlazorAzureInventory.Models.Options;
using BlazorAzureInventory.Models.Tags;
using BlazorAzureInventory.Services.AI.TagTools;
using BlazorAzureInventory.Services.Graph;
using BlazorAzureInventory.Services.Tags;

namespace BlazorAzureInventory.Tests;

public class TagWriteServiceTests
{
    private static ResourceTagDiff Diff(TagTestHost host, string id, params TagChange[] changes)
    {
        var node = host.Index.NodesById[id];
        return new ResourceTagDiff(id, node.Label, node.Type, node.Data["armType"]?.ToString(), Fixture.TenantId, node.GetTags(), changes);
    }

    [Fact]
    public async Task Azure_created_tags_with_invalid_names_do_not_block_edits_and_are_never_resent()
    {
        var host = new TagTestHost(TagTestHost.SnapshotWithTags((Fixture.ShopWeb, TagTestHost.Tags(
            ("hidden-link: /app-insights-resource-id", "/subscriptions/x/components/ai"), ("env", "prod")))));

        var result = await host.Writer.ApplyAsync(Diff(host, Fixture.ShopWeb,
            new TagChange("owner", TagChangeKind.Add, null, "team-a"), new TagChange("env", TagChangeKind.Remove, "prod", null)), default);

        Assert.Equal(TagWriteOutcome.Succeeded, result.Outcome);
        Assert.Equal(["owner"], host.TagStore.MergedNames);
        Assert.Equal(TagTestHost.Tags(("hidden-link: /app-insights-resource-id", "/subscriptions/x/components/ai"), ("owner", "team-a")),
            host.TagStore.Tags[Fixture.ShopWeb]);

        // Connection tag proposals for the same resource are valid too.
        var diff = host.Generator.Generate(host.Index, Fixture.ShopWeb, host.Session.Snapshot!.ScannedUtc);
        Assert.Equal(TagWriteOutcome.Succeeded, (await host.Writer.ApplyAsync(diff, default)).Outcome);
    }

    [Fact]
    public void Patches_split_merge_and_delete_and_handle_case_only_renames()
    {
        var current = TagTestHost.Tags(("env", "prod"), ("owner", "a"));
        var (merge, delete) = TagWriteService.ToPatch(current,
            [new TagChange("Env", TagChangeKind.Update, "prod", "prod"), new TagChange("owner", TagChangeKind.Remove, "a", null), new TagChange("x", TagChangeKind.Add, null, "1")]);
        Assert.Equal(new Dictionary<string, string> { ["Env"] = "prod", ["x"] = "1" }, merge);
        Assert.Equal(new Dictionary<string, string> { ["env"] = "prod", ["owner"] = "a" }, delete);
    }

    [Fact]
    public async Task Writes_merged_tags_and_syncs_the_session()
    {
        var host = new TagTestHost(TagTestHost.SnapshotWithTags((Fixture.ShopWeb, TagTestHost.Tags(("env", "prod")))));
        host.TagStore.Tags[Fixture.ShopWeb]["added-in-portal"] = "x";
        string? changed = null;
        host.Store.TagsChanged += (_, id) => changed = id;

        var result = await host.Writer.ApplyAsync(Diff(host, Fixture.ShopWeb, new TagChange("owner", TagChangeKind.Add, null, "team-a")), default);

        Assert.Equal(TagWriteOutcome.Succeeded, result.Outcome);
        Assert.Equal(TagTestHost.Tags(("env", "prod"), ("added-in-portal", "x"), ("owner", "team-a")), host.TagStore.Tags[Fixture.ShopWeb]);
        Assert.Equal("team-a", host.Index.NodesById[Fixture.ShopWeb].GetTags()["owner"]);
        Assert.Equal("team-a", host.Session.Snapshot!.Resources.Single(r => r.Id == Fixture.ShopWeb).Tags["owner"]);
        Assert.Equal(Fixture.ShopWeb, changed);
    }

    [Fact]
    public async Task Detects_conflicts_without_writing()
    {
        var host = new TagTestHost(TagTestHost.SnapshotWithTags((Fixture.ShopWeb, TagTestHost.Tags(("env", "prod")))));
        host.TagStore.Tags[Fixture.ShopWeb]["env"] = "test";

        var result = await host.Writer.ApplyAsync(Diff(host, Fixture.ShopWeb, new TagChange("env", TagChangeKind.Update, "prod", "staging")), default);

        Assert.Equal(TagWriteOutcome.Conflict, result.Outcome);
        Assert.Equal("test", Assert.Single(result.Conflicts).CurrentValue);
        Assert.Equal(0, host.TagStore.Puts);
    }

    [Fact]
    public async Task Retries_throttling_then_succeeds()
    {
        var host = new TagTestHost();
        host.TagStore.PutFailures.Enqueue(new RequestFailedException(429, "slow down"));
        host.TagStore.PutFailures.Enqueue(new RequestFailedException(429, "slow down"));

        var result = await host.Writer.ApplyAsync(Diff(host, Fixture.ShopWeb, new TagChange("a", TagChangeKind.Add, null, "b")), default);

        Assert.Equal(TagWriteOutcome.Succeeded, result.Outcome);
        Assert.Equal(2, result.Retries);
        Assert.Equal(3, host.TagStore.Puts);
    }

    [Fact]
    public async Task Returns_the_tags_azure_stored()
    {
        var host = new TagTestHost();
        host.TagStore.OnPut = tags => tags["costCenter"] = "added-by-policy";

        var result = await host.Writer.ApplyAsync(Diff(host, Fixture.ShopWeb, new TagChange("a", TagChangeKind.Add, null, "b")), default);

        Assert.Equal("added-by-policy", result.NewTags!["costCenter"]);
        Assert.Equal("added-by-policy", host.Index.NodesById[Fixture.ShopWeb].GetTags()["costCenter"]);
    }

    [Fact]
    public async Task Rejects_unknown_untaggable_and_invalid_targets()
    {
        var host = new TagTestHost();
        var unknown = new ResourceTagDiff("/subscriptions/x/resourcegroups/y/providers/a/b/c", "c", "WebApp", null, "", new Dictionary<string, string>(),
            [new TagChange("a", TagChangeKind.Add, null, "b")]);
        Assert.Equal(TagWriteOutcome.ValidationFailed, (await host.Writer.ApplyAsync(unknown, default)).Outcome);
        Assert.Equal(TagWriteOutcome.ValidationFailed, (await host.Writer.ApplyAsync(Diff(host, Fixture.Uploads, new TagChange("a", TagChangeKind.Add, null, "b")), default)).Outcome);
        Assert.Equal(TagWriteOutcome.ValidationFailed, (await host.Writer.ApplyAsync(Diff(host, Fixture.ShopWeb, new TagChange("a/b", TagChangeKind.Add, null, "b")), default)).Outcome);
        Assert.Equal(0, host.TagStore.Puts);
    }

    [Fact]
    public async Task Batch_failures_are_per_resource()
    {
        var host = new TagTestHost();
        host.TagStore.Tags.Remove(Fixture.OrdersDb);
        var progress = new List<int>();

        var results = await host.Writer.ApplyManyAsync(
            [Diff(host, Fixture.ShopWeb, new TagChange("a", TagChangeKind.Add, null, "1")),
             Diff(host, Fixture.OrdersDb, new TagChange("a", TagChangeKind.Add, null, "1")),
             Diff(host, Fixture.ContosoKv, new TagChange("a", TagChangeKind.Add, null, "1"))],
            new SyncProgress(p => progress.Add(p.Done)), default);

        Assert.Equal([TagWriteOutcome.Succeeded, TagWriteOutcome.NotFound, TagWriteOutcome.Succeeded], results.Select(r => r.Outcome));
        Assert.Equal(3, progress.Count);
    }

    [Fact]
    public async Task Batches_are_capped()
    {
        var host = new TagTestHost(options: new TaggingOptions { MaxResourcesPerBatch = 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Writer.ApplyManyAsync(
            [Diff(host, Fixture.ShopWeb), Diff(host, Fixture.ContosoKv)], null, default));
    }

    [Fact]
    public async Task Disabled_tagging_writes_nothing()
    {
        var host = new TagTestHost(options: new TaggingOptions { Enabled = false });
        var result = await host.Writer.ApplyAsync(Diff(host, Fixture.ShopWeb, new TagChange("a", TagChangeKind.Add, null, "b")), default);
        Assert.Equal(TagWriteOutcome.Failed, result.Outcome);
        Assert.Equal(0, host.TagStore.Gets);
    }

    private sealed class SyncProgress(Action<(int Done, int Total)> report) : IProgress<(int Done, int Total)>
    {
        public void Report((int Done, int Total) value)
        {
            lock (this) report(value);
        }
    }
}

public class TagSyncCoordinatorTests
{
    [Fact]
    public void Updates_resources_groups_subscriptions_and_marks_pending_proposals()
    {
        var host = new TagTestHost();
        var proposal = host.Proposals.Create(host.Session, "t", "r",
            [host.Generator.Generate(host.Index, Fixture.ShopWeb, host.Session.Snapshot!.ScannedUtc)]);

        Assert.True(TagSyncCoordinator.Apply(host.Session, Fixture.ShopWeb, TagTestHost.Tags(("a", "1"))));
        Assert.True(TagSyncCoordinator.Apply(host.Session, InventoryGraphBuilder.ResourceGroupId(Fixture.SubscriptionId, "rg-shop"), TagTestHost.Tags(("b", "2"))));
        Assert.True(TagSyncCoordinator.Apply(host.Session, $"/subscriptions/{Fixture.SubscriptionId}", TagTestHost.Tags(("c", "3"))));
        Assert.False(TagSyncCoordinator.Apply(host.Session, "/subscriptions/unknown", TagTestHost.Tags(("d", "4"))));

        Assert.Equal("1", host.Session.Snapshot!.Resources.Single(r => r.Id == Fixture.ShopWeb).Tags["a"]);
        Assert.Equal("3", host.Session.Snapshot.Subscriptions.Single().Tags["c"]);
        Assert.Equal("2", host.Index.NodesById[InventoryGraphBuilder.ResourceGroupId(Fixture.SubscriptionId, "rg-shop")].GetTags()["b"]);
        Assert.Contains(Fixture.ShopWeb, proposal.BaselineChanged);
    }

    [Fact]
    public void Parallel_applies_do_not_lose_updates()
    {
        var host = new TagTestHost();
        var ids = host.Session.Snapshot!.Resources.Where(r => r.Kind != Models.Inventory.AzureResourceKind.BlobContainer).Select(r => r.Id).ToList();
        Parallel.ForEach(ids, id => TagSyncCoordinator.Apply(host.Session, id, TagTestHost.Tags(("x", id))));
        Assert.All(ids, id => Assert.Equal(id, host.Session.Snapshot!.Resources.Single(r => r.Id == id).Tags["x"]));
    }
}

public class TagProposalTests
{
    private static InventoryTagTools Tools(TagTestHost host, int hardCap = 100) =>
        new(host.Session, host.Index, host.Generator, host.Validator, host.Proposals, host.Options, hardCap);

    [Fact]
    public void Proposals_never_write_and_select_valid_diffs()
    {
        var host = new TagTestHost();
        var summary = Tools(host).ProposeConnectionTags("all", null, null, "Discovery tags");

        Assert.NotNull(summary.ProposalId);
        Assert.True(summary.Resources > 0);
        Assert.Equal(0, host.TagStore.Gets + host.TagStore.Puts);
        var proposal = host.Proposals.Get(host.Session, summary.ProposalId!)!;
        Assert.Equal(TagProposalStatus.Pending, proposal.Status);
        Assert.Equal(proposal.Diffs.Count(d => d.IsValid && !d.IsEmpty), proposal.SelectedResourceIds.Count);
    }

    [Fact]
    public void Connection_proposals_match_the_generator()
    {
        var host = new TagTestHost();
        var summary = Tools(host).ProposeConnectionTags(null, ["shop-web"], null, "r");
        var diff = Assert.Single(host.Proposals.Get(host.Session, summary.ProposalId!)!.Diffs);
        var expected = host.Generator.Generate(host.Index, Fixture.ShopWeb, host.Session.Snapshot!.ScannedUtc);
        Assert.Equal(expected.Changes, diff.Changes);
    }

    [Fact]
    public void Unknown_ambiguous_and_untaggable_resources_are_reported()
    {
        var host = new TagTestHost();
        var summary = Tools(host).ProposeTagChanges(
        [
            new TagChangeRequest("does-not-exist", "a", "b", false),
            new TagChangeRequest("uploads", "a", "b", false),
            new TagChangeRequest("shop", "a", "b", false),
            new TagChangeRequest("shop-web", "owner", "team-a", false),
        ], "r");

        Assert.Contains(summary.Errors, e => e.Contains("does-not-exist"));
        Assert.Contains(summary.Errors, e => e.Contains("'shop'"));
        Assert.Contains(summary.Warnings, w => w.Contains("uploads"));
        Assert.Equal(1, summary.Resources);
        Assert.Equal(1, summary.Adds);
    }

    [Fact]
    public void Scope_and_proposal_size_are_enforced()
    {
        var host = new TagTestHost(options: new TaggingOptions { MaxResourcesPerProposal = 2 });
        var tools = Tools(host);
        Assert.Equal("Rejected", tools.ProposeConnectionTags("all", null, null, "r").Status);
        Assert.Equal("Rejected", tools.ProposeConnectionTags("no-such-scope", null, null, "r").Status);
        Assert.NotNull(tools.ProposeConnectionTags("rg-shop", null, "ContainerApp", "r").ProposalId);
        Assert.DoesNotContain(host.Session.TagProposals.Values, p => p.Diffs.Count > 2);
    }

    [Fact]
    public void Proposals_expire_after_a_new_scan()
    {
        var host = new TagTestHost();
        var id = Tools(host).ProposeConnectionTags("all", null, null, "r").ProposalId!;
        host.Session.Snapshot = host.Session.Snapshot! with { ScannedUtc = host.Session.Snapshot.ScannedUtc.AddHours(1) };
        Assert.Equal(TagProposalStatus.Expired, host.Proposals.Get(host.Session, id)!.Status);
    }

    [Fact]
    public void Read_tools_mask_secret_like_values_and_never_match_them()
    {
        const string sentinel = "S3NTINEL-TAG";
        var host = new TagTestHost(TagTestHost.SnapshotWithTags(
            (Fixture.ShopWeb, TagTestHost.Tags(("env", "prod"), ("note", $"AccountKey={sentinel}"), ("password", sentinel))),
            (Fixture.OrdersFunc, TagTestHost.Tags(("conn-out-SqlDatabase", "contoso-sql/orders"), ("conn-out-SqlDatabase-2", "contoso-sql/customers")))));
        var tools = Tools(host);

        var outputs = new[]
        {
            Json(tools.GetTags("shop-web")),
            Json(tools.FindByTag("note", null, null, 10)),
            Json(tools.FindByTag("note", "S3N", "contains", 10)),
            Json(tools.ListTagNames(null, 50)),
            Json(new Services.AI.GraphTools.InventoryGraphTools(host.Session.Snapshot!, host.Index, 100).GetNode("shop-web")),
        };

        Assert.All(outputs, o => Assert.DoesNotContain(sentinel, o));
        Assert.Equal("prod", tools.GetTags("shop-web")!.Tags["env"]);
        Assert.Equal(InventoryTagTools.MaskedValue, tools.GetTags("shop-web")!.Tags["note"]);
        Assert.Empty(tools.FindByTag("note", "S3N", "contains", 10));
        Assert.Equal(2, tools.FindByTag("conn-out-SqlDatabase", null, null, 10).Length);
        Assert.Single(tools.FindByTag("conn-out-SqlDatabase", "customers", "contains", 10));
    }

    private static string Json(object? o) => JsonSerializer.Serialize(o, GraphJson.Options);
}

public class TagChatSafetyTests
{
    [Fact]
    public void Mcp_allowlist_contains_no_write_capable_tools()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BlazorAzureInventory", "appsettings.json"))) dir = dir.Parent;
        Assert.NotNull(dir);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "BlazorAzureInventory", "appsettings.json")));
        var tools = doc.RootElement.GetProperty("AzureMcp").GetProperty("AllowedTools").EnumerateArray().Select(t => t.GetString()!).ToList();
        Assert.NotEmpty(tools);

        var writeVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "create", "update", "delete", "set", "tag", "tags", "write", "put", "patch", "remove", "add", "assign", "deploy", "start", "stop", "restart" };
        Assert.All(tools, t => Assert.DoesNotContain(Regex.Split(t, "[_-]"), segment => writeVerbs.Contains(segment)));
    }

    [Fact]
    public void Resource_links_render_only_for_known_ids()
    {
        var known = Fixture.OrdersDb;
        var html = MarkdownRenderer.ToSafeHtml($"Uses [orders](res:{known}) and [evil](res:/subscriptions/unknown) and [ext](https://example.com)",
            id => id == known);

        Assert.Contains($"data-resource-id=\"{known}\"", html);
        Assert.Contains("class=\"resource-link\"", html);
        Assert.DoesNotContain("/subscriptions/unknown", html);
        Assert.Contains("evil", html);
        Assert.Contains("href=\"https://example.com\"", html);

        var hostile = MarkdownRenderer.ToSafeHtml("[x](res:<a\"onmouseover=alert(1)>)", _ => true);
        Assert.DoesNotContain("<a\"", hostile);
        Assert.DoesNotContain("\"onmouseover", hostile);

        var unknown = MarkdownRenderer.ToSafeHtml($"[orders](res:{known})");
        Assert.DoesNotContain("<a", unknown);
        Assert.Contains("orders", unknown);
    }
}
