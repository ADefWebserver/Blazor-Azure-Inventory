using BlazorAzureInventory.Services.Graph;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace BlazorAzureInventory.Services.Auth;

public static class LogoutEndpoint
{
    public static async Task<IResult> HandleAsync(HttpContext context, IAntiforgery antiforgery, InventorySessionStore store)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest();
        }

        // Drops the snapshot, graph, and encrypted AI keys for this user.
        store.Clear(context.User);

        return Results.SignOut(
            new AuthenticationProperties { RedirectUri = "/" },
            [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]);
    }
}
