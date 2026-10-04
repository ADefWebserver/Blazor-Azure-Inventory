namespace BlazorAzureInventory.Services.Auth;

public enum SetupRequirement { ClientId, ClientCredential, McpScope }

/// <summary>Detects required configuration values that are missing or still hold "&lt;...&gt;" placeholders.</summary>
public sealed class SetupStatusService(IConfiguration configuration)
{
    public string? WebClientId =>
        Guid.TryParse(configuration["AzureAd:ClientId"], out var id) ? id.ToString() : null;

    public IReadOnlyList<SetupRequirement> GetMissing()
    {
        var missing = new List<SetupRequirement>();
        var ad = configuration.GetSection("AzureAd");

        if (WebClientId is null) missing.Add(SetupRequirement.ClientId);
        if (!HasClientCredential(ad)) missing.Add(SetupRequirement.ClientCredential);

        if (configuration.GetValue("AzureMcp:Enabled", true))
        {
            var scopes = configuration.GetSection("AzureMcp:Scopes").GetChildren().Select(s => s.Value).ToList();
            if (scopes.Count == 0 || !scopes.All(IsSet)) missing.Add(SetupRequirement.McpScope);
        }

        return missing;
    }

    public static bool BlocksSignIn(SetupRequirement requirement) => requirement != SetupRequirement.McpScope;

    private static bool HasClientCredential(IConfigurationSection ad)
    {
        if (IsSet(ad["ClientSecret"])) return true;

        return ad.GetSection("ClientCredentials").GetChildren().Any(c => c["SourceType"]?.Trim() switch
        {
            null or "" => false,
            var t when t.Equals("ClientSecret", StringComparison.OrdinalIgnoreCase) => IsSet(c["ClientSecret"]),
            // No ManagedIdentityClientId means the system-assigned identity.
            var t when t.Equals("SignedAssertionFromManagedIdentity", StringComparison.OrdinalIgnoreCase) =>
                c["ManagedIdentityClientId"] is null || IsSet(c["ManagedIdentityClientId"]),
            _ => true
        });
    }

    private static bool IsSet(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !(value.Contains('<') && value.Contains('>'));
}
