# Blazor Azure Inventory

**Azure Inventory** is a .NET 10 Blazor Server app (Radzen UI) that signs users in with Microsoft Entra ID, inventories the Azure resources *they* can see, analyzes Web App, Function App, and Container App configuration to detect connections, builds an in-memory knowledge graph, visualizes it with Cytoscape.js (Overview and Focus views), and answers questions through an AI chat backed by graph tools and a self-hosted, read-only Azure MCP Server.

## See the [blog post](https://blazorhelpwebsite.com/ViewBlogPost/20093)

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
- **Read-only, except for tags and role assignments you approve.** The only writes are Azure tags (`Microsoft.Resources/tags`), made with the signed-in user's token after the user reviews a diff and clicks Save or Apply, and the required role assignments, made only after the user confirms them under **Required Azure roles**. The MCP server runs with `--read-only` and an allowlist that is enforced on both the server and the client.
- **Resource properties popup and tags.** Click any resource name (Inventory grids, Graph nodes and edge endpoints, the App Configuration header, or resource links in chat) to see all its properties, connections, and tags. The **Tags** tab edits tags with validation, a diff preview, and conflict detection. **Tag connections...** on the Inventory page, or the AI chat, proposes `conn-out-<Type>` / `conn-in-<Type>` tags that list what each resource connects to. The AI can only propose tag changes; it cannot apply them. See [Docs/tags-feature-plan.md](Docs/tags-feature-plan.md).
- **In-memory, per-user sessions.** Snapshots, graphs, and encrypted AI keys live in a bounded in-memory cache keyed by tenant and object ID, and are cleared on sign-out.

## Prerequisites

- .NET SDK 10
- Docker (only to run the Azure MCP Server container locally through Aspire)
- Two multi-tenant Entra ID app registrations (see section 6.1 of the plan):
  - `azinventory-web`: redirect URI `https://localhost:7113/signin-oidc` (and your App Service URL), front-channel logout `/signout-oidc`, delegated permissions `User.Read` and Azure Service Management `user_impersonation`, plus the MCP scope. Optionally add Microsoft Graph `User.ReadBasic.All` (requested on first use otherwise) so Owners can grant the required roles to other users from the app.
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
| `Security:UsernameAllowListEnabled` | Enables username-based sign-in restriction; defaults to `false` |
| `Security:AllowedUsernames` | Comma-separated sign-in usernames allowed when the username allow-list is enabled. Matching is case-insensitive and ignores surrounding spaces; an enabled empty list denies all new sign-ins |
| `AI:DefaultProvider` | Optional app-wide AI provider used by everyone who has not entered their own on **Settings**. Set `ServiceType` (`OpenAI`, `AzureOpenAI`, `AzureAIFoundry`, `Anthropic`, or `GoogleAI`), `ApiKey`, `AIModel`, and for Azure providers `Endpoint` (plus optional `ApiVersion`, `DeploymentPath`, `ApiProtocol`). Leave `ServiceType` or `ApiKey` empty to require each user to supply their own. Keep the key in user secrets, environment variables (`AI__DefaultProvider__ApiKey`), or Key Vault, not in `appsettings.json` |

For local development, replace the `<...>` placeholders in `BlazorAzureInventory/appsettings.Local.json` (web app) and `BlazorAzureInventorySolution.AppHost/appsettings.Local.json` (`mcpTenantId`, `mcpClientId`, and `mcpClientSecret` parameters for the MCP container). Both files are git-ignored, loaded right after `appsettings.{Environment}.json`, and the web app's file is loaded only in Development and never published. While any required value is missing, the home page lists the exact steps to fix it.

Alternatively, override values with user secrets on the `BlazorAzureInventory` project (these take precedence over `appsettings.Local.json`):

```powershell
dotnet user-secrets --project BlazorAzureInventory set "AzureAd:ClientId" "<client-id>"
dotnet user-secrets --project BlazorAzureInventory set "AzureAd:ClientCredentials:0:SourceType" "ClientSecret"
dotnet user-secrets --project BlazorAzureInventory set "AzureAd:ClientCredentials:0:ClientSecret" "<dev-only-secret>"

# Optional: a default AI provider so users do not need their own key
dotnet user-secrets --project BlazorAzureInventory set "AI:DefaultProvider:ServiceType" "AzureOpenAI"
dotnet user-secrets --project BlazorAzureInventory set "AI:DefaultProvider:Endpoint" "https://contoso.openai.azure.com/"
dotnet user-secrets --project BlazorAzureInventory set "AI:DefaultProvider:AIModel" "gpt-4o"
dotnet user-secrets --project BlazorAzureInventory set "AI:DefaultProvider:ApiKey" "<key>"
```

The AppHost asks for the `mcpTenantId` and `mcpClientId` parameters used by the MCP container's incoming authentication. When running locally it also asks for `mcpClientSecret` (an `azinventory-mcp` client secret the container uses for On-Behalf-Of); keep it in `appsettings.Local.json` or AppHost user secrets (`Parameters:mcpClientSecret`).

When deploying to Azure Container Apps (`aspire deploy` or `azd up`), the AppHost also asks for `webClientId` (the `azinventory-web` client ID). No secrets are deployed: each app gets a user-assigned managed identity, so add a federated identity credential for that identity to the matching app registration. The web container app name comes from `Deployment:WebAppName` (default `azinventory-web`).

Organization-specific values (allowed tenants, bootstrap admins, subscription, resource group, registry, and app names) belong in the git-ignored `BlazorAzureInventorySolution.AppHost/appsettings.Org.json`; start from [appsettings.Org.sample.json](BlazorAzureInventorySolution.AppHost/appsettings.Org.sample.json). To deploy to an existing environment with `deploy/Publish-AzInventory.ps1`, see [Docs/deployment.md](Docs/deployment.md).

## Run

```powershell
# Everything (app + Azure MCP Server container) through Aspire
dotnet run --project BlazorAzureInventorySolution.AppHost

# The app on its own (chat falls back to graph tools only)
dotnet run --project BlazorAzureInventory
```

Then select **Connect to Azure**, open **Inventory**, choose subscriptions, and select **Scan**. **Knowledge Graph** and **AI Chat** become available after the first scan. If `AI:DefaultProvider` is configured, chat works right away; otherwise enter your AI provider key on **Settings**. A key you enter is kept only in your session and overrides the default.

## Test

```powershell
dotnet test BlazorAzureInventory.Tests
```

The suite covers the connection classifier (including the table in section 9.4 of the plan), secret detection, flattening and precedence, SCM host and AI endpoint guards, resource matching, graph building, graph tools, bUnit component checks, and the secret sentinel test.

## Notes and limitations

- Azure calls use each signed-in user's own identity, so roles are assigned to users on the subscriptions they scan:
  - **Reader** lists subscriptions, resources, blob containers, and Container Apps environment variables.
  - **Website Contributor** reads app settings and connection strings (`Microsoft.Web/sites/config/list/action`) and Kudu files (`Microsoft.Web/sites/publish/action`). It does not include `*/read`, so full results need **Reader and Website Contributor** together (or Contributor/Owner).
  - Reader-only users see `AccessDenied` badges, and the scan issues dialog names the exact missing role, the affected subscriptions, and the `az role assignment create` commands to fix it. The same table is on the setup page and under **Required Azure roles** on the home page.
  - **Tag Contributor** (optional) edits tags (`Microsoft.Resources/tags/write`). Without it, the Tags tab is read-only.
  - Users who can write role assignments (Owner, User Access Administrator, or Role Based Access Control Administrator) but lack these roles can click **Check my roles** under **Required Azure roles** on the home page, then **Assign to me** to assign the missing roles to their own account at subscription scope (after a confirmation prompt). Assignments can take a few minutes to take effect.
  - Those users can also **grant the roles to another user** from the same section: enter the user's email address, user principal name, or object ID, pick subscriptions and roles, and confirm. The user is looked up in each subscription's directory through Microsoft Graph with the delegated `User.ReadBasic.All` permission, which the app requests (incremental consent) the first time. The role check and **Check my roles** always reflect the account signed in to the app, which may differ from the account the Azure CLI uses.
- SCM sites reachable only through private endpoints cannot be read; app settings from ARM still work.
- All providers (OpenAI, Azure OpenAI, Azure AI Foundry, Anthropic, and Google AI) use tool calling. Models without function calling (for example embeddings, TTS, realtime, or `o1-mini`) use the grounding fallback.
- Azure OpenAI and Foundry deployment names cannot be listed with an API key, so they are entered manually.
- Pin the Azure MCP Server image tag (`AZURE_MCP_TAG`) and verify tool names against that version before release.
- Cytoscape.js and the Fluent UI System Icons are served from `wwwroot/lib/cytoscape` and `wwwroot/lib/fluent-icons` (see `libman.json`) so the Content Security Policy needs no external script origins. Run `deploy/Verify-VendoredAssets.ps1` to check their SHA-256 hashes; CI does this on every push.
- **Knowledge Graph release note:** the graph opens in an Overview that hides unconnected resources by default. Turn off **Only connected** (under **Advanced** in the filter panel, or remove its chip) to see them. This is applied once to filters saved before the change, and your choice is kept afterwards. Child resources (databases, containers, slots) are folded into their parent; turn on **Show child resources** to see them. `graph?focus=<resource-id>` links now open a Focus view of that resource.