using BlazorAzureInventory.Components.Inventory;
using BlazorAzureInventory.Models.Inventory;
using BlazorAzureInventory.Services.Azure;
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

    [Fact]
    public void RequiredRoles_fills_commands_with_signed_in_user()
    {
        var user = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim("preferred_username", "alex@contoso.com")], "test"));

        var cut = Render<BlazorAzureInventory.Components.Shared.RequiredRoles>(p => p
            .AddCascadingValue(Task.FromResult(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(user))));

        var markup = System.Net.WebUtility.HtmlDecode(cut.Markup);
        Assert.Contains("az role assignment create --assignee \"alex@contoso.com\" --role \"Reader\"", markup);
        Assert.DoesNotContain("<user@domain>", markup);
    }

    [Fact]
    public void RequiredRoles_uses_placeholder_when_signed_out()
    {
        var cut = Render<BlazorAzureInventory.Components.Shared.RequiredRoles>(p => p
            .AddCascadingValue(Task.FromResult(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(new System.Security.Claims.ClaimsPrincipal()))));

        Assert.Contains("--assignee \"<user@domain>\"", System.Net.WebUtility.HtmlDecode(cut.Markup));
    }

    // Permission payloads shaped like GET {scope}/providers/Microsoft.Authorization/permissions for built-in roles.
    private const string ReaderPerms = """{"value":[{"actions":["*/read"],"notActions":[]}]}""";
    private const string OwnerPerms = """{"value":[{"actions":["*"],"notActions":[]}]}""";
    private const string ContributorPerms = """{"value":[{"actions":["*"],"notActions":["Microsoft.Authorization/*/Delete","Microsoft.Authorization/*/Write"]}]}""";
    private const string UserAccessAdminPerms = """{"value":[{"actions":["*/read","Microsoft.Authorization/*","Microsoft.Support/*"],"notActions":[]}]}""";
    private const string ReaderAndWebsitePerms = """{"value":[{"actions":["*/read"]},{"actions":["Microsoft.Web/sites/*","Microsoft.Web/serverFarms/join/action"]}]}""";

    [Theory]
    [InlineData(OwnerPerms, "", true)]
    [InlineData(ContributorPerms, "", false)]
    [InlineData(UserAccessAdminPerms, "Website Contributor,Tag Contributor", true)]
    [InlineData(ReaderPerms, "Website Contributor,Tag Contributor", false)]
    [InlineData(ReaderAndWebsitePerms, "Tag Contributor", false)]
    [InlineData("""{"value":[]}""", "Reader,Website Contributor,Tag Contributor", false)]
    public void Self_assignment_detects_missing_roles_and_assign_permission(string json, string expectedMissing, bool canAssign)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        Assert.Equal(expectedMissing.Split(',', StringSplitOptions.RemoveEmptyEntries), RoleSelfAssignmentService.MissingRoles(doc.RootElement));
        Assert.Equal(canAssign, RoleSelfAssignmentService.CanAssign(doc.RootElement));
    }

    [Fact]
    public void Self_assignment_has_a_definition_for_every_required_role()
    {
        Assert.All(AzureRoleRequirements.All, r => Assert.True(RoleSelfAssignmentService.RoleDefinitionIds.ContainsKey(r.Role), r.Role));
    }

    [Fact]
    public void Self_assignment_reads_object_id_from_token()
    {
        static string Encode(string json) => Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(json);
        var token = $"{Encode("""{"alg":"none","typ":"JWT"}""")}.{Encode("""{"oid":"33333333-3333-3333-3333-333333333333","tid":"t"}""")}.";
        var noOid = $"{Encode("""{"alg":"none","typ":"JWT"}""")}.{Encode("""{"tid":"t"}""")}.";

        Assert.Equal("33333333-3333-3333-3333-333333333333", RoleSelfAssignmentService.ObjectIdFromToken(token));
        Assert.Null(RoleSelfAssignmentService.ObjectIdFromToken(noOid));
    }
}
