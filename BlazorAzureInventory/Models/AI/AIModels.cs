namespace BlazorAzureInventory.Models.AI;

public enum AIServiceType { OpenAI, AzureOpenAI, AzureAIFoundry, Anthropic, GoogleAI }

public static class AIServiceTypes
{
    public static readonly IReadOnlyList<AIServiceType> All = Enum.GetValues<AIServiceType>();

    public static string ToDisplayName(AIServiceType type) => type switch
    {
        AIServiceType.OpenAI => "OpenAI",
        AIServiceType.AzureOpenAI => "Azure OpenAI",
        AIServiceType.AzureAIFoundry => "Microsoft Foundry",
        AIServiceType.Anthropic => "Anthropic",
        AIServiceType.GoogleAI => "Google AI",
        _ => type.ToString()
    };
}

public enum FoundryApiProtocol { Auto, ChatCompletions, Responses, AnthropicMessages }

/// <summary>
/// Editable provider settings. <see cref="ApiKey"/> only holds a newly typed key in the editor's
/// working copy; stored keys are never decrypted into this object.
/// </summary>
public sealed class AIProviderSettings
{
    public AIServiceType ServiceType { get; set; }
    public string ApiKey { get; set; } = "";
    public bool HasStoredKey { get; set; }
    public string? ApiKeyLast4 { get; set; }
    public string Endpoint { get; set; } = "";
    public string ApiVersion { get; set; } = "";
    public string DeploymentPath { get; set; } = "";
    public string ApiProtocol { get; set; } = "";
    public string AIModel { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) || HasStoredKey;

    public FoundryApiProtocol ParsedApiProtocol =>
        Enum.TryParse<FoundryApiProtocol>(ApiProtocol, ignoreCase: true, out var p) ? p : FoundryApiProtocol.Auto;

    public AIProviderSettings Clone() => (AIProviderSettings)MemberwiseClone();
}

public sealed class AISettings
{
    public AIServiceType ActiveServiceType { get; set; } = AIServiceType.OpenAI;
    public Dictionary<AIServiceType, AIProviderSettings> Providers { get; set; } = new();

    public AIProviderSettings GetOrCreate(AIServiceType type)
    {
        if (!Providers.TryGetValue(type, out var settings))
        {
            settings = new AIProviderSettings { ServiceType = type };
            Providers[type] = settings;
        }
        return settings;
    }
}

/// <summary>Session-stored settings. API keys are encrypted with Data Protection.</summary>
public sealed class ProtectedAISettings
{
    public AIServiceType ActiveServiceType { get; set; } = AIServiceType.OpenAI;
    public Dictionary<AIServiceType, ProtectedProviderSettings> Providers { get; } = new();
}

public sealed class ProtectedProviderSettings
{
    public string? ProtectedApiKey { get; set; }
    public string? ApiKeyLast4 { get; set; }
    public string Endpoint { get; set; } = "";
    public string ApiVersion { get; set; } = "";
    public string DeploymentPath { get; set; } = "";
    public string ApiProtocol { get; set; } = "";
    public string AIModel { get; set; } = "";
}

public enum ModelListStatus { Success, Unreachable, Empty, Unauthorized }

public sealed record ModelListResult(ModelListStatus Status, IReadOnlyList<string> Models, string? ErrorMessage);

public sealed record AIConnectionTestResult(
    bool Success, string Message, string Protocol, string OperationUrl, TimeSpan Elapsed, bool SupportsTools);

public enum ChatRoleKind { User, Assistant, Tool, Error }

public sealed class ToolCallInfo
{
    public string Name { get; init; } = "";
    public string ArgumentSummary { get; init; } = "";
    public bool IsLiveAzureTool { get; init; }
    public bool IsRunning { get; set; } = true;
    public int? ResultCount { get; set; }
    public string? ResultSummary { get; set; }
    public bool Failed { get; set; }
}

public sealed class ChatMessageItem
{
    public ChatRoleKind Role { get; init; }
    public string Text { get; set; } = "";
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public ToolCallInfo? ToolCall { get; init; }

    /// <summary>Set on items that render a tag proposal card.</summary>
    public string? ProposalId { get; init; }
    public bool IsStreaming { get; set; }
}
