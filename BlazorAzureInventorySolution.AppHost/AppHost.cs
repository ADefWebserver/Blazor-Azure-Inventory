using Aspire.Hosting.Azure;
using Azure.Provisioning.AppContainers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;

var builder = DistributedApplication.CreateBuilder(args);

// Inserted right after the appsettings files so user secrets, environment variables, and the command line still win.
// Run mode only: inserting reloads every source, and deploy adds a one-shot stream source for its saved state.
var sources = builder.Configuration.Sources;
var settingsIndex = sources.ToList().FindLastIndex(s =>
    s is FileConfigurationSource { Path: { } path } && path.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase));

// appsettings.Org.json holds a deployment's organization-specific values; it is git-ignored and shared privately.
JsonConfigurationSource AppHostJson(string path) => new()
{
    FileProvider = new PhysicalFileProvider(builder.AppHostDirectory),
    Path = path,
    Optional = true
};

if (builder.ExecutionContext.IsRunMode)
{
    sources.Insert(settingsIndex + 1, AppHostJson("appsettings.Org.json"));
    sources.Insert(settingsIndex + 2, AppHostJson("appsettings.Local.json"));
}
else
{
    // Appended, not inserted: inserting reloads the one-shot deploy-state source.
    sources.Add(AppHostJson("appsettings.Org.json"));
}

// Self-hosted Azure MCP Server (HTTP transport, read-only, On-Behalf-Of). Confirm the container's listening port
// and incoming-auth environment variable names against the pinned image version.
var mcpClientId = builder.AddParameter("mcpClientId");
var mcp = builder.AddDockerfile("azure-mcp", "../AzureMcpServer")
    .WithHttpEndpoint(targetPort: 8080, name: "http")
    .WithEnvironment("ASPNETCORE_URLS", "http://+:8080")
    // Plain HTTP is only reachable inside the Aspire network; without this the server redirects to HTTPS.
    .WithEnvironment("AZURE_MCP_DANGEROUSLY_DISABLE_HTTPS_REDIRECTION", "true")
    .WithEnvironment("AzureAd__Instance", "https://login.microsoftonline.com/")
    .WithEnvironment("AzureAd__TenantId", builder.AddParameter("mcpTenantId"))
    .WithEnvironment("AzureAd__ClientId", mcpClientId);

var web = builder.AddProject<Projects.BlazorAzureInventory>("blazorazureinventory")
    .WithReference(mcp.GetEndpoint("http"))
    .WithEnvironment("UserManagement__BootstrapAdmins", builder.Configuration["UserManagement:BootstrapAdmins"] ?? "")
    .WithEnvironment("UserManagement__SupportContact", builder.Configuration["UserManagement:SupportContact"] ?? "")
    .WithExternalHttpEndpoints();

if (builder.ExecutionContext.IsPublishMode)
{
    // Azure: both apps authenticate with a federated credential from their own managed identity, so no secrets are deployed.
    builder.AddAzureContainerAppEnvironment("aca-env");
    var mcpIdentity = builder.AddAzureUserAssignedIdentity("mcp-identity");
    var webIdentity = builder.AddAzureUserAssignedIdentity("web-identity");

    mcp.WithAzureUserAssignedIdentity(mcpIdentity)
        .WithEnvironment("AzureAd__ClientCredentials__0__SourceType", "SignedAssertionFromManagedIdentity")
        .WithEnvironment("AzureAd__ClientCredentials__0__ManagedIdentityClientId", mcpIdentity.GetOutput("clientId"))
        .PublishAsAzureContainerApp((_, app) => app.Template.Scale.MinReplicas = 1);

    web.WithAzureUserAssignedIdentity(webIdentity)
        .WithEnvironment("AzureAd__ClientId", builder.AddParameter("webClientId"))
        .WithEnvironment("AzureAd__ClientCredentials__0__SourceType", "SignedAssertionFromManagedIdentity")
        .WithEnvironment("AzureAd__ClientCredentials__0__ManagedIdentityClientId", webIdentity.GetOutput("clientId"))
        .WithEnvironment("AzureMcp__Scopes__0", ReferenceExpression.Create($"api://{mcpClientId}/Mcp.Tools.ReadWrite"))
        // Container Apps terminates TLS; forwarded headers keep the OIDC redirect URI on https.
        .WithEnvironment("ASPNETCORE_FORWARDEDHEADERS_ENABLED", "true")
        // Saved scans, users.json, the audit log, and Data Protection keys live on the Azure Files volume mounted at /data.
        // Aspire 13.6 cannot declare volumes on projects, so the mount is set up once per environment (see Docs/persistence-and-user-management-plan.md 4.2).
        .WithEnvironment("Persistence__RootPath", "/data")
        .WithEnvironment("Persistence__RequirePersistentVolume", "true")
        .PublishAsAzureContainerApp((_, app) =>
        {
            app.Name = builder.Configuration["Deployment:WebAppName"] ?? "azinventory-web";
            // Circuits and the MSAL token cache are per replica, so each user must stay on one replica.
            app.Configuration.Ingress.StickySessionsAffinity = StickySessionAffinity.Sticky;
            app.Template.Scale.MinReplicas = 1;
            app.Template.Scale.MaxReplicas = 1;
        });

    var allowedTenantIds = builder.Configuration.GetSection("Security:AllowedTenantIds").Get<string[]>() ?? [];
    for (var i = 0; i < allowedTenantIds.Length; i++)
    {
        web.WithEnvironment($"Security__AllowedTenantIds__{i}", allowedTenantIds[i]);
    }
}
else
{
    mcp.WithEnvironment("AzureAd__ClientCredentials__0__SourceType", "ClientSecret")
        .WithEnvironment("AzureAd__ClientCredentials__0__ClientSecret", builder.AddParameter("mcpClientSecret", secret: true));
}

builder.Build().Run();
