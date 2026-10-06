using BlazorAzureInventory.Models.AI;

namespace BlazorAzureInventory.Services.AI;

/// <summary>Decides between the tool-calling path and the grounding fallback path.</summary>
public static class AICapabilities
{
    // Model families without function calling. Matched on name segments so arbitrary deployment names don't trip them.
    private static readonly string[] NonToolModelMarkers =
        ["o1-mini", "o1-preview", "embedding", "dall-e", "whisper", "tts", "davinci", "babbage", "instruct", "realtime"];

    private static readonly char[] Separators = ['-', '_', '.', ':', '/', ' '];

    public static bool SupportsToolCalling(AIServiceType provider, string? model, FoundryApiProtocol protocol = FoundryApiProtocol.Auto)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        var name = "-" + string.Join('-', model.Split(Separators, StringSplitOptions.RemoveEmptyEntries)) + "-";
        return !NonToolModelMarkers.Any(m => name.Contains("-" + m + "-", StringComparison.OrdinalIgnoreCase)
                                             || name.Contains("-" + m + "s-", StringComparison.OrdinalIgnoreCase));
    }
}
