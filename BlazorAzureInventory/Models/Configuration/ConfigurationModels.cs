using System.Text.Json.Serialization;

namespace BlazorAzureInventory.Models.Configuration;

public enum ConfigSource
{
    AppSettingsJson, AppSettingsEnvironmentJson,
    AppServiceAppSetting, AppServiceConnectionString,
    ContainerAppEnvValue, ContainerAppEnvSecretRef
}

public enum ConfigReadStatus { Read, AccessDenied, NotFound, NotScanned, Error }

/// <summary>Why config files could not be read through the SCM site.</summary>
public enum ConfigAccessReason { None, MissingRole, ScmAccessRestricted, ScmUnreachable, AuthRejected, Unknown }

/// <summary>Which route served the config files.</summary>
public enum ConfigFilesRoute { None, Direct, ArmTunnel }

public enum ConnectionTargetKind
{
    SqlServer, SqlDatabase, StorageAccount, BlobContainer, CosmosAccount,
    RedisCache, ServiceBusNamespace, KeyVault, ContainerRegistry, External,
    StorageQueue, ExternalService
}

public enum Confidence { Low, Medium, High }

/// <param name="AccountOrServerName">For <see cref="ConnectionTargetKind.ExternalService"/>, the catalog service id.</param>
/// <param name="ContainerName">Child entity name: blob container or storage queue.</param>
public sealed record ConnectionReference(
    ConnectionTargetKind Kind,
    string? Host,
    string? AccountOrServerName,
    string? DatabaseName,
    string? ContainerName,
    string? SecretName,
    bool IsLocalEmulator,
    Confidence Confidence,
    string? ResolvedResourceId)
{
    /// <summary>Identifier-only description, e.g. "contoso-sql.database.windows.net / orders".</summary>
    [JsonIgnore]
    public string TargetDisplay
    {
        get
        {
            if (IsLocalEmulator) return "Local emulator";
            var main = Host ?? AccountOrServerName ?? "";
            var child = DatabaseName ?? ContainerName ?? SecretName;
            return string.IsNullOrEmpty(child) ? main : $"{main} / {child}";
        }
    }
}

/// <summary>A container image reference (registry host and repository only; tags and digests are dropped).</summary>
public sealed record ImageReference(string RegistryHost, string Repository, string? ResolvedResourceId);

/// <summary>
/// One declared configuration entry. Deliberately has NO value property so raw values
/// cannot leak into the graph, cache, logs, or UI.
/// </summary>
public sealed record ConfigEntry(
    string AppId,
    string? SlotName,
    string Key,
    ConfigSource Source,
    string? FileName,
    string? Environment,
    bool IsSlotSticky,
    bool IsOverridden,
    bool IsSecretLike,
    bool IsKeyVaultReference,
    IReadOnlyList<ConnectionReference> References);

public sealed record AppConfigAnalysis(
    string AppId,
    string? Environment,
    ConfigReadStatus SettingsStatus,
    ConfigReadStatus ConfigFilesStatus,
    IReadOnlyList<string> ConfigFilesFound,
    IReadOnlyList<ConfigEntry> Entries)
{
    public IReadOnlyList<ImageReference> Images { get; init; } = [];
    public ConfigAccessReason ConfigFilesReason { get; init; }
    public ConfigFilesRoute ConfigFilesVia { get; init; }
    public IReadOnlyList<string> ConfigFilesRead { get; init; } = [];
    public IReadOnlyList<string> ConfigFilesSkipped { get; init; } = [];

    [JsonIgnore]
    public int ConnectionCount => Entries
        .Where(e => !e.IsOverridden)
        .SelectMany(e => e.References)
        .Select(r => r.ResolvedResourceId ?? r.TargetDisplay)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();
}

public static class ConfigSourceNames
{
    public static string ToDisplay(ConfigSource source, string? fileName) => source switch
    {
        ConfigSource.AppSettingsJson => fileName ?? "appsettings.json",
        ConfigSource.AppSettingsEnvironmentJson => fileName ?? "appsettings.{Environment}.json",
        ConfigSource.AppServiceAppSetting => "App setting",
        ConfigSource.AppServiceConnectionString => "App Service connection string",
        ConfigSource.ContainerAppEnvValue => "Container env var",
        ConfigSource.ContainerAppEnvSecretRef => "Container secret",
        _ => source.ToString()
    };

    public static string ToDisplay(ConnectionTargetKind kind) => kind switch
    {
        ConnectionTargetKind.SqlServer => "SQL Server",
        ConnectionTargetKind.SqlDatabase => "SQL Database",
        ConnectionTargetKind.StorageAccount => "Storage Account",
        ConnectionTargetKind.BlobContainer => "Blob Container",
        ConnectionTargetKind.CosmosAccount => "Cosmos DB",
        ConnectionTargetKind.RedisCache => "Redis",
        ConnectionTargetKind.ServiceBusNamespace => "Service Bus",
        ConnectionTargetKind.KeyVault => "Key Vault",
        ConnectionTargetKind.ContainerRegistry => "Container Registry",
        ConnectionTargetKind.StorageQueue => "Storage Queue",
        ConnectionTargetKind.ExternalService => "External Service",
        _ => "External"
    };
}
