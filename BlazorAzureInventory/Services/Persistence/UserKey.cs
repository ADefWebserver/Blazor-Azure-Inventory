using System.Security.Claims;
using Microsoft.Identity.Web;

namespace BlazorAzureInventory.Services.Persistence;

/// <summary>A signed-in user's tenant and object ID, validated as GUIDs so they are safe to use in file paths.</summary>
public readonly record struct UserKey
{
    public string TenantId { get; }
    public string ObjectId { get; }

    public UserKey(string tenantId, string objectId)
    {
        TenantId = Normalize(tenantId, nameof(tenantId));
        ObjectId = Normalize(objectId, nameof(objectId));
    }

    public static UserKey? FromPrincipal(ClaimsPrincipal? user) =>
        TryCreate(user?.GetTenantId(), user?.GetObjectId());

    /// <summary>Parses the <c>inv:{tid}:{oid}</c> key used by <see cref="Graph.InventorySessionStore"/>.</summary>
    public static UserKey? FromSessionKey(string? sessionKey)
    {
        var parts = sessionKey?.Split(':');
        return parts is { Length: 3 } && parts[0] == "inv" ? TryCreate(parts[1], parts[2]) : null;
    }

    public static UserKey? TryCreate(string? tenantId, string? objectId) =>
        Guid.TryParse(tenantId, out var t) && Guid.TryParse(objectId, out var o) ? new UserKey(t.ToString("D"), o.ToString("D")) : null;

    public string SessionKey => $"inv:{TenantId}:{ObjectId}";

    public override string ToString() => $"{TenantId}:{ObjectId}";

    private static string Normalize(string value, string name) =>
        Guid.TryParse(value, out var guid) ? guid.ToString("D") : throw new ArgumentException("Must be a GUID.", name);
}
