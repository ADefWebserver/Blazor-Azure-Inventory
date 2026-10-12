using System.Text.Json.Serialization;

namespace BlazorAzureInventory.Models.Users;

public static class AppRoles
{
    public const string Admin = "Admin";
    public const string User = "User";

    public static bool IsValid(string? role) => role is Admin or User;
}

public static class AppPolicies
{
    public const string User = "AppUser";
    public const string Admin = "AppAdmin";
}

/// <summary>Claim added by the app from the user directory. Entra ID never issues it, so tokens cannot grant app roles.</summary>
public static class AppClaimTypes
{
    public const string Role = "azinv_role";
    public const string AuthenticationType = "AppDirectory";
}

[JsonConverter(typeof(JsonStringEnumConverter<UserStatus>))]
public enum UserStatus { Pending, Active, Disabled }

public sealed class UserRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("D");
    public string Username { get; set; } = "";
    public string? DisplayName { get; set; }
    public string Role { get; set; } = AppRoles.User;
    public UserStatus Status { get; set; } = UserStatus.Pending;
    public string? TenantId { get; set; }
    public string? ObjectId { get; set; }
    public bool IsBootstrap { get; set; }
    public string AddedBy { get; set; } = "";
    public DateTimeOffset AddedUtc { get; set; }
    public DateTimeOffset? BoundUtc { get; set; }
    public string? ModifiedBy { get; set; }
    public DateTimeOffset? ModifiedUtc { get; set; }
    public DateTimeOffset? LastSignInUtc { get; set; }

    public bool IsBoundTo(string tenantId, string objectId) =>
        string.Equals(TenantId, tenantId, StringComparison.OrdinalIgnoreCase) && string.Equals(ObjectId, objectId, StringComparison.OrdinalIgnoreCase);

    public UserRecord Clone() => (UserRecord)MemberwiseClone();
}

public sealed class UserDirectoryDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public long Version { get; set; }
    public List<UserRecord> Users { get; set; } = [];

    public UserDirectoryDocument Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        Version = Version,
        Users = Users.Select(u => u.Clone()).ToList()
    };
}

public sealed record AuditActor(string? TenantId, string? ObjectId, string? Username)
{
    public static readonly AuditActor System = new(null, null, "system");

    public override string ToString() => TenantId is null ? Username ?? "system" : $"{Username} ({TenantId}:{ObjectId})";
}

public sealed record AuditTarget(string? Id, string? Username);

public sealed record AuditEvent(
    DateTimeOffset TimestampUtc,
    string Event,
    string Replica,
    AuditActor? Actor,
    AuditTarget? Target,
    IReadOnlyDictionary<string, string?>? Before,
    IReadOnlyDictionary<string, string?>? After,
    string? CorrelationId);

public static class AuditEvents
{
    public const string DirectorySeeded = nameof(DirectorySeeded);
    public const string UserAdded = nameof(UserAdded);
    public const string UserRemoved = nameof(UserRemoved);
    public const string UserDataDeleted = nameof(UserDataDeleted);
    public const string RoleChanged = nameof(RoleChanged);
    public const string UserDisabled = nameof(UserDisabled);
    public const string UserEnabled = nameof(UserEnabled);
    public const string BindingReset = nameof(BindingReset);
    public const string UserBound = nameof(UserBound);
    public const string BindingMismatch = nameof(BindingMismatch);
    public const string SignInDenied = nameof(SignInDenied);
    public const string UnauthorizedAdminAction = nameof(UnauthorizedAdminAction);
    public const string UserDataExpired = nameof(UserDataExpired);
}
