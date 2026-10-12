using System.Security.Claims;
using BlazorAzureInventory.Models.Users;
using BlazorAzureInventory.Services.Users;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;

namespace BlazorAzureInventory.Services.Auth;

/// <summary>Wires the user directory into sign-in, every HTTP request, and open Blazor circuits.</summary>
public static class UserDirectoryAuth
{
    public const string AccessDeniedPath = "/access-denied";

    public static IServiceCollection AddUserDirectoryAuth(this IServiceCollection services)
    {
        services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
        {
            var previous = options.Events.OnTokenValidated;
            options.Events.OnTokenValidated = async context =>
            {
                if (previous is not null) await previous(context);
                if (context.Result?.Handled == true || context.Result?.Failure is not null || TenantAccess.IsTenantAccess(context.Properties)) return;

                var principal = context.Principal;
                var tenantId = principal?.GetTenantId();
                var objectId = principal?.GetObjectId();
                var decision = tenantId is null || objectId is null
                    ? SignInDecision.Deny(DenyReasons.NotInvited)
                    : await context.HttpContext.RequestServices.GetRequiredService<UserDirectory>()
                        .SignInAsync(tenantId, objectId, UserClaims.GetUsername(principal), principal?.FindFirst("name")?.Value, context.HttpContext.RequestAborted);
                if (decision.Allowed) return;

                context.Response.Redirect($"{AccessDeniedPath}?reason={Uri.EscapeDataString(decision.DenyReason ?? DenyReasons.NotInvited)}");
                context.HandleResponse();
            };
        });

        services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, cookie =>
        {
            cookie.ExpireTimeSpan = TimeSpan.FromHours(8);
            cookie.SlidingExpiration = true;
            var previous = cookie.Events.OnValidatePrincipal;
            cookie.Events.OnValidatePrincipal = async context =>
            {
                if (previous is not null) await previous(context);
                if (context.Principal?.Identity?.IsAuthenticated != true) return;

                var directory = context.HttpContext.RequestServices.GetRequiredService<UserDirectory>();
                await directory.RefreshAsync(ct: context.HttpContext.RequestAborted);
                if (directory.GetActiveRole(context.Principal.GetTenantId(), context.Principal.GetObjectId(), UserClaims.GetUsername(context.Principal)) is not null) return;

                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            };
        });

        services.AddSingleton<IClaimsTransformation, RoleClaimsTransformation>();
        services.AddScoped<AuthenticationStateProvider, DirectoryRevalidatingAuthStateProvider>();
        return services;
    }
}

/// <summary>Adds the app role from the user directory as <see cref="AppClaimTypes.Role"/>, replacing any incoming claim of that type.</summary>
public sealed class RoleClaimsTransformation(UserDirectory directory) : IClaimsTransformation
{
    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true) return principal;
        if (principal.Identities.Any(i => i.AuthenticationType == AppClaimTypes.AuthenticationType)) return principal;

        await directory.RefreshAsync();
        var role = directory.GetActiveRole(principal.GetTenantId(), principal.GetObjectId(), UserClaims.GetUsername(principal));

        var identities = principal.Identities.Select(i =>
        {
            var copy = i.Clone();
            foreach (var claim in copy.FindAll(AppClaimTypes.Role).ToList()) copy.RemoveClaim(claim);
            return copy;
        }).ToList();
        var appIdentity = new ClaimsIdentity(AppClaimTypes.AuthenticationType, ClaimTypes.Name, AppClaimTypes.Role);
        if (role is not null) appIdentity.AddClaim(new Claim(AppClaimTypes.Role, role));
        identities.Add(appIdentity);
        return new ClaimsPrincipal(identities);
    }
}

/// <summary>Ends an open circuit's authentication when the user is disabled, removed, or their role changes.</summary>
public sealed class DirectoryRevalidatingAuthStateProvider(
    ILoggerFactory loggerFactory,
    UserDirectory directory,
    IOptions<Models.Options.UserManagementOptions> options)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(options.Value.RevalidationMinutes);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        var user = authenticationState.User;
        if (user.Identity?.IsAuthenticated != true) return true;
        await directory.RefreshAsync(ct: cancellationToken);
        var role = directory.GetActiveRole(user.GetTenantId(), user.GetObjectId(), UserClaims.GetUsername(user));
        return role is not null && role == UserClaims.GetAppRole(user);
    }
}

/// <summary>Seeds or repairs users.json at startup.</summary>
public sealed class DirectoryBootstrapper(UserDirectory directory, ILogger<DirectoryBootstrapper> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await directory.EnsureSeededAsync(cancellationToken);
            await directory.RefreshAsync(force: true, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogCritical(ex, "Seeding the user directory failed; only bootstrap admins can sign in until it is readable");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
