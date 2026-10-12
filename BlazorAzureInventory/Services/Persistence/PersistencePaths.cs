using BlazorAzureInventory.Models.Options;
using Microsoft.Extensions.Options;

namespace BlazorAzureInventory.Services.Persistence;

/// <summary>Builds every path under the persistence root and refuses anything that would escape it.</summary>
public sealed class PersistencePaths
{
    public PersistencePaths(IOptions<PersistenceOptions> options)
        : this(ResolveRoot(options.Value.RootPath))
    {
    }

    public PersistencePaths(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    public string Root { get; }

    public string SystemDirectory => Combine("system");
    public string UsersFile => Combine("system", "users.json");
    public string UsersLock => Combine("system", "users.lock");
    public string MaintenanceLock => Combine("system", "maintenance.lock");
    public string AuditDirectory => Combine("system", "audit");
    public string DataProtectionDirectory => Combine("system", "dataprotection");
    public string HealthDirectory => Combine("system", "health");
    public string UserDataRoot => Combine("userdata");

    public string UserDirectory(UserKey key) => Combine("userdata", key.TenantId, key.ObjectId);
    public string Snapshot(UserKey key) => Combine("userdata", key.TenantId, key.ObjectId, "snapshot.json.gz");
    public string PreviousSnapshot(UserKey key) => Combine("userdata", key.TenantId, key.ObjectId, "snapshot.previous.json.gz");
    public string Preferences(UserKey key) => Combine("userdata", key.TenantId, key.ObjectId, "preferences.json");
    public string LastAccess(UserKey key) => Combine("userdata", key.TenantId, key.ObjectId, "lastaccess.json");

    public static string ResolveRoot(string? configured) =>
        string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlazorAzureInventory", "data")
            : configured;

    private string Combine(params string[] parts)
    {
        var path = Path.GetFullPath(Path.Combine([Root, .. parts]));
        if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Path escapes the persistence root.");
        return path;
    }
}
