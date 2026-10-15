using System.Text.Json;
using BlazorAzureInventory.Models.Graph;
using BlazorAzureInventory.Models.Options;
using BlazorAzureInventory.Services.Graph;
using BlazorAzureInventory.Services.Persistence;

namespace BlazorAzureInventory.Tests;

/// <summary>Graph page URL parsing (<c>focus</c> and <c>expand</c>) and saved filter migration.</summary>
public class GraphFocusRouteTests
{
    private const string Web = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-contoso-web/providers/Microsoft.Web/sites/contoso-web";
    private const string Sql = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-contoso-data/providers/Microsoft.Sql/servers/contoso-sql";
    private const string Kv = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-contoso-data/providers/Microsoft.KeyVault/vaults/contoso-kv";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_null_or_blank_focus_returns_null(string? focus)
    {
        Assert.Null(GraphFocusRoute.Parse(focus, null, 20, out var dropped));
        Assert.Equal(0, dropped);
    }

    [Fact]
    public void Parse_ignores_expand_without_focus()
    {
        Assert.Null(GraphFocusRoute.Parse(null, [Sql, Kv], 20, out var dropped));
        Assert.Equal(2, dropped);
    }

    [Fact]
    public void Parse_trims_focus_and_expand()
    {
        var route = GraphFocusRoute.Parse($"  {Web} ", [$" {Sql}  "], 20, out var dropped);
        Assert.NotNull(route);
        Assert.Equal(Web, route.Focus);
        Assert.Equal([Sql], route.Expand);
        Assert.Equal(0, dropped);
    }

    [Fact]
    public void Parse_dedupes_expand_case_insensitively_and_removes_focus()
    {
        var route = GraphFocusRoute.Parse(Web, [Sql, Sql.ToUpperInvariant(), Web.ToLowerInvariant(), Kv, "", "  "], 20, out var dropped);
        Assert.NotNull(route);
        Assert.Equal([Sql, Kv], route.Expand);
        Assert.Equal(4, dropped);
    }

    [Fact]
    public void Parse_keeps_order_and_caps_expand_count()
    {
        var values = Enumerable.Range(1, 25).Select(i => $"{Sql}/databases/ContosoDb{i:00}").ToList();
        var route = GraphFocusRoute.Parse(Web, values, 20, out var dropped);
        Assert.NotNull(route);
        Assert.Equal(values.Take(20), route.Expand);
        Assert.Equal(5, dropped);
    }

    [Fact]
    public void Parse_zero_cap_drops_all_expand()
    {
        var route = GraphFocusRoute.Parse(Web, [Sql, Kv], 0, out var dropped);
        Assert.NotNull(route);
        Assert.Empty(route.Expand);
        Assert.Equal(2, dropped);
    }

    [Fact]
    public void Parse_drops_values_over_2048_chars()
    {
        var tooLong = Web + new string('a', GraphFocusRoute.MaxIdLength);
        Assert.Null(GraphFocusRoute.Parse(tooLong, [Sql], 20, out _));

        var route = GraphFocusRoute.Parse(Web, [tooLong, Sql], 20, out var dropped);
        Assert.NotNull(route);
        Assert.Equal([Sql], route.Expand);
        Assert.Equal(1, dropped);
    }

    [Fact]
    public void Routes_compare_by_value_ignoring_case_and_respecting_order()
    {
        var a = new GraphFocusRoute(Web, [Sql, Kv]);
        Assert.Equal(a, new GraphFocusRoute(Web.ToUpperInvariant(), [Sql.ToLowerInvariant(), Kv]));
        Assert.Equal(a.GetHashCode(), new GraphFocusRoute(Web.ToUpperInvariant(), [Sql.ToLowerInvariant(), Kv]).GetHashCode());
        Assert.NotEqual(a, new GraphFocusRoute(Web, [Kv, Sql]));
        Assert.NotEqual(a, new GraphFocusRoute(Web, [Sql]));
    }

    [Fact]
    public void Upgrade_turns_on_only_connected_for_version_0()
    {
        var filter = new GraphFilterState { FilterVersion = 0, OnlyConnected = false };
        filter.Upgrade();
        Assert.True(filter.OnlyConnected);
        Assert.Equal(GraphFilterState.CurrentFilterVersion, filter.FilterVersion);
        Assert.Equal(2, filter.FilterVersion);
    }

    [Fact]
    public void Upgrade_keeps_choice_for_current_version()
    {
        var filter = new GraphFilterState { FilterVersion = 2, OnlyConnected = false };
        filter.Upgrade();
        Assert.False(filter.OnlyConnected);
        Assert.Equal(2, filter.FilterVersion);
    }

    [Fact]
    public void New_filter_defaults_to_only_connected_without_children()
    {
        var filter = new GraphFilterState();
        Assert.True(filter.OnlyConnected);
        Assert.False(filter.ShowChildResources);
    }

    [Fact]
    public void Saved_filter_without_version_deserializes_as_version_0()
    {
        const string json = """{"graphFilter":{"nodeTypes":["WebApp"],"onlyConnected":false,"hideExternal":true}}""";
        var filter = JsonSerializer.Deserialize<UserPreferences>(json, PersistenceJson.Options)!.GraphFilter!;
        Assert.Equal(0, filter.FilterVersion);
        Assert.False(filter.OnlyConnected);
        Assert.False(filter.ShowChildResources);
        Assert.True(filter.HideExternal);

        filter.Upgrade();
        Assert.True(filter.OnlyConnected);
    }

    [Fact]
    public void Saved_filter_round_trips_version_and_child_toggle()
    {
        var prefs = new UserPreferences { GraphFilter = new GraphFilterState { FilterVersion = 2, OnlyConnected = false, ShowChildResources = true } };
        var back = JsonSerializer.Deserialize<UserPreferences>(JsonSerializer.Serialize(prefs, PersistenceJson.Options), PersistenceJson.Options)!.GraphFilter!;
        back.Upgrade();
        Assert.False(back.OnlyConnected);
        Assert.True(back.ShowChildResources);
    }

    [Fact]
    public void Every_node_type_has_a_category()
    {
        foreach (var (type, _) in NodeTypeStyles.All)
            Assert.Contains(NodeCategories.ByType[type], NodeCategories.Order);
    }

    [Fact]
    public void GraphView_options_defaults_match_appsettings()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BlazorAzureInventory", "appsettings.json"))) dir = dir.Parent;
        Assert.NotNull(dir);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "BlazorAzureInventory", "appsettings.json")));
        var section = doc.RootElement.GetProperty("GraphView");
        var defaults = new GraphViewOptions();

        Assert.Equal(defaults.UseWebGl, section.GetProperty(nameof(GraphViewOptions.UseWebGl)).GetBoolean());
        Assert.Equal(defaults.LayoutTimeoutSeconds, section.GetProperty(nameof(GraphViewOptions.LayoutTimeoutSeconds)).GetInt32());
        Assert.Equal(defaults.OverviewMaxNodes, section.GetProperty(nameof(GraphViewOptions.OverviewMaxNodes)).GetInt32());
        Assert.Equal(defaults.FocusMaxNeighbours, section.GetProperty(nameof(GraphViewOptions.FocusMaxNeighbours)).GetInt32());
        Assert.Equal(defaults.FocusMaxNodes, section.GetProperty(nameof(GraphViewOptions.FocusMaxNodes)).GetInt32());
        Assert.Equal(defaults.HoverHighlightMaxElements, section.GetProperty(nameof(GraphViewOptions.HoverHighlightMaxElements)).GetInt32());
        Assert.Equal(defaults.MaxExpandInUrl, section.GetProperty(nameof(GraphViewOptions.MaxExpandInUrl)).GetInt32());
    }
}
