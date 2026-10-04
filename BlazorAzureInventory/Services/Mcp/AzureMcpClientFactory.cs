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

    public bool IsEnabled =>
        options.Value.Enabled && !string.IsNullOrWhiteSpace(options.Value.Endpoint) && options.Value.Scopes.Length > 0;

    /// <summary>Returns the allowlisted tools, or an empty list when the server is disabled or unreachable.</summary>
    public async Task<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken ct)
    {
        if (!IsEnabled || _unavailable) return [];
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
            return _tools;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _unavailable = true;
            _logger.LogWarning("Azure MCP server unavailable ({ExceptionType}); chat continues with graph tools only", ex.GetType().Name);
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

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
        throw new InvalidOperationException($"No service discovery entry found for '{uri.Host}'.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
        _gate.Dispose();
    }
}
