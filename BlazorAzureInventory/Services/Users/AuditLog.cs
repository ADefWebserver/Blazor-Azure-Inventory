using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BlazorAzureInventory.Models.Users;
using BlazorAzureInventory.Services.Persistence;

namespace BlazorAzureInventory.Services.Users;

/// <summary>
/// Append-only JSON-lines audit of user management, one file per month and replica so replicas never append to the same file.
/// Every event is also logged through <see cref="ILogger"/>.
/// </summary>
public sealed class AuditLog(PersistencePaths paths, PersistenceStatus status, TimeProvider time, ILogger<AuditLog> logger)
{
    private static readonly TimeSpan DeniedThrottle = TimeSpan.FromHours(1);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastDenied = new(StringComparer.OrdinalIgnoreCase);

    public async Task WriteAsync(string eventName, AuditActor? actor, AuditTarget? target,
        IReadOnlyDictionary<string, string?>? before = null, IReadOnlyDictionary<string, string?>? after = null,
        CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        if (eventName == AuditEvents.SignInDenied && target?.Username is { } denied)
        {
            if (_lastDenied.TryGetValue(denied, out var last) && now - last < DeniedThrottle) return;
            _lastDenied[denied] = now;
        }

        var entry = new AuditEvent(now, eventName, FileLease.ReplicaName, actor, target, before, after, Activity.Current?.Id);
        logger.LogInformation("Audit {AuditEvent} by {Actor} on {Target}", eventName, actor?.ToString(), target?.Username);
        if (!status.IsPersistent) return;

        var line = JsonSerializer.Serialize(entry, PersistenceJson.Options) + "\n";
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(paths.AuditDirectory);
            var file = Path.Combine(paths.AuditDirectory, $"users-{now:yyyy-MM}.{Sanitize(FileLease.ReplicaName)}.jsonl");
            await using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(line), ct);
            await stream.FlushAsync(ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Writing audit event {AuditEvent} failed", eventName);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The newest events from this and the previous month, across all replicas.</summary>
    public async Task<IReadOnlyList<AuditEvent>> ReadRecentAsync(int max, CancellationToken ct)
    {
        if (!status.IsPersistent || !Directory.Exists(paths.AuditDirectory)) return [];
        var now = time.GetUtcNow();
        var months = new[] { $"users-{now:yyyy-MM}.", $"users-{now.AddMonths(-1):yyyy-MM}." };
        var events = new List<AuditEvent>();
        foreach (var file in Directory.EnumerateFiles(paths.AuditDirectory, "users-*.jsonl")
                     .Where(f => months.Any(m => Path.GetFileName(f).StartsWith(m, StringComparison.Ordinal))))
        {
            using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    if (JsonSerializer.Deserialize<AuditEvent>(line, PersistenceJson.Options) is { } e) events.Add(e);
                }
                catch (JsonException)
                {
                    // A torn last line from a crash is skipped.
                }
            }
        }
        return events.OrderByDescending(e => e.TimestampUtc).Take(max).ToList();
    }

    private static string Sanitize(string name) => new(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
}
