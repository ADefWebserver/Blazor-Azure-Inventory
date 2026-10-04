# BlazorAzureInventorySolution

**Azure Inventory** is a .NET 10 Blazor Server app (Radzen UI) that signs users in with Microsoft Entra ID, inventories the Azure resources *they* can see, analyzes Web App, Function App, and Container App configuration to detect connections, builds an in-memory knowledge graph, visualizes it with vis-network, and answers questions through an AI chat backed by graph tools and a self-hosted, read-only Azure MCP Server.

See [Docs/implementation-plan.md](Docs/implementation-plan.md) for the full design and [Docs/screen-descriptions.md](Docs/screen-descriptions.md) for the screens.

## Projects

| Project | Purpose |
|---|---|
| `BlazorAzureInventory` | The Blazor Server app (pages, Radzen components, Azure SDK inventory, analysis, graph, AI chat, MCP client) |
| `BlazorAzureInventory.Tests` | xUnit and bUnit tests, including the secret sentinel test |
| `BlazorAzureInventorySolution.AppHost` | Aspire AppHost: runs the app and the `azure-mcp` container |
| `BlazorAzureInventorySolution.ServiceDefaults` | OpenTelemetry, health checks, service discovery, resilience |
| `AzureMcpServer` | Dockerfile for the derived Azure MCP Server image (HTTP, On-Behalf-Of, `--read-only`, tool allowlist) |

## Key behaviors

- **User identity drives every Azure call.** ARM, Resource Graph, and Kudu calls use the signed-in user's delegated token. The app never reads customer resources with its own identity.
- **Secrets are never stored or shown.** `ConfigEntry` has no value property; only identifiers (hosts, database, account, container, vault, and secret names) leave the analysis layer. Values render as `••••••`.
- **Read-only.** No write APIs are called. The MCP server runs with `--read-only` and an allowlist that is enforced on both the server and the client.
- **In-memory, per-user sessions.** Snapshots, graphs, and encrypted AI keys live in a bounded in-memory cache keyed by tenant and object ID, and are cleared on sign-out.

## Prerequisites

- .NET SDK 10
- Docker (only to run the Azure MCP Server container locally through Aspire)
- Two multi-tenant Entra ID app registrations (see section 6.1 of the plan):
  - `azinventory-web`: redirect URI `https://localhost:7113/signin-oidc` (and your App Service URL), front-channel logout `/signout-oidc`, delegated permissions `User.Read` and Azure Service Management `user_impersonation`, plus the MCP scope.
  - `azinventory-mcp`: exposes a delegated scope such as `Mcp.Tools.Read`, lists `azinventory-web` in `knownClientApplications`, and is allowed to call ARM on behalf of the user.

## Configuration

Settings live in [BlazorAzureInventory/appsettings.json](BlazorAzureInventory/appsettings.json). Replace the placeholders:

| Setting | Value |
|---|---|
| `AzureAd:ClientId` | `azinventory-web` client ID |
| `AzureAd:ClientCredentials` | Production: federated identity credential from a user-assigned managed identity (`SignedAssertionFromManagedIdentity`). Local development: override with a certificate or client secret in user secrets |
| `AzureMcp:Scopes` | `api://<azinventory-mcp-client-id>/Mcp.Tools.Read` |
| `AzureMcp:AllowedTools` | Must match the `--tool` list in [AzureMcpServer/Dockerfile](AzureMcpServer/Dockerfile) |
| `Security:AllowedTenantIds` | Optional tenant allowlist (empty allows any organization) |

For local development, replace the `<...>` placeholders in `BlazorAzureInventory/appsettings.Local.json` (web app) and `BlazorAzureInventorySolution.AppHost/appsettings.Local.json` (`mcpTenantId` and `mcpClientId` parameters for the MCP container). Both files are git-ignored, loaded right after `appsettings.{Environment}.json`, and the web app's file is loaded only in Development and never published. While any required value is missing, the home page lists the exact steps to fix it.

Alternatively, override values with user secrets on the `BlazorAzureInventory` project (these take precedence over `appsettings.Local.json`):

```powershell
dotnet user-secrets --project BlazorAzureInventory set "AzureAd:ClientId" "<client-id>"
dotnet user-secrets --project BlazorAzureInventory set "AzureAd:ClientCredentials:0:SourceType" "ClientSecret"
dotnet user-secrets --project BlazorAzureInventory set "AzureAd:ClientCredentials:0:ClientSecret" "<dev-only-secret>"
```

The AppHost asks for the `mcpTenantId` and `mcpClientId` parameters used by the MCP container's incoming authentication.

## Run

```powershell
# Everything (app + Azure MCP Server container) through Aspire
dotnet run --project BlazorAzureInventorySolution.AppHost

# The app on its own (chat falls back to graph tools only)
dotnet run --project BlazorAzureInventory
```

Then select **Connect to Azure**, open **Inventory**, choose subscriptions, and select **Scan**. **Knowledge Graph** and **AI Chat** become available after the first scan. Enter your AI provider key on **Settings**; it is kept only in your session.

## Test

```powershell
dotnet test BlazorAzureInventory.Tests
```

The suite covers the connection classifier (including the table in section 9.4 of the plan), secret detection, flattening and precedence, SCM host and AI endpoint guards, resource matching, graph building, graph tools, bUnit component checks, and the secret sentinel test.

## Notes and limitations

- Reading app settings and Kudu files requires **Website Contributor** (or `Microsoft.Web/sites/config/list/action`). Reader-only users see `AccessDenied` badges and scan issues instead of failures.
- SCM sites reachable only through private endpoints cannot be read; app settings from ARM still work.
- The Anthropic and Google AI adapters are text-only and use the grounding fallback instead of tool calling.
- Azure OpenAI and Foundry deployment names cannot be listed with an API key, so they are entered manually.
- Pin the Azure MCP Server image tag (`AZURE_MCP_TAG`) and verify tool names against that version before release.
- vis-network is served from `wwwroot/lib/vis-network` (see `libman.json`) so the Content Security Policy needs no external script origins.