using BlazorAzureInventory.Components.Graph;
using BlazorAzureInventory.Components.Inventory;
using BlazorAzureInventory.Components.Layout;
using BlazorAzureInventory.Models.Configuration;
using BlazorAzureInventory.Models.Graph;
using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Radzen;

namespace BlazorAzureInventory.Tests;

public class ComponentTests : BunitContext
{
    public ComponentTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddRadzenComponents();
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

    private sealed class FakeAntiforgeryStateProvider : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => new("test-token", "__RequestVerificationToken");
    }
}
