using BlazorAzureInventory.Components.Inventory;
using BlazorAzureInventory.Models.Inventory;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Radzen;

namespace BlazorAzureInventory.Tests;

public class RoleRequirementTests : BunitContext
{
    private const string Sub1 = "11111111-1111-1111-1111-111111111111";
    private const string Sub2 = "22222222-2222-2222-2222-222222222222";

    public RoleRequirementTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddRadzenComponents();
    }

    private static string Site(string sub, string name) => $"/subscriptions/{sub}/resourceGroups/rg/providers/Microsoft.Web/sites/{name}";

    private static ScanIssue Denied(string resourceId, RoleRequirement r) =>
        new(resourceId, r.Stage, ScanIssueKind.AccessDenied, $"403: {r.Hint}");

    [Fact]
    public void Detect_groups_access_denied_issues_by_role_with_subscriptions()
    {
        var issues = new[]
        {
            Denied(Site(Sub1, "a"), AzureRoleRequirements.AppSettings),
            Denied(Site(Sub2, "b"), AzureRoleRequirements.AppSettings),
            Denied(Site(Sub1, "a"), AzureRoleRequirements.ConfigFiles),
            Denied($"/subscriptions/{Sub2}/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/s", AzureRoleRequirements.BlobContainers),
        };

        var missing = AzureRoleRequirements.Detect(issues);

        Assert.Equal([AzureRoleRequirements.Reader, AzureRoleRequirements.WebsiteContributor], missing.Select(m => m.Role));
        var website = missing[1];
        Assert.Equal(3, website.IssueCount);
        Assert.Equal([Sub1, Sub2], website.SubscriptionIds);
        Assert.Contains(AzureRoleRequirements.AppSettings, website.Requirements);
        Assert.Contains(AzureRoleRequirements.ConfigFiles, website.Requirements);
    }

    [Fact]
    public void Detect_ignores_non_rbac_failures()
    {
        var issues = new[]
        {
            // Private-endpoint-only SCM site: access denied kind, but not a role problem.
            new ScanIssue(Site(Sub1, "a"), AzureRoleRequirements.ConfigFiles.Stage, ScanIssueKind.AccessDenied,
                "SCM site unreachable (ConnectionError). It may be restricted to a private endpoint."),
            new ScanIssue(Site(Sub1, "b"), AzureRoleRequirements.AppSettings.Stage, ScanIssueKind.Unexpected, "ArgumentNullException"),
            new ScanIssue(Site(Sub1, "c"), AzureRoleRequirements.AppSettings.Stage, ScanIssueKind.Throttled, "429"),
        };

        Assert.Empty(AzureRoleRequirements.Detect(issues));
    }

    [Fact]
    public void ScanIssuesDialog_names_missing_role_subscription_and_command()
    {
        var issues = new[] { Denied(Site(Sub1, "a"), AzureRoleRequirements.AppSettings) };
        var subscriptions = new[] { new SubscriptionInfo(Sub1, "Production", "tenant", "Enabled") };

        var cut = Render<ScanIssuesDialog>(p => p
            .Add(x => x.Issues, issues)
            .Add(x => x.Subscriptions, subscriptions)
            .Add(x => x.Assignee, "alex@contoso.com"));

        Assert.Contains("Missing Azure roles detected", cut.Markup);
        Assert.Contains("Production", cut.Markup);
        Assert.Contains("Microsoft.Web/sites/config/list/action", cut.Markup);
        Assert.Contains(
            $"az role assignment create --assignee \"alex@contoso.com\" --role \"Website Contributor\" --scope \"/subscriptions/{Sub1}\"",
            System.Net.WebUtility.HtmlDecode(cut.Markup));
    }

    [Fact]
    public void ScanIssuesDialog_hides_role_guidance_when_no_roles_are_missing()
    {
        var issues = new[] { new ScanIssue(Site(Sub1, "a"), AzureRoleRequirements.AppSettings.Stage, ScanIssueKind.Timeout, "timeout") };

        var cut = Render<ScanIssuesDialog>(p => p.Add(x => x.Issues, issues));

        Assert.DoesNotContain("Missing Azure roles detected", cut.Markup);
    }
}
