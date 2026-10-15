using BlazorAzureInventory.Components;
using BlazorAzureInventory.Models.Options;
using BlazorAzureInventory.Models.Users;
using BlazorAzureInventory.Services.AI;
using BlazorAzureInventory.Services.Analysis;
using BlazorAzureInventory.Services.Auth;
using BlazorAzureInventory.Services.Azure;
using BlazorAzureInventory.Services.Changes;
using BlazorAzureInventory.Services.Graph;
using BlazorAzureInventory.Services.Mcp;
using BlazorAzureInventory.Services.Persistence;
using BlazorAzureInventory.Services.Quotas;
using BlazorAzureInventory.Services.Reports;
using BlazorAzureInventory.Services.Resources;
using BlazorAzureInventory.Services.Scan;
using BlazorAzureInventory.Services.Tags;
using BlazorAzureInventory.Services.Users;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using Radzen;

namespace BlazorAzureInventory;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (builder.Environment.IsDevelopment())
            AddLocalSettings(builder.Configuration, builder.Environment.ContentRootFileProvider);
        builder.AddServiceDefaults();

        // An empty ClientId makes every request fail; a placeholder keeps the home page up to show the setup steps.
        if (string.IsNullOrWhiteSpace(builder.Configuration["AzureAd:ClientId"]))
            builder.Configuration["AzureAd:ClientId"] = "<not-set>";

        var armScopes = builder.Configuration.GetSection("AzureResourceManager:Scopes").Get<string[]>()
                        ?? ["https://management.azure.com/user_impersonation"];

        builder.Services
            .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
            .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"))
            .EnableTokenAcquisitionToCallDownstreamApi(armScopes)
            .AddInMemoryTokenCaches();
        builder.Services.AddTenantGuard();
        builder.Services.AddTokenCacheSessionGuard(armScopes);
        builder.Services.AddTenantAccess();
        builder.Services.AddUserDirectoryAuth();
        builder.Services.AddSingleton<SetupStatusService>();

        builder.Services.AddControllersWithViews().AddMicrosoftIdentityUI();
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents()
            .AddMicrosoftIdentityConsentHandler();
        var userPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireClaim(AppClaimTypes.Role, AppRoles.User, AppRoles.Admin)
            .Build();
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(AppPolicies.User, userPolicy)
            .AddPolicy(AppPolicies.Admin, p => p.RequireAuthenticatedUser().RequireClaim(AppClaimTypes.Role, AppRoles.Admin))
            .SetDefaultPolicy(userPolicy);
        builder.Services.AddCascadingAuthenticationState();

        builder.Services.AddRadzenComponents();
        builder.Services.AddSingleton(TimeProvider.System);
        AddPersistence(builder.Services);
        AddHttpRateLimiting(builder.Services, builder.Configuration);

        builder.Services.AddOptions<InventoryOptions>().BindConfiguration("Inventory").ValidateDataAnnotations().ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<InventoryOptions>, ExternalServiceOptionsValidator>();
        builder.Services.AddOptions<AzureMcpOptions>().BindConfiguration("AzureMcp").ValidateDataAnnotations().ValidateOnStart();
        builder.Services.AddOptions<GraphSessionOptions>().BindConfiguration("GraphSession").ValidateDataAnnotations();
        builder.Services.AddOptions<GraphViewOptions>().BindConfiguration("GraphView").ValidateDataAnnotations();
        builder.Services.AddOptions<SecurityOptions>().BindConfiguration("Security");
        builder.Services.AddOptions<AIOptions>().BindConfiguration("AI").ValidateDataAnnotations();
        builder.Services.AddOptions<TaggingOptions>().BindConfiguration("Tagging").ValidateDataAnnotations();
        builder.Services.AddOptions<PersistenceOptions>().BindConfiguration("Persistence").ValidateDataAnnotations().ValidateOnStart();
        builder.Services.AddOptions<UserManagementOptions>().BindConfiguration("UserManagement").ValidateDataAnnotations().ValidateOnStart();
        builder.Services.AddOptions<QuotaOptions>().BindConfiguration("Quotas").ValidateDataAnnotations().ValidateOnStart();
        builder.Services.AddOptions<ChangeTrackingOptions>().BindConfiguration("ChangeTracking");

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
        builder.Services.AddScoped<ScmAccessDiagnostics>();
        builder.Services.AddHttpClient(ScmAccessDiagnostics.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

        // Stateless analysis
        builder.Services.AddSingleton<ScmHostValidator>();
        builder.Services.AddSingleton<CloudEndpointSuffixes>();
        builder.Services.AddSingleton(sp => new ExternalServiceCatalog(sp.GetRequiredService<IOptions<InventoryOptions>>()));
        builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<InventoryOptions>>().Value.StorageNameHints);
        builder.Services.AddSingleton<ConfigFlattener>();
        builder.Services.AddSingleton<SecretClassifier>();
        builder.Services.AddSingleton<ConnectionClassifier>();
        builder.Services.AddSingleton<ConfigAnalyzer>();
        builder.Services.AddSingleton<ResourceMatcher>();
        builder.Services.AddSingleton<InventoryGraphBuilder>();
        builder.Services.AddSingleton<InventorySessionStore>();
        builder.Services.AddSingleton<GraphQueryService>();
        builder.Services.AddSingleton<GraphViewService>();
        builder.Services.AddSingleton<GraphLayoutService>();

        // Tags (TagWriteService is the only code path that writes to Azure)
        builder.Services.AddSingleton<TagValidator>();
        builder.Services.AddSingleton<ConnectionTagGenerator>();
        builder.Services.AddSingleton<TagProposalStore>();
        builder.Services.AddScoped<ITagStore, ArmTagStore>();
        builder.Services.AddScoped<TagSyncCoordinator>();
        builder.Services.AddScoped<TagWriteService>();
        builder.Services.AddScoped<ITagPermissionChecker, TagPermissionChecker>();
        builder.Services.AddScoped<ResourceInspector>();
        builder.Services.AddScoped<ResourceDialogLauncher>();
        builder.Services.AddHttpClient(TagPermissionChecker.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

        // Role assignment for yourself or (for Owners) other users; only writes when the user confirms
        builder.Services.AddScoped<RoleSelfAssignmentService>();
        builder.Services.AddHttpClient(RoleSelfAssignmentService.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        builder.Services.AddScoped<DirectoryUserLookup>();
        builder.Services.AddHttpClient(DirectoryUserLookup.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

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
        app.UseRateLimiter();

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();

        app.MapStaticAssets();
        app.MapControllers();
        app.MapPost("/account/logout", LogoutEndpoint.HandleAsync).RequireAuthorization();
        app.MapGet(TenantAccess.Path, TenantAccess.HandleAsync).RequireAuthorization();
        // Page-level [Authorize] is enforced by AuthorizeRouteView, which renders the "Sign in required" card instead of redirecting.
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode()
            .AllowAnonymous();

        app.Run();
    }

    public const string LocalSettingsFile = "appsettings.Local.json";

    private static void AddPersistence(IServiceCollection services)
    {
        services.AddSingleton<PersistencePaths>();
        services.AddSingleton(sp => PersistentVolumeCheck.Evaluate(
            sp.GetRequiredService<IOptions<PersistenceOptions>>().Value,
            sp.GetRequiredService<PersistencePaths>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("BlazorAzureInventory.Persistence")));
        services.AddSingleton<IUserDataStore>(sp => sp.GetRequiredService<PersistenceStatus>().IsPersistent
            ? ActivatorUtilities.CreateInstance<FileUserDataStore>(sp)
            : new NullUserDataStore());
        services.AddSingleton<IUserDirectoryStore>(sp => sp.GetRequiredService<PersistenceStatus>().IsPersistent
            ? new FileUserDirectoryStore(sp.GetRequiredService<PersistencePaths>())
            : new InMemoryUserDirectoryStore(canWrite: false));

        // Without persistent storage the default in-memory key ring is used, as before.
        services.AddDataProtection().SetApplicationName("BlazorAzureInventory");
        services.AddOptions<KeyManagementOptions>()
            .Configure<PersistenceStatus, PersistencePaths, ILoggerFactory>((options, status, paths, loggers) =>
            {
                if (status.IsPersistent)
                    options.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(paths.DataProtectionDirectory), loggers);
            });

        services.AddScoped<InventorySessionLoader>();
        services.AddScoped<UserPreferencesService>();
        services.AddSingleton<InventoryPersistenceCoordinator>();
        services.AddHostedService(sp => sp.GetRequiredService<InventoryPersistenceCoordinator>());
        services.AddHostedService<RetentionCleanupService>();
        services.AddHealthChecks().AddCheck<PersistenceHealthCheck>("persistence-volume", tags: ["ready"]);

        services.AddSingleton<AuditLog>();
        services.AddSingleton<UserDirectory>();
        services.AddScoped<UserAdminService>();
        services.AddHostedService<DirectoryBootstrapper>();

        services.AddSingleton<UserQuotaService>();
        services.AddSingleton<SnapshotDiffService>();
        services.AddScoped<ChangeTrackingService>();
        services.AddSingleton<ReportExportService>();
        services.AddScoped<FileDownloadService>();
    }

    /// <summary>Limits sign-in, sign-out, and tenant-access requests per client IP. Blazor interactions use <see cref="UserQuotaService"/>.</summary>
    private static void AddHttpRateLimiting(IServiceCollection services, IConfiguration configuration)
    {
        var http = configuration.GetSection("Quotas:Http").Get<HttpQuotaOptions>() ?? new HttpQuotaOptions();
        string[] anonymousPaths = ["/signin-oidc", "/signout-callback-oidc", "/MicrosoftIdentity/Account"];
        string[] authenticatedPaths = ["/account/logout", TenantAccess.Path];
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var path = context.Request.Path;
                var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                if (anonymousPaths.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
                    return RateLimitPartition.GetFixedWindowLimiter("anon:" + ip, _ => Window(http.AnonymousPerIpPerMinute));
                if (authenticatedPaths.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
                    return RateLimitPartition.GetFixedWindowLimiter("auth:" + ip, _ => Window(http.AuthenticatedPerUserPerMinute));
                return RateLimitPartition.GetNoLimiter("none");
            });
        });

        static FixedWindowRateLimiterOptions Window(int permits) =>
            new() { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 };
    }

    // Inserted right after appsettings.{env}.json so user secrets, environment variables, and the command line still win.
    private static void AddLocalSettings(ConfigurationManager configuration, IFileProvider fileProvider)
    {
        var sources = configuration.Sources;
        var index = sources.ToList().FindLastIndex(s =>
            s is FileConfigurationSource { Path: { } path } && path.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase));
        sources.Insert(index + 1, new JsonConfigurationSource
        {
            FileProvider = fileProvider,
            Path = LocalSettingsFile,
            Optional = true,
            ReloadOnChange = true
        });
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
