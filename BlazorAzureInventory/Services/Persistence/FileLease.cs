using System.Text.Json;

namespace BlazorAzureInventory.Services.Persistence;

/// <summary>
/// Cross-process and cross-replica mutex backed by a lock file created with <see cref="FileMode.CreateNew"/>,
/// which is an atomic exclusive create on local disks and SMB shares. Locks older than their TTL are taken over.
/// </summary>
public sealed class FileLease : IAsyncDisposable
{
    private readonly string _path;
    private readonly string _owner;
    private bool _released;

    private FileLease(string path, string owner)
    {
        _path = path;
        _owner = owner;
    }

    public static async Task<FileLease> AcquireAsync(string path, TimeSpan ttl, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (TryAcquire(path, ttl) is { } lease) return lease;
            if (DateTime.UtcNow >= deadline) throw new TimeoutException($"Could not acquire lock '{Path.GetFileName(path)}'.");
            await Task.Delay(Random.Shared.Next(50, 250), ct);
        }
    }

    /// <summary>Returns the lease, or null when another owner holds a lock that has not expired.</summary>
    public static FileLease? TryAcquire(string path, TimeSpan ttl)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var owner = $"{ReplicaName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(stream, new LockInfo(owner, DateTimeOffset.UtcNow + ttl));
            return new FileLease(path, owner);
        }
        catch (IOException) when (File.Exists(path))
        {
            if (IsExpired(path)) TryDeleteStale(path);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The previous holder's lock file is still being deleted (Windows reports this as access denied); retry.
            return null;
        }
    }

    public static string ReplicaName =>
        Environment.GetEnvironmentVariable("CONTAINER_APP_REPLICA_NAME") is { Length: > 0 } name ? name : Environment.MachineName;

    public ValueTask DisposeAsync()
    {
        if (_released) return ValueTask.CompletedTask;
        _released = true;
        try
        {
            if (File.Exists(_path) && ReadOwner(_path) == _owner) File.Delete(_path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return ValueTask.CompletedTask;
    }

    private static bool IsExpired(string path)
    {
        try
        {
            var info = JsonSerializer.Deserialize<LockInfo>(File.ReadAllText(path));
            return info is null || info.ExpiresUtc < DateTimeOffset.UtcNow;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // An unreadable lock is either being written right now or corrupt; treat it as expired after the write window.
            return File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddMinutes(-1);
        }
    }

    private static string? ReadOwner(string path)
    {
        try { return JsonSerializer.Deserialize<LockInfo>(File.ReadAllText(path))?.Owner; }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }

    private static void TryDeleteStale(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed record LockInfo(string Owner, DateTimeOffset ExpiresUtc);
}
