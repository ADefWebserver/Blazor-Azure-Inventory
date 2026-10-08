using Aspire.Hosting.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;

var builder = DistributedApplication.CreateBuilder(args);

// Inserted right after the appsettings files so user secrets, environment variables, and the command line still win.
// Run mode only: inserting reloads every source, and deploy adds a one-shot stream source for its saved state.
var sources = builder.Configuration.Sources;
var settingsIndex = sources.ToList().FindLastIndex(s =>
    s is FileConfigurationSource { Path: { } path } && path.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase));
if (builder.ExecutionContext.IsRunMode)
{
    sources.Insert(settingsIndex + 1, new JsonConfigurationSource
    {
        FileProvider = new PhysicalFileProvider(builder.AppHostDirectory),
        Path = "appsettings.Local.json",
        Optional = true
    });
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
        .PublishAsAzureContainerApp((_, app) =>
        {
            app.Name = "azinventory-dev";
            // One replica: sessions, token cache, and Data Protection keys are in memory.
            app.Template.Scale.MinReplicas = 1;
            app.Template.Scale.MaxReplicas = 1;
        });
}
else
{
    mcp.WithEnvironment("AzureAd__ClientCredentials__0__SourceType", "ClientSecret")
        .WithEnvironment("AzureAd__ClientCredentials__0__ClientSecret", builder.AddParameter("mcpClientSecret", secret: true));
}

builder.Build().Run();
