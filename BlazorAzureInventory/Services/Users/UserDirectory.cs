using System.Text.RegularExpressions;
using BlazorAzureInventory.Models.Options;
using BlazorAzureInventory.Models.Users;
using BlazorAzureInventory.Services.Azure;
using Microsoft.Extensions.Options;

namespace BlazorAzureInventory.Services.Users;

public static class DenyReasons
{
    public const string NotInvited = "not-invited";
    public const string Disabled = "disabled";
    public const string Mismatch = "mismatch";
    public const string Unavailable = "unavailable";
}

public sealed record SignInDecision(bool Allowed, string? Role, string? DenyReason)
{
    public static SignInDecision Allow(string role) => new(true, role, null);
    public static SignInDecision Deny(string reason) => new(false, null, reason);
}

public sealed record AddUsersResult(IReadOnlyList<string> Added, IReadOnlyList<string> Existing, IReadOnlyList<string> Invalid);

/// <summary>A rule violation the admin can fix; the message is safe to show in the UI.</summary>
public sealed class UserDirectoryException(string message) : Exception(message);

/// <summary>
/// Who may sign in and with which app role. Cached in memory and refreshed when users.json changes, so lookups on every
/// request are cheap. All rules are enforced here, not only in the UI.
/// </summary>
public sealed partial class UserDirectory(
    IUserDirectoryStore store,
    AuditLog audit,
    IOptionsMonitor<UserManagementOptions> options,
    IOptions<SecurityOptions> security,
    TimeProvider time,
    ILogger<UserDirectory> logger)
{
    private static readonly TimeSpan LastSignInThrottle = TimeSpan.FromHours(1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private UserDirectoryDocument? _doc;
    private DateTime? _stamp;
    private DateTimeOffset _lastCheck = DateTimeOffset.MinValue;

    public bool CanWrite => store.CanWrite;

    /// <summary>False until users.json has been read at least once; sign-in then falls back to bootstrap admins only.</summary>
    public bool IsAvailable => _doc is not null;

    public IReadOnlySet<string> BootstrapAdmins => ParseUsernames(options.CurrentValue.BootstrapAdmins);

    public IReadOnlyList<UserRecord> Users => _doc?.Users.Select(u => u.Clone()).ToList() ?? [];

    [GeneratedRegex(@"^[^@\s,;]+@[^@\s,;]+\.[^@\s,;]+$")]
    private static partial Regex UsernamePattern();

    public static string? NormalizeUsername(string? value)
    {
        var normalized = DirectoryUserLookup.Normalize(value)?.ToLowerInvariant();
        return normalized is not null && UsernamePattern().IsMatch(normalized) ? normalized : null;
    }

    public static HashSet<string> ParseUsernames(string? value) =>
        (value ?? "").Split([',', ';', '\n', '\r'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeUsername).OfType<string>().ToHashSet(StringComparer.Ordinal);

    public async Task RefreshAsync(bool force = false, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        if (!force && _doc is not null && now - _lastCheck < TimeSpan.FromSeconds(options.CurrentValue.DirectoryRefreshSeconds)) return;
        await _refreshGate.WaitAsync(ct);
        try
        {
            _lastCheck = now;
            var stamp = store.GetChangeStamp();
            if (!force && _doc is not null && stamp == _stamp) return;
            if (await store.ReadAsync(ct) is { } doc)
            {
                _doc = doc;
                _stamp = stamp;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Keep the last good copy; never replace it with nothing.
            logger.LogError(ex, "Reading the user directory failed; using the cached copy");
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public UserRecord? Find(string? tenantId, string? objectId) =>
        tenantId is null || objectId is null ? null : _doc?.Users.FirstOrDefault(u => u.IsBoundTo(tenantId, objectId))?.Clone();

    public UserRecord? FindById(string id) => _doc?.Users.FirstOrDefault(u => u.Id == id)?.Clone();

    /// <summary>The app role of an active user, or null. Without a readable directory only bootstrap admins have a role.</summary>
    public string? GetActiveRole(string? tenantId, string? objectId, string? username)
    {
        if (_doc is null || !store.CanWrite)
        {
            return NormalizeUsername(username) is { } name && BootstrapAdmins.Contains(name) ? AppRoles.Admin : null;
        }
        return Find(tenantId, objectId) is { Status: UserStatus.Active } user ? user.Role : null;
    }

    public async Task<SignInDecision> SignInAsync(string tenantId, string objectId, string? username, string? displayName, CancellationToken ct)
    {
        await RefreshAsync(force: true, ct);
        var name = NormalizeUsername(username);
        var isBootstrap = name is not null && BootstrapAdmins.Contains(name);
        var target = new AuditTarget(null, name ?? username);

        if (_doc is null || !store.CanWrite)
        {
            if (isBootstrap) return SignInDecision.Allow(AppRoles.Admin);
            logger.LogWarning("Sign-in for {Username} refused because the user directory is unavailable", name);
            return SignInDecision.Deny(DenyReasons.Unavailable);
        }

        if (Find(tenantId, objectId) is { } bound)
        {
            if (bound.Status != UserStatus.Active)
            {
                await audit.WriteAsync(AuditEvents.SignInDenied, Actor(tenantId, objectId, name), new AuditTarget(bound.Id, bound.Username), ct: ct);
                return SignInDecision.Deny(DenyReasons.Disabled);
            }
            await TouchLastSignInAsync(bound, ct);
            return SignInDecision.Allow(bound.Role);
        }

        if (name is null)
        {
            await audit.WriteAsync(AuditEvents.SignInDenied, Actor(tenantId, objectId, username), target, ct: ct);
            return SignInDecision.Deny(DenyReasons.NotInvited);
        }

        var byName = _doc.Users.FirstOrDefault(u => u.Username == name);
        if (byName is { Status: UserStatus.Pending, ObjectId: null })
        {
            return await BindAsync(byName.Id, tenantId, objectId, name, displayName, ct);
        }
        if (byName is not null)
        {
            await audit.WriteAsync(AuditEvents.BindingMismatch, Actor(tenantId, objectId, name), new AuditTarget(byName.Id, byName.Username), ct: ct);
            return SignInDecision.Deny(DenyReasons.Mismatch);
        }
        if (isBootstrap)
        {
            return await CreateBootstrapAdminAsync(tenantId, objectId, name, displayName, ct);
        }

        await audit.WriteAsync(AuditEvents.SignInDenied, Actor(tenantId, objectId, name), target, ct: ct);
        return SignInDecision.Deny(DenyReasons.NotInvited);
    }

    public async Task<AddUsersResult> AddUsersAsync(AuditActor actor, IEnumerable<string> usernames, string role, CancellationToken ct)
    {
        if (!AppRoles.IsValid(role)) throw new UserDirectoryException("Unknown role.");
        await EnsureAdminAsync(actor, ct);
        var requested = usernames.Select(u => u.Trim()).Where(u => u.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (requested.Count > 100) throw new UserDirectoryException("Add at most 100 users at a time.");

        var invalid = requested.Where(u => NormalizeUsername(u) is null).ToList();
        var valid = requested.Select(NormalizeUsername).OfType<string>().Distinct().ToList();
        var added = new List<UserRecord>();
        var existing = new List<string>();
        await UpdateAsync(doc =>
        {
            added.Clear();
            existing.Clear();
            foreach (var name in valid)
            {
                if (doc.Users.Any(u => u.Username == name)) { existing.Add(name); continue; }
                if (doc.Users.Count >= options.CurrentValue.MaxUsers) throw new UserDirectoryException("User limit reached.");
                var record = new UserRecord
                {
                    Username = name, Role = role, Status = UserStatus.Pending,
                    AddedBy = actor.ToString(), AddedUtc = time.GetUtcNow()
                };
                doc.Users.Add(record);
                added.Add(record);
            }
        }, ct);
        foreach (var record in added)
        {
            await audit.WriteAsync(AuditEvents.UserAdded, actor, new AuditTarget(record.Id, record.Username), after: new Dictionary<string, string?> { ["role"] = role }, ct: ct);
        }
        return new AddUsersResult(added.Select(a => a.Username).ToList(), existing, invalid);
    }

    public Task SetRoleAsync(AuditActor actor, string id, string role, CancellationToken ct)
    {
        if (!AppRoles.IsValid(role)) throw new UserDirectoryException("Unknown role.");
        return ChangeAsync(actor, id, AuditEvents.RoleChanged, ct, (doc, user) =>
        {
            if (user.Role == role) return null;
            ThrowIfProtected(actor, user);
            if (role != AppRoles.Admin) ThrowIfLastAdmin(doc, user);
            var before = user.Role;
            user.Role = role;
            return (new() { ["role"] = before }, new() { ["role"] = role });
        });
    }

    public Task SetEnabledAsync(AuditActor actor, string id, bool enabled, CancellationToken ct) =>
        ChangeAsync(actor, id, enabled ? AuditEvents.UserEnabled : AuditEvents.UserDisabled, ct, (doc, user) =>
        {
            if (enabled == (user.Status != UserStatus.Disabled)) return null;
            ThrowIfProtected(actor, user);
            if (!enabled) ThrowIfLastAdmin(doc, user);
            var before = user.Status.ToString();
            user.Status = enabled ? (user.ObjectId is null ? UserStatus.Pending : UserStatus.Active) : UserStatus.Disabled;
            return (new() { ["status"] = before }, new() { ["status"] = user.Status.ToString() });
        });

    public Task ResetBindingAsync(AuditActor actor, string id, CancellationToken ct) =>
        ChangeAsync(actor, id, AuditEvents.BindingReset, ct, (doc, user) =>
        {
            if (user.ObjectId is null) return null;
            ThrowIfProtected(actor, user);
            if (user.Status == UserStatus.Active) ThrowIfLastAdmin(doc, user);
            var before = $"{user.TenantId}:{user.ObjectId}";
            user.TenantId = null;
            user.ObjectId = null;
            user.BoundUtc = null;
            user.Status = UserStatus.Pending;
            return (new() { ["binding"] = before }, new() { ["binding"] = null });
        });

    /// <summary>Removes the user and returns the removed record, so the caller can delete the user's saved data.</summary>
    public async Task<UserRecord?> RemoveAsync(AuditActor actor, string id, CancellationToken ct)
    {
        await EnsureAdminAsync(actor, ct);
        UserRecord? removed = null;
        await UpdateAsync(doc =>
        {
            var user = doc.Users.FirstOrDefault(u => u.Id == id) ?? throw new UserDirectoryException("The user no longer exists.");
            ThrowIfProtected(actor, user);
            ThrowIfLastAdmin(doc, user);
            doc.Users.Remove(user);
            removed = user.Clone();
        }, ct);
        await audit.WriteAsync(AuditEvents.UserRemoved, actor, new AuditTarget(id, removed!.Username),
            before: new Dictionary<string, string?> { ["role"] = removed.Role, ["status"] = removed.Status.ToString() }, ct: ct);
        return removed;
    }

    /// <summary>Creates users.json on first start, and makes sure every bootstrap admin is present and an active admin.</summary>
    public async Task EnsureSeededAsync(CancellationToken ct)
    {
        if (!store.CanWrite)
        {
            logger.LogWarning("User directory is read-only (no persistent storage); only bootstrap admins can sign in");
            return;
        }
        var bootstrap = BootstrapAdmins;
        var current = await store.ReadAsync(ct);
        if (current is null && bootstrap.Count == 0)
        {
            logger.LogCritical("No user directory exists and UserManagement:BootstrapAdmins is empty; every sign-in will be denied");
            return;
        }
        if (current is not null && !NeedsBootstrapFix(current, bootstrap))
        {
            _doc = current;
            _stamp = store.GetChangeStamp();
            return;
        }

        var created = current is null;
        var legacy = 0;
        var doc = await store.UpdateAsync(existing =>
        {
            var d = existing ?? new UserDirectoryDocument();
            var now = time.GetUtcNow();
            foreach (var name in bootstrap)
            {
                var user = d.Users.FirstOrDefault(u => u.Username == name);
                if (user is null)
                {
                    d.Users.Add(new UserRecord { Username = name, Role = AppRoles.Admin, Status = UserStatus.Pending, IsBootstrap = true, AddedBy = "system:bootstrap", AddedUtc = now });
                    continue;
                }
                user.IsBootstrap = true;
                user.Role = AppRoles.Admin;
                if (user.Status == UserStatus.Disabled) user.Status = user.ObjectId is null ? UserStatus.Pending : UserStatus.Active;
            }
            foreach (var user in d.Users.Where(u => u.IsBootstrap && !bootstrap.Contains(u.Username))) user.IsBootstrap = false;

            var sec = security.Value;
            if (existing is null && options.CurrentValue.MigrateLegacyAllowList && sec.UsernameAllowListEnabled)
            {
                foreach (var name in ParseUsernames(sec.AllowedUsernames).Where(n => d.Users.All(u => u.Username != n)))
                {
                    d.Users.Add(new UserRecord { Username = name, Role = AppRoles.User, Status = UserStatus.Pending, AddedBy = "system:migration", AddedUtc = now });
                    legacy++;
                }
            }
            return d;
        }, ct);
        _doc = doc;
        _stamp = store.GetChangeStamp();
        await audit.WriteAsync(AuditEvents.DirectorySeeded, AuditActor.System, null,
            after: new Dictionary<string, string?> { ["created"] = created.ToString(), ["bootstrapAdmins"] = bootstrap.Count.ToString(), ["migratedUsers"] = legacy.ToString() }, ct: ct);
    }

    private static bool NeedsBootstrapFix(UserDirectoryDocument doc, IReadOnlySet<string> bootstrap) =>
        bootstrap.Any(name => doc.Users.FirstOrDefault(u => u.Username == name) is not { IsBootstrap: true, Role: AppRoles.Admin } user
                              || user.Status == UserStatus.Disabled)
        || doc.Users.Any(u => u.IsBootstrap && !bootstrap.Contains(u.Username));

    private async Task<SignInDecision> BindAsync(string id, string tenantId, string objectId, string name, string? displayName, CancellationToken ct)
    {
        string? role = null;
        try
        {
            await UpdateAsync(doc =>
            {
                var user = doc.Users.FirstOrDefault(u => u.Id == id);
                if (user is not { Status: UserStatus.Pending, ObjectId: null } || doc.Users.Any(u => u.IsBoundTo(tenantId, objectId)))
                    throw new UserDirectoryException("The invitation changed while signing in.");
                var now = time.GetUtcNow();
                user.TenantId = tenantId;
                user.ObjectId = objectId;
                user.Status = UserStatus.Active;
                user.BoundUtc = now;
                user.LastSignInUtc = now;
                if (!string.IsNullOrWhiteSpace(displayName)) user.DisplayName = displayName.Trim();
                role = user.Role;
            }, ct);
        }
        catch (Exception ex) when (ex is UserDirectoryException or IOException or TimeoutException)
        {
            logger.LogWarning("Binding {Username} failed ({ExceptionType})", name, ex.GetType().Name);
            return BootstrapAdmins.Contains(name) ? SignInDecision.Allow(AppRoles.Admin) : SignInDecision.Deny(DenyReasons.Unavailable);
        }
        await audit.WriteAsync(AuditEvents.UserBound, Actor(tenantId, objectId, name), new AuditTarget(id, name), ct: ct);
        return SignInDecision.Allow(role!);
    }

    private async Task<SignInDecision> CreateBootstrapAdminAsync(string tenantId, string objectId, string name, string? displayName, CancellationToken ct)
    {
        var record = new UserRecord
        {
            Username = name, DisplayName = displayName, Role = AppRoles.Admin, Status = UserStatus.Active, IsBootstrap = true,
            TenantId = tenantId, ObjectId = objectId, AddedBy = "system:bootstrap", AddedUtc = time.GetUtcNow(),
            BoundUtc = time.GetUtcNow(), LastSignInUtc = time.GetUtcNow()
        };
        try
        {
            await UpdateAsync(doc =>
            {
                if (!doc.Users.Any(u => u.Username == name)) doc.Users.Add(record);
            }, ct);
            await audit.WriteAsync(AuditEvents.UserBound, Actor(tenantId, objectId, name), new AuditTarget(record.Id, name), ct: ct);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            logger.LogWarning("Recording bootstrap admin {Username} failed ({ExceptionType})", name, ex.GetType().Name);
        }
        return SignInDecision.Allow(AppRoles.Admin);
    }

    private async Task TouchLastSignInAsync(UserRecord user, CancellationToken ct)
    {
        if (user.LastSignInUtc is { } last && time.GetUtcNow() - last < LastSignInThrottle) return;
        try
        {
            await UpdateAsync(doc =>
            {
                if (doc.Users.FirstOrDefault(u => u.Id == user.Id) is { } current) current.LastSignInUtc = time.GetUtcNow();
            }, ct);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            logger.LogWarning("Updating last sign-in for {Username} failed ({ExceptionType})", user.Username, ex.GetType().Name);
        }
    }

    private async Task ChangeAsync(AuditActor actor, string id, string auditEvent, CancellationToken ct,
        Func<UserDirectoryDocument, UserRecord, (Dictionary<string, string?> Before, Dictionary<string, string?> After)?> change)
    {
        await EnsureAdminAsync(actor, ct);
        (Dictionary<string, string?> Before, Dictionary<string, string?> After)? diff = null;
        string? username = null;
        await UpdateAsync(doc =>
        {
            var user = doc.Users.FirstOrDefault(u => u.Id == id) ?? throw new UserDirectoryException("The user no longer exists.");
            diff = change(doc, user);
            if (diff is not null)
            {
                user.ModifiedBy = actor.ToString();
                user.ModifiedUtc = time.GetUtcNow();
            }
            username = user.Username;
        }, ct);
        if (diff is { } d) await audit.WriteAsync(auditEvent, actor, new AuditTarget(id, username), d.Before, d.After, ct);
    }

    private async Task UpdateAsync(Action<UserDirectoryDocument> apply, CancellationToken ct)
    {
        if (!store.CanWrite) throw new UserDirectoryException("Persistent storage is unavailable. Changes cannot be saved.");
        var doc = await store.UpdateAsync(current =>
        {
            var d = current ?? new UserDirectoryDocument();
            apply(d);
            return d;
        }, ct);
        _doc = doc;
        _stamp = store.GetChangeStamp();
    }

    private async Task EnsureAdminAsync(AuditActor actor, CancellationToken ct)
    {
        await RefreshAsync(force: true, ct);
        if (GetActiveRole(actor.TenantId, actor.ObjectId, actor.Username) == AppRoles.Admin) return;
        await audit.WriteAsync(AuditEvents.UnauthorizedAdminAction, actor, null, ct: ct);
        throw new UnauthorizedAccessException("Only administrators can manage users.");
    }

    private static void ThrowIfProtected(AuditActor actor, UserRecord user)
    {
        if (actor.TenantId is not null && actor.ObjectId is not null && user.IsBoundTo(actor.TenantId, actor.ObjectId))
            throw new UserDirectoryException("You cannot change your own access.");
        if (user.IsBootstrap)
            throw new UserDirectoryException("This administrator is configured by deployment settings.");
    }

    private static void ThrowIfLastAdmin(UserDirectoryDocument doc, UserRecord user)
    {
        if (user is not { Role: AppRoles.Admin, Status: UserStatus.Active }) return;
        if (doc.Users.Count(u => u is { Role: AppRoles.Admin, Status: UserStatus.Active }) <= 1)
            throw new UserDirectoryException("At least one active administrator is required.");
    }

    private static AuditActor Actor(string tenantId, string objectId, string? username) => new(tenantId, objectId, username);
}
