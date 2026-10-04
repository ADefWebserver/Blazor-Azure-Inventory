using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;

var builder = DistributedApplication.CreateBuilder(args);

// Inserted right after the appsettings files so user secrets, environment variables, and the command line still win.
var sources = builder.Configuration.Sources;
var settingsIndex = sources.ToList().FindLastIndex(s =>
    s is FileConfigurationSource { Path: { } path } && path.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase));
sources.Insert(settingsIndex + 1, new JsonConfigurationSource
{
    FileProvider = new PhysicalFileProvider(builder.AppHostDirectory),
    Path = "appsettings.Local.json",
    Optional = true
});

// Self-hosted Azure MCP Server (HTTP transport, read-only, On-Behalf-Of). Confirm the container's listening port
// and incoming-auth environment variable names against the pinned image version.
var mcp = builder.AddDockerfile("azure-mcp", "../AzureMcpServer")
    .WithHttpEndpoint(targetPort: 8080, name: "http")
    .WithEnvironment("AzureAd__TenantId", builder.AddParameter("mcpTenantId"))
    .WithEnvironment("AzureAd__ClientId", builder.AddParameter("mcpClientId"));

builder.AddProject<Projects.BlazorAzureInventory>("blazorazureinventory")
    .WithReference(mcp.GetEndpoint("http"))
    .WithExternalHttpEndpoints();

builder.Build().Run();
