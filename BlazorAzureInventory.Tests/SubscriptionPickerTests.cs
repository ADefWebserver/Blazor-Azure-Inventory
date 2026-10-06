using BlazorAzureInventory.Models.Inventory;
using BlazorAzureInventory.Services.Azure;
using Bunit;
using Radzen;

namespace BlazorAzureInventory.Tests;

public class SubscriptionPickerTests : BunitContext
{
    private static readonly AzureContext Context = new(
        [new TenantInfo("t1", "Default Directory", "contoso.onmicrosoft.com")],
        [
            new SubscriptionInfo("s1", "Azure Free Trial", "t1", "Enabled"),
            new SubscriptionInfo("s2", "Extended Azure Credit", "t1", "Enabled"),
            new SubscriptionInfo("s3", "Princess Valerie LLC", "t1", "Enabled"),
        ]);

    public SubscriptionPickerTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddRadzenComponents();
    }

    [Fact]
    public void Items_can_be_selected_after_unselecting_all()
    {
        var cut = Render<SubscriptionPickerHost>(p => p
            .Add(x => x.Context, Context)
            .Add(x => x.Selected, new List<string> { "s1", "s2", "s3" }));

        cut.Find(".rz-multiselect-header .rz-chkbox-box").Click();
        Assert.Empty(cut.Instance.Selected);

        cut.FindAll("li.rz-multiselect-item")[0].Click();
        Assert.Equal(["s1"], cut.Instance.Selected.ToList());

        cut.FindAll("li.rz-multiselect-item")[1].Click();
        Assert.Equal(["s1", "s2"], cut.Instance.Selected.OrderBy(x => x).ToList());
    }

    [Fact]
    public void Items_can_be_deselected_and_reselected()
    {
        var cut = Render<SubscriptionPickerHost>(p => p
            .Add(x => x.Context, Context)
            .Add(x => x.Selected, new List<string> { "s1", "s2", "s3" }));

        cut.FindAll("li.rz-multiselect-item")[0].Click();
        Assert.Equal(["s2", "s3"], cut.Instance.Selected.OrderBy(x => x).ToList());

        cut.FindAll("li.rz-multiselect-item")[0].Click();
        Assert.Equal(["s1", "s2", "s3"], cut.Instance.Selected.OrderBy(x => x).ToList());
    }
}
