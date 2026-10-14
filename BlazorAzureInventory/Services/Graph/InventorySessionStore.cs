using System.Security.Claims;
using BlazorAzureInventory.Models.AI;
using BlazorAzureInventory.Models.Graph;
using BlazorAzureInventory.Models.Inventory;
using BlazorAzureInventory.Models.Options;
using BlazorAzureInventory.Models.Tags;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;

namespace BlazorAzureInventory.Services.Graph;

public enum SessionDataSource { Scan, Saved }

public sealed class InventorySession
{
    public InventorySnapshot? Snapshot { get; set; }
    public GraphDocument? Graph { get; set; }
    public GraphIndex? Index { get; set; }
    public ProtectedAISettings? AISettings { get; set; }

    /// <summary>Whether <see cref="Snapshot"/> came from a scan in this session or was loaded from saved data.</summary>
    public SessionDataSource Source { get; set; }

    /// <summary>Subscriptions in the saved scan that the user can no longer access, and that were hidden on load.</summary>
    public int HiddenSubscriptions { get; set; }

    /// <summary>Cached comparison with the previous saved scan, valid while its CurrentScannedUtc matches <see cref="Snapshot"/>.</summary>
    public SnapshotDiff? Changes { get; set; }

    /// <summary>Guards tag mutations; one session can be shared by several circuits (browser tabs) of the same user.</summary>
    public object SyncRoot { get; } = new();

    /// <summary>Pending and completed tag proposals, keyed by proposal ID.</summary>
    public Dictionary<string, TagProposal> TagProposals { get; } = new(StringComparer.Ordinal);

    /// <summary>Overview layouts for <see cref="Graph"/>, owned by <see cref="GraphLayoutService"/>. In memory only.</summary>
    internal GraphViewCache? ViewCache { get; set; }
}

/// <summary>
/// Per-user, in-memory session store keyed by <c>inv:{tid}:{oid}</c>. Uses a private, size-bounded cache so
/// it cannot evict (or be evicted by) other <see cref="IMemoryCache"/> users such as the MSAL token cache.
/// </summary>
public sealed class InventorySessionStore : IDisposable
{
    private readonly MemoryCache _cache;
    private readonly GraphSessionOptions _options;

    public InventorySessionStore(IOptions<GraphSessionOptions> options)
    {
        _options = options.Value;
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = _options.MaxSessions });
    }

    /// <summary>Raised with the session key whenever a session is set or cleared.</summary>
    public event Action<string>? SessionChanged;

    /// <summary>Raised with the session key and resource ID after tags of a resource were written.</summary>
    public event Action<string, string>? TagsChanged;

    public void NotifyTagsChanged(ClaimsPrincipal user, string resourceId)
    {
        if (KeyFor(user) is { } key) TagsChanged?.Invoke(key, resourceId);
    }

    public static string? KeyFor(ClaimsPrincipal user)
    {
        var tid = user.GetTenantId();
        var oid = user.GetObjectId();
        return tid is null || oid is null ? null : $"inv:{tid}:{oid}";
    }

    public InventorySession? Get(ClaimsPrincipal user) =>
        KeyFor(user) is { } key && _cache.TryGetValue(key, out InventorySession? session) ? session : null;

    /// <summary>Looks up a session by its <c>inv:{tid}:{oid}</c> key, for background work that has no principal.</summary>
    public InventorySession? GetByKey(string key) => _cache.TryGetValue(key, out InventorySession? session) ? session : null;

    public InventorySession GetOrCreate(ClaimsPrincipal user)
    {
        var existing = Get(user);
        if (existing is not null) return existing;
        var session = new InventorySession();
        Set(user, session);
        return session;
    }

    public void Set(ClaimsPrincipal user, InventorySession session)
    {
        var key = KeyFor(user) ?? throw new InvalidOperationException("The signed-in user has no tenant or object ID claim.");
        _cache.Set(key, session, new MemoryCacheEntryOptions
        {
            Size = 1,
            SlidingExpiration = TimeSpan.FromMinutes(_options.SlidingExpirationMinutes),
            AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(_options.AbsoluteExpirationHours)
        });
        SessionChanged?.Invoke(key);
    }

    public void Clear(ClaimsPrincipal user)
    {
        if (KeyFor(user) is { } key)
        {
            _cache.Remove(key);
            SessionChanged?.Invoke(key);
        }
    }

    public void Dispose() => _cache.Dispose();
}
