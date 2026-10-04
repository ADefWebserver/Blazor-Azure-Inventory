using System.Text.RegularExpressions;

namespace BlazorAzureInventory.Services.Analysis;

/// <summary>Flags settings that look like secrets stored as plain text. Never stores or returns values.</summary>
public sealed partial class SecretClassifier
{
    private static readonly string[] SecretValueMarkers =
    [
        "Password=", "Pwd=", "AccountKey=", "SharedAccessKey=", "SharedAccessSignature=", "sig=", "password="
    ];

    [GeneratedRegex("(password|pwd|secret|key|token|sas|signature|credential|connectionstring|apikey|clientsecret)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretKeyPattern();

    [GeneratedRegex(@"^eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*$")]
    private static partial Regex JwtPattern();

    [GeneratedRegex(@"^[A-Za-z0-9+/]{32,}={0,2}$")]
    private static partial Regex LongBase64Pattern();

    [GeneratedRegex(@"^\s*@Microsoft\.KeyVault\(", RegexOptions.IgnoreCase)]
    private static partial Regex KeyVaultReferencePattern();

    public bool IsKeyVaultReference(string? value) =>
        value is not null && KeyVaultReferencePattern().IsMatch(value);

    public bool IsSecretLike(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || IsKeyVaultReference(value))
        {
            return false;
        }

        if (SecretKeyPattern().IsMatch(key))
        {
            return true;
        }

        foreach (var marker in SecretValueMarkers)
        {
            if (value.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var trimmed = value.Trim();
        return JwtPattern().IsMatch(trimmed) || LongBase64Pattern().IsMatch(trimmed);
    }
}
