using BlazorAzureInventory.Models.AI;

namespace BlazorAzureInventory.Services.AI;

public sealed record FoundryEndpointResolution(
    FoundryApiProtocol Protocol, Uri ResourceRoot, Uri ClientBaseUri, string OperationUrl, IReadOnlyList<string> Notices);

/// <summary>Normalizes any Foundry URL (project, /openai/v1, /anthropic) to the resource root and picks a protocol.</summary>
public static class FoundryEndpointResolver
{
    public static bool TryResolve(
        string? endpoint, string? deployment, FoundryApiProtocol requested,
        out FoundryEndpointResolution resolution, out string error)
    {
        resolution = null!;
        error = "";
        if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri))
        {
            error = "Enter a valid Foundry endpoint URL.";
            return false;
        }
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            error = "Endpoint must use https.";
            return false;
        }

        var notices = new List<string>();
        var root = new Uri($"https://{uri.Host.ToLowerInvariant()}/");
        var path = uri.AbsolutePath.Trim('/');
        if (path.Length > 0)
        {
            notices.Add($"The path '/{path}' was normalized to the resource root.");
        }

        var protocol = requested;
        if (protocol == FoundryApiProtocol.Auto)
        {
            protocol = path.Contains("anthropic", StringComparison.OrdinalIgnoreCase)
                       || (deployment?.StartsWith("claude", StringComparison.OrdinalIgnoreCase) ?? false)
                ? FoundryApiProtocol.AnthropicMessages
                : FoundryApiProtocol.ChatCompletions;
        }

        var (clientBase, operation) = protocol switch
        {
            FoundryApiProtocol.Responses => (new Uri(root, "openai/v1"), new Uri(root, "openai/v1/responses")),
            FoundryApiProtocol.AnthropicMessages => (new Uri(root, "anthropic/v1/messages"), new Uri(root, "anthropic/v1/messages")),
            _ => (new Uri(root, "openai/v1"), new Uri(root, "openai/v1/chat/completions"))
        };

        resolution = new FoundryEndpointResolution(protocol, root, clientBase, operation.ToString(), notices);
        return true;
    }

    public static string Describe(FoundryApiProtocol protocol) => protocol switch
    {
        FoundryApiProtocol.ChatCompletions => "Chat Completions",
        FoundryApiProtocol.Responses => "Responses",
        FoundryApiProtocol.AnthropicMessages => "Anthropic Messages",
        _ => "Auto"
    };
}
