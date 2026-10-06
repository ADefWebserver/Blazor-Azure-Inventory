using BlazorAzureInventory.Models.AI;
using BlazorAzureInventory.Models.Options;
using BlazorAzureInventory.Services.Auth;
using BlazorAzureInventory.Services.Graph;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace BlazorAzureInventory.Services.AI;

/// <summary>
/// Keeps each user's AI settings in their in-memory session. API keys are encrypted with Data Protection and are
/// only decrypted by <see cref="GetApiKeyAsync"/> for outbound provider calls. Nothing is persisted.
/// When <c>AI:DefaultProvider</c> is configured, it fills in anything the user has not set, and its key is used
/// until the user enters their own. The default key is never returned to the UI and is only sent to the
/// configured default endpoint host.
/// </summary>
public sealed class SessionAISettingsService(
    InventorySessionStore store, UserContextAccessor userContext, IDataProtectionProvider dataProtection,
    IOptions<AIOptions> aiOptions)
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("BlazorAzureInventory.AIKeys.v1");

    /// <summary>The configured default provider, or null when none is fully configured.</summary>
    public AIServiceType? DefaultServiceType =>
        aiOptions.Value.DefaultProvider is { IsConfigured: true } d && d.TryGetServiceType(out var type) ? type : null;

    public async Task<AISettings> GetSettingsAsync()
    {
        var stored = await GetProtectedAsync();
        var settings = new AISettings { ActiveServiceType = stored?.ActiveServiceType ?? DefaultServiceType ?? AIServiceType.OpenAI };
        foreach (var type in AIServiceTypes.All)
        {
            settings.Providers[type] = ToEditable(type, stored?.Providers.GetValueOrDefault(type));
        }
        return settings;
    }

    public async Task<AIProviderSettings> GetProviderSettingsAsync(AIServiceType type)
    {
        var stored = await GetProtectedAsync();
        return ToEditable(type, stored?.Providers.GetValueOrDefault(type));
    }

    public async Task SaveSettingsAsync(AISettings settings)
    {
        var user = await userContext.GetUserAsync();
        var session = store.GetOrCreate(user);
        var target = session.AISettings ?? new ProtectedAISettings();
        target.ActiveServiceType = settings.ActiveServiceType;

        foreach (var (type, p) in settings.Providers)
        {
            var existing = target.Providers.GetValueOrDefault(type) ?? new ProtectedProviderSettings();
            if (!string.IsNullOrWhiteSpace(p.ApiKey))
            {
                var key = p.ApiKey.Trim();
                existing.ProtectedApiKey = _protector.Protect(key);
                existing.ApiKeyLast4 = key.Length > 4 ? key[^4..] : key;
            }
            existing.Endpoint = p.Endpoint.Trim();
            existing.ApiVersion = p.ApiVersion.Trim();
            existing.DeploymentPath = p.DeploymentPath.Trim();
            existing.ApiProtocol = p.ApiProtocol;
            existing.AIModel = p.AIModel.Trim();
            target.Providers[type] = existing;

            // The plaintext key does not stay in the editor's working copy after saving.
            p.ApiKey = "";
            p.HasStoredKey = existing.ProtectedApiKey is not null;
            p.ApiKeyLast4 = existing.ApiKeyLast4;
            p.UsesDefaultKey = !p.HasStoredKey && CanUseDefaultKey(p);
        }

        session.AISettings = target;
        store.Set(user, session);
    }

    public async Task ClearAsync()
    {
        var user = await userContext.GetUserAsync();
        if (store.Get(user) is { } session)
        {
            session.AISettings = null;
            store.Set(user, session);
        }
    }

    /// <summary>
    /// Decrypts the user's stored key, or falls back to the configured default key when <paramref name="provider"/>
    /// still targets the default provider and endpoint host. For outbound calls only. Never return this value to the UI.
    /// </summary>
    public async Task<string?> GetApiKeyAsync(AIProviderSettings provider)
    {
        var stored = await GetProtectedAsync();
        var protectedKey = stored?.Providers.GetValueOrDefault(provider.ServiceType)?.ProtectedApiKey;
        if (protectedKey is not null) return _protector.Unprotect(protectedKey);
        return CanUseDefaultKey(provider) ? aiOptions.Value.DefaultProvider.ApiKey.Trim() : null;
    }

    /// <summary>
    /// True when the default key may be used for <paramref name="provider"/>: same provider type and, for Azure
    /// providers, the same https endpoint host as configured, so the key cannot be redirected elsewhere.
    /// </summary>
    public bool CanUseDefaultKey(AIProviderSettings provider)
    {
        if (DefaultServiceType != provider.ServiceType) return false;
        if (provider.ServiceType is not (AIServiceType.AzureOpenAI or AIServiceType.AzureAIFoundry)) return true;

        return TryGetHttpsHost(aiOptions.Value.DefaultProvider.Endpoint, out var defaultHost)
            && TryGetHttpsHost(provider.Endpoint, out var host)
            && string.Equals(defaultHost, host, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetHttpsHost(string? endpoint, out string host)
    {
        host = "";
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        host = uri.IdnHost;
        return host.Length > 0;
    }

    private async Task<ProtectedAISettings?> GetProtectedAsync()
    {
        var user = await userContext.GetUserAsync();
        return store.Get(user)?.AISettings;
    }

    private AIProviderSettings ToEditable(AIServiceType type, ProtectedProviderSettings? p)
    {
        var settings = new AIProviderSettings
        {
            ServiceType = type,
            HasStoredKey = p?.ProtectedApiKey is not null,
            ApiKeyLast4 = p?.ApiKeyLast4,
            Endpoint = p?.Endpoint ?? "",
            ApiVersion = p?.ApiVersion ?? "",
            DeploymentPath = p?.DeploymentPath ?? "",
            ApiProtocol = p?.ApiProtocol ?? "",
            AIModel = p?.AIModel ?? ""
        };

        // Users who bring their own key keep exactly what they entered; otherwise blanks come from the defaults.
        if (!settings.HasStoredKey && DefaultServiceType == type)
        {
            var d = aiOptions.Value.DefaultProvider;
            settings.Endpoint = OrDefault(settings.Endpoint, d.Endpoint);
            settings.ApiVersion = OrDefault(settings.ApiVersion, d.ApiVersion);
            settings.DeploymentPath = OrDefault(settings.DeploymentPath, d.DeploymentPath);
            settings.ApiProtocol = OrDefault(settings.ApiProtocol, d.ApiProtocol);
            settings.AIModel = OrDefault(settings.AIModel, d.AIModel);
            settings.UsesDefaultKey = CanUseDefaultKey(settings);
        }
        return settings;
    }

    private static string OrDefault(string value, string? fallback) =>
        string.IsNullOrWhiteSpace(value) ? (fallback ?? "").Trim() : value;
}
