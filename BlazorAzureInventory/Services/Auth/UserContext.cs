using System.Security.Claims;
using Azure.Core;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Identity.Web;

namespace BlazorAzureInventory.Services.Auth;

/// <summary>Reads the signed-in user inside a Blazor circuit, where HttpContext is not reliable.</summary>
public sealed class UserContextAccessor(AuthenticationStateProvider auth)
{
    public async Task<ClaimsPrincipal> GetUserAsync() => (await auth.GetAuthenticationStateAsync()).User;
}

/// <summary>
/// A <see cref="TokenCredential"/> that acquires delegated tokens for the signed-in user. Always requests the
/// configured scopes (ARM audience), which also authorize Kudu (SCM) calls.
/// </summary>
public sealed class UserTokenCredential(
    ITokenAcquisition tokens, ClaimsPrincipal user, string? tenantId, IReadOnlyList<string> scopes) : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        => GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        var result = await tokens.GetAuthenticationResultForUserAsync(
            scopes, tenantId: tenantId, user: user,
            tokenAcquisitionOptions: new TokenAcquisitionOptions { CancellationToken = cancellationToken });
        return new AccessToken(result.AccessToken, result.ExpiresOn);
    }
}
