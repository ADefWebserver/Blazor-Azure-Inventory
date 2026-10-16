using System.Text.Json;
using BlazorAzureInventory.Models.Graph;
using BlazorAzureInventory.Models.Options;
using BlazorAzureInventory.Services.Graph;
using Xunit.Abstractions;

namespace BlazorAzureInventory.Tests;

/// <summary>Overview projection of the Knowledge Graph (plan section 6.4).</summary>
public class GraphViewServiceTests(ITestOutputHelper output)
{
    private static readonly GraphViewService Service = new(Microsoft.Extensions.Options.Options.Create(new GraphViewOptions()));

    private static GraphIndex SampleIndex() => new(Fixture.Builder().Build(Fixture.Snapshot(), new GraphBuildOptions()));

    private static GraphLayout Layout(GraphDocument graph, bool positions = false) => new(graph,
        graph.Nodes.Where(_ => positions)
            .Select((n, i) => (n.Id, P: ((float)(i * 37 % 10_000), (float)(i * 53 % 10_000))))
            .ToDictionary(x => x.Id, x => x.P),
        false, TimeSpan.Zero);

    private static GraphViewPayload Overview(GraphIndex index, bool children = false) =>
        Service.BuildOverview(index, Layout(index.Graph), children);

    private static int IndexOf(GraphViewPayload payload, string id) =>
        payload.NodeIds.ToList().FindIndex(x => x.Equals(id, StringComparison.OrdinalIgnoreCase));

    private static ViewEdge? EdgeBetween(GraphViewPayload payload, string source, string target, string type = EdgeTypes.ConnectsTo) =>
        payload.Edges.SingleOrDefault(e => e.Source == IndexOf(payload, source) && e.Target == IndexOf(payload, target) && e.Type == type);

    private static GraphNode Node(string id, string type, string label, Dictionary<string, object?>? data = null) =>
        new() { Id = id, Type = type, Label = label, Data = data ?? new() };

    private static GraphEdge Edge(string source, string type, string target, string? confidence = null) => new()
    {
        Id = InventoryGraphBuilder.EdgeId(source, type, target), Source = source, Target = target, Type = type,
        Data = confidence is null ? new() : new() { ["confidence"] = confidence }
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Overview_excludes_scope_nodes_and_structural_edges(bool children)
    {
        var payload = Overview(SampleIndex(), children);

        Assert.DoesNotContain(payload.Nodes, n => NodeCategories.ByType[n.Type] == NodeCategories.Scope);
        Assert.DoesNotContain(payload.Edges, e => EdgeTypes.Structural.Contains(e.Type));
        Assert.Contains(payload.Edges, e => e.Type == EdgeTypes.ConnectsTo);
        Assert.Equal(GraphViewMode.Overview, payload.Mode);
        Assert.Null(payload.FocusIndex);
        Assert.Equal(payload.Nodes.Count, payload.NodeIds.Count);
        Assert.Equal(payload.Edges.Count, payload.EdgeSources.Count);
    }

    [Fact]
    public void Overview_collapses_children_into_parents()
    {
        var payload = Overview(SampleIndex());

        Assert.Equal(-1, IndexOf(payload, Fixture.OrdersDb));
        Assert.Equal(-1, IndexOf(payload, Fixture.Uploads));
        Assert.Equal(-1, IndexOf(payload, Fixture.ShopWebStaging));
        Assert.NotNull(EdgeBetween(payload, Fixture.ShopWeb, Fixture.ContosoSql));
        Assert.NotNull(EdgeBetween(payload, Fixture.OrdersFunc, Fixture.ContosoSql));

        Assert.Equal(2, payload.Nodes[IndexOf(payload, Fixture.ContosoSql)].CollapsedChildren);
        Assert.Equal(2, payload.Nodes[IndexOf(payload, Fixture.ContosoData)].CollapsedChildren);
        Assert.Equal(1, payload.Nodes[IndexOf(payload, Fixture.ShopWeb)].CollapsedChildren);
        Assert.Null(payload.Nodes[IndexOf(payload, Fixture.ContosoKv)].CollapsedChildren);
    }

    [Fact]
    public void Overview_merges_parallel_edges_and_keeps_sources()
    {
        var payload = Overview(SampleIndex());

        var edge = EdgeBetween(payload, Fixture.ShopWeb, Fixture.ContosoData);
        Assert.NotNull(edge);
        Assert.Equal(2, edge.Count);
        var sources = payload.EdgeSources[payload.Edges.ToList().IndexOf(edge)];
        Assert.Equal(
            new[] { InventoryGraphBuilder.EdgeId(Fixture.ShopWeb, EdgeTypes.ConnectsTo, Fixture.Uploads),
                    InventoryGraphBuilder.EdgeId(Fixture.ShopWeb, EdgeTypes.ConnectsTo, Fixture.ContosoData) }.Order(),
            sources.Order());

        Assert.Null(EdgeBetween(payload, Fixture.OrdersFunc, Fixture.ContosoSql)!.Count);
        var web = IndexOf(payload, Fixture.ShopWeb);
        Assert.Equal(payload.Edges.Count(e => e.Source == web || e.Target == web), payload.Nodes[web].Degree);
    }

    [Fact]
    public void Overview_low_flag_only_when_all_merged_edges_low()
    {
        const string Web = "contoso-web", Api = "contoso-api", Sql = "contoso-sql", Db1 = "contoso-sql/contosodb", Db2 = "contoso-sql/contosodb2";
        var graph = new GraphDocument
        {
            Nodes = [Node(Web, "WebApp", "contoso-web"), Node(Api, "WebApp", "contoso-api"), Node(Sql, "SqlServer", "contoso-sql"),
                     Node(Db1, "SqlDatabase", "ContosoDb"), Node(Db2, "SqlDatabase", "ContosoDb2")],
            Edges = [Edge(Sql, EdgeTypes.HostsDatabase, Db1), Edge(Sql, EdgeTypes.HostsDatabase, Db2),
                     Edge(Web, EdgeTypes.ConnectsTo, Db1, "Low"), Edge(Web, EdgeTypes.ConnectsTo, Db2, "High"),
                     Edge(Api, EdgeTypes.ConnectsTo, Db1, "Low"), Edge(Api, EdgeTypes.ConnectsTo, Db2, "Low")]
        };
        var payload = Overview(new GraphIndex(graph));

        var mixed = EdgeBetween(payload, Web, Sql)!;
        Assert.Equal(2, mixed.Count);
        Assert.Null(mixed.Low);
        var allLow = EdgeBetween(payload, Api, Sql)!;
        Assert.Equal(2, allLow.Count);
        Assert.True(allLow.Low);
        Assert.Equal(2, payload.Nodes[IndexOf(payload, Sql)].Degree);
    }

    [Fact]
    public void Overview_with_children_keeps_child_nodes()
    {
        var payload = Overview(SampleIndex(), children: true);

        Assert.NotEqual(-1, IndexOf(payload, Fixture.OrdersDb));
        Assert.NotEqual(-1, IndexOf(payload, Fixture.Uploads));
        Assert.NotEqual(-1, IndexOf(payload, Fixture.ShopWebStaging));
        Assert.NotNull(EdgeBetween(payload, Fixture.ShopWeb, Fixture.OrdersDb));
        Assert.All(payload.Nodes, n => Assert.Null(n.CollapsedChildren));
        Assert.All(payload.Edges, e => Assert.Null(e.Count));
    }

    [Fact]
    public void Overview_is_deterministic()
    {
        static (string Json, GraphViewPayload Payload) Build()
        {
            var graph = Fixture.Builder().Build(Fixture.LargeSnapshot(), new GraphBuildOptions());
            var payload = Service.BuildOverview(new GraphIndex(graph), Layout(graph, positions: true), false);
            return (JsonSerializer.Serialize(payload, GraphViewJson.Options), payload);
        }

        var a = Build();
        var b = Build();

        Assert.Equal(a.Json, b.Json);
        Assert.Equal(a.Payload.NodeIds, b.Payload.NodeIds);
        Assert.Equal(a.Payload.EdgeSources.SelectMany(s => s), b.Payload.EdgeSources.SelectMany(s => s));
        Assert.Equal(a.Payload.NodeIds.Order(StringComparer.Ordinal), a.Payload.NodeIds);
        Assert.All(a.Payload.Nodes, n => Assert.True(n.X is not null && n.Y is not null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Payload_contains_no_ids_tags_or_references(bool children)
    {
        var index = SampleIndex();
        index.NodesById[Fixture.ShopWeb].Data[TagKeys.Tags] =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["contoso-owner"] = "contoso-tag-value" };

        var json = JsonSerializer.Serialize(Service.BuildOverview(index, Layout(index.Graph, positions: true), children), GraphViewJson.Options);

        string[] forbidden =
        [
            "/subscriptions/", "/providers/", "external:", "tenant:", "\"references\"", "\"tags\"", "contoso-owner", "contoso-tag-value",
            "ConnectionStrings:Orders", "ConnectionStrings:Legacy", "Uploads:Uri", "ApiKey", "SqlConnection", "AzureWebJobsStorage",
            ".windows.net", ".azurewebsites.net", ".azurecr.io", "appsettings.json", "Production"
        ];
        Assert.All(forbidden, value => Assert.DoesNotContain(value, json, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("\"mode\":\"Overview\"", json);
    }

    [Fact]
    public void Payload_rejects_invalid_colour()
    {
        var graph = new GraphDocument
        {
            Nodes =
            [
                Node("external:service:a", "ExternalService", "Contoso Mail", new() { ["color"] = "#12345G", ["category"] = "Email" }),
                Node("external:service:b", "ExternalService", "Contoso Pay", new() { ["color"] = "red", ["category"] = "" }),
                Node("external:service:c", "ExternalService", "Contoso Sms", new() { ["color"] = "#1A82E2", ["category"] = "Messaging" }),
                Node("contoso-web", "WebApp", "contoso-web", new() { ["color"] = "#1A82E2", ["category"] = "Email" }),
            ]
        };
        var payload = Overview(new GraphIndex(graph));

        ViewNode ByLabel(string label) => payload.Nodes.Single(n => n.Label == label);
        Assert.Null(ByLabel("Contoso Mail").Color);
        Assert.Equal("Email", ByLabel("Contoso Mail").Category);
        Assert.Null(ByLabel("Contoso Pay").Color);
        Assert.Null(ByLabel("Contoso Pay").Category);
        Assert.Equal("#1A82E2", ByLabel("Contoso Sms").Color);
        Assert.Null(ByLabel("contoso-web").Color);
        Assert.Null(ByLabel("contoso-web").Category);
    }

    [Fact]
    public void Overview_maps_scope_lookups_and_counts_totals()
    {
        var index = SampleIndex();
        var payload = Overview(index);

        Assert.Equal([Fixture.SubscriptionId], payload.Subscriptions);
        Assert.Equal(["rg-data", "rg-shop"], payload.ResourceGroups);
        var sql = payload.Nodes[IndexOf(payload, Fixture.ContosoSql)];
        Assert.Equal(0, sql.Sub);
        Assert.Equal("rg-data", payload.ResourceGroups[sql.Rg!.Value]);
        Assert.Equal(index.Graph.Nodes.Count(n => NodeCategories.ByType[n.Type] != NodeCategories.Scope), payload.TotalNodes);
        Assert.Equal(index.Graph.Edges.Count(e => !EdgeTypes.Structural.Contains(e.Type)), payload.TotalEdges);
    }

    // AC11: the default Overview payload is at least 3x smaller than the full GraphDocument stream.
    [Fact]
    public void Overview_payload_of_large_fixture_is_at_least_3x_smaller()
    {
        var graph = Fixture.Builder().Build(Fixture.LargeSnapshot(), new GraphBuildOptions());
        var full = JsonSerializer.SerializeToUtf8Bytes(graph, GraphJson.Options).Length;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var payload = Service.BuildOverview(new GraphIndex(graph), Layout(graph, positions: true), false);
        var buildMs = sw.Elapsed.TotalMilliseconds;
        var overview = JsonSerializer.SerializeToUtf8Bytes(payload, GraphViewJson.Options).Length;

        output.WriteLine($"overview nodes={payload.Nodes.Count} edges={payload.Edges.Count} payload={overview:N0} bytes " +
                         $"full={full:N0} bytes ratio={full / (double)overview:F1}x build={buildMs:F0} ms");
        Assert.Equal(491, payload.Nodes.Count);
        Assert.True(full >= 3 * overview, $"Overview {overview} bytes vs full {full} bytes");
    }

    [Fact]
    public void Focus_returns_one_hop_and_parent_context_without_other_consumers()
    {
        var index = SampleIndex();
        var payload = Service.BuildFocus(index, Fixture.ShopWeb, [], hideEmulator: true, showLowConfidence: false)!;

        Assert.Equal(GraphViewMode.Focus, payload.Mode);
        Assert.Equal(0, payload.FocusIndex);
        Assert.Contains(Fixture.OrdersDb, payload.NodeIds);
        Assert.Contains(Fixture.ContosoSql, payload.NodeIds);
        Assert.Contains(Fixture.ContosoKv, payload.NodeIds);
        Assert.DoesNotContain(Fixture.OrdersFunc, payload.NodeIds);
        Assert.Equal(IndexOf(payload, Fixture.ContosoSql), payload.Nodes[IndexOf(payload, Fixture.OrdersDb)].Parent);
        Assert.Contains(payload.Edges, e => e.Type == EdgeTypes.HasSlot);
    }

    [Fact]
    public void Focus_hub_is_aggregated_above_neighbour_cap()
    {
        const string hub = "sendgrid";
        var nodes = Enumerable.Range(0, 120)
            .Select(i => Node($"app-{i:D3}", "WebApp", $"app-{i:D3}"))
            .Append(Node(hub, "ExternalService", "SendGrid"))
            .ToList();
        var edges = Enumerable.Range(0, 120).Select(i => Edge($"app-{i:D3}", EdgeTypes.ConnectsTo, hub)).ToList();
        var service = WithOptions(new GraphViewOptions { FocusMaxNeighbours = 60 });
        var payload = service.BuildFocus(new GraphIndex(new GraphDocument { Nodes = nodes, Edges = edges }),
            hub, [], hideEmulator: false, showLowConfidence: false)!;

        Assert.Equal(62, payload.Nodes.Count);
        var aggregate = Assert.Single(payload.Nodes, n => n.Aggregate == true);
        Assert.Equal("+60 more apps", aggregate.Label);
        Assert.Equal(61, payload.Edges.Count);
    }

    [Fact]
    public void Focus_aggregate_retains_hidden_member_ids()
    {
        var (index, edges) = HubIndex(120);
        var payload = WithOptions(new GraphViewOptions { FocusMaxNeighbours = 60 })
            .BuildFocus(index, "sendgrid", [], hideEmulator: false, showLowConfidence: false)!;

        var (aggregateIndex, membership) = Assert.Single(payload.Aggregates);
        Assert.True(payload.Nodes[aggregateIndex].Aggregate);
        Assert.Equal("sendgrid", membership.CenterId);
        Assert.Equal(NodeCategories.Apps, membership.Category);
        Assert.Equal(Enumerable.Range(60, 60).Select(HubAppId), membership.Members.Select(m => m.NodeId));
        Assert.All(membership.Members, m => Assert.Same(edges.Single(e => e.Source == m.NodeId), m.Edge));
        Assert.All(membership.Members, m => Assert.DoesNotContain(m.NodeId, payload.NodeIds));
    }

    [Theory]
    [InlineData(4, "+1 more apps")]
    [InlineData(3, null)]
    public void Focus_aggregate_excludes_members_made_visible_by_expansion(int apps, string? expectedLabel)
    {
        // app-0 and app-1 are shown around the hub, the rest are hidden. app-2 also talks to app-0, so expanding app-0 draws it.
        var nodes = Enumerable.Range(0, apps).Select(i => Node($"app-{i}", "WebApp", $"app-{i}"))
            .Append(Node("hub", "ExternalService", "Hub")).ToList();
        var edges = Enumerable.Range(0, apps).Select(i => Edge($"app-{i}", EdgeTypes.ConnectsTo, "hub"))
            .Append(Edge("app-2", EdgeTypes.ConnectsTo, "app-0")).ToList();
        var index = new GraphIndex(new GraphDocument { Nodes = nodes, Edges = edges });
        var service = WithOptions(new GraphViewOptions { FocusMaxNeighbours = 2 });

        var before = service.BuildFocus(index, "hub", [], hideEmulator: false, showLowConfidence: false)!;
        Assert.Equal($"+{apps - 2} more apps", before.Nodes[Assert.Single(before.Aggregates).Key].Label);

        var payload = service.BuildFocus(index, "hub", ["app-0"], hideEmulator: false, showLowConfidence: false)!;

        Assert.Contains("app-2", payload.NodeIds);
        Assert.NotNull(EdgeBetween(payload, "app-2", "hub"));
        if (expectedLabel is null)
        {
            Assert.Empty(payload.Aggregates);
            Assert.DoesNotContain(payload.Nodes, n => n.Aggregate == true);
            Assert.Equal(payload.EdgeSources.Count(s => s.Count > 0), payload.Edges.Count);
            Assert.Equal(3, payload.Nodes[IndexOf(payload, "hub")].Degree);
        }
        else
        {
            var (aggregateIndex, membership) = Assert.Single(payload.Aggregates);
            Assert.Equal(expectedLabel, payload.Nodes[aggregateIndex].Label);
            Assert.Equal(["app-3"], membership.Members.Select(m => m.NodeId));
        }
    }

    [Fact]
    public void Focus_structural_aggregate_members_have_structural_edge()
    {
        var nodes = Enumerable.Range(0, 4).Select(i => Node($"db-{i}", "SqlDatabase", $"db-{i}"))
            .Append(Node("sql", "SqlServer", "SQL")).ToList();
        var edges = Enumerable.Range(0, 4).Select(i => Edge("sql", EdgeTypes.HostsDatabase, $"db-{i}")).ToList();
        var index = new GraphIndex(new GraphDocument { Nodes = nodes, Edges = edges });

        var payload = WithOptions(new GraphViewOptions { FocusMaxNeighbours = 2 })
            .BuildFocus(index, "sql", [], hideEmulator: false, showLowConfidence: false)!;

        var membership = Assert.Single(payload.Aggregates).Value;
        Assert.Equal(["db-2", "db-3"], membership.Members.Select(m => m.NodeId));
        Assert.All(membership.Members, m => Assert.Equal(EdgeTypes.HostsDatabase, m.Edge.Type));
        var items = Assert.Single(Service.SummarizeMembers(index, membership)).Items;
        Assert.All(items, item =>
        {
            Assert.Equal(EdgeTypes.HostsDatabase, item.EdgeType);
            Assert.Equal("", item.SettingKeys);
            Assert.Null(item.Confidence);
        });
    }

    [Fact]
    public void Focus_payload_json_contains_no_aggregate_member_ids()
    {
        var (index, _) = HubIndex(120);
        var payload = WithOptions(new GraphViewOptions { FocusMaxNeighbours = 60 })
            .BuildFocus(index, "sendgrid", [], hideEmulator: false, showLowConfidence: false)!;
        Assert.Equal(60, Assert.Single(payload.Aggregates).Value.Members.Count);

        var json = JsonSerializer.Serialize(payload, GraphViewJson.Options);

        Assert.DoesNotContain("/subscriptions/", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("aggregate:", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Aggregates", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SendGrid:ApiKeyName", json, StringComparison.OrdinalIgnoreCase);
        Assert.All(Enumerable.Range(60, 60), i => Assert.DoesNotContain($"contoso-web-{i:D3}", json));
    }

    [Fact]
    public void SummarizeMembers_lists_members_with_setting_keys_only()
    {
        const string secret = "contoso-secret-value";
        var (index, edges) = HubIndex(3);
        foreach (var edge in edges) edge.Data["value"] = secret;
        var membership = new AggregateMembership("sendgrid", NodeCategories.Apps,
        [
            new AggregateMember(HubAppId(2), edges[2]),
            new AggregateMember("missing", edges[1]),
            new AggregateMember(HubAppId(1), edges[1])
        ]);

        var group = Assert.Single(Service.SummarizeMembers(index, membership));

        Assert.Equal(NodeCategories.Apps, group.Category);
        Assert.Equal([HubAppId(2), HubAppId(1)], group.Items.Select(i => i.NodeId));
        Assert.All(group.Items, item =>
        {
            Assert.Equal("SendGrid:ApiKeyName", item.SettingKeys);
            Assert.Equal("High", item.Confidence);
            Assert.Equal(EdgeTypes.ConnectsTo, item.EdgeType);
            Assert.DoesNotContain(secret, $"{item.NodeId}{item.Label}{item.Type}{item.EdgeType}{item.SettingKeys}{item.Confidence}");
        });
        Assert.Equal("contoso-web-002", group.Items[0].Label);
        Assert.Empty(Service.SummarizeMembers(index, membership with { Members = [] }));
    }

    private static string HubAppId(int i) => $"/subscriptions/sub-1/providers/Microsoft.Web/sites/contoso-web-{i:D3}";

    /// <summary><paramref name="apps"/> web apps that each talk to the <c>sendgrid</c> hub through a SendGrid API key setting.</summary>
    private static (GraphIndex Index, List<GraphEdge> Edges) HubIndex(int apps)
    {
        var nodes = Enumerable.Range(0, apps)
            .Select(i => Node(HubAppId(i), "WebApp", $"contoso-web-{i:D3}"))
            .Append(Node("sendgrid", "ExternalService", "SendGrid"))
            .ToList();
        var edges = Enumerable.Range(0, apps).Select(i =>
        {
            var edge = Edge(HubAppId(i), EdgeTypes.ConnectsTo, "sendgrid", "High");
            edge.Data["references"] = new List<EdgeReference> { new("SendGrid:ApiKeyName", Confidence: "High") };
            return edge;
        }).ToList();
        return (new GraphIndex(new GraphDocument { Nodes = nodes, Edges = edges }), edges);
    }

    [Fact]
    public void Focus_sets_hidden_neighbour_count()
    {
        const string hub = "sendgrid";
        var nodes = Enumerable.Range(0, 120)
            .Select(i => Node($"app-{i:D3}", "WebApp", $"app-{i:D3}"))
            .Append(Node(hub, "ExternalService", "SendGrid"))
            .ToList();
        var edges = Enumerable.Range(0, 120).Select(i => Edge($"app-{i:D3}", EdgeTypes.ConnectsTo, hub)).ToList();
        var payload = Service.BuildFocus(new GraphIndex(new GraphDocument { Nodes = nodes, Edges = edges }),
            "app-000", [], hideEmulator: false, showLowConfidence: false)!;

        Assert.Equal(119, payload.Nodes[IndexOf(payload, hub)].HiddenNeighbours);
    }

    [Fact]
    public void Focus_expansion_adds_neighbour_hop_and_respects_node_cap()
    {
        var graph = new GraphDocument
        {
            Nodes = [Node("app-a", "WebApp", "App A"), Node("app-b", "WebApp", "App B"), Node("db", "SqlDatabase", "Database")],
            Edges = [Edge("app-a", EdgeTypes.ConnectsTo, "db"), Edge("app-b", EdgeTypes.ConnectsTo, "db")]
        };
        var index = new GraphIndex(graph);
        var expanded = Service.BuildFocus(index, "app-a", ["db"], hideEmulator: false, showLowConfidence: false)!;
        Assert.Contains("app-b", expanded.NodeIds);

        var cappedNodes = new List<GraphNode> { Node("app-a", "WebApp", "App A"), Node("app-b", "WebApp", "App B"), Node("db", "SqlDatabase", "Database") };
        var cappedEdges = new List<GraphEdge> { Edge("app-a", EdgeTypes.ConnectsTo, "db"), Edge("app-b", EdgeTypes.ConnectsTo, "db") };
        for (var i = 0; i < 25; i++)
        {
            var id = $"filler-{i:D2}";
            cappedNodes.Add(Node(id, "ServiceBusNamespace", id));
            cappedEdges.Add(Edge("app-a", EdgeTypes.ConnectsTo, id));
        }
        var strictCap = WithOptions(new GraphViewOptions { FocusMaxNodes = 20 });
        var limited = strictCap.BuildFocus(new GraphIndex(new GraphDocument { Nodes = cappedNodes, Edges = cappedEdges }),
            "app-a", ["db"], hideEmulator: false, showLowConfidence: false)!;
        Assert.Equal(20, limited.Nodes.Count);
        Assert.Contains("db", limited.NodeIds);
        Assert.DoesNotContain("app-b", limited.NodeIds);
    }

    [Fact]
    public void Focus_unknown_or_scope_id_returns_null()
    {
        var index = new GraphIndex(new GraphDocument { Nodes = [Node("tenant", "Tenant", "Tenant"), Node("app", "WebApp", "App")] });

        Assert.Null(Service.BuildFocus(index, "missing", [], hideEmulator: false, showLowConfidence: false));
        Assert.Null(Service.BuildFocus(index, "tenant", [], hideEmulator: false, showLowConfidence: false));
    }

    [Fact]
    public void Focus_honours_emulator_and_low_confidence_filters()
    {
        var graph = new GraphDocument
        {
            Nodes =
            [
                Node("app", "WebApp", "App"),
                Node("emulator", "External", "Emulator", new() { ["isLocalEmulator"] = true }),
                Node("low", "ExternalService", "Low confidence")
            ],
            Edges = [Edge("app", EdgeTypes.ConnectsTo, "emulator"), Edge("app", EdgeTypes.ConnectsTo, "low", "Low")]
        };
        var payload = Service.BuildFocus(new GraphIndex(graph), "app", [], hideEmulator: true, showLowConfidence: false)!;

        Assert.DoesNotContain("emulator", payload.NodeIds);
        Assert.DoesNotContain("low", payload.NodeIds);
        var shown = Service.BuildFocus(new GraphIndex(graph), "app", [], hideEmulator: false, showLowConfidence: true)!;
        Assert.Contains("emulator", shown.NodeIds);
        Assert.Contains("low", shown.NodeIds);
    }

    [Fact]
    public void MatchTag_matches_overflow_keys_and_value_substring()
    {
        var graph = new GraphDocument
        {
            Nodes = [Node("app", "WebApp", "App", new()
            {
                [TagKeys.Tags] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["conn-out-SqlDatabase-2"] = "Secret setting value"
                }
            })]
        };
        var index = new GraphIndex(graph);
        var payload = Overview(index);

        Assert.Equal([0], Service.MatchTag(payload, index, "conn-out-SqlDatabase", "SETTING"));
        Assert.Empty(Service.MatchTag(payload, index, "conn-out-SqlDatabase", "missing"));
        Assert.Empty(Service.MatchTag(payload, index, "conn-out-SqlDatabase-x", null));
    }

    [Fact]
    public void SummarizeDependencies_groups_by_category_with_setting_keys_only()
    {
        var graph = new GraphDocument
        {
            Nodes =
            [
                Node("app", "WebApp", "App"),
                Node("sql", "SqlServer", "SQL"),
                Node("vault", "KeyVault", "Vault"),
                Node("external", "ExternalService", "Mail")
            ],
            Edges =
            [
                Edge("app", EdgeTypes.ConnectsTo, "sql"),
                Edge("app", EdgeTypes.ReferencesSecret, "vault"),
                Edge("app", EdgeTypes.ConnectsTo, "external")
            ]
        };
        graph.Edges[0].Data["references"] = new List<EdgeReference>
        {
            new("ConnectionStrings:Orders", Confidence: "High"),
            new("Storage", Confidence: "High")
        };
        graph.Edges[1].Data["references"] = new List<EdgeReference> { new("ApiKey", Confidence: "High") };
        var groups = Service.SummarizeDependencies(new GraphIndex(graph), "app");

        Assert.Equal([NodeCategories.Data, NodeCategories.Secrets, NodeCategories.External], groups.Select(g => g.Category));
        Assert.Equal("ConnectionStrings:Orders, Storage", Assert.Single(groups[0].Items).SettingKeys);
        Assert.Equal("ApiKey", Assert.Single(groups[1].Items).SettingKeys);
        Assert.Equal("", Assert.Single(groups[2].Items).SettingKeys);
        Assert.Empty(Service.SummarizeDependencies(new GraphIndex(graph), "unknown"));
    }

    private static GraphViewService WithOptions(GraphViewOptions options) =>
        new(Microsoft.Extensions.Options.Options.Create(options));
}
