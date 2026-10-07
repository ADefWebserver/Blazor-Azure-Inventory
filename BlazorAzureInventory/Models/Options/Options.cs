using System.ComponentModel.DataAnnotations;
using BlazorAzureInventory.Models.AI;

namespace BlazorAzureInventory.Models.Options;

public sealed class InventoryOptions
{
    [Range(1, 64)] public int MaxParallelism { get; set; } = 8;
    public bool IncludeSlots { get; set; } = true;
    public bool IncludeFunctionApps { get; set; } = true;
    public bool ReadConfigFiles { get; set; } = true;
    public string[] ConfigFilePatterns { get; set; } = [];
    [Range(1024, 16 * 1024 * 1024)] public int MaxConfigFileBytes { get; set; } = 1024 * 1024;
    [Range(1, 5000)] public int MaxBlobContainersPerAccount { get; set; } = 200;
    public string[] ScmHostSuffixes { get; set; } = [];
    public CloudEndpointSuffixOptions CloudEndpointSuffixes { get; set; } = new();

    public IReadOnlyList<string> EffectiveConfigFilePatterns =>
        ConfigFilePatterns.Length > 0 ? ConfigFilePatterns : ["appsettings.json", "appsettings.*.json"];

    public IReadOnlyList<string> EffectiveScmHostSuffixes =>
        ScmHostSuffixes.Length > 0 ? ScmHostSuffixes : [".azurewebsites.net", ".appserviceenvironment.net"];
}

/// <summary>Arrays default to empty because the configuration binder appends to pre-populated arrays.</summary>
public sealed class CloudEndpointSuffixOptions
{
    public string[] Sql { get; set; } = [];
    public string[] Storage { get; set; } = [];
    public string[] Cosmos { get; set; } = [];
    public string[] Redis { get; set; } = [];
    public string[] ServiceBus { get; set; } = [];
    public string[] KeyVault { get; set; } = [];
    public string[] ContainerRegistry { get; set; } = [];
}

public sealed class AzureMcpOptions
{
    public bool Enabled { get; set; } = true;
    public string Endpoint { get; set; } = "";
    public string[] Scopes { get; set; } = [];
    public string[] AllowedTools { get; set; } = [];
    [Range(5, 600)] public int TimeoutSeconds { get; set; } = 60;
}

public sealed class GraphSessionOptions
{
    [Range(1, 1440)] public int SlidingExpirationMinutes { get; set; } = 30;
    [Range(1, 72)] public int AbsoluteExpirationHours { get; set; } = 8;
    [Range(1, 100000)] public int MaxSessions { get; set; } = 500;
}

public sealed class SecurityOptions
{
    public string[] AllowedTenantIds { get; set; } = [];
    public bool UsernameAllowListEnabled { get; set; }
    public string AllowedUsernames { get; set; } = string.Empty;
}

public sealed class AIOptions
{
    public string[] AllowedAzureAIHostSuffixes { get; set; } = [];
    public AIToolOptions Tools { get; set; } = new();
    public AIDefaultOptions Defaults { get; set; } = new();
    public AIDefaultProviderOptions DefaultProvider { get; set; } = new();

    public IReadOnlyList<string> EffectiveAzureAIHostSuffixes =>
        AllowedAzureAIHostSuffixes.Length > 0
            ? AllowedAzureAIHostSuffixes
            : [".openai.azure.com", ".services.ai.azure.com", ".cognitiveservices.azure.com"];
}

public sealed class AIToolOptions
{
    public bool Enabled { get; set; } = true;
    [Range(1, 32)] public int MaxCallsPerTurn { get; set; } = 8;
    [Range(1, 1000)] public int MaxResultsHardCap { get; set; } = 100;
}

public sealed class AIDefaultOptions
{
    [Range(0.0, 2.0)] public float Temperature { get; set; } = 0.2f;
    [Range(16, 32768)] public int MaxOutputTokens { get; set; } = 2048;
}

/// <summary>
/// Optional app-wide AI provider used by every user who has not entered their own settings. Keep
/// <see cref="ApiKey"/> in user secrets, environment variables, or Key Vault rather than appsettings.json.
/// </summary>
public sealed class AIDefaultProviderOptions
{
    /// <summary>One of <see cref="AIServiceType"/>: OpenAI, AzureOpenAI, AzureAIFoundry, Anthropic, GoogleAI.</summary>
    public string ServiceType { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string ApiVersion { get; set; } = "";
    public string DeploymentPath { get; set; } = "";
    public string ApiProtocol { get; set; } = "";
    public string AIModel { get; set; } = "";

    public bool TryGetServiceType(out AIServiceType type) =>
        Enum.TryParse(ServiceType?.Trim(), ignoreCase: true, out type) && Enum.IsDefined(type);

    /// <summary>True when a provider and key are set, plus an endpoint for the Azure providers.</summary>
    public bool IsConfigured =>
        TryGetServiceType(out var type)
        && !string.IsNullOrWhiteSpace(ApiKey)
        && (type is not (AIServiceType.AzureOpenAI or AIServiceType.AzureAIFoundry) || !string.IsNullOrWhiteSpace(Endpoint));
}

public sealed class TaggingOptions
{
    public bool Enabled { get; set; } = true;
    [Range(1, 32)] public int MaxParallelWrites { get; set; } = 4;
    [Range(1, 5000)] public int MaxResourcesPerBatch { get; set; } = 200;
    [Range(1, 5000)] public int MaxResourcesPerProposal { get; set; } = 200;
    [Range(1, 1440)] public int ProposalTtlMinutes { get; set; } = 60;
    [Range(1, 720)] public int StaleScanWarningHours { get; set; } = 24;
    [Range(1, 120)] public int PermissionCacheMinutes { get; set; } = 10;
    public ConnectionTagOptions Connections { get; set; } = new();
}

/// <summary>Arrays default to empty because the configuration binder appends to pre-populated arrays.</summary>
public sealed class ConnectionTagOptions
{
    public string Prefix { get; set; } = "conn";
    public string OutSegment { get; set; } = "out";
    public string InSegment { get; set; } = "in";
    public string UpdatedTagName { get; set; } = "conn-updated";
    public string Separator { get; set; } = ";";
    [Range(16, 256)] public int MaxValueLength { get; set; } = 256;
    public bool IncludeStructural { get; set; }
    public bool IncludeLowConfidence { get; set; }
    public bool IncludeExternal { get; set; } = true;
    public bool WriteWhenEmpty { get; set; } = true;
    public string[] EdgeTypes { get; set; } = [];

    public IReadOnlyList<string> EffectiveEdgeTypes =>
        EdgeTypes.Length > 0 ? EdgeTypes : ["CONNECTS_TO", "PULLS_IMAGE_FROM", "REFERENCES_SECRET"];

    public string OutPrefix => $"{Prefix}-{OutSegment}-";
    public string InPrefix => $"{Prefix}-{InSegment}-";
}
