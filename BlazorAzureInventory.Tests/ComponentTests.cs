using BlazorAzureInventory.Components.Graph;
using BlazorAzureInventory.Components.Inventory;
using BlazorAzureInventory.Components.Layout;
using BlazorAzureInventory.Models.Configuration;
using BlazorAzureInventory.Models.Graph;
using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;

namespace BlazorAzureInventory.Tests;

public class ComponentTests : BunitContext
{
    public ComponentTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddRadzenComponents();
        Services.AddScoped<BlazorAzureInventory.Services.Resources.ResourceDialogLauncher>();
        Services.AddSingleton<AntiforgeryStateProvider, FakeAntiforgeryStateProvider>();
    }

    [Fact]
    public void LoginDisplay_shows_connect_button_for_anonymous_users()
    {
        AddAuthorization();
        var cut = Render<LoginDisplay>();
        Assert.Contains("Connect to Azure", cut.Markup);
        Assert.DoesNotContain("Sign out", cut.Markup);
    }

    [Fact]
    public void LoginDisplay_shows_name_and_sign_out_for_signed_in_users()
    {
        AddAuthorization().SetAuthorized("Alex Morgan");
        var cut = Render<LoginDisplay>();
        Assert.Contains("Alex Morgan", cut.Markup);
        Assert.Contains("Sign out", cut.Markup);
    }

    [Fact]
    public void ConfigEntriesGrid_always_masks_values()
    {
        var entries = Fixture.Snapshot().AppConfigs.Single(a => a.AppId == Fixture.ShopWeb).Entries;
        var cut = Render<ConfigEntriesGrid>(p => p.Add(x => x.Entries, entries));

        Assert.Equal(entries.Count, cut.FindAll("span.masked").Count);
        Assert.All(cut.FindAll("span.masked"), e => Assert.Equal("••••••", e.TextContent));
        Assert.DoesNotContain("Password=", cut.Markup);
    }

    [Fact]
    public void NodePropertiesPanel_renders_hostile_labels_as_text()
    {
        const string hostile = "<img src=x onerror=alert(1)>";
        var graph = new GraphDocument
        {
            Nodes =
            [
                new GraphNode { Id = "a", Type = "WebApp", Label = hostile, Data = new() { ["note"] = hostile } },
                new GraphNode { Id = "b", Type = "SqlDatabase", Label = "orders" }
            ],
            Edges =
            [
                new GraphEdge { Id = "e1", Source = "a", Target = "b", Type = EdgeTypes.ConnectsTo,
                    Data = new() { ["references"] = new List<EdgeReference> { new(hostile) } } }
            ]
        };
        var index = new GraphIndex(graph);

        var cut = Render<NodePropertiesPanel>(p => p.Add(x => x.Node, graph.Nodes[0]).Add(x => x.Index, index));

        Assert.Empty(cut.FindAll("img"));
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", cut.Markup);
    }

    [Fact]
    public async Task FilterPanel_category_checkbox_toggles_all_types()
    {
        var filter = new GraphFilterState { NodeTypes = ["WebApp", "SqlServer"] };
        var cut = RenderFilterPanel(filter, [], out var changes);

        Assert.Null(CategoryBox(cut, NodeCategories.Apps).Instance.Value);
        await cut.InvokeAsync(() => CategoryBox(cut, NodeCategories.Apps).Instance.ValueChanged.InvokeAsync(true));

        Assert.True(CategoryBox(cut, NodeCategories.Apps).Instance.Value);
        Assert.Contains("WebAppSlot", filter.NodeTypes);
        Assert.Contains("ContainerApp", filter.NodeTypes);
        Assert.Contains("SqlServer", filter.NodeTypes);
        Assert.Equal(1, changes.Count);

        await cut.InvokeAsync(() => CategoryBox(cut, NodeCategories.Apps).Instance.ValueChanged.InvokeAsync(false));

        Assert.False(CategoryBox(cut, NodeCategories.Apps).Instance.Value);
        Assert.DoesNotContain("WebApp", filter.NodeTypes);
        Assert.Contains("SqlServer", filter.NodeTypes);
    }

    [Fact]
    public async Task FilterPanel_category_expands_to_individual_types()
    {
        var cut = RenderFilterPanel(new GraphFilterState { NodeTypes = ["WebApp"] }, [], out _);
        Assert.Empty(cut.FindAll(".graph-type-row"));

        await cut.Find("button[aria-label='Expand Apps']").ClickAsync();

        Assert.Equal(4, cut.FindAll(".graph-type-row").Count);
        Assert.Equal("true", cut.Find("button[aria-label='Collapse Apps']").GetAttribute("aria-expanded"));
    }

    [Fact]
    public void FilterPanel_does_not_rebuild_options_when_nodes_unchanged()
    {
        var nodes = new List<GraphNode> { new() { Id = "a", Type = "WebApp", Label = "contoso-web" } };
        var cut = RenderFilterPanel(new GraphFilterState(), nodes, out _);
        var first = cut.Instance.NodeOptionsInstance;

        cut.Render(p => p.Add(x => x.Filter, new GraphFilterState { HideExternal = true }));
        Assert.Same(first, cut.Instance.NodeOptionsInstance);

        cut.Render(p => p.Add(x => x.Nodes, new List<GraphNode>(nodes)));
        Assert.NotSame(first, cut.Instance.NodeOptionsInstance);
    }

    [Fact]
    public void FilterPanel_refreshes_tag_names_when_tags_version_changes()
    {
        var node = new GraphNode
        {
            Id = "a", Type = "WebApp", Label = "contoso-web",
            Data = { [TagKeys.Tags] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["old-key"] = "x" } }
        };
        var cut = RenderFilterPanel(new GraphFilterState(), [node], out _);
        var options = cut.Instance.NodeOptionsInstance;
        Assert.Equal(["old-key"], cut.Instance.TagNamesInstance);

        node.Data[TagKeys.Tags] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["new-key"] = "y" };
        cut.Render(p => p.Add(x => x.Filter, new GraphFilterState()));
        Assert.Equal(["old-key"], cut.Instance.TagNamesInstance);

        cut.Render(p => p.Add(x => x.TagsVersion, 1));

        Assert.Equal(["new-key"], cut.Instance.TagNamesInstance);
        Assert.Same(options, cut.Instance.NodeOptionsInstance);
    }

    [Fact]
    public async Task FilterPanel_active_chips_show_migrated_toggle_and_remove_it()
    {
        var filter = new GraphFilterState { NodeTypes = ["WebApp"], HideEmulator = false };
        var cut = RenderFilterPanel(filter, [], out var changes);
        Assert.Contains("Only connected", cut.Find(".graph-chips").TextContent);

        await cut.Find("button[aria-label='Remove filter Only connected']").ClickAsync();

        Assert.False(filter.OnlyConnected);
        Assert.Equal(1, changes.Count);
        Assert.Empty(cut.FindAll(".graph-chip"));
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Reset all", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FilterPanel_reset_all_restores_defaults()
    {
        var filter = new GraphFilterState
        {
            NodeTypes = ["WebApp"],
            OnlyConnected = false,
            HideExternal = true,
            ShowChildResources = true,
            Subscriptions = ["sub"],
            TagName = "name",
            TagValue = "x"
        };
        var cut = RenderFilterPanel(filter, [], out var changes);

        await cut.FindAll("button").Single(b => b.TextContent.Contains("Reset all", StringComparison.Ordinal)).ClickAsync();

        Assert.True(filter.OnlyConnected);
        Assert.True(filter.HideEmulator);
        Assert.False(filter.HideExternal);
        Assert.False(filter.ShowChildResources);
        Assert.Empty(filter.Subscriptions);
        Assert.Null(filter.TagName);
        Assert.Null(filter.TagValue);
        Assert.Contains("SqlServer", filter.NodeTypes);
        Assert.DoesNotContain("Tenant", filter.NodeTypes);
        Assert.Equal(1, changes.Count);
    }

    [Fact]
    public async Task FilterPanel_debounces_tag_value_changes()
    {
        var filter = new GraphFilterState();
        var cut = RenderFilterPanel(filter, [], out var changes);
        var box = cut.FindComponent<RadzenTextBox>().Instance;

        var first = cut.InvokeAsync(() => box.ValueChanged.InvokeAsync("c"));
        var second = cut.InvokeAsync(() => box.ValueChanged.InvokeAsync("co"));
        await Task.WhenAll(first, second);

        Assert.Equal("co", filter.TagValue);
        Assert.Equal(1, changes.Count);
    }

    [Fact]
    public async Task NodePropertiesPanel_focus_and_expand_raise_callbacks()
    {
        var graph = new GraphDocument
        {
            Nodes = [new GraphNode { Id = "a", Type = "WebApp", Label = "app" }, new GraphNode { Id = "b", Type = "SqlDatabase", Label = "db" }],
            Edges = [new GraphEdge { Id = "e1", Source = "a", Target = "b", Type = EdgeTypes.ConnectsTo }]
        };
        var index = new GraphIndex(graph);
        var focused = new List<string>();
        var expanded = new List<string>();

        var overview = Render<NodePropertiesPanel>(p => p
            .Add(x => x.Node, graph.Nodes[0]).Add(x => x.Index, index)
            .Add(x => x.OnFocus, (string id) => focused.Add(id)));
        await overview.FindAll("button").Single(b => b.TextContent.Contains("Focus", StringComparison.Ordinal)).ClickAsync();
        Assert.Equal(["a"], focused);
        Assert.DoesNotContain(overview.FindAll("button"), b => b.TextContent.Contains("Expand", StringComparison.Ordinal));

        var focus = Render<NodePropertiesPanel>(p => p
            .Add(x => x.Node, graph.Nodes[1]).Add(x => x.Index, index)
            .Add(x => x.IsFocusMode, true).Add(x => x.FocusId, "a").Add(x => x.CanExpand, true)
            .Add(x => x.OnExpand, (string id) => expanded.Add(id)));
        await focus.FindAll("button").Single(b => b.TextContent.Contains("Expand", StringComparison.Ordinal)).ClickAsync();
        Assert.Equal(["b"], expanded);
    }

    [Fact]
    public void NodePropertiesPanel_hides_talks_to_for_non_centre_node()
    {
        var (graph, index, dependencies) = FocusPanelGraph();

        var cut = Render<NodePropertiesPanel>(p => p
            .Add(x => x.Node, graph.Nodes[1]).Add(x => x.Index, index)
            .Add(x => x.IsFocusMode, true).Add(x => x.FocusId, "a")
            .Add(x => x.Dependencies, dependencies));

        Assert.DoesNotContain("Talks to", cut.Markup);
        Assert.DoesNotContain("No connections detected.", cut.Markup);

        cut.Render(p => p.Add(x => x.Dependencies, []));
        Assert.DoesNotContain("No connections detected.", cut.Markup);
    }

    [Fact]
    public void NodePropertiesPanel_matches_focus_centre_ignoring_case()
    {
        var (graph, index, dependencies) = FocusPanelGraph();

        var cut = Render<NodePropertiesPanel>(p => p
            .Add(x => x.Node, graph.Nodes[0]).Add(x => x.Index, index)
            .Add(x => x.IsFocusMode, true).Add(x => x.FocusId, "A").Add(x => x.CanExpand, true)
            .Add(x => x.Dependencies, dependencies));

        Assert.Contains("Talks to (1)", cut.Markup);
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Expand", StringComparison.Ordinal));

        cut.Render(p => p.Add(x => x.Dependencies, []));
        Assert.Contains("No connections detected.", cut.Markup);
    }

    private static (GraphDocument Graph, GraphIndex Index, IReadOnlyList<DependencyGroup> Dependencies) FocusPanelGraph()
    {
        var graph = new GraphDocument
        {
            Nodes = [new GraphNode { Id = "a", Type = "WebApp", Label = "app" }, new GraphNode { Id = "b", Type = "SqlDatabase", Label = "db" }],
            Edges = [new GraphEdge { Id = "e1", Source = "a", Target = "b", Type = EdgeTypes.ConnectsTo }]
        };
        var index = new GraphIndex(graph);
        var dependencies = new BlazorAzureInventory.Services.Graph.GraphViewService(
                Microsoft.Extensions.Options.Options.Create(new BlazorAzureInventory.Models.Options.GraphViewOptions()))
            .SummarizeDependencies(index, "a");
        return (graph, index, dependencies);
    }

    [Fact]
    public async Task NodePropertiesPanel_aggregate_card_lists_members_and_focuses_on_click()
    {
        var focused = new List<string>();
        var members = new List<DependencyGroup>
        {
            new(NodeCategories.Apps,
            [
                new DependencyItem("web-61", "contoso-web-61", "WebApp", EdgeTypes.ConnectsTo, "SendGrid:ApiKeyName", "High"),
                new DependencyItem("web-62", "contoso-web-62", "WebApp", EdgeTypes.ConnectsTo, "SendGrid:ApiKeyName", "High")
            ])
        };

        var cut = Render<NodePropertiesPanel>(p => p
            .Add(x => x.Index, new GraphIndex(new GraphDocument()))
            .Add(x => x.IsFocusMode, true).Add(x => x.FocusId, "sendgrid").Add(x => x.CanExpand, true)
            .Add(x => x.AggregateLabel, "+2 more apps").Add(x => x.AggregateMembers, members)
            .Add(x => x.OnFocus, (string id) => focused.Add(id)));

        Assert.Contains("+2 more apps", cut.Markup);
        Assert.Contains("Not shown in this Focus view.", cut.Markup);
        Assert.Contains("Hidden resources (2)", cut.Markup);
        Assert.Contains("contoso-web-62", cut.Markup);
        Assert.DoesNotContain(cut.FindAll("button"), b =>
            b.TextContent.Contains("Expand", StringComparison.Ordinal)
            || b.TextContent.Contains("Open details", StringComparison.Ordinal)
            || b.TextContent.Contains("Focus", StringComparison.Ordinal));

        await cut.FindAll("button.graph-dependency-link").Single(b => b.TextContent == "contoso-web-62").ClickAsync();
        Assert.Equal(["web-62"], focused);
    }

    [Fact]
    public async Task CopyLinkDialog_focuses_link_field()
    {
        const string url = "http://localhost/graph?focus=contoso-web";
        var host = Render<RadzenDialog>();
        var dialogs = Services.GetRequiredService<DialogService>();
        Task<dynamic?>? open = null;

        // A statement lambda, so InvokeAsync does not await the dialog, which completes only when it closes.
        await host.InvokeAsync(() => { open = dialogs.OpenAsync<CopyLinkDialog>("Copy graph link",
            new Dictionary<string, object?> { [nameof(CopyLinkDialog.Url)] = url },
            new DialogOptions { AutoFocusFirstElement = false }); });
        host.WaitForAssertion(() => Assert.Equal(url, host.Find("input[aria-label='Graph link']").GetAttribute("value")));

        var focused = Assert.IsType<Microsoft.AspNetCore.Components.ElementReference>(JSInterop.VerifyFocusAsyncInvoke().Arguments[0]);
        var dialog = host.FindComponent<CopyLinkDialog>();
        Assert.Contains($"blazor:elementReference=\"{focused.Id}\"", dialog.Markup);
        Assert.Contains("readonly", host.Find("input[aria-label='Graph link']").OuterHtml);

        var dialogRoot = host.Find("[role='dialog']");
        Assert.Equal("true", dialogRoot.GetAttribute("aria-modal"));
        Assert.NotNull(dialogRoot.QuerySelector("input[aria-label='Graph link']"));

        await host.FindAll("button").Single(b => b.TextContent.Trim() == "Close").ClickAsync();
        await open!;
        Assert.Empty(host.FindComponents<CopyLinkDialog>());
    }

    [Fact]
    public void NodePropertiesPanel_lists_every_merged_edge()
    {
        var graph = new GraphDocument
        {
            Nodes =
            [
                new GraphNode { Id = "a", Type = "WebApp", Label = "app" },
                new GraphNode { Id = "db1", Type = "SqlDatabase", Label = "orders" },
                new GraphNode { Id = "db2", Type = "SqlDatabase", Label = "billing" }
            ],
            Edges =
            [
                new GraphEdge { Id = "e1", Source = "a", Target = "db1", Type = EdgeTypes.ConnectsTo },
                new GraphEdge { Id = "e2", Source = "a", Target = "db2", Type = EdgeTypes.ConnectsTo }
            ]
        };

        var cut = Render<NodePropertiesPanel>(p => p
            .Add(x => x.Edge, graph.Edges[0]).Add(x => x.Edges, graph.Edges).Add(x => x.Index, new GraphIndex(graph)));

        Assert.Contains("Underlying connections (2)", cut.Markup);
        Assert.Contains("orders", cut.Markup);
        Assert.Contains("billing", cut.Markup);
    }

    [Fact]
    public async Task DependencySummaryList_renders_hostile_labels_as_text()
    {
        const string hostile = "<script>alert(1)</script>\"><img src=x onerror=alert(1)>";
        var selected = new List<string>();
        var groups = new List<DependencyGroup>
        {
            new("Data", [new DependencyItem("db", hostile, "SqlDatabase", EdgeTypes.ConnectsTo, hostile, "high")])
        };

        var cut = Render<DependencySummaryList>(p => p
            .Add(x => x.Groups, groups).Add(x => x.OnSelect, (string id) => selected.Add(id)));

        Assert.Empty(cut.FindAll("img"));
        Assert.Empty(cut.FindAll("script"));
        Assert.Contains("&lt;script&gt;", cut.Markup);
        Assert.Contains("Talks to (1)", cut.Markup);
        await cut.Find("button.graph-dependency-link").ClickAsync();
        Assert.Equal(["db"], selected);
    }

    [Fact]
    public void DependencySummaryList_renders_nothing_without_groups()
    {
        var cut = Render<DependencySummaryList>(p => p.Add(x => x.Groups, []));
        Assert.Empty(cut.Markup.Trim());
    }

    private IRenderedComponent<GraphFilterPanel> RenderFilterPanel(GraphFilterState filter, IReadOnlyList<GraphNode> nodes, out FilterCounter counter)
    {
        var calls = new FilterCounter();
        counter = calls;
        var facets = new GraphFacets(new Dictionary<string, int> { ["WebApp"] = 3 }, [], []);
        return Render<GraphFilterPanel>(p => p
            .Add(x => x.Facets, facets).Add(x => x.Filter, filter).Add(x => x.Nodes, nodes)
            .Add(x => x.DebounceMs, 250)
            .Add(x => x.FilterChanged, () => { calls.Count++; }));
    }

    private static IRenderedComponent<RadzenCheckBox<bool?>> CategoryBox(IRenderedComponent<GraphFilterPanel> cut, string category) =>
        cut.FindComponents<RadzenCheckBox<bool?>>().Single(c => c.Instance.Name == $"category-{category}");

    private sealed class FilterCounter
    {
        public int Count;
    }

    private sealed class FakeAntiforgeryStateProvider : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => new("test-token", "__RequestVerificationToken");
    }
}
