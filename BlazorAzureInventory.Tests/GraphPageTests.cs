using System.Security.Claims;
using BlazorAzureInventory.Components.Pages;
using BlazorAzureInventory.Components.Graph;
using BlazorAzureInventory.Models.Graph;
using BlazorAzureInventory.Models.Inventory;
using BlazorAzureInventory.Models.Options;
using BlazorAzureInventory.Services.Analysis;
using BlazorAzureInventory.Services.Auth;
using BlazorAzureInventory.Services.Azure;
using BlazorAzureInventory.Services.Changes;
using BlazorAzureInventory.Services.Graph;
using BlazorAzureInventory.Services.Persistence;
using BlazorAzureInventory.Services.Reports;
using BlazorAzureInventory.Services.Resources;
using BlazorAzureInventory.Services.Scan;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Radzen;

namespace BlazorAzureInventory.Tests;

public class GraphPageTests : BunitContext
{
    private const string AppA = "app-a";
    private const string Db = "db";
    private const string AppB = "app-b";
    private const string Queue = "queue";
    private const string AppC = "app-c";
    private TestGraphPageHost? _host;

    public GraphPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddRadzenComponents();
        Services.AddScoped<ResourceDialogLauncher>();
        Services.AddSingleton<AntiforgeryStateProvider, FakeAntiforgeryStateProvider>();
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>(new TestWebHostEnvironment());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Services is IAsyncDisposable asyncServices)
            asyncServices.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }

    [Fact]
    public void GraphPage_focus_query_builds_focus_payload_without_waiting_for_overview_layout()
    {
        var host = ConfigurePage();

        var cut = RenderPage($"?focus={AppA}");
        var canvas = cut.FindComponent<Graph>().FindComponent<GraphCanvas>().Instance;

        Assert.Equal(GraphViewMode.Focus, canvas.View!.Mode);
        Assert.Contains(Db, canvas.View.NodeIds);
        Assert.Equal(0, host.Layouts.ComputeCount);
    }

    [Fact]
    public void GraphPage_focus_and_expand_query_passes_expanded_ids_in_order()
    {
        var cut = RenderPage($"?focus={AppA}&expand={Db}&expand={Queue}");
        var page = cut.FindComponent<Graph>();
        var canvas = page.FindComponent<GraphCanvas>().Instance;

        Assert.Equal(GraphViewMode.Focus, canvas.View!.Mode);
        Assert.Contains(AppC, canvas.View.NodeIds);
        Assert.Contains("App A", page.Markup);
        Assert.Contains("Database", page.Markup);
        Assert.Contains("Queue", page.Markup);
    }

    [Fact]
    public async Task GraphPage_expand_click_navigates_with_appended_expand()
    {
        ConfigurePage();
        var cut = RenderPage($"?focus={AppA}");
        var page = cut.FindComponent<Graph>();
        var canvas = page.FindComponent<GraphCanvas>().Instance;
        var dbIndex = canvas.View!.NodeIds.ToList().FindIndex(id => id == Db);

        await cut.InvokeAsync(() => page.FindComponent<GraphCanvas>().Instance.OnNodeSelected.InvokeAsync(dbIndex));
        await page.FindAll("button").Single(button => button.TextContent.Contains("Expand", StringComparison.Ordinal)).ClickAsync();

        Assert.Contains("focus=app-a", Services.GetRequiredService<NavigationManager>().Uri);
        Assert.Contains("expand=db", Services.GetRequiredService<NavigationManager>().Uri);
        Assert.StartsWith("http://localhost/graph?", Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Fact]
    public async Task GraphPage_breadcrumb_removes_later_expansions()
    {
        var cut = RenderPage($"?focus={AppA}&expand={Db}&expand={Queue}");

        Assert.Contains("Overview", cut.Find("nav[aria-label='Graph navigation']").TextContent);
        await cut.FindAll("nav[aria-label='Graph navigation'] button")
            .Single(button => button.TextContent == "Database")
            .ClickAsync();

        var uri = Services.GetRequiredService<NavigationManager>().Uri;
        Assert.Contains($"focus={AppA}", uri);
        Assert.Contains($"expand={Db}", uri);
        Assert.DoesNotContain($"expand={Queue}", uri);
    }

    [Fact]
    public async Task GraphPage_copy_link_opens_dialog_when_clipboard_is_unavailable()
    {
        var cut = RenderPage($"?focus={AppA}&expand={Db}");
        var dialogHost = RenderDialogHost();
        var uri = Services.GetRequiredService<NavigationManager>().Uri;

        // CopyLinkAsync awaits the dialog, which completes only when it closes, so the click is awaited last.
        var click = cut.FindAll("button").Single(button => button.TextContent.Contains("Copy link", StringComparison.Ordinal)).ClickAsync();
        dialogHost.WaitForAssertion(() => Assert.Contains("Copy graph link", dialogHost.Markup));

        Assert.Equal(uri, dialogHost.Find("input[aria-label='Graph link']").GetAttribute("value"));
        Assert.Empty(cut.FindAll(".graph-copy-backdrop"));
        Assert.Empty(cut.FindAll("input[aria-label='Graph link']"));

        await dialogHost.FindAll("button").Single(button => button.TextContent.Trim() == "Close").ClickAsync();
        await click;
        dialogHost.WaitForAssertion(() => Assert.DoesNotContain("Copy graph link", dialogHost.Markup));
    }

    [Fact]
    public void GraphPage_navigating_to_graph_without_query_renders_overview()
    {
        var cut = RenderPage($"?focus={AppA}");

        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/graph");

        cut.WaitForAssertion(() =>
            Assert.Equal(GraphViewMode.Overview, cut.FindComponent<Graph>().FindComponent<GraphCanvas>().Instance.View!.Mode));
    }

    [Fact]
    public void GraphPage_unknown_focus_replaces_url_with_overview()
    {
        var cut = RenderPage("?focus=missing");
        var navigation = Services.GetRequiredService<NavigationManager>();

        cut.WaitForAssertion(() =>
            Assert.Equal(GraphViewMode.Overview, cut.FindComponent<Graph>().FindComponent<GraphCanvas>().Instance.View!.Mode));
        Assert.DoesNotContain("focus=", navigation.Uri);
    }

    [Fact]
    public void GraphPage_unknown_expansion_is_removed_from_url()
    {
        var cut = RenderPage($"?focus={AppA}&expand=missing");
        var navigation = Services.GetRequiredService<NavigationManager>();

        cut.WaitForAssertion(() =>
            Assert.Equal(GraphViewMode.Focus, cut.FindComponent<Graph>().FindComponent<GraphCanvas>().Instance.View!.Mode));
        Assert.Contains($"focus={AppA}", navigation.Uri);
        Assert.DoesNotContain("expand=", navigation.Uri);
    }

    [Fact]
    public async Task GraphPage_count_caption_reports_visible_of_total_nodes()
    {
        var cut = RenderPage($"?focus={AppA}");
        var canvas = cut.FindComponent<Graph>().FindComponent<GraphCanvas>().Instance;
        var total = canvas.View!.TotalNodes;

        await cut.InvokeAsync(() => canvas.OnVisibleCounts(2, 1));

        var caption = cut.Find(".graph-count");
        Assert.Equal($"Showing 2 of {total} nodes", caption.TextContent.Trim());
        Assert.Equal("polite", caption.GetAttribute("aria-live"));
        Assert.Contains("Legend", cut.Find("details.graph-legend summary").TextContent);
    }

    [Fact]
    public async Task GraphPage_show_child_resources_toggle_rebuilds_overview_once()
    {
        var host = ConfigurePage();
        var cut = RenderPage(string.Empty);
        var page = cut.FindComponent<Graph>();
        cut.WaitForAssertion(() => Assert.NotNull(page.FindComponent<GraphCanvas>().Instance.View));
        var before = page.FindComponent<GraphCanvas>().Instance.View;

        await cut.InvokeAsync(() => page.FindComponent<GraphFilterPanel>().Instance.FilterChanged.InvokeAsync());
        Assert.Same(before, page.FindComponent<GraphCanvas>().Instance.View);

        await cut.InvokeAsync(() =>
        {
            page.FindComponent<GraphFilterPanel>().Instance.Filter.ShowChildResources = true;
            return page.FindComponent<GraphFilterPanel>().Instance.FilterChanged.InvokeAsync();
        });

        Assert.NotSame(before, page.FindComponent<GraphCanvas>().Instance.View);
        Assert.Equal(2, host.Layouts.ComputeCount);
    }

    [Fact]
    public async Task GraphPage_stale_overview_layout_does_not_replace_newer_focus()
    {
        var (held, layout) = await HoldOverviewLayoutAsync(DefaultGraph(), showChildResources: false);
        var cut = RenderPage(string.Empty);

        Services.GetRequiredService<NavigationManager>().NavigateTo($"http://localhost/graph?focus={AppA}");
        cut.WaitForAssertion(() => Assert.Equal(GraphViewMode.Focus, CanvasView(cut)?.Mode));
        var focusView = CanvasView(cut);
        await CompleteLayoutAsync(cut, held, layout);

        Assert.Same(focusView, CanvasView(cut));
        Assert.Equal(GraphViewMode.Focus, CanvasView(cut)!.Mode);
    }

    [Fact]
    public async Task GraphPage_stale_overview_does_not_rewrite_focus_url()
    {
        var (held, layout) = await HoldOverviewLayoutAsync(DefaultGraph(), showChildResources: false);
        var cut = RenderPage(string.Empty);
        var navigation = Services.GetRequiredService<NavigationManager>();

        navigation.NavigateTo($"http://localhost/graph?focus={AppA}");
        cut.WaitForAssertion(() => Assert.Equal(GraphViewMode.Focus, CanvasView(cut)?.Mode));
        await CompleteLayoutAsync(cut, held, layout);

        Assert.Contains($"focus={AppA}", navigation.Uri);
    }

    [Fact]
    public async Task GraphPage_child_resources_on_then_off_ends_collapsed()
    {
        var (held, layout) = await HoldOverviewLayoutAsync(GraphWithSqlServer(), showChildResources: true);
        // Shifted so a view built from the stale "on" layout is told apart from one built from the collapsed layout.
        var staleLayout = layout with
        {
            Positions = layout.Positions.ToDictionary(p => p.Key, p => (p.Value.X + 1000f, p.Value.Y + 1000f))
        };
        var cut = RenderPage(string.Empty);
        cut.WaitForAssertion(() => Assert.Equal(GraphViewMode.Overview, CanvasView(cut)?.Mode));
        var collapsedPositions = CanvasView(cut)!.Nodes.Select(n => (n.X, n.Y)).ToList();
        var filterPanel = cut.FindComponent<Graph>().FindComponent<GraphFilterPanel>().Instance;

        var turnOn = cut.InvokeAsync(() =>
        {
            filterPanel.Filter.ShowChildResources = true;
            return filterPanel.FilterChanged.InvokeAsync();
        });
        await cut.InvokeAsync(() =>
        {
            filterPanel.Filter.ShowChildResources = false;
            return filterPanel.FilterChanged.InvokeAsync();
        });
        await CompleteLayoutAsync(cut, held, staleLayout);
        await turnOn;

        var view = CanvasView(cut)!;
        Assert.Equal(GraphViewMode.Overview, view.Mode);
        Assert.Contains(SqlServer, view.NodeIds);
        Assert.DoesNotContain(Db, view.NodeIds);
        Assert.Equal(collapsedPositions, view.Nodes.Select(n => (n.X, n.Y)).ToList());
    }

    [Fact]
    public async Task GraphPage_stale_scope_focus_does_not_change_filters()
    {
        var savedFilter = new GraphFilterState
        {
            FilterVersion = GraphFilterState.CurrentFilterVersion,
            Subscriptions = ["sub-saved"]
        };
        var (held, layout) = await HoldOverviewLayoutAsync(GraphWithResourceGroup(), showChildResources: false, savedFilter);
        var cut = RenderPage($"?focus={ResourceGroupId}");
        var navigation = Services.GetRequiredService<NavigationManager>();

        navigation.NavigateTo($"http://localhost/graph?focus={AppA}");
        cut.WaitForAssertion(() => Assert.Equal(GraphViewMode.Focus, CanvasView(cut)?.Mode));
        await CompleteLayoutAsync(cut, held, layout);

        var filter = cut.FindComponent<Graph>().FindComponent<GraphFilterPanel>().Instance.Filter;
        Assert.Equal(["sub-saved"], filter.Subscriptions);
        Assert.Empty(filter.ResourceGroups);
        Assert.Contains($"focus={AppA}", navigation.Uri);
        Assert.Equal(GraphViewMode.Focus, CanvasView(cut)!.Mode);
    }

    [Fact]
    public void GraphPage_focus_selects_centre_and_shows_talks_to()
    {
        var cut = RenderPage($"?focus={AppA}");

        var panel = cut.FindComponent<Graph>().FindComponent<NodePropertiesPanel>();
        Assert.Equal(AppA, panel.Instance.Node?.Id);
        Assert.Contains("App A", panel.Markup);
        Assert.Contains("Talks to (1)", panel.Markup);
        Assert.DoesNotContain(panel.FindAll("button"), b => b.TextContent.Contains("Expand", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GraphPage_selecting_aggregate_lists_hidden_resources()
    {
        var cut = RenderPage($"?focus={Hub}", GraphWithHub(), new GraphViewOptions { FocusMaxNeighbours = 2 });
        var page = cut.FindComponent<Graph>();
        var aggregateIndex = Assert.Single(CanvasView(cut)!.Aggregates).Key;

        await cut.InvokeAsync(() => page.FindComponent<GraphCanvas>().Instance.OnNodeSelected.InvokeAsync(aggregateIndex));

        var panel = page.FindComponent<NodePropertiesPanel>();
        Assert.Contains("+2 more apps", panel.Markup);
        Assert.Contains("Hidden resources (2)", panel.Markup);
        Assert.Contains("Hub app 2", panel.Markup);
        Assert.Contains("Hub app 3", panel.Markup);
        Assert.DoesNotContain(panel.FindAll("button"), b => b.TextContent.Contains("Open details", StringComparison.Ordinal));

        await panel.FindAll("button.graph-dependency-link").Single(b => b.TextContent == "Hub app 3").ClickAsync();

        Assert.Contains("focus=hub-app-3", Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Fact]
    public async Task GraphPage_selecting_node_after_aggregate_clears_aggregate_card()
    {
        var cut = RenderPage($"?focus={Hub}", GraphWithHub(), new GraphViewOptions { FocusMaxNeighbours = 2 });
        var page = cut.FindComponent<Graph>();
        var canvas = page.FindComponent<GraphCanvas>().Instance;
        var aggregateIndex = Assert.Single(canvas.View!.Aggregates).Key;
        var appIndex = canvas.View.NodeIds.ToList().IndexOf("hub-app-0");

        await cut.InvokeAsync(() => canvas.OnNodeSelected.InvokeAsync(aggregateIndex));
        Assert.Contains("Hidden resources", page.FindComponent<NodePropertiesPanel>().Markup);
        await cut.InvokeAsync(() => canvas.OnNodeSelected.InvokeAsync(appIndex));

        var panel = page.FindComponent<NodePropertiesPanel>();
        Assert.DoesNotContain("Hidden resources", panel.Markup);
        Assert.Contains("Hub app 0", panel.Markup);

        await cut.InvokeAsync(() => canvas.OnNodeSelected.InvokeAsync(aggregateIndex));
        await cut.InvokeAsync(() => canvas.OnNodeSelected.InvokeAsync((int?)null));

        panel = page.FindComponent<NodePropertiesPanel>();
        Assert.DoesNotContain("Hidden resources", panel.Markup);
        Assert.Contains("Select a node or edge", panel.Markup);

        await cut.InvokeAsync(() => canvas.OnNodeSelected.InvokeAsync(aggregateIndex));
        await cut.InvokeAsync(() => canvas.OnEdgeSelected.InvokeAsync(0));
        Assert.DoesNotContain("Hidden resources", page.FindComponent<NodePropertiesPanel>().Markup);
    }

    private const string Hub = "hub";

    /// <summary>Four web apps that each talk to one hub, so a small FocusMaxNeighbours hides some behind "+N more".</summary>
    private static GraphDocument GraphWithHub()
    {
        var graph = DefaultGraph();
        graph.Nodes.Add(new GraphNode { Id = Hub, Type = "ExternalService", Label = "Hub" });
        for (var i = 0; i < 4; i++)
        {
            graph.Nodes.Add(new GraphNode { Id = $"hub-app-{i}", Type = "WebApp", Label = $"Hub app {i}" });
            graph.Edges.Add(new GraphEdge { Id = $"hub-e{i}", Source = $"hub-app-{i}", Target = Hub, Type = EdgeTypes.ConnectsTo });
        }
        return graph;
    }

    [Fact]
    public async Task GraphPage_focus_rebuilds_when_low_confidence_toggled()
    {
        var cut = RenderPage($"?focus={AppA}", GraphWithFilteredNeighbours());
        var before = CanvasView(cut)!;
        Assert.DoesNotContain(LowNeighbour, before.NodeIds);

        await ChangeFilterAsync(cut, filter => filter.ShowLowConfidence = true);

        Assert.NotSame(before, CanvasView(cut));
        Assert.Contains(LowNeighbour, CanvasView(cut)!.NodeIds);
        Assert.Equal(AppA, cut.FindComponent<Graph>().FindComponent<NodePropertiesPanel>().Instance.Node?.Id);
    }

    [Fact]
    public async Task GraphPage_focus_rebuilds_when_hide_emulator_toggled()
    {
        var cut = RenderPage($"?focus={AppA}", GraphWithFilteredNeighbours());
        Assert.DoesNotContain(EmulatorNeighbour, CanvasView(cut)!.NodeIds);

        await ChangeFilterAsync(cut, filter => filter.HideEmulator = false);
        Assert.Contains(EmulatorNeighbour, CanvasView(cut)!.NodeIds);

        await ChangeFilterAsync(cut, filter => filter.HideEmulator = true);
        Assert.DoesNotContain(EmulatorNeighbour, CanvasView(cut)!.NodeIds);
        Assert.Equal(GraphViewMode.Focus, CanvasView(cut)!.Mode);
        Assert.Contains($"focus={AppA}", Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Fact]
    public async Task GraphPage_focus_other_filter_changes_stay_client_side()
    {
        var cut = RenderPage($"?focus={AppA}", GraphWithFilteredNeighbours());
        var before = CanvasView(cut);

        await ChangeFilterAsync(cut, filter => filter.HideExternal = !filter.HideExternal);
        await ChangeFilterAsync(cut, filter => filter.OnlyConnected = !filter.OnlyConnected);

        Assert.Same(before, CanvasView(cut));
    }

    private const string LowNeighbour = "low";
    private const string EmulatorNeighbour = "emulator";

    /// <summary>App A also has a low-confidence neighbour and a local-emulator neighbour, which BuildFocus drops by default.</summary>
    private static GraphDocument GraphWithFilteredNeighbours()
    {
        var graph = DefaultGraph();
        graph.Nodes.Add(new GraphNode { Id = LowNeighbour, Type = "ExternalService", Label = "Low confidence" });
        graph.Nodes.Add(new GraphNode { Id = EmulatorNeighbour, Type = "External", Label = "Emulator", Data = { ["isLocalEmulator"] = true } });
        graph.Edges.Add(new GraphEdge { Id = "e-low", Source = AppA, Target = LowNeighbour, Type = EdgeTypes.ConnectsTo, Data = { ["confidence"] = "Low" } });
        graph.Edges.Add(new GraphEdge { Id = "e-emu", Source = AppA, Target = EmulatorNeighbour, Type = EdgeTypes.ConnectsTo });
        return graph;
    }

    private static Task ChangeFilterAsync(IRenderedComponent<Router> cut, Action<GraphFilterState> change)
    {
        var panel = cut.FindComponent<Graph>().FindComponent<GraphFilterPanel>().Instance;
        return cut.InvokeAsync(() =>
        {
            change(panel.Filter);
            return panel.FilterChanged.InvokeAsync();
        });
    }

    [Fact]
    public void GraphPage_tag_change_refreshes_filter_suggestions()
    {
        var host = ConfigurePage();
        var cut = RenderPage($"?focus={AppA}");
        var panel = cut.FindComponent<Graph>().FindComponent<GraphFilterPanel>();
        var options = panel.Instance.NodeOptionsInstance;
        Assert.DoesNotContain("contoso-owner", panel.Instance.TagNamesInstance);

        host.Session.Graph!.Nodes.Single(n => n.Id == AppA).Data[TagKeys.Tags] =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["contoso-owner"] = "team-a" };
        host.Store.NotifyTagsChanged(host.User, AppA);

        cut.WaitForAssertion(() => Assert.Contains("contoso-owner", panel.Instance.TagNamesInstance));
        Assert.Same(options, panel.Instance.NodeOptionsInstance);
    }

    [Fact]
    public void GraphPage_resource_group_scope_focus_sets_subscription_and_group()
    {
        var cut = RenderPage($"?focus={ResourceGroupId}", GraphWithResourceGroup());
        var navigation = Services.GetRequiredService<NavigationManager>();

        cut.WaitForAssertion(() => Assert.Equal(GraphViewMode.Overview, CanvasView(cut)?.Mode));
        var filter = cut.FindComponent<Graph>().FindComponent<GraphFilterPanel>().Instance.Filter;
        Assert.Equal(["sub-1"], filter.Subscriptions);
        Assert.Equal(["rg-shop"], filter.ResourceGroups);
        Assert.DoesNotContain("focus=", navigation.Uri);
        Assert.True(Assert.IsType<Bunit.TestDoubles.BunitNavigationManager>(navigation).History.First().Options.ReplaceHistoryEntry);
    }

    private static GraphViewPayload? CanvasView(IRenderedComponent<Router> cut) =>
        cut.FindComponent<Graph>().FindComponent<GraphCanvas>().Instance.View;

    /// <summary>
    /// Holds the page's Overview layout for <paramref name="showChildResources"/> on a task the test completes, and
    /// returns a real layout computed on a separate session (the layout cache lives on the session).
    /// </summary>
    private async Task<(TaskCompletionSource<GraphLayout> Held, GraphLayout Layout)> HoldOverviewLayoutAsync(
        GraphDocument graph, bool showChildResources, GraphFilterState? savedFilter = null)
    {
        var host = ConfigurePage(graph, savedFilter: savedFilter);
        var layout = await host.Layouts.GetOverviewLayoutAsync(
            new InventorySession { Graph = graph, Index = new GraphIndex(graph) }, showChildResources, default);
        var held = new TaskCompletionSource<GraphLayout>();
        host.Session.ViewCache = showChildResources
            ? new GraphViewCache(graph) { Expanded = new(() => held.Task) }
            : new GraphViewCache(graph) { Collapsed = new(() => held.Task) };
        return (held, layout);
    }

    // The layout continuation runs on the renderer's dispatcher, so the second, empty dispatch runs after it.
    private static async Task CompleteLayoutAsync(IRenderedComponent<Router> cut, TaskCompletionSource<GraphLayout> held,
        GraphLayout layout)
    {
        await cut.InvokeAsync(() => held.SetResult(layout));
        await cut.InvokeAsync(() => { });
    }

    private const string SqlServer = "sql";
    private const string ResourceGroupId = "rg-shop";

    private static GraphDocument GraphWithSqlServer()
    {
        var graph = DefaultGraph();
        graph.Nodes.Add(new GraphNode { Id = SqlServer, Type = "SqlServer", Label = "SQL Server" });
        graph.Edges.Add(new GraphEdge { Id = "e5", Source = SqlServer, Target = Db, Type = EdgeTypes.HostsDatabase });
        return graph;
    }

    private static GraphDocument GraphWithResourceGroup()
    {
        var graph = DefaultGraph();
        graph.Nodes.Add(new GraphNode
        {
            Id = ResourceGroupId,
            Type = "ResourceGroup",
            Label = "rg-shop",
            Data = { ["resourceGroup"] = "rg-shop", ["subscriptionId"] = "sub-1" }
        });
        return graph;
    }

    private IRenderedComponent<Router> RenderPage(string pathAndQuery, GraphDocument? graph = null,
        GraphViewOptions? options = null, GraphFilterState? savedFilter = null)
    {
        ConfigurePage(graph, options, savedFilter);
        Services.GetRequiredService<NavigationManager>().NavigateTo($"http://localhost/graph{pathAndQuery}");
        return Render<Router>(parameters => parameters
            .Add(router => router.AppAssembly, typeof(Graph).Assembly)
            .Add(router => router.Found, routeData => builder =>
            {
                builder.OpenComponent<RouteView>(0);
                builder.AddAttribute(1, nameof(RouteView.RouteData), routeData);
                builder.CloseComponent();
            })
            .Add(router => router.NotFoundPage, typeof(Graph)));
    }

    // Radzen dialogs render in this host, not in the page, because the test router does not render MainLayout.
    private IRenderedComponent<Radzen.Blazor.RadzenDialog> RenderDialogHost()
    {
        ConfigurePage();
        return Render<Radzen.Blazor.RadzenDialog>();
    }

    private TestGraphPageHost ConfigurePage(GraphDocument? graph = null, GraphViewOptions? options = null,
        GraphFilterState? savedFilter = null)
    {
        if (_host is not null)
        {
            if (graph is not null || options is not null || savedFilter is not null)
                throw new InvalidOperationException("Pass graph, options, and savedFilter on the first ConfigurePage or RenderPage call.");
            return _host;
        }

        var user = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("tid", Fixture.TenantId),
            new Claim("oid", "22222222-2222-2222-2222-222222222222")
        ], "test"));
        var dataStore = new TestUserDataStore(savedFilter);
        var userContext = new UserContextAccessor(new TestAuthenticationStateProvider(user));
        graph ??= DefaultGraph();
        var snapshot = Fixture.Snapshot();
        var session = new InventorySession { Snapshot = snapshot, Graph = graph, Index = new GraphIndex(graph) };
        var store = new InventorySessionStore(Options.Create(new GraphSessionOptions()));
        store.Set(user, session);
        var inventoryOptions = Options.Create(new InventoryOptions());
        var viewOptions = Options.Create(options ?? new GraphViewOptions());
        var builder = Fixture.Builder();
        var layouts = new GraphLayoutService(viewOptions, NullLogger<GraphLayoutService>.Instance);
        var loader = new InventorySessionLoader(store, dataStore, null!, builder, layouts, userContext, inventoryOptions,
            NullLogger<InventorySessionLoader>.Instance);

        Services.AddSingleton<AuthenticationStateProvider>(new TestAuthenticationStateProvider(user));
        Services.AddSingleton(store);
        Services.AddSingleton<IUserDataStore>(dataStore);
        Services.AddScoped(_ => userContext);
        Services.AddSingleton<IOptions<GraphViewOptions>>(viewOptions);
        Services.AddSingleton<IOptions<InventoryOptions>>(inventoryOptions);
        Services.AddSingleton<GraphViewService>();
        Services.AddSingleton(layouts);
        Services.AddSingleton<GraphQueryService>();
        Services.AddScoped(_ => loader);
        Services.AddScoped(_ => new UserPreferencesService(dataStore, userContext, NullLogger<UserPreferencesService>.Instance));
        Services.AddScoped(_ => new ChangeTrackingService(
            new SnapshotDiffService(builder, Options.Create(new ChangeTrackingOptions()), inventoryOptions),
            dataStore, loader, userContext, NullLogger<ChangeTrackingService>.Instance));
        Services.AddScoped(_ => new FileDownloadService(JSInterop.JSRuntime));
        Services.AddScoped(_ => new InventoryScanOrchestrator(null!, null!, null!, null!, builder, store, layouts,
            userContext, dataStore, null!, inventoryOptions, NullLogger<InventoryScanOrchestrator>.Instance));

        return _host = new TestGraphPageHost(store, layouts, session, user);
    }

    private static GraphDocument DefaultGraph() =>
        new()
        {
            Nodes =
            [
                new GraphNode { Id = AppA, Type = "WebApp", Label = "App A" },
                new GraphNode { Id = Db, Type = "SqlDatabase", Label = "Database" },
                new GraphNode { Id = AppB, Type = "WebApp", Label = "App B" },
                new GraphNode { Id = Queue, Type = "StorageQueue", Label = "Queue" },
                new GraphNode { Id = AppC, Type = "WebApp", Label = "App C" }
            ],
            Edges =
            [
                new GraphEdge { Id = "e1", Source = AppA, Target = Db, Type = EdgeTypes.ConnectsTo },
                new GraphEdge { Id = "e2", Source = AppB, Target = Db, Type = EdgeTypes.ConnectsTo },
                new GraphEdge { Id = "e3", Source = Db, Target = Queue, Type = EdgeTypes.ConnectsTo },
                new GraphEdge { Id = "e4", Source = AppC, Target = Queue, Type = EdgeTypes.ConnectsTo }
            ]
        };

    private sealed class TestGraphPageHost(InventorySessionStore store, GraphLayoutService layouts,
        InventorySession session, ClaimsPrincipal user)
    {
        public InventorySessionStore Store { get; } = store;
        public GraphLayoutService Layouts { get; } = layouts;
        public InventorySession Session { get; } = session;
        public ClaimsPrincipal User { get; } = user;
    }

    /// <summary>Behaves like <see cref="NullUserDataStore"/> but loads preferences holding the given saved graph filter.</summary>
    private sealed class TestUserDataStore(GraphFilterState? savedFilter) : IUserDataStore
    {
        private readonly NullUserDataStore _inner = new();
        public bool IsPersistent => _inner.IsPersistent;
        public Task<SavedDataInfo> GetInfoAsync(UserKey key, CancellationToken ct) => _inner.GetInfoAsync(key, ct);
        public Task<InventorySnapshot?> LoadSnapshotAsync(UserKey key, CancellationToken ct) => _inner.LoadSnapshotAsync(key, ct);
        public Task<InventorySnapshot?> LoadPreviousSnapshotAsync(UserKey key, CancellationToken ct) => _inner.LoadPreviousSnapshotAsync(key, ct);
        public Task SaveSnapshotAsync(UserKey key, InventorySnapshot snapshot, bool rotate, CancellationToken ct) =>
            _inner.SaveSnapshotAsync(key, snapshot, rotate, ct);
        public Task<UserPreferences> LoadPreferencesAsync(UserKey key, CancellationToken ct) =>
            Task.FromResult(new UserPreferences { GraphFilter = savedFilter });
        public Task SavePreferencesAsync(UserKey key, UserPreferences preferences, CancellationToken ct) =>
            _inner.SavePreferencesAsync(key, preferences, ct);
        public Task TouchAsync(UserKey key, CancellationToken ct) => _inner.TouchAsync(key, ct);
        public Task DeleteAllAsync(UserKey key, CancellationToken ct) => _inner.DeleteAllAsync(key, ct);
    }

    private sealed class TestAuthenticationStateProvider(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user));
    }

    private sealed class FakeAntiforgeryStateProvider : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => new("test-token", "__RequestVerificationToken");
    }

    private sealed class TestWebHostEnvironment : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = nameof(BlazorAzureInventory);
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = string.Empty;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
