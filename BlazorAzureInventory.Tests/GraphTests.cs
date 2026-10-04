using BlazorAzureInventory.Models.Configuration;
using BlazorAzureInventory.Models.Graph;
using BlazorAzureInventory.Services.AI.GraphTools;
using BlazorAzureInventory.Services.Analysis;
using BlazorAzureInventory.Services.Graph;

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
