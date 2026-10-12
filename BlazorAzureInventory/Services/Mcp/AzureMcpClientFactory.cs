using System.Net.Http.Headers;
using System.Security.Claims;
using BlazorAzureInventory.Models.Options;
using BlazorAzureInventory.Services.Auth;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace BlazorAzureInventory.Services.Mcp;

/// <summary>Attaches a delegated bearer token for the MCP server audience to every request.</summary>
public sealed class McpBearerTokenHandler(ITokenAcquisition tokens, ClaimsPrincipal user, string? tenantId, IReadOnlyList<string> scopes)
    : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var result = await tokens.GetAuthenticationResultForUserAsync(scopes, tenantId: tenantId, user: user,
            tokenAcquisitionOptions: new TokenAcquisitionOptions { CancellationToken = cancellationToken });
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", result.AccessToken);
        return await base.SendAsync(request, cancellationToken);
    }
}

/// <summary>Keeps only allowlisted MCP tools and logs allowlisted tools the server no longer exposes.</summary>
public static class McpToolFilter
{
    public static IReadOnlyList<AITool> Filter(IEnumerable<McpClientTool> tools, IEnumerable<string> allowed, ILogger logger)
    {
        var allow = allowed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = tools.ToList();
        var kept = list.Where(t => allow.Contains(t.Name)).Cast<AITool>().ToList();

        var missing = allow.Except(list.Select(t => t.Name), StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count > 0)
        {
            logger.LogWarning("Allowlisted MCP tools not exposed by the server: {Tools}", string.Join(", ", missing));
        }
        return kept;
    }
}

/// <summary>
/// One MCP client per user circuit over Streamable HTTP. Uses the IHttpClientFactory pipeline (service discovery and
/// resilience) wrapped by a per-user token handler. Elicitation requests are always declined.
/// </summary>
public sealed class AzureMcpClientFactory(
    IOptions<AzureMcpOptions> options,
    IHttpMessageHandlerFactory handlerFactory,
    ITokenAcquisition tokens,
    UserContextAccessor userContext,
    IConfiguration configuration,
    ILoggerFactory loggerFactory) : IAsyncDisposable
{
    public const string HttpClientName = "AzureMcp";
    private readonly ILogger _logger = loggerFactory.CreateLogger<AzureMcpClientFactory>();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpClient? _client;
    private IReadOnlyList<AITool>? _tools;
    private bool _unavailable;

    /// <summary>Why live Azure tools are unavailable and what to do about it; null while they work or before the first check.</summary>
    public string? UnavailableReason { get; private set; }

    public bool IsEnabled =>
        options.Value.Enabled && !string.IsNullOrWhiteSpace(options.Value.Endpoint) && options.Value.Scopes.Length > 0;

    /// <summary>Returns the allowlisted tools, or an empty list when the server is disabled or unreachable.</summary>
    public async Task<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken ct)
    {
        if (!IsEnabled)
        {
            UnavailableReason = DescribeDisabled(options.Value);
            return [];
        }
        if (_unavailable) return [];
        if (_tools is not null) return _tools;

        await _gate.WaitAsync(ct);
        try
        {
            if (_tools is not null) return _tools;
            var opts = options.Value;
            var user = await userContext.GetUserAsync();

            var handler = new McpBearerTokenHandler(tokens, user, user.GetTenantId(), opts.Scopes)
            {
                InnerHandler = handlerFactory.CreateHandler(HttpClientName)
            };
            var http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds + 15) };

            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = ResolveEndpoint(opts.Endpoint),
                TransportMode = HttpTransportMode.StreamableHttp,
                Name = "azure-mcp",
                EnableStandaloneGetStream = false
            }, http, loggerFactory, ownsHttpClient: true);

            var clientOptions = new McpClientOptions
            {
                Handlers = new McpClientHandlers
                {
                    ElicitationHandler = (_, _) => ValueTask.FromResult(new ElicitResult { Action = "decline" })
                }
            };

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(opts.TimeoutSeconds));
            _client = await McpClient.CreateAsync(transport, clientOptions, loggerFactory, timeout.Token);
            var all = await _client.ListToolsAsync(cancellationToken: timeout.Token);
            _tools = McpToolFilter.Filter(all, opts.AllowedTools, _logger);
            if (_tools.Count == 0)
            {
                UnavailableReason = "The Azure MCP Server is running but exposes none of the tools listed in AzureMcp:AllowedTools. " +
                                    "Update AzureMcp:AllowedTools to match the tool names of the pinned server image.";
                _logger.LogWarning("Azure MCP server unavailable: {Reason}", UnavailableReason);
            }
            return _tools;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _unavailable = true;
            UnavailableReason = DescribeFailure(ex, options.Value);
            _logger.LogWarning("Azure MCP server unavailable ({ExceptionType}): {Reason} Chat continues with graph tools only.",
                ex.GetType().Name, UnavailableReason);
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    public static string DescribeDisabled(AzureMcpOptions opts) =>
        !opts.Enabled ? "The Azure MCP Server is turned off. Set AzureMcp:Enabled to true to use live Azure tools."
        : string.IsNullOrWhiteSpace(opts.Endpoint)
            ? "No Azure MCP Server address is configured. Run the app through the Aspire AppHost, or set AzureMcp:Endpoint to the server URL."
            : "No Azure MCP Server scope is configured. Set AzureMcp:Scopes to api://<azinventory-mcp client ID>/Mcp.Tools.Read.";

    /// <summary>Turns a connection failure into the concrete fix; never includes exception text, which may contain tokens or URLs.</summary>
    public static string DescribeFailure(Exception ex, AzureMcpOptions opts)
    {
        var chain = new List<Exception>();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            chain.Add(e);
            if (e is MicrosoftIdentityWebChallengeUserException { MsalUiRequiredException: { } msal }) chain.Add(msal);
        }
        bool HasCode(string code) => chain.Any(e => e.Message.Contains(code, StringComparison.Ordinal));

        if (HasCode("AADSTS65001"))
            return "Consent is missing for the Azure MCP Server (AADSTS65001). A tenant administrator must open Microsoft Entra ID > App registrations > " +
                   "azinventory-web > API permissions, confirm azinventory-mcp / Mcp.Tools.Read is listed, and select 'Grant admin consent'. " +
                   "Do the same in azinventory-mcp > API permissions for Azure Service Management / user_impersonation. Then sign out and sign in again.";
        if (HasCode("AADSTS500011") || HasCode("AADSTS650057") || HasCode("AADSTS70011"))
            return "The Azure MCP Server scope is not recognized by Microsoft Entra ID. Check that AzureMcp:Scopes is " +
                   "api://<azinventory-mcp client ID>/Mcp.Tools.Read, that azinventory-mcp > Expose an API defines the Mcp.Tools.Read scope, " +
                   "and that azinventory-web > API permissions includes it.";
        if (chain.Any(e => e is MicrosoftIdentityWebChallengeUserException or Microsoft.Identity.Client.MsalUiRequiredException))
            return "Your sign-in needs to be refreshed for the Azure MCP Server. Sign out and sign in again.";
        if (chain.OfType<HttpRequestException>().FirstOrDefault(e => e.StatusCode is not null) is { StatusCode: { } status })
            return status is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                ? $"The Azure MCP Server rejected the sign-in token ({(int)status}). Check that the AppHost parameters mcpTenantId and mcpClientId " +
                  "match the azinventory-mcp app registration, then restart the AppHost."
                : $"The Azure MCP Server returned HTTP {(int)status}. Check the azure-mcp resource logs in the Aspire dashboard.";
        if (chain.Any(e => e is ServiceDiscoveryException))
            return "The Azure MCP Server address could not be resolved. Run the app through the Aspire AppHost, or set AzureMcp:Endpoint to the server URL.";
        if (chain.Any(e => e is HttpRequestException or System.Net.Sockets.SocketException))
            return "The Azure MCP Server cannot be reached. In the Aspire dashboard, check that the azure-mcp resource is Running. If it is waiting for " +
                   "mcpClientSecret, add Parameters:mcpClientSecret (a client secret of azinventory-mcp) to " +
                   "BlazorAzureInventorySolution.AppHost/appsettings.Local.json and restart the AppHost.";
        if (chain.Any(e => e is OperationCanceledException or TimeoutException))
            return $"The Azure MCP Server did not respond within {opts.TimeoutSeconds} seconds. Check the azure-mcp resource logs in the Aspire dashboard, " +
                   "or raise AzureMcp:TimeoutSeconds.";
        return $"The Azure MCP Server could not be started ({ex.GetType().Name}). Check the azure-mcp resource logs in the Aspire dashboard.";
    }

    private sealed class ServiceDiscoveryException(string message) : InvalidOperationException(message);

    /// <summary>Resolves Aspire-style <c>https+http://name</c> endpoints from service discovery configuration.</summary>
    private Uri ResolveEndpoint(string endpoint)
    {
        var uri = new Uri(endpoint);
        if (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) return uri;

        foreach (var scheme in uri.Scheme.Split('+'))
        {
            var resolved = configuration[$"services:{uri.Host}:{scheme}:0"];
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return new Uri(new Uri(resolved), uri.PathAndQuery);
            }
        }
        throw new ServiceDiscoveryException($"No service discovery entry found for '{uri.Host}'.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
        _gate.Dispose();
    }
}
