var builder = DistributedApplication.CreateBuilder(args);

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
