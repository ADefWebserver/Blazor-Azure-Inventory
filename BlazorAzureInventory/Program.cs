using BlazorAzureInventory.Components;
using BlazorAzureInventory.Models.Options;
using BlazorAzureInventory.Services.AI;
using BlazorAzureInventory.Services.Analysis;
using BlazorAzureInventory.Services.Auth;
using BlazorAzureInventory.Services.Azure;
using BlazorAzureInventory.Services.Graph;
using BlazorAzureInventory.Services.Mcp;
using BlazorAzureInventory.Services.Scan;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using Radzen;

namespace BlazorAzureInventory;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();

        var armScopes = builder.Configuration.GetSection("AzureResourceManager:Scopes").Get<string[]>()
                        ?? ["https://management.azure.com/user_impersonation"];

        builder.Services
            .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
            .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"))
            .EnableTokenAcquisitionToCallDownstreamApi(armScopes)
            .AddInMemoryTokenCaches();
        builder.Services.AddTenantGuard();

        builder.Services.AddControllersWithViews().AddMicrosoftIdentityUI();
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents()
            .AddMicrosoftIdentityConsentHandler();
        builder.Services.AddAuthorization();
        builder.Services.AddCascadingAuthenticationState();

        builder.Services.AddRadzenComponents();
        builder.Services.AddDataProtection();

        builder.Services.AddOptions<InventoryOptions>().BindConfiguration("Inventory").ValidateDataAnnotations().ValidateOnStart();
        builder.Services.AddOptions<AzureMcpOptions>().BindConfiguration("AzureMcp").ValidateDataAnnotations().ValidateOnStart();
        builder.Services.AddOptions<GraphSessionOptions>().BindConfiguration("GraphSession").ValidateDataAnnotations();
        builder.Services.AddOptions<SecurityOptions>().BindConfiguration("Security");
        builder.Services.AddOptions<AIOptions>().BindConfiguration("AI").ValidateDataAnnotations();

        // Azure access (scoped == per Blazor circuit == per signed-in user)
        builder.Services.AddScoped<UserContextAccessor>();
        builder.Services.AddScoped<ArmClientFactory>();
        builder.Services.AddScoped<AzureContextService>();
        builder.Services.AddScoped<ResourceGraphInventoryService>();
        builder.Services.AddScoped<ResourceEnrichmentService>();
        builder.Services.AddScoped<AppConfigurationReader>();
        builder.Services.AddScoped<InventoryScanOrchestrator>();
        builder.Services.AddHttpClient<KuduConfigFileReader>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

        // Stateless analysis
        builder.Services.AddSingleton<ScmHostValidator>();
        builder.Services.AddSingleton<CloudEndpointSuffixes>();
        builder.Services.AddSingleton<ConfigFlattener>();
        builder.Services.AddSingleton<SecretClassifier>();
        builder.Services.AddSingleton<ConnectionClassifier>();
        builder.Services.AddSingleton<ConfigAnalyzer>();
        builder.Services.AddSingleton<ResourceMatcher>();
        builder.Services.AddSingleton<InventoryGraphBuilder>();
        builder.Services.AddSingleton<InventorySessionStore>();
        builder.Services.AddSingleton<GraphQueryService>();

        // AI and MCP
        builder.Services.AddScoped<SessionAISettingsService>();
        builder.Services.AddScoped<AIModelCacheService>();
        builder.Services.AddScoped<AIConnectionTester>();
        builder.Services.AddScoped<ChatClientFactory>();
        builder.Services.AddScoped<AzureMcpClientFactory>();
        builder.Services.AddScoped<ChatService>();
        builder.Services.AddSingleton<EndpointGuard>();

        // LLM and MCP calls run longer than the default resilience timeouts.
        builder.Services.AddHttpClient(ChatClientFactory.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        builder.Services.AddHttpClient(AzureMcpClientFactory.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        ConfigureLongTimeouts(builder.Services, ChatClientFactory.HttpClientName, 120);
        ConfigureLongTimeouts(builder.Services, AzureMcpClientFactory.HttpClientName,
            builder.Configuration.GetValue("AzureMcp:TimeoutSeconds", 60));

        var app = builder.Build();

        app.MapDefaultEndpoints();

        // Configure the HTTP request pipeline.
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error");
            // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
            app.UseHsts();
        }

        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy =
                "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
                "font-src 'self' data:; connect-src 'self' wss:; frame-ancestors 'none'; base-uri 'self'";
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            await next();
        });

        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        app.UseHttpsRedirection();

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();

        app.MapStaticAssets();
        app.MapControllers();
        app.MapPost("/account/logout", LogoutEndpoint.HandleAsync).RequireAuthorization();
        // Page-level [Authorize] is enforced by AuthorizeRouteView, which renders the "Sign in required" card instead of redirecting.
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode()
            .AllowAnonymous();

        app.Run();
    }

    private static void ConfigureLongTimeouts(IServiceCollection services, string clientName, int seconds)
    {
        services.Configure<HttpStandardResilienceOptions>($"{clientName}-standard", o =>
        {
            o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(seconds);
            o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(seconds * 2);
            o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(seconds * 2);
            o.Retry.DisableForUnsafeHttpMethods();
        });
    }
}
