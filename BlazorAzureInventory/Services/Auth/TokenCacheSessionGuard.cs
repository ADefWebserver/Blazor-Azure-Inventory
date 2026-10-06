using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;

namespace BlazorAzureInventory.Services.Auth;

/// <summary>
/// Rejects the auth cookie when the user's account is no longer in the token cache (for example after an app restart
/// with the in-memory cache), so the user signs in again instead of every token request failing with <c>user_null</c>.
/// </summary>
public static class TokenCacheSessionGuard
{
    public static IServiceCollection AddTokenCacheSessionGuard(this IServiceCollection services, IReadOnlyList<string> scopes)
    {
        services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, cookie =>
        {
            var previous = cookie.Events.OnValidatePrincipal;
            cookie.Events.OnValidatePrincipal = async context =>
            {
                if (previous is not null) await previous(context);
                if (context.Principal?.Identity?.IsAuthenticated != true) return;

                var tokens = context.HttpContext.RequestServices.GetRequiredService<ITokenAcquisition>();
                try
                {
                    await tokens.GetAuthenticationResultForUserAsync(scopes, tenantId: context.Principal.GetTenantId(),
                        user: context.Principal,
                        tokenAcquisitionOptions: new TokenAcquisitionOptions { CancellationToken = context.HttpContext.RequestAborted });
                }
                catch (Exception ex) when (IsAccountMissing(ex))
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Consent, conditional access and transient failures are handled where tokens are actually used.
                }
            };
        });
        return services;
    }

    private static bool IsAccountMissing(Exception ex) => ex switch
    {
        MicrosoftIdentityWebChallengeUserException c => c.MsalUiRequiredException?.ErrorCode == MsalError.UserNullError,
        MsalUiRequiredException m => m.ErrorCode == MsalError.UserNullError,
        _ => false
    };
}
