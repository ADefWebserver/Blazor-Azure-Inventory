using System.ClientModel;
using System.Diagnostics;
using BlazorAzureInventory.Models.AI;
using Microsoft.Extensions.AI;

namespace BlazorAzureInventory.Services.AI;

/// <summary>Sends a minimal request to the configured provider. Azure endpoints pass <see cref="EndpointGuard"/> first.</summary>
public sealed class AIConnectionTester(SessionAISettingsService settings, ChatClientFactory factory, EndpointGuard guard)
{
    public async Task<AIConnectionTestResult> TestAsync(AIProviderSettings provider, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var key = string.IsNullOrWhiteSpace(provider.ApiKey) ? await settings.GetApiKeyAsync(provider.ServiceType) : provider.ApiKey.Trim();
        if (string.IsNullOrEmpty(key)) return Fail("Enter an API key first.", "", "");
        if (string.IsNullOrWhiteSpace(provider.AIModel)) return Fail("Select or enter a model first.", "", "");

        if (provider.ServiceType is AIServiceType.AzureOpenAI or AIServiceType.AzureAIFoundry
            && await guard.ValidateAsync(provider.Endpoint, ct) is { } blocked)
        {
            return Fail(blocked, "", provider.Endpoint);
        }

        ChatClientInfo? info = null;
        try
        {
            info = await factory.CreateForAsync(provider, key, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await info.Client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Reply with the single word OK.")],
                new ChatOptions { MaxOutputTokens = 16 }, timeout.Token);

            var tools = info.SupportsTools ? "supports" : "does not support";
            return new AIConnectionTestResult(true, $"Connected. Model {info.ModelId} {tools} tool calling.",
                info.Protocol, info.OperationUrl, sw.Elapsed, info.SupportsTools);
        }
        catch (ClientResultException ex)
        {
            return Fail($"Connection failed: {ex.Status} {Reason(ex.Status)}.", info?.Protocol ?? "", info?.OperationUrl ?? "");
        }
        catch (HttpRequestException ex)
        {
            return Fail($"Connection failed: {(ex.StatusCode is { } s ? $"{(int)s} {s}" : "network error")}.", info?.Protocol ?? "", info?.OperationUrl ?? "");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail("Connection failed: the request timed out.", info?.Protocol ?? "", info?.OperationUrl ?? "");
        }
        catch (InvalidOperationException ex)
        {
            return Fail(ex.Message, "", provider.Endpoint);
        }

        AIConnectionTestResult Fail(string message, string protocol, string url) =>
            new(false, message, protocol, url, sw.Elapsed, false);
    }

    private static string Reason(int status) => status switch
    {
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found (check the deployment or model name)",
        429 => "Too Many Requests",
        _ => "Error"
    };
}
