using BlazorAzureInventory.Models.Options;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;

namespace BlazorAzureInventory.Services.Auth;

/// <summary>Rejects sign-ins from tenants outside <see cref="SecurityOptions.AllowedTenantIds"/> when the list is non-empty.</summary>
public static class TenantGuard
{
    public static IServiceCollection AddTenantGuard(this IServiceCollection services)
    {
        services.AddOptions<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme)
            .Configure<IOptions<SecurityOptions>>((oidc, security) =>
            {
                var allowed = security.Value.AllowedTenantIds
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => t.Trim())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (allowed.Count == 0) return;

                var previous = oidc.Events.OnTokenValidated;
                oidc.Events.OnTokenValidated = async context =>
                {
                    if (previous is not null) await previous(context);
                    // Tenant-access sign-ins carry the other tenant's tid; TenantAccess restores the original, already-validated identity.
                    if (TenantAccess.IsTenantAccess(context.Properties)) return;
                    var tenantId = context.Principal?.GetTenantId();
                    if (tenantId is null || !allowed.Contains(tenantId))
                    {
                        context.Fail("This organization is not allowed to sign in to this application.");
                    }
                };
            });
        return services;
    }
}
