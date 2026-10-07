using System.Text.Json;
using System.Text.Json.Nodes;

namespace BlazorAzureInventory.Services.Auth;

public sealed class SecuritySettingsStore(IHostEnvironment environment)
{
    private static readonly SemaphoreSlim WriteLock = new(1, 1);
    private readonly string _settingsPath = Path.Combine(environment.ContentRootPath, "appsettings.json");

    public async Task SaveUsernameAllowListAsync(bool enabled, string? usernames, CancellationToken cancellationToken = default)
    {
        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            var json = await File.ReadAllTextAsync(_settingsPath, cancellationToken);
            var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            })?.AsObject() ?? throw new InvalidOperationException("appsettings.json does not contain a JSON object.");

            var security = root["Security"] as JsonObject ?? new JsonObject();
            root["Security"] = security;
            security["UsernameAllowListEnabled"] = enabled;
            security["AllowedUsernames"] = string.Join(", ", UsernameGuard.ParseAllowedUsernames(usernames));

            var temporaryPath = _settingsPath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, cancellationToken);
            File.Move(temporaryPath, _settingsPath, true);
        }
        finally
        {
            WriteLock.Release();
        }
    }
}
