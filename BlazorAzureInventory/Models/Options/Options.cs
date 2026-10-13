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
    [Range(1, 5000)] public int MaxQueuesPerAccount { get; set; } = 200;
    public string[] ScmHostSuffixes { get; set; } = [];
    public CloudEndpointSuffixOptions CloudEndpointSuffixes { get; set; } = new();

    /// <summary>Reads config files through ARM (<c>{siteId}/extensions/api/vfs</c>) when the SCM site is blocked or unreachable.</summary>
    public bool UseArmKuduFallback { get; set; } = true;

    /// <summary>Alternative environment names, for example Production = [Prod, Prd]. Matching works in both directions.</summary>
    public Dictionary<string, string[]> EnvironmentAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public StorageNameHintOptions StorageNameHints { get; set; } = new();
    public ExternalServiceDefinition[] ExternalServices { get; set; } = [];

    public IReadOnlyList<string> EffectiveConfigFilePatterns =>
        ConfigFilePatterns.Length > 0 ? ConfigFilePatterns : ["appsettings*.json"];

    public IReadOnlyList<string> EffectiveScmHostSuffixes =>
        ScmHostSuffixes.Length > 0 ? ScmHostSuffixes : [".azurewebsites.net", ".appserviceenvironment.net"];

    public IReadOnlyDictionary<string, string[]> EffectiveEnvironmentAliases =>
        EnvironmentAliases.Count > 0
            ? EnvironmentAliases
            : new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["Production"] = ["Prod", "Prd"],
                ["Development"] = ["Dev"],
                ["Staging"] = ["Stage", "Stg"],
                ["Test"] = ["Tst", "QA"]
            };

    public IReadOnlyList<ExternalServiceDefinition> EffectiveExternalServices =>
        ExternalServices.Length > 0 ? ExternalServices : [ExternalServiceDefinition.SendGrid];
}

/// <summary>Arrays default to empty because the configuration binder appends to pre-populated arrays.</summary>
public sealed class StorageNameHintOptions
{
    public string[] ContainerKeywords { get; set; } = [];
    public string[] QueueKeywords { get; set; } = [];

    public IReadOnlyList<string> EffectiveContainerKeywords => ContainerKeywords.Length > 0 ? ContainerKeywords : ["Container", "Blob"];
    public IReadOnlyList<string> EffectiveQueueKeywords => QueueKeywords.Length > 0 ? QueueKeywords : ["Queue"];
}

/// <summary>A non-Azure service detected from configuration, shown as one shared graph node.</summary>
public sealed class ExternalServiceDefinition
{
    [Required, RegularExpression("^[a-z0-9-]{2,40}$")] public string Id { get; set; } = "";
    [Required, StringLength(60)] public string DisplayName { get; set; } = "";
    [StringLength(60)] public string Category { get; set; } = "";
    [RegularExpression("^#[0-9A-Fa-f]{6}$")] public string Color { get; set; } = "#607D8B";

    /// <summary>Matched on a label boundary: the host equals the suffix or ends with "." + suffix.</summary>
    public string[] HostSuffixes { get; set; } = [];

    /// <summary>Matched when the last setting-key segment contains the keyword. Values are never inspected.</summary>
    public string[] KeyKeywords { get; set; } = [];

    public static ExternalServiceDefinition SendGrid => new()
    {
        Id = "sendgrid",
        DisplayName = "SendGrid",
        Category = "Email",
        Color = "#1A82E2",
        HostSuffixes = ["sendgrid.net", "sendgrid.com"],
        KeyKeywords = ["SendGrid"]
    };
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

    /// <summary>Legacy allow-list; only read once to seed the user directory.</summary>
    public bool UsernameAllowListEnabled { get; set; }

    /// <summary>Legacy allow-list; only read once to seed the user directory.</summary>
    public string AllowedUsernames { get; set; } = string.Empty;
}

public sealed class PersistenceOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Root folder for saved data. Empty uses %LOCALAPPDATA%/BlazorAzureInventory/data.</summary>
    public string RootPath { get; set; } = "";

    /// <summary>When true, saving is disabled unless <see cref="RootPath"/> is on a mounted network share.</summary>
    public bool RequirePersistentVolume { get; set; }

    [Range(1, 1024)] public int MaxSnapshotMegabytes { get; set; } = 50;
    [Range(1, 8760)] public int StaleAfterHours { get; set; } = 24;
    public bool KeepPreviousSnapshot { get; set; } = true;
    [Range(1, 3650)] public int RetentionDays { get; set; } = 90;
    [Range(1, 3650)] public int AuditRetentionDays { get; set; } = 365;
    [Range(1, 168)] public int CleanupIntervalHours { get; set; } = 24;
}

public sealed class UserManagementOptions
{
    /// <summary>Comma-separated usernames that are always administrators and cannot be changed in the UI.</summary>
    public string BootstrapAdmins { get; set; } = "";
    public bool MigrateLegacyAllowList { get; set; } = true;
    [Range(5, 3600)] public int DirectoryRefreshSeconds { get; set; } = 60;
    [Range(1, 60)] public int RevalidationMinutes { get; set; } = 5;
    [Range(1, 100000)] public int MaxUsers { get; set; } = 1000;
    public string SupportContact { get; set; } = "";
}

public sealed class QuotaOptions
{
    public bool AdminsExempt { get; set; }
    public ScanQuotaOptions Scans { get; set; } = new();
    public ReportQuotaOptions Reports { get; set; } = new();
    public ChatQuotaOptions Chat { get; set; } = new();
    public HttpQuotaOptions Http { get; set; } = new();
}

public sealed class ScanQuotaOptions
{
    [Range(1, 1000)] public int PerUserPerHour { get; set; } = 6;
    [Range(1, 10)] public int MaxConcurrentPerUser { get; set; } = 1;
    [Range(1, 100)] public int MaxConcurrentGlobal { get; set; } = 3;
}

public sealed class ReportQuotaOptions
{
    [Range(1, 1000)] public int PerUserPerHour { get; set; } = 10;
    [Range(1000, 1_048_575)] public int MaxRows { get; set; } = 200_000;
}

public sealed class ChatQuotaOptions
{
    [Range(1, 1000)] public int TurnsPerUserPer10Minutes { get; set; } = 30;
    [Range(1000, 100_000_000)] public long DefaultProviderTokensPerUserPerDay { get; set; } = 200_000;
}

public sealed class HttpQuotaOptions
{
    [Range(1, 10000)] public int AnonymousPerIpPerMinute { get; set; } = 20;
    [Range(1, 10000)] public int AuthenticatedPerUserPerMinute { get; set; } = 30;
}

public sealed class ChangeTrackingOptions
{
    /// <summary>Property keys ignored when comparing scans because they change on every read.</summary>
    public string[] IgnoredProperties { get; set; } = [];

    public IReadOnlyList<string> EffectiveIgnoredProperties =>
        IgnoredProperties.Length > 0 ? IgnoredProperties : ["lastModifiedTime", "lastModified", "provisioningState"];
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
