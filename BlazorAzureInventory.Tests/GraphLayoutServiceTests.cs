using BlazorAzureInventory.Models.Graph;
using BlazorAzureInventory.Models.Options;
using BlazorAzureInventory.Services.Graph;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace BlazorAzureInventory.Tests;

/// <summary>Overview layout with MSAGL, the fallback grid, and the per-session cache (plan sections 6.3, 7.3, and 9).</summary>
public class GraphLayoutServiceTests(ITestOutputHelper output)
{
    private static readonly Lazy<GraphDocument> Large = new(() => Fixture.Builder().Build(Fixture.LargeSnapshot(), new GraphBuildOptions()));

    private static GraphLayoutService Service(GraphViewOptions? options = null) =>
        new(Microsoft.Extensions.Options.Options.Create(options ?? new GraphViewOptions()), NullLogger<GraphLayoutService>.Instance);

    private static GraphDocument Sample() => Fixture.Builder().Build(Fixture.Snapshot(), new GraphBuildOptions());

    private static InventorySession Session(GraphDocument graph) => new() { Graph = graph, Index = new GraphIndex(graph) };

    private static (List<string> NodeIds, List<(int S, int T)> Edges) Project(GraphDocument graph, bool children = false)
    {
        var projection = GraphViewService.ProjectOverview(graph, children);
        return (projection.Nodes.Select(n => n.Id).ToList(), projection.Edges.Select(e => (e.Source, e.Target)).ToList());
    }

    private static void AssertFiniteAndComplete(GraphLayout layout, IReadOnlyList<string> nodeIds)
    {
        Assert.Equal(nodeIds.Count, layout.Positions.Count);
        Assert.All(nodeIds, id =>
        {
            var (x, y) = layout.Positions[id];
            Assert.True(float.IsFinite(x) && float.IsFinite(y));
            Assert.InRange(x, 0f, GraphLayoutService.MaxCoordinate);
            Assert.InRange(y, 0f, GraphLayoutService.MaxCoordinate);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Msagl_positions_every_node_finite(bool children)
    {
        var graph = Sample();
        var (nodeIds, edges) = Project(graph, children);

        var layout = GraphLayoutService.ComputeMsagl(graph, nodeIds, edges, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.False(layout.IsFallback);
        Assert.Same(graph, layout.Graph);
        AssertFiniteAndComplete(layout, nodeIds);
    }

    [Fact]
    public void Msagl_does_not_overlap_nodes()
    {
        var graph = Sample();
        var (nodeIds, edges) = Project(graph);

        var layout = GraphLayoutService.ComputeMsagl(graph, nodeIds, edges, TimeSpan.FromSeconds(30), CancellationToken.None);

        var points = nodeIds.Select(id => layout.Positions[id]).ToList();
        for (var i = 0; i < points.Count; i++)
        for (var j = i + 1; j < points.Count; j++)
        {
            var distance = Math.Sqrt(Math.Pow(points[i].X - points[j].X, 2) + Math.Pow(points[i].Y - points[j].Y, 2));
            Assert.True(distance >= 24, $"Nodes {i} and {j} are {distance:F1} apart");
        }
    }

    [Fact]
    public void Msagl_is_deterministic_for_same_graph()
    {
        var graph = Large.Value;
        var (nodeIds, edges) = Project(graph);

        var first = GraphLayoutService.ComputeMsagl(graph, nodeIds, edges, TimeSpan.FromSeconds(60), CancellationToken.None);
        var second = GraphLayoutService.ComputeMsagl(graph, nodeIds, edges, TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.False(first.IsFallback);
        Assert.False(second.IsFallback);
        AssertFiniteAndComplete(first, nodeIds);
        Assert.All(nodeIds, id =>
        {
            Assert.Equal(first.Positions[id].X, second.Positions[id].X, 1e-3f);
            Assert.Equal(first.Positions[id].Y, second.Positions[id].Y, 1e-3f);
        });
    }

    [Fact]
    public void Timeout_returns_fallback()
    {
        var graph = Large.Value;
        var (nodeIds, edges) = Project(graph, children: true);

        var layout = GraphLayoutService.ComputeMsagl(graph, nodeIds, edges, TimeSpan.FromMilliseconds(1), CancellationToken.None);

        Assert.True(layout.IsFallback);
        AssertFiniteAndComplete(layout, nodeIds);
    }

    [Fact]
    public void Fallback_is_deterministic_and_does_not_overlap()
    {
        var graph = Large.Value;
        var (nodeIds, edges) = Project(graph);

        var first = GraphLayoutService.ComputeFallback(graph, nodeIds, edges);
        var second = GraphLayoutService.ComputeFallback(graph, nodeIds, edges);

        Assert.True(first.IsFallback);
        AssertFiniteAndComplete(first, nodeIds);
        Assert.Equal(first.Positions.OrderBy(p => p.Key), second.Positions.OrderBy(p => p.Key));
        Assert.Equal(nodeIds.Count, first.Positions.Values.Distinct().Count());
    }

    [Fact]
    public void Empty_projection_gives_empty_layout()
    {
        var graph = new GraphDocument();

        Assert.Empty(GraphLayoutService.ComputeMsagl(graph, [], [], TimeSpan.FromSeconds(1), CancellationToken.None).Positions);
        Assert.Empty(GraphLayoutService.ComputeFallback(graph, [], []).Positions);
    }

    [Fact]
    public async Task Over_cap_uses_fallback_without_msagl()
    {
        var service = Service(new GraphViewOptions { OverviewMaxNodes = 100 });
        var graph = Large.Value;

        var layout = await service.GetOverviewLayoutAsync(Session(graph), showChildResources: false, CancellationToken.None);

        Assert.True(layout.IsFallback);
        Assert.Equal(0, service.MsaglCount);
        AssertFiniteAndComplete(layout, Project(graph).NodeIds);
    }

    [Fact]
    public async Task Layout_matches_overview_payload_nodes()
    {
        var service = Service();
        var graph = Sample();
        var index = new GraphIndex(graph);

        var layout = await service.GetOverviewLayoutAsync(Session(graph), showChildResources: false, CancellationToken.None);
        var payload = new GraphViewService(Microsoft.Extensions.Options.Options.Create(new GraphViewOptions()))
            .BuildOverview(index, layout, showChildResources: false);

        Assert.False(payload.FallbackLayout);
        Assert.All(payload.Nodes, n => Assert.True(n.X.HasValue && n.Y.HasValue));
        Assert.Equal(payload.NodeIds.Order(StringComparer.Ordinal), layout.Positions.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Cache_reused_for_same_graph_reference()
    {
        var service = Service();
        var session = Session(Sample());

        var first = await service.GetOverviewLayoutAsync(session, showChildResources: false, CancellationToken.None);
        var second = await service.GetOverviewLayoutAsync(session, showChildResources: false, CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(1, service.ComputeCount);
    }

    [Fact]
    public async Task Child_mode_is_cached_separately()
    {
        var service = Service();
        var session = Session(Sample());

        var collapsed = await service.GetOverviewLayoutAsync(session, showChildResources: false, CancellationToken.None);
        var expanded = await service.GetOverviewLayoutAsync(session, showChildResources: true, CancellationToken.None);

        Assert.NotSame(collapsed, expanded);
        Assert.True(expanded.Positions.Count > collapsed.Positions.Count);
        Assert.Same(expanded, await service.GetOverviewLayoutAsync(session, showChildResources: true, CancellationToken.None));
        Assert.Equal(2, service.ComputeCount);
    }

    [Fact]
    public async Task New_graph_invalidates_cache()
    {
        var service = Service();
        var session = Session(Sample());
        var first = await service.GetOverviewLayoutAsync(session, showChildResources: false, CancellationToken.None);

        var rebuilt = Sample();
        session.Graph = rebuilt;
        session.Index = new GraphIndex(rebuilt);
        var second = await service.GetOverviewLayoutAsync(session, showChildResources: false, CancellationToken.None);

        Assert.NotSame(first, second);
        Assert.Same(rebuilt, second.Graph);
        Assert.Same(rebuilt, session.ViewCache!.Graph);
        Assert.Equal(2, service.ComputeCount);
    }

    [Fact]
    public async Task Concurrent_calls_compute_once()
    {
        var service = Service();
        var session = Session(Large.Value);

        var layouts = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            Task.Run(() => service.GetOverviewLayoutAsync(session, showChildResources: false, CancellationToken.None))));

        Assert.Equal(1, service.ComputeCount);
        Assert.All(layouts, l => Assert.Same(layouts[0], l));
    }

    [Fact]
    public async Task Caller_cancellation_does_not_cancel_shared_layout()
    {
        var service = Service();
        var session = Session(Large.Value);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetOverviewLayoutAsync(session, showChildResources: false, cts.Token));
        var layout = await service.GetOverviewLayoutAsync(session, showChildResources: false, CancellationToken.None);

        Assert.False(layout.IsFallback);
        Assert.Equal(1, service.ComputeCount);
    }

    [Fact]
    public async Task WarmUp_fills_the_cache()
    {
        var service = Service();
        var session = Session(Sample());

        service.WarmUp(session);
        var layout = await service.GetOverviewLayoutAsync(session, showChildResources: false, CancellationToken.None);

        Assert.Equal(1, service.ComputeCount);
        Assert.Same(session.ViewCache!.Graph, layout.Graph);
    }

    [Fact]
    public void WarmUp_without_graph_does_nothing()
    {
        var service = Service();
        var session = new InventorySession();

        service.WarmUp(session);

        Assert.Null(session.ViewCache);
        Assert.Equal(0, service.ComputeCount);
    }

    [PerfTheory]
    [Trait("Category", "Perf")]
    [InlineData(false)]
    [InlineData(true)]
    public void Layout_1300_nodes_under_budget(bool children)
    {
        var graph = Large.Value;
        var (nodeIds, edges) = Project(graph, children);

        var layout = GraphLayoutService.ComputeMsagl(graph, nodeIds, edges, TimeSpan.FromSeconds(10), CancellationToken.None);

        output.WriteLine($"children={children} nodes={nodeIds.Count} edges={edges.Count} elapsed={layout.Elapsed.TotalMilliseconds:F0} ms fallback={layout.IsFallback}");
        Assert.False(layout.IsFallback);
        Assert.True(layout.Elapsed < TimeSpan.FromSeconds(5));
    }
}

/// <summary>Perf tests are skipped unless RUN_PERF_TESTS=1, so timings on a busy CI agent never fail the default run.</summary>
public sealed class PerfTheoryAttribute : TheoryAttribute
{
    public PerfTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_PERF_TESTS") != "1") Skip = "Perf test: set RUN_PERF_TESTS=1 to run.";
    }
}
