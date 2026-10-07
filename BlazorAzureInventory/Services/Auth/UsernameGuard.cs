using System.Security.Claims;
using BlazorAzureInventory.Models.Options;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace BlazorAzureInventory.Services.Auth;

public static class UsernameGuard
{
    private const string DeniedMessage = "This username is not allowed to access the application.";

    public static IServiceCollection AddUsernameGuard(this IServiceCollection services)
    {
        services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
        {
            var previous = options.Events.OnTokenValidated;
            options.Events.OnTokenValidated = async context =>
            {
                if (previous is not null) await previous(context);
                if (context.Result?.Failure is not null || TenantAccess.IsTenantAccess(context.Properties)) return;

                var security = context.HttpContext.RequestServices
                    .GetRequiredService<IOptionsMonitor<SecurityOptions>>().CurrentValue;
                if (!IsUsernameAllowed(security, context.Principal)) context.Fail(DeniedMessage);
            };
        });
        return services;
    }

    public static bool IsUsernameAllowed(SecurityOptions options, ClaimsPrincipal? principal)
    {
        if (!options.UsernameAllowListEnabled) return true;
        var username = GetUsername(principal);
        return username is not null && ParseAllowedUsernames(options.AllowedUsernames).Contains(username);
    }

    public static HashSet<string> ParseAllowedUsernames(string? value) =>
        new((value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            StringComparer.OrdinalIgnoreCase);

    public static string? GetUsername(ClaimsPrincipal? principal) =>
        FindClaim(principal, "preferred_username")
        ?? FindClaim(principal, ClaimTypes.Upn)
        ?? FindClaim(principal, ClaimTypes.Email)
        ?? FindClaim(principal, "upn")
        ?? FindClaim(principal, "email");

    private static string? FindClaim(ClaimsPrincipal? principal, string type)
    {
        var value = principal?.FindFirst(type)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
