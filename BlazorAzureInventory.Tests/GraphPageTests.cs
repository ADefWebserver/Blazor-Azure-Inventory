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
    public async Task GraphPage_copy_link_shows_manual_copy_when_clipboard_is_unavailable()
    {
        var cut = RenderPage($"?focus={AppA}&expand={Db}");
        var uri = Services.GetRequiredService<NavigationManager>().Uri;

        await cut.FindAll("button").Single(button => button.TextContent.Contains("Copy link", StringComparison.Ordinal)).ClickAsync();

        Assert.Contains("Copy graph link", cut.Markup);
        Assert.Contains(uri, cut.Find("input[aria-label='Graph link']").GetAttribute("value"));
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

    private IRenderedComponent<Router> RenderPage(string pathAndQuery)
    {
        ConfigurePage();
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

    private TestGraphPageHost ConfigurePage()
    {
        if (_host is not null) return _host;

        var user = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("tid", Fixture.TenantId),
            new Claim("oid", "22222222-2222-2222-2222-222222222222")
        ], "test"));
        var dataStore = new NullUserDataStore();
        var userContext = new UserContextAccessor(new TestAuthenticationStateProvider(user));
        var graph = new GraphDocument
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
        var snapshot = Fixture.Snapshot();
        var session = new InventorySession { Snapshot = snapshot, Graph = graph, Index = new GraphIndex(graph) };
        var store = new InventorySessionStore(Options.Create(new GraphSessionOptions()));
        store.Set(user, session);
        var inventoryOptions = Options.Create(new InventoryOptions());
        var viewOptions = Options.Create(new GraphViewOptions());
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

        return _host = new TestGraphPageHost(store, layouts);
    }

    private sealed class TestGraphPageHost(InventorySessionStore store, GraphLayoutService layouts)
    {
        public InventorySessionStore Store { get; } = store;
        public GraphLayoutService Layouts { get; } = layouts;
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
