using BlazorAzureInventory.Models.Configuration;
using BlazorAzureInventory.Models.Graph;
using BlazorAzureInventory.Services.AI.GraphTools;
using BlazorAzureInventory.Services.Analysis;
using BlazorAzureInventory.Services.Graph;
using Xunit.Abstractions;

namespace BlazorAzureInventory.Tests;

public class ResourceMatcherTests
{
    private readonly ResourceMatcher _matcher = Fixture.Matcher;
    private readonly ResourceIndex _index = Fixture.Matcher.BuildIndex(Fixture.Resources());

    private static ConnectionReference Ref(ConnectionTargetKind kind, string? host = null, string? name = null, string? db = null, string? container = null) =>
        new(kind, host, name, db, container, null, false, Confidence.High, null);

    [Fact]
    public void Matches_database_case_insensitively()
    {
        var r = _matcher.Match(Ref(ConnectionTargetKind.SqlDatabase, "CONTOSO-SQL.database.windows.net", db: "Orders"), _index);
        Assert.Equal(Fixture.OrdersDb, r.ResolvedResourceId);
    }

    [Fact]
    public void Falls_back_to_server_when_database_is_not_visible()
    {
        var r = _matcher.Match(Ref(ConnectionTargetKind.SqlDatabase, "contoso-sql.database.windows.net", db: "hidden"), _index);
        Assert.Equal(Fixture.ContosoSql, r.ResolvedResourceId);
    }

    [Fact]
    public void Matches_container_then_account()
    {
        Assert.Equal(Fixture.Uploads, _matcher.Match(Ref(ConnectionTargetKind.BlobContainer, name: "contosodata", container: "uploads"), _index).ResolvedResourceId);
        Assert.Equal(Fixture.ContosoData, _matcher.Match(Ref(ConnectionTargetKind.BlobContainer, name: "contosodata", container: "missing"), _index).ResolvedResourceId);
    }

    [Fact]
    public void Matches_hosts_with_ports_and_trailing_dots()
    {
        Assert.Equal(Fixture.ContosoBus, _matcher.Match(Ref(ConnectionTargetKind.ServiceBusNamespace, "contoso-bus.servicebus.windows.net."), _index).ResolvedResourceId);
        Assert.Equal(Fixture.ContosoCache, _matcher.Match(Ref(ConnectionTargetKind.RedisCache, "contoso-cache.redis.cache.windows.net:6380"), _index).ResolvedResourceId);
    }

    [Fact]
    public void Unmatched_reference_gets_stable_external_id()
    {
        var r = _matcher.Match(Ref(ConnectionTargetKind.SqlDatabase, "10.0.0.5", db: "hr"), _index);
        Assert.Null(r.ResolvedResourceId);
        Assert.Equal("external:sql:10.0.0.5/hr", ResourceMatcher.ExternalNodeId(r));
        Assert.Equal("NotAzure", _matcher.ExternalReason(r));
    }

    [Fact]
    public void Emulator_references_share_one_node()
    {
        var r = new ConnectionReference(ConnectionTargetKind.External, null, null, null, null, null, true, Confidence.High, null);
        Assert.Equal(ResourceMatcher.EmulatorNodeId, ResourceMatcher.ExternalNodeId(_matcher.Match(r, _index)));
    }
}

public class InventoryGraphBuilderTests
{
    [Fact]
    public void Same_snapshot_gives_identical_ids()
    {
        var snapshot = Fixture.Snapshot();
        var a = Fixture.Builder().Build(snapshot, new GraphBuildOptions());
        var b = Fixture.Builder().Build(snapshot, new GraphBuildOptions());

        Assert.Equal(a.Nodes.Select(n => n.Id), b.Nodes.Select(n => n.Id));
        Assert.Equal(a.Edges.Select(e => e.Id), b.Edges.Select(e => e.Id));
    }

    [Fact]
    public void Graph_has_no_dangling_edges()
    {
        var graph = Fixture.Builder().Build(Fixture.Snapshot(), new GraphBuildOptions());
        var ids = graph.Nodes.Select(n => n.Id).ToHashSet();
        Assert.All(graph.Edges, e =>
        {
            Assert.Contains(e.Source, ids);
            Assert.Contains(e.Target, ids);
        });
    }

    [Fact]
    public void Creates_expected_relationships()
    {
        var graph = Fixture.Builder().Build(Fixture.Snapshot(), new GraphBuildOptions());
        bool Has(string source, string type, string target) =>
            graph.Edges.Any(e => e.Source == source && e.Type == type && e.Target == target);

        Assert.True(Has(Fixture.ShopWeb, EdgeTypes.ConnectsTo, Fixture.OrdersDb));
        Assert.True(Has(Fixture.ShopWeb, EdgeTypes.ConnectsTo, Fixture.Uploads));
        Assert.True(Has(Fixture.ShopWeb, EdgeTypes.ReferencesSecret, Fixture.ContosoKv));
        Assert.True(Has(Fixture.OrdersFunc, EdgeTypes.ConnectsTo, Fixture.ContosoBus));
        Assert.True(Has(Fixture.CatalogApi, EdgeTypes.PullsImageFrom, Fixture.ContosoAcr));
        Assert.True(Has(Fixture.CatalogApi, EdgeTypes.ConnectsTo, Fixture.ContosoCache));
        Assert.True(Has(Fixture.BillingApi, EdgeTypes.ConnectsTo, "external:sql:10.0.0.5/hr"));
        Assert.True(Has(Fixture.ShopWeb, EdgeTypes.HasSlot, Fixture.ShopWebStaging));
        Assert.True(Has(Fixture.ContosoSql, EdgeTypes.HostsDatabase, Fixture.OrdersDb));
    }

    [Fact]
    public void Edges_are_aggregated_and_only_use_effective_entries()
    {
        var graph = Fixture.Builder().Build(Fixture.Snapshot(), new GraphBuildOptions());
        var edge = graph.Edges.Single(e => e.Source == Fixture.ShopWeb && e.Target == Fixture.OrdersDb);
        var refs = Assert.IsType<List<EdgeReference>>(edge.Data["references"]);

        var reference = Assert.Single(refs);
        Assert.Equal("ConnectionStrings:Orders", reference.SettingKey);
        Assert.Equal(nameof(ConfigSource.AppServiceConnectionString), reference.Source);
    }

    [Fact]
    public void Container_cap_keeps_referenced_containers_and_adds_aggregate()
    {
        var graph = Fixture.Builder().Build(Fixture.Snapshot(), new GraphBuildOptions(MaxBlobContainersPerAccount: 1));

        Assert.Contains(graph.Nodes, n => n.Id == Fixture.Uploads);
        Assert.DoesNotContain(graph.Nodes, n => n.Id == Fixture.ProductImages);
        var aggregate = Assert.Single(graph.Nodes, n => n.Data.ContainsKey("isAggregate"));
        Assert.Equal("+1 more containers", aggregate.Label);
    }
}

public class InventoryGraphToolsTests
{
    private static InventoryGraphTools Tools(int cap = 100)
    {
        var snapshot = Fixture.Snapshot();
        var graph = Fixture.Builder().Build(snapshot, new GraphBuildOptions());
        return new InventoryGraphTools(snapshot, new GraphIndex(graph), cap);
    }

    [Fact]
    public void FindConsumers_returns_apps_with_setting_keys()
    {
        var consumers = Tools().FindConsumers("orders", 20);

        Assert.Contains(consumers, c => c.App == "shop-web" && c.SettingKeys.Contains("ConnectionStrings:Orders"));
        Assert.Contains(consumers, c => c.App == "orders-func" && c.SettingKeys.Contains("SqlConnection"));
    }

    [Fact]
    public void FindConsumers_accepts_server_slash_database()
    {
        Assert.Equal(2, Tools().FindConsumers("contoso-sql/orders", 20).Select(c => c.App).Distinct().Count());
    }

    [Fact]
    public void Shared_unreferenced_and_unresolved_queries()
    {
        var tools = Tools();
        Assert.Contains(tools.ListSharedResources(2, 20), s => s.Resource == "contosodata");
        Assert.Contains(tools.ListUnreferencedResources("SqlDatabase", 20), u => u.Resource == "contoso-sql/customers");
        Assert.Contains(tools.ListUnresolvedConnections(20), u => u.App == "billing-api");
        Assert.Contains(tools.ListSecretLikePlainSettings(20), f => f.App == "shop-web" && f.SettingKey == "Storage");
        Assert.DoesNotContain(tools.ListSecretLikePlainSettings(20), f => f.SettingKey == "ApiKey");
    }

    [Fact]
    public void Results_respect_the_hard_cap()
    {
        var tools = Tools(cap: 2);
        Assert.True(tools.SearchNodes("", null, 1000).Length <= 2);
        Assert.True(tools.ListUnreferencedResources(null, 1000).Length <= 2);
        Assert.True(tools.ListApps(null, null, null, 1000).Length <= 2);
    }
}

public class LargeFixtureTests(ITestOutputHelper output)
{
    private static readonly HashSet<string> Structural =
        [EdgeTypes.Contains, EdgeTypes.HasSlot, EdgeTypes.HostsDatabase, EdgeTypes.HasContainer, EdgeTypes.HasQueue];

    [Fact]
    public void Large_snapshot_matches_the_plan_counts()
    {
        var graph = Fixture.Builder().Build(Fixture.LargeSnapshot(), new GraphBuildOptions());
        int Count(string type) => graph.Nodes.Count(n => n.Type == type);

        Assert.Equal(400, Count("WebApp"));
        Assert.Equal(40, Count("WebAppSlot"));
        Assert.Equal(40, Count("SqlServer"));
        Assert.Equal(380, Count("SqlDatabase"));
        Assert.Equal(30, Count("StorageAccount"));
        Assert.Equal(300, Count("BlobContainer"));
        Assert.Equal(60, Count("StorageQueue"));
        Assert.Equal(20, Count("KeyVault"));
        Assert.Equal(0, Count("External"));
        Assert.InRange(graph.Nodes.Count, 1250, 1350);

        var hub = Assert.Single(graph.Nodes, n => n.Type == "ExternalService");
        Assert.Equal(120, graph.Edges.Count(e => e.Target == hub.Id));

        var consumed = graph.Edges.Where(e => e.Type == EdgeTypes.ConnectsTo).Select(e => e.Target).ToHashSet();
        Assert.Equal(60, graph.Nodes.Count(n => n.Type == "SqlDatabase" && !consumed.Contains(n.Id)));

        var dependencies = graph.Edges.Where(e => !Structural.Contains(e.Type)).ToList();
        var low = dependencies.Count(e => e.Data.GetValueOrDefault("confidence") as string == "Low");
        Assert.InRange(low / (double)dependencies.Count, 0.04, 0.06);

        var ids = graph.Nodes.Select(n => n.Id).ToHashSet();
        Assert.All(graph.Edges, e => Assert.True(ids.Contains(e.Source) && ids.Contains(e.Target)));
    }

    [Fact]
    public void Large_snapshot_is_deterministic_per_seed()
    {
        static string Json(int seed) => System.Text.Json.JsonSerializer.Serialize(
            Fixture.Builder().Build(Fixture.LargeSnapshot(seed: seed), new GraphBuildOptions()), GraphJson.Options);

        Assert.Equal(Json(1), Json(1));
        Assert.NotEqual(Json(1), Json(2));
    }

    // Baseline for the graph performance plan (T0, AC11): logs the size of the full GraphDocument stream.
    // Set GRAPH_FIXTURE_OUT to a file path to also write the payload for browser measurements.
    [Theory]
    [InlineData(400)]
    [InlineData(1200)]
    public void Baseline_full_graph_payload(int apps)
    {
        var snapshot = Fixture.LargeSnapshot(apps);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var graph = Fixture.Builder().Build(snapshot, new GraphBuildOptions());
        var buildMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(graph, GraphJson.Options);
        var serializeMs = sw.Elapsed.TotalMilliseconds;

        var dependencies = graph.Edges.Count(e => !Structural.Contains(e.Type));
        output.WriteLine($"apps={apps} nodes={graph.Nodes.Count} edges={graph.Edges.Count} (dependency {dependencies}) " +
                         $"payload={bytes.Length:N0} bytes build={buildMs:F0} ms serialize={serializeMs:F0} ms");

        if (apps == 400 && Environment.GetEnvironmentVariable("GRAPH_FIXTURE_OUT") is { Length: > 0 } path)
        {
            File.WriteAllBytes(path, bytes);
        }
        Assert.True(bytes.Length > 0);
    }
}
