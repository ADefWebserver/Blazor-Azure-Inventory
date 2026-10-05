using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Identity.Web;

namespace BlazorAzureInventory.Services.Auth;

/// <summary>
/// Interactive sign-in against a specific (non-home) tenant so the user can satisfy that tenant's consent or MFA
/// requirement (AADSTS65001 / AADSTS50076). The default challenge targets the configured multi-tenant authority,
/// which resolves to the user's home tenant and therefore never clears the other tenant's requirement.
/// The resulting tokens are cached under the same account; the callback does not sign in, so the existing session is kept.
/// </summary>
public static class TenantAccess
{
    public const string Path = "/account/tenant-access";
    private const string TenantItem = "tenant_access.tenant";
    private const string LoginHintItem = "tenant_access.login_hint";
    private const string ClaimsItem = "tenant_access.claims";
    private const string AccountItem = "tenant_access.account";

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

                    // The session cookie is SameSite=Lax and is not sent on the cross-site form_post callback, so the expected
                    // account travels in the protected state instead.
                    var tenantId = context.Properties!.Items[TenantItem];
                    context.Properties.Items.TryGetValue(AccountItem, out var expectedAccount);
                    var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(TenantAccess));
                    if (string.IsNullOrEmpty(expectedAccount) ||
                        !string.Equals(expectedAccount, context.Principal?.GetMsalAccountId(), StringComparison.OrdinalIgnoreCase))
                    {
                        logger.LogWarning("Tenant access for {TenantId} rejected: a different account signed in", tenantId);
                        context.Fail("Sign in with the same account that is signed in to this application.");
                        return;
                    }

                    // The other tenant's tokens are already cached for this account; skip sign-in so the existing
                    // session (home-tenant identity) is left untouched.
                    logger.LogInformation("Tenant access for {TenantId} granted", tenantId);
                    context.Response.Redirect(context.ReturnUri is { Length: > 0 } r ? r : "/");
                    context.HandleResponse();
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
        if (context.User.GetMsalAccountId() is not { } account) return Results.Forbid();
        properties.Items[AccountItem] = account;
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
