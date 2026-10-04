using BlazorAzureInventory.Models.AI;

namespace BlazorAzureInventory.Services.AI;

/// <summary>Decides between the tool-calling path and the grounding fallback path.</summary>
public static class AICapabilities
{
    private static readonly string[] NonToolModelMarkers =
        ["o1-mini", "o1-preview", "embedding", "dall-e", "whisper", "tts", "davinci", "babbage", "instruct", "realtime"];

    public static bool SupportsToolCalling(AIServiceType provider, string? model, FoundryApiProtocol protocol = FoundryApiProtocol.Auto)
    {
        // The Anthropic and Google adapters are text-only and use the grounding fallback.
        if (provider is AIServiceType.Anthropic or AIServiceType.GoogleAI) return false;
        if (provider == AIServiceType.AzureAIFoundry && protocol == FoundryApiProtocol.AnthropicMessages) return false;
        if (string.IsNullOrWhiteSpace(model)) return false;
        return !NonToolModelMarkers.Any(m => model.Contains(m, StringComparison.OrdinalIgnoreCase));
    }
}
