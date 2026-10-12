using System.Security.Claims;
using BlazorAzureInventory.Models.Users;

namespace BlazorAzureInventory.Services.Auth;

public static class UserClaims
{
    /// <summary>The sign-in name from <c>preferred_username</c> or <c>upn</c>. The unverified <c>email</c> claim is never used.</summary>
    public static string? GetUsername(ClaimsPrincipal? principal) =>
        FindClaim(principal, "preferred_username")
        ?? FindClaim(principal, ClaimTypes.Upn)
        ?? FindClaim(principal, "upn");

    public static string? GetAppRole(ClaimsPrincipal? principal) => principal?.FindFirst(AppClaimTypes.Role)?.Value;

    private static string? FindClaim(ClaimsPrincipal? principal, string type)
    {
        var value = principal?.FindFirst(type)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
