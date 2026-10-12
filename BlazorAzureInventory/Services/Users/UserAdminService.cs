using BlazorAzureInventory.Models.Users;
using BlazorAzureInventory.Services.Auth;
using BlazorAzureInventory.Services.Persistence;
using Microsoft.Identity.Web;

namespace BlazorAzureInventory.Services.Users;

/// <summary>Entry point for the admin UI. The directory re-checks the current actor's role on every command.</summary>
public sealed class UserAdminService(
    UserDirectory directory,
    AuditLog audit,
    IUserDataStore dataStore,
    UserContextAccessor userContext,
    ILogger<UserAdminService> logger)
{
    public bool CanWrite => directory.CanWrite;

    public async Task<AuditActor> GetActorAsync()
    {
        var user = await userContext.GetUserAsync();
        return new AuditActor(user.GetTenantId(), user.GetObjectId(), UserClaims.GetUsername(user));
    }

    public async Task<IReadOnlyList<UserRecord>> ListAsync(CancellationToken ct = default)
    {
        await directory.RefreshAsync(force: true, ct);
        return directory.Users.OrderBy(u => u.Username, StringComparer.Ordinal).ToList();
    }

    public async Task<AddUsersResult> AddAsync(IEnumerable<string> usernames, string role, CancellationToken ct = default) =>
        await directory.AddUsersAsync(await GetActorAsync(), usernames, role, ct);

    public async Task SetRoleAsync(string id, string role, CancellationToken ct = default) =>
        await directory.SetRoleAsync(await GetActorAsync(), id, role, ct);

    public async Task SetEnabledAsync(string id, bool enabled, CancellationToken ct = default) =>
        await directory.SetEnabledAsync(await GetActorAsync(), id, enabled, ct);

    public async Task ResetBindingAsync(string id, CancellationToken ct = default) =>
        await directory.ResetBindingAsync(await GetActorAsync(), id, ct);

    /// <summary>Removes the user and deletes their saved inventory data.</summary>
    public async Task RemoveAsync(string id, CancellationToken ct = default)
    {
        var actor = await GetActorAsync();
        var removed = await directory.RemoveAsync(actor, id, ct);
        if (UserKey.TryCreate(removed?.TenantId, removed?.ObjectId) is not { } key) return;
        try
        {
            await dataStore.DeleteAllAsync(key, ct);
            await audit.WriteAsync(AuditEvents.UserDataDeleted, actor, new AuditTarget(id, removed!.Username), ct: ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Deleting saved data of removed user {Username} failed ({ExceptionType})", removed!.Username, ex.GetType().Name);
        }
    }

    public Task<IReadOnlyList<AuditEvent>> GetAuditAsync(int max = 500, CancellationToken ct = default) => audit.ReadRecentAsync(max, ct);
}
