using BlazorAzureInventory.Models.AI;
using BlazorAzureInventory.Services.Auth;
using BlazorAzureInventory.Services.Graph;
using Microsoft.AspNetCore.DataProtection;

namespace BlazorAzureInventory.Services.AI;

/// <summary>
/// Keeps each user's AI settings in their in-memory session. API keys are encrypted with Data Protection and are
/// only decrypted by <see cref="GetApiKeyAsync"/> for outbound provider calls. Nothing is persisted.
/// </summary>
public sealed class SessionAISettingsService(
    InventorySessionStore store, UserContextAccessor userContext, IDataProtectionProvider dataProtection)
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("BlazorAzureInventory.AIKeys.v1");

    public async Task<AISettings> GetSettingsAsync()
    {
        var stored = await GetProtectedAsync();
        var settings = new AISettings { ActiveServiceType = stored?.ActiveServiceType ?? AIServiceType.OpenAI };
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

    /// <summary>Decrypts the stored key for outbound calls only. Never return this value to the UI.</summary>
    public async Task<string?> GetApiKeyAsync(AIServiceType type)
    {
        var stored = await GetProtectedAsync();
        var protectedKey = stored?.Providers.GetValueOrDefault(type)?.ProtectedApiKey;
        return protectedKey is null ? null : _protector.Unprotect(protectedKey);
    }

    private async Task<ProtectedAISettings?> GetProtectedAsync()
    {
        var user = await userContext.GetUserAsync();
        return store.Get(user)?.AISettings;
    }

    private static AIProviderSettings ToEditable(AIServiceType type, ProtectedProviderSettings? p) => new()
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
}
