using System.Text.Json;
using BlazorAzureInventory.Models.Users;
using BlazorAzureInventory.Services.Persistence;

namespace BlazorAzureInventory.Services.Users;

public interface IUserDirectoryStore
{
    /// <summary>False when changes would not survive a restart; admin changes are then refused.</summary>
    bool CanWrite { get; }

    /// <summary>A value that changes whenever the document is replaced, used to detect writes from other replicas.</summary>
    DateTime? GetChangeStamp();

    Task<UserDirectoryDocument?> ReadAsync(CancellationToken ct);

    /// <summary>Applies <paramref name="apply"/> to the latest document under an exclusive lock and saves the result with a higher version.</summary>
    Task<UserDirectoryDocument> UpdateAsync(Func<UserDirectoryDocument?, UserDirectoryDocument> apply, CancellationToken ct);
}

public sealed class FileUserDirectoryStore(PersistencePaths paths) : IUserDirectoryStore
{
    private static readonly TimeSpan LockTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(20);

    public bool CanWrite => true;

    public DateTime? GetChangeStamp() => File.Exists(paths.UsersFile) ? File.GetLastWriteTimeUtc(paths.UsersFile) : null;

    public async Task<UserDirectoryDocument?> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(paths.UsersFile)) return null;
        await using var stream = new FileStream(paths.UsersFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
        var doc = await JsonSerializer.DeserializeAsync<UserDirectoryDocument>(stream, PersistenceJson.Options, ct)
                  ?? throw new JsonException("users.json is empty.");
        if (doc.SchemaVersion > UserDirectoryDocument.CurrentSchemaVersion)
            throw new JsonException($"users.json has unsupported schema version {doc.SchemaVersion}.");
        return doc;
    }

    public async Task<UserDirectoryDocument> UpdateAsync(Func<UserDirectoryDocument?, UserDirectoryDocument> apply, CancellationToken ct)
    {
        await using var lease = await FileLease.AcquireAsync(paths.UsersLock, LockTtl, LockTimeout, ct);
        var current = await ReadAsync(ct);
        var updated = apply(current?.Clone());
        updated.SchemaVersion = UserDirectoryDocument.CurrentSchemaVersion;
        updated.Version = (current?.Version ?? 0) + 1;
        await AtomicFile.WriteAsync(paths.UsersFile,
            (stream, token) => JsonSerializer.SerializeAsync(stream, updated, PersistenceJson.Indented, token), ct);
        return updated.Clone();
    }
}

/// <summary>Used without persistent storage (read-only) and in tests (writable).</summary>
public sealed class InMemoryUserDirectoryStore(bool canWrite) : IUserDirectoryStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UserDirectoryDocument? _doc;
    private DateTime? _stamp;

    public bool CanWrite { get; } = canWrite;

    public DateTime? GetChangeStamp() => _stamp;

    public Task<UserDirectoryDocument?> ReadAsync(CancellationToken ct) => Task.FromResult(_doc?.Clone());

    public async Task<UserDirectoryDocument> UpdateAsync(Func<UserDirectoryDocument?, UserDirectoryDocument> apply, CancellationToken ct)
    {
        if (!CanWrite) throw new InvalidOperationException("Persistent storage is unavailable. Changes cannot be saved.");
        await _gate.WaitAsync(ct);
        try
        {
            var updated = apply(_doc?.Clone());
            updated.Version = (_doc?.Version ?? 0) + 1;
            _doc = updated.Clone();
            _stamp = DateTime.UtcNow.AddTicks(updated.Version);
            return updated.Clone();
        }
        finally
        {
            _gate.Release();
        }
    }
}
