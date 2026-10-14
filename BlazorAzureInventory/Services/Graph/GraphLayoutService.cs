using System.Diagnostics;
using BlazorAzureInventory.Models.Graph;
using BlazorAzureInventory.Models.Inventory;
using BlazorAzureInventory.Models.Options;
using Microsoft.Extensions.Options;
using Microsoft.Msagl.Core;
using Microsoft.Msagl.Core.Geometry.Curves;
using Microsoft.Msagl.Core.Layout;
using Microsoft.Msagl.Core.Routing;
using Microsoft.Msagl.Layout.MDS;
using Microsoft.Msagl.Miscellaneous;
using MsaglPoint = Microsoft.Msagl.Core.Geometry.Point;

namespace BlazorAzureInventory.Services.Graph;

/// <summary>
/// Computes Overview node positions with MSAGL MDS on a worker thread, bounded by a timeout and a node cap, with a
/// deterministic grid fallback. One layout per <see cref="GraphDocument"/> and child-resource mode is cached in the
/// session, and concurrent requests share a single computation.
/// </summary>
public sealed class GraphLayoutService(IOptions<GraphViewOptions> options, ILogger<GraphLayoutService> logger)
{
    /// <summary>Upper bound of both coordinates after normalization.</summary>
    internal const float MaxCoordinate = 10_000f;

    /// <summary>Distance between grid cells in the fallback layout, wide enough for the largest node and its label.</summary>
    private const double FallbackCell = 120;

    private int _computations;
    private int _msaglRuns;

    internal GraphViewOptions Options { get; } = options.Value;

    /// <summary>Number of layouts computed (cache misses), for tests.</summary>
    internal int ComputeCount => Volatile.Read(ref _computations);

    /// <summary>Number of times MSAGL was started, for tests.</summary>
    internal int MsaglCount => Volatile.Read(ref _msaglRuns);

    /// <summary>
    /// Returns the Overview layout for <see cref="InventorySession.Graph"/>. <paramref name="ct"/> only stops this caller
    /// from waiting: the shared computation continues and fills the cache for the next request.
    /// </summary>
    public Task<GraphLayout> GetOverviewLayoutAsync(InventorySession session, bool showChildResources, CancellationToken ct)
    {
        Lazy<Task<GraphLayout>> lazy;
        lock (session.SyncRoot)
        {
            var graph = session.Graph ?? throw new InvalidOperationException("The session has no graph.");
            var cache = session.ViewCache is { } existing && ReferenceEquals(existing.Graph, graph) ? existing : new GraphViewCache(graph);
            session.ViewCache = cache;
            // A failed computation is not reused; the next request retries.
            lazy = showChildResources
                ? cache.Expanded = Usable(cache.Expanded) ?? NewLayout(graph, showChildResources: true)
                : cache.Collapsed = Usable(cache.Collapsed) ?? NewLayout(graph, showChildResources: false);
        }

        return lazy.Value.WaitAsync(ct);
    }

    /// <summary>Starts the default (collapsed) Overview layout in the background so the first Graph page visit is fast.</summary>
    public void WarmUp(InventorySession session)
    {
        if (session.Graph is null) return;
        _ = WarmUpAsync(session);
    }

    private async Task WarmUpAsync(InventorySession session)
    {
        try
        {
            await GetOverviewLayoutAsync(session, showChildResources: false, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Overview layout warm-up failed ({ExceptionType})", ex.GetType().Name);
        }
    }

    private Lazy<Task<GraphLayout>> NewLayout(GraphDocument graph, bool showChildResources) =>
        new(() => Task.Run(() => Compute(graph, showChildResources)), LazyThreadSafetyMode.ExecutionAndPublication);

    private static Lazy<Task<GraphLayout>>? Usable(Lazy<Task<GraphLayout>>? lazy) =>
        lazy is { IsValueCreated: true, Value: { IsFaulted: true } or { IsCanceled: true } } ? null : lazy;

    private GraphLayout Compute(GraphDocument graph, bool showChildResources)
    {
        Interlocked.Increment(ref _computations);
        var sw = Stopwatch.StartNew();
        var projection = GraphViewService.ProjectOverview(graph, showChildResources);
        var nodeIds = projection.Nodes.Select(n => n.Id).ToList();
        var edges = projection.Edges.Select(e => (e.Source, e.Target)).ToList();

        GraphLayout layout;
        if (nodeIds.Count > Options.OverviewMaxNodes)
        {
            layout = ComputeFallback(graph, nodeIds, edges);
        }
        else
        {
            try
            {
                Interlocked.Increment(ref _msaglRuns);
                layout = ComputeMsagl(graph, nodeIds, edges, TimeSpan.FromSeconds(Options.LayoutTimeoutSeconds), CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Overview layout failed for {NodeCount} nodes and {EdgeCount} edges ({ExceptionType}); using the fallback layout",
                    nodeIds.Count, edges.Count, ex.GetType().Name);
                layout = ComputeFallback(graph, nodeIds, edges);
            }
        }

        layout = layout with { Elapsed = sw.Elapsed };
        logger.LogInformation("Overview layout: {NodeCount} nodes, {EdgeCount} edges, {ElapsedMs} ms, fallback={Fallback}",
            nodeIds.Count, edges.Count, (long)sw.Elapsed.TotalMilliseconds, layout.IsFallback);
        return layout;
    }

    /// <summary>
    /// MSAGL MDS layout (plan section 17.2): no edge routing, majorization only up to 1,000 nodes, overlaps removed.
    /// Returns the fallback layout when <paramref name="budget"/> runs out; the late MSAGL result is discarded.
    /// Throws if MSAGL fails or returns a non-finite position.
    /// </summary>
    internal static GraphLayout ComputeMsagl(GraphDocument graph, IReadOnlyList<string> nodeIds,
                                             IReadOnlyList<(int S, int T)> edges, TimeSpan budget, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (nodeIds.Count == 0) return Empty(graph, sw.Elapsed);

        var types = NodeTypes(graph);
        var degree = Degrees(nodeIds.Count, edges);
        var geometry = new GeometryGraph();
        var msaglNodes = new Node[nodeIds.Count];
        for (var i = 0; i < nodeIds.Count; i++)
        {
            var radius = NodeDiameter(types.GetValueOrDefault(nodeIds[i]), degree[i]) / 2;
            msaglNodes[i] = new Node(CurveFactory.CreateCircle(radius, new MsaglPoint()), i);
            geometry.Nodes.Add(msaglNodes[i]);
        }
        foreach (var (s, t) in edges)
        {
            var edge = new Edge(msaglNodes[s], msaglNodes[t]);
            geometry.Edges.Add(edge);
        }

        var settings = new MdsLayoutSettings
        {
            RemoveOverlaps = true,
            CallIterationsWithMajorizationThreshold = 1000,
            EdgeRoutingSettings = new EdgeRoutingSettings { EdgeRoutingMode = EdgeRoutingMode.None }
        };

        var cancel = new CancelToken();
        var run = Task.Run(() => LayoutHelpers.CalculateLayout(geometry, settings, cancel), CancellationToken.None);
        try
        {
            run.WaitAsync(budget, ct).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            cancel.Canceled = true;
            _ = run.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            if (ex is OperationCanceledException) throw;
            return ComputeFallback(graph, nodeIds, edges) with { Elapsed = sw.Elapsed };
        }

        var x = new double[nodeIds.Count];
        var y = new double[nodeIds.Count];
        for (var i = 0; i < nodeIds.Count; i++)
        {
            var center = msaglNodes[i].Center;
            if (!double.IsFinite(center.X) || !double.IsFinite(center.Y))
                throw new InvalidOperationException("MSAGL returned a non-finite node position.");
            x[i] = center.X;
            // MSAGL's y axis points up; the browser's points down.
            y[i] = -center.Y;
        }
        return new GraphLayout(graph, Normalize(nodeIds, x, y), IsFallback: false, sw.Elapsed);
    }

    /// <summary>
    /// Deterministic component-packed grid: each connected component is a square grid (highest degree first), and
    /// components are packed into rows, largest first.
    /// </summary>
    internal static GraphLayout ComputeFallback(GraphDocument graph, IReadOnlyList<string> nodeIds,
                                                IReadOnlyList<(int S, int T)> edges)
    {
        var sw = Stopwatch.StartNew();
        var count = nodeIds.Count;
        if (count == 0) return Empty(graph, sw.Elapsed) with { IsFallback = true };

        var degree = Degrees(count, edges);
        var parent = Enumerable.Range(0, count).ToArray();
        int Find(int i)
        {
            while (parent[i] != i) i = parent[i] = parent[parent[i]];
            return i;
        }
        foreach (var (s, t) in edges)
        {
            int a = Find(s), b = Find(t);
            if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
        }

        var components = Enumerable.Range(0, count)
            .GroupBy(Find)
            .Select(g => g.OrderByDescending(i => degree[i]).ThenBy(i => i).ToList())
            .OrderByDescending(c => c.Count).ThenBy(c => c.Min())
            .ToList();

        var columns = components.Select(c => (int)Math.Ceiling(Math.Sqrt(c.Count))).ToList();
        var rowWidth = Math.Max(columns.Max(), (int)Math.Ceiling(Math.Sqrt(count * 1.5))) * FallbackCell;

        var x = new double[count];
        var y = new double[count];
        double left = 0, top = 0, rowHeight = 0;
        for (var c = 0; c < components.Count; c++)
        {
            var nodes = components[c];
            var cols = columns[c];
            var width = cols * FallbackCell;
            var height = (int)Math.Ceiling(nodes.Count / (double)cols) * FallbackCell;
            if (left > 0 && left + width > rowWidth)
            {
                left = 0;
                top += rowHeight + FallbackCell;
                rowHeight = 0;
            }
            for (var k = 0; k < nodes.Count; k++)
            {
                x[nodes[k]] = left + k % cols * FallbackCell;
                y[nodes[k]] = top + k / cols * FallbackCell;
            }
            left += width + FallbackCell;
            rowHeight = Math.Max(rowHeight, height);
        }

        return new GraphLayout(graph, Normalize(nodeIds, x, y), IsFallback: true, sw.Elapsed);
    }

    /// <summary>Node diameter in layout units; matches the Cytoscape node size rule in plan section 5.4.</summary>
    internal static double NodeDiameter(string? nodeType, int degree) =>
        nodeType == nameof(AzureResourceKind.ExternalService) ? 72 : Math.Clamp(24 + 6 * Math.Sqrt(degree), 24, 64);

    /// <summary>
    /// Moves the layout so it starts at (0, 0), and scales it down uniformly only if its larger side exceeds
    /// <see cref="MaxCoordinate"/>. Layout units stay equal to screen pixels at zoom 1, so node sizes and spacing match.
    /// </summary>
    private static Dictionary<string, (float X, float Y)> Normalize(IReadOnlyList<string> nodeIds, double[] x, double[] y)
    {
        double minX = x.Min(), minY = y.Min();
        var extent = Math.Max(x.Max() - minX, y.Max() - minY);
        var scale = extent > MaxCoordinate ? MaxCoordinate / extent : 1;
        var positions = new Dictionary<string, (float X, float Y)>(nodeIds.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < nodeIds.Count; i++)
        {
            positions[nodeIds[i]] = ((float)Math.Round((x[i] - minX) * scale, 1), (float)Math.Round((y[i] - minY) * scale, 1));
        }
        return positions;
    }

    private static GraphLayout Empty(GraphDocument graph, TimeSpan elapsed) =>
        new(graph, new Dictionary<string, (float X, float Y)>(StringComparer.OrdinalIgnoreCase), IsFallback: false, elapsed);

    private static int[] Degrees(int count, IReadOnlyList<(int S, int T)> edges)
    {
        var degree = new int[count];
        foreach (var (s, t) in edges)
        {
            degree[s]++;
            degree[t]++;
        }
        return degree;
    }

    private static Dictionary<string, string> NodeTypes(GraphDocument graph)
    {
        var types = new Dictionary<string, string>(graph.Nodes.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var node in graph.Nodes) types.TryAdd(node.Id, node.Type);
        return types;
    }
}

/// <summary>Per-session Overview layouts for one <see cref="GraphDocument"/>; replaced when the session's graph changes.</summary>
internal sealed class GraphViewCache(GraphDocument graph)
{
    public GraphDocument Graph { get; } = graph;
    public Lazy<Task<GraphLayout>>? Collapsed { get; set; }
    public Lazy<Task<GraphLayout>>? Expanded { get; set; }
}
