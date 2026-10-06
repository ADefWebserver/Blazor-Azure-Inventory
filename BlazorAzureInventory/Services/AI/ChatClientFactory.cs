using System.ClientModel;
using System.ClientModel.Primitives;
using BlazorAzureInventory.Models.AI;
using BlazorAzureInventory.Models.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;

namespace BlazorAzureInventory.Services.AI;

public sealed record ChatClientInfo(IChatClient Client, string ModelId, bool SupportsTools, string ProviderName, string Protocol, string OperationUrl);

/// <summary>Creates an <see cref="IChatClient"/> for the session's active provider. API keys are decrypted only here.</summary>
public sealed class ChatClientFactory(
    SessionAISettingsService settings,
    EndpointGuard guard,
    IHttpClientFactory httpClientFactory,
    IOptions<AIOptions> aiOptions)
{
    public const string HttpClientName = "AIProvider";
    private const string DefaultAzureApiVersion = "2024-10-21";

    /// <summary>Returns null when no provider with a key and model is configured for this session.</summary>
    public async Task<ChatClientInfo?> CreateAsync(CancellationToken ct)
    {
        var all = await settings.GetSettingsAsync();
        var provider = all.GetOrCreate(all.ActiveServiceType);
        if (!provider.IsConfigured || string.IsNullOrWhiteSpace(provider.AIModel)) return null;

        var key = await settings.GetApiKeyAsync(provider);
        return string.IsNullOrEmpty(key) ? null : await CreateForAsync(provider, key, ct);
    }

    public async Task<ChatClientInfo> CreateForAsync(AIProviderSettings p, string apiKey, CancellationToken ct)
    {
        var model = p.AIModel.Trim();
        var maxTokens = aiOptions.Value.Defaults.MaxOutputTokens;
        var name = AIServiceTypes.ToDisplayName(p.ServiceType);

        IChatClient inner;
        string protocol = "Chat Completions";
        string operationUrl;
        var supportsTools = AICapabilities.SupportsToolCalling(p.ServiceType, model, p.ParsedApiProtocol);

        switch (p.ServiceType)
        {
            case AIServiceType.OpenAI:
                inner = new OpenAIClient(new ApiKeyCredential(apiKey)).GetChatClient(model).AsIChatClient();
                operationUrl = "https://api.openai.com/v1/chat/completions";
                break;

            case AIServiceType.AzureOpenAI:
            {
                await ThrowIfBlockedAsync(p.Endpoint, ct);
                guard.TryValidateFormat(p.Endpoint, out var root, out _);
                var (baseUri, apiVersion) = AzureOpenAIBase(root, p, model);
                inner = CreateAzureOpenAIClient(baseUri, apiKey, apiVersion).GetChatClient(model).AsIChatClient();
                operationUrl = $"{baseUri}/chat/completions{(apiVersion is null ? "" : "?api-version=" + apiVersion)}";
                break;
            }

            case AIServiceType.AzureAIFoundry:
            {
                await ThrowIfBlockedAsync(p.Endpoint, ct);
                if (!FoundryEndpointResolver.TryResolve(p.Endpoint, model, p.ParsedApiProtocol, out var resolution, out var error))
                {
                    throw new InvalidOperationException(error);
                }
                protocol = FoundryEndpointResolver.Describe(resolution.Protocol);
                operationUrl = resolution.OperationUrl;
                supportsTools = AICapabilities.SupportsToolCalling(p.ServiceType, model, resolution.Protocol);
                inner = resolution.Protocol switch
                {
                    FoundryApiProtocol.AnthropicMessages => new AnthropicChatClient(
                        httpClientFactory.CreateClient(HttpClientName), resolution.ClientBaseUri, apiKey, model, maxTokens),
#pragma warning disable OPENAI001 // The Responses API surface is marked for evaluation in the OpenAI SDK.
                    FoundryApiProtocol.Responses => CreateAzureOpenAIClient(resolution.ClientBaseUri, apiKey, null)
                        .GetResponsesClient().AsIChatClient(model),
#pragma warning restore OPENAI001
                    _ => CreateAzureOpenAIClient(resolution.ClientBaseUri, apiKey, null).GetChatClient(model).AsIChatClient()
                };
                break;
            }

            case AIServiceType.Anthropic:
                protocol = "Anthropic Messages";
                operationUrl = "https://api.anthropic.com/v1/messages";
                inner = new AnthropicChatClient(httpClientFactory.CreateClient(HttpClientName), new Uri(operationUrl), apiKey, model, maxTokens);
                break;

            default:
                protocol = "Gemini generateContent";
                operationUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";
                inner = new GoogleAIChatClient(httpClientFactory.CreateClient(HttpClientName), apiKey, model, maxTokens);
                break;
        }

        var client = inner.AsBuilder()
            .UseFunctionInvocation(configure: f => f.MaximumIterationsPerRequest = aiOptions.Value.Tools.MaxCallsPerTurn)
            .Build();
        return new ChatClientInfo(client, model, supportsTools, name, protocol, operationUrl);
    }

    private async Task ThrowIfBlockedAsync(string endpoint, CancellationToken ct)
    {
        if (await guard.ValidateAsync(endpoint, ct) is { } error)
        {
            throw new InvalidOperationException(error);
        }
    }

    /// <summary>Uses the v1 API by default, or the deployment path with an api-version when one is set.</summary>
    private static (Uri BaseUri, string? ApiVersion) AzureOpenAIBase(Uri root, AIProviderSettings p, string model)
    {
        var version = p.ApiVersion.Trim();
        if (!string.IsNullOrWhiteSpace(p.DeploymentPath))
        {
            // An absolute or scheme-relative path would bypass the endpoint checks, so it must stay on the endpoint's host.
            var deploymentBase = new Uri(root, p.DeploymentPath.Trim().Trim('/'));
            if (!string.Equals(deploymentBase.Host, root.Host, StringComparison.OrdinalIgnoreCase) || deploymentBase.Scheme != root.Scheme)
            {
                throw new InvalidOperationException("The deployment path must be relative to the endpoint.");
            }
            return (deploymentBase, version.Length > 0 ? version : DefaultAzureApiVersion);
        }
        if (version.Length > 0 && !version.Equals("v1", StringComparison.OrdinalIgnoreCase) && !version.Equals("preview", StringComparison.OrdinalIgnoreCase))
        {
            return (new Uri(root, $"openai/deployments/{Uri.EscapeDataString(model)}"), version);
        }
        return (new Uri(root, "openai/v1"), version.Equals("preview", StringComparison.OrdinalIgnoreCase) ? "preview" : null);
    }

    private static OpenAIClient CreateAzureOpenAIClient(Uri baseUri, string apiKey, string? apiVersion)
    {
        var options = new OpenAIClientOptions { Endpoint = baseUri };
        if (apiVersion is not null)
        {
            options.AddPolicy(new QueryParameterPolicy("api-version", apiVersion), PipelinePosition.PerCall);
        }
        // Azure expects the key in an "api-key" header rather than a bearer token.
        options.AddPolicy(new AzureApiKeyHeaderPolicy(apiKey), PipelinePosition.BeforeTransport);
        return new OpenAIClient(new ApiKeyCredential(apiKey), options);
    }

    private sealed class AzureApiKeyHeaderPolicy(string apiKey) : PipelinePolicy
    {
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            Apply(message);
            ProcessNext(message, pipeline, currentIndex);
        }

        public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            Apply(message);
            await ProcessNextAsync(message, pipeline, currentIndex);
        }

        private void Apply(PipelineMessage message)
        {
            message.Request.Headers.Remove("Authorization");
            message.Request.Headers.Set("api-key", apiKey);
        }
    }

    private sealed class QueryParameterPolicy(string name, string value) : PipelinePolicy
    {
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            Apply(message);
            ProcessNext(message, pipeline, currentIndex);
        }

        public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            Apply(message);
            await ProcessNextAsync(message, pipeline, currentIndex);
        }

        private void Apply(PipelineMessage message)
        {
            if (message.Request.Uri is not { } uri) return;
            var builder = new UriBuilder(uri);
            var query = builder.Query.TrimStart('?');
            builder.Query = (query.Length > 0 ? query + "&" : "") + $"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}";
            message.Request.Uri = builder.Uri;
        }
    }
}
