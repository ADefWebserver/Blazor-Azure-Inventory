using System.Net;
using System.Text;
using BlazorAzureInventory.Components.Shared;
using BlazorAzureInventory.Models.Inventory;
using BlazorAzureInventory.Services.Auth;
using BlazorAzureInventory.Services.Azure;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Radzen;

namespace BlazorAzureInventory.Tests;

public class RoleGrantTests : BunitContext
{
    private const string Sub1 = "11111111-1111-1111-1111-111111111111";
    private const string UserId = "44444444-4444-4444-4444-444444444444";
    private static readonly SubscriptionInfo Production = new(Sub1, "Production", "tenant", "Enabled");

    private sealed class FakeHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : this((r, _) => respond(r)) { }

        public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request, body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private const string UserJson = $$"""{"id":"{{UserId}}","displayName":"Alex Wilber","userPrincipalName":"alex@contoso.com","mail":"alex@contoso.com"}""";

    [Fact]
    public async Task Lookup_resolves_member_upn_directly()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, UserJson));

        var user = await DirectoryUserLookup.FindAsync(new HttpClient(handler), "token", "  alex@contoso.com ", CancellationToken.None);

        Assert.Equal(UserId, user?.ObjectId);
        Assert.Equal("Alex Wilber (alex@contoso.com)", user?.Label);
        var request = Assert.Single(handler.Requests).Request;
        Assert.Equal("https://graph.microsoft.com/v1.0/users/alex%40contoso.com", request.RequestUri!.GetLeftPart(UriPartial.Path));
        Assert.Equal("Bearer token", request.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Lookup_falls_back_to_mail_filter_for_guests()
    {
        var handler = new FakeHandler(r => r.RequestUri!.AbsolutePath.EndsWith("/users")
            ? Json(HttpStatusCode.OK, $$"""{"value":[{{UserJson}}]}""")
            : Json(HttpStatusCode.NotFound, """{"error":{"code":"Request_ResourceNotFound"}}"""));

        var user = await DirectoryUserLookup.FindAsync(new HttpClient(handler), "token", "o'neil@fabrikam.com", CancellationToken.None);

        Assert.Equal(UserId, user?.ObjectId);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("mail eq 'o''neil@fabrikam.com' or userPrincipalName eq 'o''neil@fabrikam.com'",
            Uri.UnescapeDataString(handler.Requests[1].Request.RequestUri!.Query));
    }

    [Fact]
    public async Task Lookup_returns_null_when_not_found_and_rejects_ambiguous_matches()
    {
        var none = new FakeHandler(r => r.RequestUri!.AbsolutePath.EndsWith("/users")
            ? Json(HttpStatusCode.OK, """{"value":[]}""")
            : Json(HttpStatusCode.NotFound, "{}"));
        Assert.Null(await DirectoryUserLookup.FindAsync(new HttpClient(none), "t", "nobody@contoso.com", CancellationToken.None));

        var guid = new FakeHandler(_ => Json(HttpStatusCode.NotFound, "{}"));
        Assert.Null(await DirectoryUserLookup.FindAsync(new HttpClient(guid), "t", UserId, CancellationToken.None));
        Assert.Single(guid.Requests);

        var two = new FakeHandler(r => r.RequestUri!.AbsolutePath.EndsWith("/users")
            ? Json(HttpStatusCode.OK, $$"""{"value":[{{UserJson}},{{UserJson}}]}""")
            : Json(HttpStatusCode.NotFound, "{}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DirectoryUserLookup.FindAsync(new HttpClient(two), "t", "alex@contoso.com", CancellationToken.None));
    }

    [Fact]
    public async Task Lookup_reports_forbidden_and_rejects_invalid_input()
    {
        var forbidden = new FakeHandler(_ => Json(HttpStatusCode.Forbidden, """{"error":{"code":"Authorization_RequestDenied"}}"""));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            DirectoryUserLookup.FindAsync(new HttpClient(forbidden), "t", "alex@contoso.com", CancellationToken.None));
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);

        Assert.Null(DirectoryUserLookup.Normalize("   "));
        Assert.Null(DirectoryUserLookup.Normalize("a\nb"));
        Assert.Null(DirectoryUserLookup.Normalize(new string('a', 257)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            DirectoryUserLookup.FindAsync(new HttpClient(forbidden), "t", " ", CancellationToken.None));
    }

    [Fact]
    public async Task Assign_puts_role_assignment_for_principal_and_treats_existing_as_success()
    {
        var readerId = RoleSelfAssignmentService.RoleDefinitionIds[AzureRoleRequirements.Reader];
        var handler = new FakeHandler((_, body) => body!.Contains(readerId)
            ? Json(HttpStatusCode.Conflict, """{"error":{"code":"RoleAssignmentExists"}}""")
            : Json(HttpStatusCode.Created, "{}"));

        var results = await RoleSelfAssignmentService.AssignAsync(new HttpClient(handler), "token", Production,
            [AzureRoleRequirements.Reader, AzureRoleRequirements.WebsiteContributor], UserId, CancellationToken.None);

        Assert.Equal([true, true], results.Select(r => r.Succeeded));
        Assert.True(results[0].AlreadyAssigned);
        Assert.False(results[1].AlreadyAssigned);
        var (request, body) = handler.Requests[1];
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.StartsWith($"https://management.azure.com/subscriptions/{Sub1}/providers/Microsoft.Authorization/roleAssignments/", request.RequestUri!.ToString());
        Assert.Contains($"\"principalId\":\"{UserId}\"", body);
        Assert.Contains(RoleSelfAssignmentService.RoleDefinitionIds[AzureRoleRequirements.WebsiteContributor], body);
    }

    [Fact]
    public async Task Assign_rejects_invalid_principal_without_calling_arm()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.Created, "{}"));

        var results = await RoleSelfAssignmentService.AssignAsync(new HttpClient(handler), "token", Production,
            [AzureRoleRequirements.Reader], "not-a-guid", CancellationToken.None);

        Assert.False(Assert.Single(results).Succeeded);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Tenant_access_only_forwards_allowed_scopes()
    {
        Assert.Equal([DirectoryUserLookup.Scope],
            TenantAccess.FilterScopes($"https://graph.microsoft.com/Directory.ReadWrite.All {DirectoryUserLookup.Scope} {DirectoryUserLookup.Scope}"));
        Assert.Empty(TenantAccess.FilterScopes(null));

        var url = TenantAccess.BuildUrl("tenant", "/", scopes: DirectoryUserLookup.Scopes);
        Assert.Contains($"&scope={Uri.EscapeDataString(DirectoryUserLookup.Scope)}", url);
        Assert.DoesNotContain("scope=", TenantAccess.BuildUrl("tenant", "/"));
    }

    [Fact]
    public void Grant_form_requires_user_subscription_and_role()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddRadzenComponents();
        Services.AddScoped(_ => new DirectoryUserLookup(null!, null!, null!));
        Services.AddScoped(_ => new RoleSelfAssignmentService(null!, null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<RoleSelfAssignmentService>.Instance));

        var cut = Render<GrantRolesToUser>(p => p.Add(x => x.Subscriptions, [Production]));

        var button = () => cut.FindAll("button").Single(b => b.TextContent.Contains("Grant roles"));
        Assert.True(button().HasAttribute("disabled"));

        cut.Find("input[name=grantRolesUser]").Input("alex@contoso.com");

        Assert.False(button().HasAttribute("disabled"));
        Assert.Contains("User.ReadBasic.All", cut.Markup);
    }
}
