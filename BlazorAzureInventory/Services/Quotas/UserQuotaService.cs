using System.Collections.Concurrent;
using System.Security.Claims;
using System.Threading.RateLimiting;
using BlazorAzureInventory.Models.Options;
using BlazorAzureInventory.Models.Users;
using BlazorAzureInventory.Services.Auth;
using Microsoft.Extensions.Options;

namespace BlazorAzureInventory.Services.Quotas;

public sealed record QuotaResult(bool Allowed, string? Message)
{
    public static readonly QuotaResult Ok = new(true, null);
}

public sealed class QuotaExceededException(string message) : Exception(message);

/// <summary>A running scan; dispose to free the per-user and global scan slots.</summary>
public sealed class ScanSlot(Action release) : IDisposable
{
    private int _released;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0) release();
    }
}

/// <summary>
/// Per-user limits on expensive operations. Blazor interactions travel over the SignalR circuit, so HTTP rate limiting
/// does not see them; these checks run inside the app. Counters are per replica.
/// </summary>
public sealed class UserQuotaService : IDisposable
{
    private readonly QuotaOptions _options;
    private readonly TimeProvider _time;
    private readonly PartitionedRateLimiter<string> _scans;
    private readonly PartitionedRateLimiter<string> _reports;
    private readonly PartitionedRateLimiter<string> _chatTurns;
    private readonly SemaphoreSlim _globalScans;
    private readonly ConcurrentDictionary<string, int> _activeScans = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateOnly Day, long Tokens)> _tokens = new(StringComparer.Ordinal);

    public UserQuotaService(IOptions<QuotaOptions> options, TimeProvider time)
    {
        _options = options.Value;
        _time = time;
        _scans = Hourly(_options.Scans.PerUserPerHour, TimeSpan.FromHours(1));
        _reports = Hourly(_options.Reports.PerUserPerHour, TimeSpan.FromHours(1));
        _chatTurns = Hourly(_options.Chat.TurnsPerUserPer10Minutes, TimeSpan.FromMinutes(10));
        _globalScans = new SemaphoreSlim(_options.Scans.MaxConcurrentGlobal, _options.Scans.MaxConcurrentGlobal);
    }

    /// <summary>Reserves a scan slot, or returns null with the reason in <paramref name="denied"/>.</summary>
    public ScanSlot? TryStartScan(ClaimsPrincipal user, out QuotaResult denied)
    {
        denied = QuotaResult.Ok;
        var key = Key(user);
        var exempt = IsExempt(user);
        if (_activeScans.GetValueOrDefault(key) >= _options.Scans.MaxConcurrentPerUser)
        {
            denied = new QuotaResult(false, "A scan is already running for your account. Wait for it to finish.");
            return null;
        }
        if (!exempt && Acquire(_scans, key) is { } scanDenied)
        {
            denied = new QuotaResult(false, $"Scan limit reached ({_options.Scans.PerUserPerHour} per hour). {scanDenied}");
            return null;
        }
        if (!_globalScans.Wait(0))
        {
            denied = new QuotaResult(false, "Too many scans are running right now. Try again in a few minutes.");
            return null;
        }
        _activeScans.AddOrUpdate(key, 1, (_, n) => n + 1);
        return new ScanSlot(() =>
        {
            _activeScans.AddOrUpdate(key, 0, (_, n) => Math.Max(0, n - 1));
            _globalScans.Release();
        });
    }

    public QuotaResult TryReport(ClaimsPrincipal user) =>
        IsExempt(user) || Acquire(_reports, Key(user)) is not { } wait
            ? QuotaResult.Ok
            : new QuotaResult(false, $"Report limit reached ({_options.Reports.PerUserPerHour} per hour). {wait}");

    public QuotaResult TryChatTurn(ClaimsPrincipal user, bool usesDefaultKey)
    {
        if (IsExempt(user)) return QuotaResult.Ok;
        var key = Key(user);
        if (Acquire(_chatTurns, key) is { } wait)
            return new QuotaResult(false, $"Chat limit reached ({_options.Chat.TurnsPerUserPer10Minutes} messages per 10 minutes). {wait}");
        if (usesDefaultKey && TokensUsedToday(key) >= _options.Chat.DefaultProviderTokensPerUserPerDay)
            return new QuotaResult(false, "You have used today's AI allowance for the shared provider. Add your own key in Settings or try again tomorrow.");
        return QuotaResult.Ok;
    }

    /// <summary>Counts tokens spent on the app-owned default provider key.</summary>
    public void RecordDefaultProviderTokens(ClaimsPrincipal user, long tokens)
    {
        if (tokens <= 0) return;
        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        _tokens.AddOrUpdate(Key(user), (today, tokens), (_, v) => v.Day == today ? (today, v.Tokens + tokens) : (today, tokens));
    }

    public long TokensUsedToday(ClaimsPrincipal user) => TokensUsedToday(Key(user));

    private long TokensUsedToday(string key)
    {
        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        return _tokens.TryGetValue(key, out var v) && v.Day == today ? v.Tokens : 0;
    }

    private bool IsExempt(ClaimsPrincipal user) => _options.AdminsExempt && UserClaims.GetAppRole(user) == AppRoles.Admin;

    private static string Key(ClaimsPrincipal user) =>
        Persistence.UserKey.FromPrincipal(user)?.ToString() ?? UserClaims.GetUsername(user) ?? "anonymous";

    /// <summary>Takes one permit; returns a retry hint when none is available, or null on success.</summary>
    private static string? Acquire(PartitionedRateLimiter<string> limiter, string key)
    {
        using var lease = limiter.AttemptAcquire(key);
        if (lease.IsAcquired) return null;
        return lease.TryGetMetadata(MetadataName.RetryAfter, out var retry)
            ? $"Try again in {Math.Max(1, (int)Math.Ceiling(retry.TotalMinutes))} min."
            : "Try again later.";
    }

    private PartitionedRateLimiter<string> Hourly(int permits, TimeSpan window) =>
        PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = permits,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = window / permits,
            QueueLimit = 0,
            AutoReplenishment = true
        }));

    public void Dispose()
    {
        _scans.Dispose();
        _reports.Dispose();
        _chatTurns.Dispose();
        _globalScans.Dispose();
    }
}
