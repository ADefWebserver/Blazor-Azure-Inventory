using BlazorAzureInventory.Components.Resources;
using BlazorAzureInventory.Models.Graph;
using BlazorAzureInventory.Models.Options;
using BlazorAzureInventory.Models.Tags;
using BlazorAzureInventory.Services.AI.TagTools;
using BlazorAzureInventory.Services.Auth;
using BlazorAzureInventory.Services.Graph;
using BlazorAzureInventory.Services.Resources;
using BlazorAzureInventory.Services.Tags;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Radzen;

namespace BlazorAzureInventory.Tests;

public class TagComponentTests : BunitContext
{
    private readonly TagTestHost _host;
    private readonly FakePermissions _permissions = new();

    public TagComponentTests()
    {
        _host = new TagTestHost(TagTestHost.SnapshotWithTags((Fixture.ShopWeb, TagTestHost.Tags(("env", "prod"), ("note", "<img src=x onerror=alert(1)>")))));
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddRadzenComponents();
        Services.AddSingleton(_host.Store);
        Services.AddSingleton(_host.UserContext);
        Services.AddSingleton(_host.Validator);
        Services.AddSingleton(_host.Generator);
        Services.AddSingleton(_host.Proposals);
        Services.AddSingleton(_host.Sync);
        Services.AddSingleton(_host.Writer);
        Services.AddSingleton<ITagPermissionChecker>(_permissions);
        Services.AddSingleton<IOptions<TaggingOptions>>(Options.Create(_host.Options));
        Services.AddScoped<ResourceInspector>();
        Services.AddScoped<ResourceDialogLauncher>();
    }

    private ResourceDetails Details(string id) =>
        ResourceInspector.Build(_host.Session.Snapshot!, _host.Index, id)!;

    [Fact]
    public void Dialog_shows_all_tabs_and_encodes_tag_values()
    {
        var cut = Render<ResourcePropertiesDialog>(p => p.Add(x => x.ResourceId, Fixture.ShopWeb).Add(x => x.InitialTab, ResourceDialogLauncher.TagsTab));

        cut.WaitForAssertion(() => Assert.Contains("Tags (2)", cut.Markup));
        Assert.Contains("Overview", cut.Markup);
        Assert.Contains("Properties (", cut.Markup);
        Assert.Contains("Connections (", cut.Markup);
        Assert.Empty(cut.FindAll("img"));
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", cut.Markup);
    }

    [Fact]
    public void Dialog_rejects_ids_that_are_not_in_the_scan()
    {
        var cut = Render<ResourcePropertiesDialog>(p => p.Add(x => x.ResourceId, "/subscriptions/x/resourcegroups/y/providers/evil/z"));
        cut.WaitForAssertion(() => Assert.Contains("not in your last scan", cut.Markup));
    }

    [Fact]
    public void Blob_containers_and_readers_get_a_read_only_tags_tab()
    {
        var container = Render<TagsTab>(p => p.Add(x => x.Details, Details(Fixture.Uploads)));
        Assert.Contains("do not support Azure tags", container.Markup);
        Assert.DoesNotContain("Add tag", container.Markup);

        _permissions.Allowed = false;
        var reader = Render<TagsTab>(p => p.Add(x => x.Details, Details(Fixture.ShopWeb)));
        reader.WaitForAssertion(() => Assert.Contains("Tag Contributor", reader.Markup));
        Assert.DoesNotContain("Add tag", reader.Markup);
    }

    [Fact]
    public void Editing_validates_reviews_and_saves()
    {
        var cut = Render<TagsTab>(p => p.Add(x => x.Details, Details(Fixture.ShopWeb)));
        cut.WaitForAssertion(() => Assert.Contains("Add tag", cut.Markup));

        cut.FindAll("button").First(b => b.TextContent.Contains("Add tag")).Click();
        var inputs = cut.FindAll("input[aria-label='Tag name']");
        inputs[^1].Input("bad/name");
        cut.WaitForAssertion(() => Assert.Contains("cannot contain", cut.Markup));

        cut.FindAll("input[aria-label='Tag name']")[^1].Input("owner");
        cut.FindAll("input[aria-label='Tag value']")[^1].Input("team-a");
        cut.FindAll("button").First(b => b.TextContent.Contains("Review changes")).Click();
        Assert.Contains("Review changes (1)", cut.Markup);

        cut.FindAll("button").First(b => b.TextContent.Trim().EndsWith("Save", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Equal("team-a", _host.TagStore.Tags[Fixture.ShopWeb]["owner"]));
        Assert.Equal("prod", _host.TagStore.Tags[Fixture.ShopWeb]["env"]);
        Assert.Equal("team-a", _host.Index.NodesById[Fixture.ShopWeb].GetTags()["owner"]);
    }

    [Fact]
    public void Generate_connection_tags_fills_pending_edits_without_writing()
    {
        var cut = Render<TagsTab>(p => p.Add(x => x.Details, Details(Fixture.ShopWeb)));
        cut.WaitForAssertion(() => Assert.Contains("Generate connection tags", cut.Markup));

        cut.FindAll("button").First(b => b.TextContent.Contains("Generate connection tags")).Click();

        Assert.Contains("conn-out-SqlDatabase", cut.Markup);
        Assert.Contains("Generated", cut.Markup);
        Assert.Equal(0, _host.TagStore.Puts);
    }

    [Fact]
    public void Proposal_card_applies_only_selected_resources()
    {
        var tools = new InventoryTagTools(_host.Session, _host.Index, _host.Generator, _host.Validator, _host.Proposals, _host.Options, 100);
        var summary = tools.ProposeConnectionTags(null, ["shop-web", "orders-func"], null, "Discovery");
        var proposal = _host.Proposals.Get(_host.Session, summary.ProposalId!)!;
        proposal.SelectedResourceIds.Remove(Fixture.OrdersFunc);

        var cut = Render<TagProposalCard>(p => p.Add(x => x.ProposalId, proposal.Id));
        Assert.Contains("Discovery", cut.Markup);
        cut.FindAll("button").First(b => b.TextContent.Contains("Apply")).Click();

        cut.WaitForAssertion(() => Assert.Equal(TagProposalStatus.PartiallyApplied, proposal.Status));
        Assert.True(_host.TagStore.Tags[Fixture.ShopWeb].ContainsKey("conn-out-SqlDatabase"));
        Assert.False(_host.TagStore.Tags[Fixture.OrdersFunc].ContainsKey("conn-out-SqlDatabase"));
        Assert.Equal(1, proposal.Succeeded);
    }

    private sealed class FakePermissions : ITagPermissionChecker
    {
        public bool Allowed { get; set; } = true;
        public Task<bool> CanWriteTagsAsync(string resourceId, string? tenantId, CancellationToken ct) => Task.FromResult(Allowed);
    }
}
