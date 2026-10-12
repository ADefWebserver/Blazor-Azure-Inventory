using BlazorAzureInventory.Models.Options;
using Microsoft.Extensions.Options;

namespace BlazorAzureInventory.Services.Persistence;

/// <summary>Deletes saved data of users inactive for <see cref="PersistenceOptions.RetentionDays"/>, old audit files, and orphaned temp files.</summary>
public sealed class RetentionCleanupService(
    PersistencePaths paths,
    PersistenceStatus status,
    IOptions<PersistenceOptions> options,
    TimeProvider time,
    ILogger<RetentionCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!status.IsPersistent) return;
        try
        {
            await Task.Delay(StartupDelay, time, stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(options.Value.CleanupIntervalHours), time);
            do
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Retention cleanup failed");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Runs one cleanup pass if no other replica is running one. Returns the number of user folders deleted.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        await using var lease = FileLease.TryAcquire(paths.MaintenanceLock, TimeSpan.FromMinutes(30));
        if (lease is null) return 0;

        var now = time.GetUtcNow();
        var cutoff = now.AddDays(-options.Value.RetentionDays);
        var deleted = 0;
        if (Directory.Exists(paths.UserDataRoot))
        {
            foreach (var tenantDir in Directory.EnumerateDirectories(paths.UserDataRoot))
            {
                foreach (var userDir in Directory.EnumerateDirectories(tenantDir))
                {
                    ct.ThrowIfCancellationRequested();
                    if (UserKey.TryCreate(Path.GetFileName(tenantDir), Path.GetFileName(userDir)) is not { } key
                        || !string.Equals(paths.UserDirectory(key), Path.GetFullPath(userDir), StringComparison.Ordinal))
                    {
                        logger.LogWarning("Skipping unexpected folder {Folder} during retention cleanup", Path.GetFileName(userDir));
                        continue;
                    }

                    DeleteOrphanedTempFiles(userDir, now);
                    if (await GetLastActiveAsync(key, userDir, ct) < cutoff)
                    {
                        Directory.Delete(userDir, recursive: true);
                        deleted++;
                        logger.LogInformation("Deleted saved data for {UserKey} after {Days} days without use", key, options.Value.RetentionDays);
                    }
                }
                if (!Directory.EnumerateFileSystemEntries(tenantDir).Any()) Directory.Delete(tenantDir);
            }
        }

        DeleteOldAuditFiles(now.AddDays(-options.Value.AuditRetentionDays));
        DeleteOrphanedTempFiles(paths.SystemDirectory, now);
        return deleted;
    }

    private async Task<DateTimeOffset> GetLastActiveAsync(UserKey key, string userDir, CancellationToken ct)
    {
        var lastAccess = await FileUserDataStore.ReadLastAccessAsync(paths.LastAccess(key), ct) ?? DateTimeOffset.MinValue;
        var newestFile = Directory.EnumerateFiles(userDir)
            .Select(f => new DateTimeOffset(File.GetLastWriteTimeUtc(f), TimeSpan.Zero))
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();
        return lastAccess > newestFile ? lastAccess : newestFile;
    }

    private void DeleteOldAuditFiles(DateTimeOffset cutoff)
    {
        if (!Directory.Exists(paths.AuditDirectory)) return;
        foreach (var file in Directory.EnumerateFiles(paths.AuditDirectory, "*.jsonl"))
        {
            if (File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime) File.Delete(file);
        }
    }

    private static void DeleteOrphanedTempFiles(string directory, DateTimeOffset now)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*" + AtomicFile.TempSuffix))
        {
            if (File.GetLastWriteTimeUtc(file) < now.UtcDateTime.AddHours(-1)) File.Delete(file);
        }
    }
}
