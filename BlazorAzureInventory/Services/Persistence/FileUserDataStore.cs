using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using BlazorAzureInventory.Models.Inventory;
using BlazorAzureInventory.Models.Options;
using Microsoft.Extensions.Options;

namespace BlazorAzureInventory.Services.Persistence;

public sealed record SavedDataInfo(SnapshotSummary? Summary, long SnapshotBytes, DateTimeOffset? SavedUtc);

public sealed class SnapshotTooLargeException(long bytes, int maxMegabytes)
    : Exception($"The scan is {bytes / 1024 / 1024} MB compressed, above the {maxMegabytes} MB limit, so it was not saved.");

/// <summary>Per-user saved data. All paths come from <see cref="UserKey"/>; nothing here accepts a path from the caller.</summary>
public interface IUserDataStore
{
    bool IsPersistent { get; }
    Task<SavedDataInfo> GetInfoAsync(UserKey key, CancellationToken ct);
    Task<InventorySnapshot?> LoadSnapshotAsync(UserKey key, CancellationToken ct);
    Task<InventorySnapshot?> LoadPreviousSnapshotAsync(UserKey key, CancellationToken ct);

    /// <param name="rotate">Moves the current snapshot to "previous" first. True for new scans, false for tag updates.</param>
    Task SaveSnapshotAsync(UserKey key, InventorySnapshot snapshot, bool rotate, CancellationToken ct);

    Task<UserPreferences> LoadPreferencesAsync(UserKey key, CancellationToken ct);
    Task SavePreferencesAsync(UserKey key, UserPreferences preferences, CancellationToken ct);
    Task TouchAsync(UserKey key, CancellationToken ct);
    Task DeleteAllAsync(UserKey key, CancellationToken ct);
}

/// <summary>Used when no persistent storage is available; keeps today's in-memory-only behavior.</summary>
public sealed class NullUserDataStore : IUserDataStore
{
    public bool IsPersistent => false;
    public Task<SavedDataInfo> GetInfoAsync(UserKey key, CancellationToken ct) => Task.FromResult(new SavedDataInfo(null, 0, null));
    public Task<InventorySnapshot?> LoadSnapshotAsync(UserKey key, CancellationToken ct) => Task.FromResult<InventorySnapshot?>(null);
    public Task<InventorySnapshot?> LoadPreviousSnapshotAsync(UserKey key, CancellationToken ct) => Task.FromResult<InventorySnapshot?>(null);
    public Task SaveSnapshotAsync(UserKey key, InventorySnapshot snapshot, bool rotate, CancellationToken ct) => Task.CompletedTask;
    public Task<UserPreferences> LoadPreferencesAsync(UserKey key, CancellationToken ct) => Task.FromResult(new UserPreferences());
    public Task SavePreferencesAsync(UserKey key, UserPreferences preferences, CancellationToken ct) => Task.CompletedTask;
    public Task TouchAsync(UserKey key, CancellationToken ct) => Task.CompletedTask;
    public Task DeleteAllAsync(UserKey key, CancellationToken ct) => Task.CompletedTask;
}

public sealed class FileUserDataStore(PersistencePaths paths, IOptions<PersistenceOptions> options, ILogger<FileUserDataStore> logger)
    : IUserDataStore
{
    private static readonly TimeSpan TouchInterval = TimeSpan.FromHours(24);
    private static readonly string AppVersion =
        typeof(FileUserDataStore).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public bool IsPersistent => true;

    public async Task<SavedDataInfo> GetInfoAsync(UserKey key, CancellationToken ct)
    {
        var path = paths.Snapshot(key);
        if (!File.Exists(path)) return new SavedDataInfo(null, 0, null);
        var file = new FileInfo(path);
        return new SavedDataInfo(await ReadSummaryAsync(path, ct), file.Length, file.LastWriteTimeUtc);
    }

    public Task<InventorySnapshot?> LoadSnapshotAsync(UserKey key, CancellationToken ct) => LoadAsync(key, paths.Snapshot(key), ct);

    public Task<InventorySnapshot?> LoadPreviousSnapshotAsync(UserKey key, CancellationToken ct) => LoadAsync(key, paths.PreviousSnapshot(key), ct);

    public async Task SaveSnapshotAsync(UserKey key, InventorySnapshot snapshot, bool rotate, CancellationToken ct)
    {
        var path = paths.Snapshot(key);
        var previous = paths.PreviousSnapshot(key);
        var maxBytes = (long)options.Value.MaxSnapshotMegabytes * 1024 * 1024;
        var envelope = new SnapshotEnvelope(
            SnapshotEnvelope.CurrentSchemaVersion, AppVersion, DateTimeOffset.UtcNow,
            new SnapshotOwner(key.TenantId, key.ObjectId),
            new SnapshotSummary(snapshot.ScannedUtc, snapshot.Resources.Count, snapshot.Subscriptions.Count),
            snapshot);

        await AtomicFile.WriteAsync(path, async (stream, token) =>
        {
            await using var gzip = new GZipStream(stream, CompressionLevel.Optimal, leaveOpen: true);
            await JsonSerializer.SerializeAsync(gzip, envelope, PersistenceJson.Options, token);
        }, ct, temp =>
        {
            var size = new FileInfo(temp).Length;
            if (size > maxBytes) throw new SnapshotTooLargeException(size, options.Value.MaxSnapshotMegabytes);
            if (rotate && options.Value.KeepPreviousSnapshot && File.Exists(path)) File.Move(path, previous, overwrite: true);
        });
        await WriteLastAccessAsync(key, ct);
        logger.LogInformation("Saved scan for {UserKey}: {ResourceCount} resources", key, snapshot.Resources.Count);
    }

    public async Task<UserPreferences> LoadPreferencesAsync(UserKey key, CancellationToken ct)
    {
        var path = paths.Preferences(key);
        if (!File.Exists(path)) return new UserPreferences();
        try
        {
            await using var stream = OpenRead(path);
            var prefs = await JsonSerializer.DeserializeAsync<UserPreferences>(stream, PersistenceJson.Options, ct);
            return prefs is null || prefs.SchemaVersion > UserPreferences.CurrentSchemaVersion ? new UserPreferences() : prefs.Sanitize();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            logger.LogWarning("Preferences for {UserKey} could not be read ({ExceptionType}); using defaults", key, ex.GetType().Name);
            return new UserPreferences();
        }
    }

    public Task SavePreferencesAsync(UserKey key, UserPreferences preferences, CancellationToken ct)
    {
        preferences.SchemaVersion = UserPreferences.CurrentSchemaVersion;
        preferences.UpdatedUtc = DateTimeOffset.UtcNow;
        return AtomicFile.WriteAsync(paths.Preferences(key),
            (stream, token) => JsonSerializer.SerializeAsync(stream, preferences.Sanitize(), PersistenceJson.Options, token), ct);
    }

    public async Task TouchAsync(UserKey key, CancellationToken ct)
    {
        if (!Directory.Exists(paths.UserDirectory(key))) return;
        var last = await ReadLastAccessAsync(paths.LastAccess(key), ct);
        if (last is null || DateTimeOffset.UtcNow - last.Value > TouchInterval) await WriteLastAccessAsync(key, ct);
    }

    public Task DeleteAllAsync(UserKey key, CancellationToken ct)
    {
        var directory = paths.UserDirectory(key);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        logger.LogInformation("Deleted saved data for {UserKey}", key);
        return Task.CompletedTask;
    }

    public static async Task<DateTimeOffset?> ReadLastAccessAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = OpenRead(path);
            return (await JsonSerializer.DeserializeAsync<LastAccess>(stream, PersistenceJson.Options, ct))?.LastAccessUtc;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private Task WriteLastAccessAsync(UserKey key, CancellationToken ct) =>
        AtomicFile.WriteAsync(paths.LastAccess(key),
            (stream, token) => JsonSerializer.SerializeAsync(stream, new LastAccess(DateTimeOffset.UtcNow), PersistenceJson.Options, token), ct);

    private async Task<InventorySnapshot?> LoadAsync(UserKey key, string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var file = OpenRead(path);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var envelope = await JsonSerializer.DeserializeAsync<SnapshotEnvelope>(gzip, PersistenceJson.Options, ct);
            if (envelope?.Snapshot is null) return null;
            if (envelope.SchemaVersion > SnapshotEnvelope.CurrentSchemaVersion)
            {
                logger.LogWarning("Saved scan {File} has newer schema {Version}; ignoring it", Path.GetFileName(path), envelope.SchemaVersion);
                return null;
            }
            if (UserKey.TryCreate(envelope.Owner?.TenantId, envelope.Owner?.ObjectId) != key)
            {
                logger.LogError("Saved scan {File} belongs to a different user; refusing to load it", Path.GetFileName(path));
                return null;
            }
            return SnapshotNormalizer.Normalize(envelope.Snapshot);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or NotSupportedException)
        {
            logger.LogWarning("Saved scan {File} for {UserKey} could not be read ({ExceptionType})", Path.GetFileName(path), key, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>Reads only the envelope header up to <c>summary</c>, without decompressing the whole snapshot.</summary>
    private static async Task<SnapshotSummary?> ReadSummaryAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var file = OpenRead(path);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var buffer = new byte[64 * 1024];
            var read = 0;
            int n;
            while (read < buffer.Length && (n = await gzip.ReadAsync(buffer.AsMemory(read), ct)) > 0) read += n;
            return ParseSummary(buffer.AsSpan(0, read));
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
        {
            return null;
        }
    }

    private static SnapshotSummary? ParseSummary(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, isFinalBlock: false, state: default);
        while (reader.Read())
        {
            if (reader.CurrentDepth == 1 && reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("summary"u8))
            {
                return reader.Read() ? JsonSerializer.Deserialize<SnapshotSummary>(ref reader, PersistenceJson.Options) : null;
            }
        }
        return null;
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
}
