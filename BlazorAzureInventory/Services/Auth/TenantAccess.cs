using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Identity.Web;

namespace BlazorAzureInventory.Services.Auth;

/// <summary>
/// Interactive sign-in against a specific (non-home) tenant so the user can satisfy that tenant's consent or MFA
/// requirement (AADSTS65001 / AADSTS50076). The default challenge targets the configured multi-tenant authority,
/// which resolves to the user's home tenant and therefore never clears the other tenant's requirement.
/// The resulting tokens are cached under the same account and the user's existing session identity is kept.
/// </summary>
public static class TenantAccess
{
    public const string Path = "/account/tenant-access";
    private const string TenantItem = "tenant_access.tenant";
    private const string LoginHintItem = "tenant_access.login_hint";
    private const string ClaimsItem = "tenant_access.claims";

    public static bool IsTenantAccess(AuthenticationProperties? properties) =>
        properties?.Items.ContainsKey(TenantItem) == true;

    public static string BuildUrl(string tenantId, string returnUrl, string? claims = null)
    {
        var url = $"{Path.TrimStart('/')}?tenantId={Uri.EscapeDataString(tenantId)}&returnUrl={Uri.EscapeDataString(returnUrl)}";
        return string.IsNullOrEmpty(claims) ? url : $"{url}&claims={Uri.EscapeDataString(claims)}";
    }

    public static IServiceCollection AddTenantAccess(this IServiceCollection services)
    {
        services.AddOptions<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme)
            .Configure(oidc =>
            {
                var onRedirect = oidc.Events.OnRedirectToIdentityProvider;
                oidc.Events.OnRedirectToIdentityProvider = async context =>
                {
                    if (onRedirect is not null) await onRedirect(context);
                    if (!context.Properties.Items.TryGetValue(TenantItem, out var tenantId) || tenantId is null) return;

                    context.ProtocolMessage.IssuerAddress = ReplaceTenant(context.ProtocolMessage.IssuerAddress, tenantId);
                    if (context.Properties.Items.TryGetValue(LoginHintItem, out var loginHint) && !string.IsNullOrEmpty(loginHint))
                        context.ProtocolMessage.LoginHint = loginHint;
                    if (context.Properties.Items.TryGetValue(ClaimsItem, out var claims) && !string.IsNullOrEmpty(claims))
                        context.ProtocolMessage.SetParameter("claims", claims);
                };

                var onCode = oidc.Events.OnAuthorizationCodeReceived;
                oidc.Events.OnAuthorizationCodeReceived = async context =>
                {
                    // Microsoft.Identity.Web redeems the code against the tenant in DomainHint; it must match the issuing tenant.
                    if (context.Properties?.Items.TryGetValue(TenantItem, out var tenantId) == true && tenantId is not null)
                        context.ProtocolMessage.DomainHint = tenantId;
                    if (onCode is not null) await onCode(context);
                };

                var onTicket = oidc.Events.OnTicketReceived;
                oidc.Events.OnTicketReceived = async context =>
                {
                    if (onTicket is not null) await onTicket(context);
                    if (context.Result is not null || !IsTenantAccess(context.Properties)) return;

                    var existing = await context.HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    var existingAccount = existing.Principal?.GetMsalAccountId();
                    if (existingAccount is null || !string.Equals(existingAccount, context.Principal?.GetMsalAccountId(), StringComparison.OrdinalIgnoreCase))
                    {
                        context.Fail("Sign in with the same account that is signed in to this application.");
                        return;
                    }

                    // Keep the home-tenant identity; only the token cache needed the other tenant's tokens.
                    context.Principal = existing.Principal;
                };
            });
        return services;
    }

    public static IResult HandleAsync(HttpContext context, string tenantId, string? returnUrl, string? claims)
    {
        if (!Guid.TryParse(tenantId, out var tenant)) return Results.BadRequest();
        if (string.IsNullOrEmpty(returnUrl) || !returnUrl.StartsWith('/') || returnUrl.StartsWith("//") || returnUrl.StartsWith("/\\"))
            returnUrl = "/";

        var properties = new AuthenticationProperties { RedirectUri = returnUrl };
        properties.Items[TenantItem] = tenant.ToString();
        var loginHint = context.User.GetLoginHint() ?? context.User.FindFirst("preferred_username")?.Value;
        if (!string.IsNullOrEmpty(loginHint)) properties.Items[LoginHintItem] = loginHint;
        if (!string.IsNullOrEmpty(claims)) properties.Items[ClaimsItem] = claims;

        return Results.Challenge(properties, [OpenIdConnectDefaults.AuthenticationScheme]);
    }

    private static string ReplaceTenant(string authorizeEndpoint, string tenantId)
    {
        var uri = new UriBuilder(authorizeEndpoint);
        var segments = uri.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return authorizeEndpoint;
        segments[0] = tenantId;
        uri.Path = "/" + string.Join('/', segments);
        return uri.Uri.ToString();
    }
}
