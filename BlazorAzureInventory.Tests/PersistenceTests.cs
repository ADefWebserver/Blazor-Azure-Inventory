using System.IO.Compression;
using System.Text;
using BlazorAzureInventory.Models.Inventory;
using BlazorAzureInventory.Models.Options;
using BlazorAzureInventory.Services.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BlazorAzureInventory.Tests;

public sealed class TempRoot : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "azinv-tests-" + Guid.NewGuid().ToString("N"));
    public PersistencePaths Paths => new(Path);
    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}

public class UserKeyAndPathTests
{
    private const string Tid = "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA";
    private const string Oid = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

    [Fact]
    public void Normalizes_guids_to_lower_case_paths()
    {
        using var root = new TempRoot();
        var key = new UserKey(Tid, Oid);

        Assert.Equal(Tid.ToLowerInvariant(), key.TenantId);
        Assert.StartsWith(root.Paths.Root + System.IO.Path.DirectorySeparatorChar, root.Paths.Snapshot(key));
        Assert.EndsWith(System.IO.Path.Combine(Tid.ToLowerInvariant(), Oid, "snapshot.json.gz"), root.Paths.Snapshot(key));
    }

    [Theory]
    [InlineData("../etc", Oid)]
    [InlineData(Tid, "..")]
    [InlineData("", Oid)]
    [InlineData(Tid, "/tmp/x")]
    public void Rejects_non_guid_values(string tid, string oid)
    {
        Assert.Throws<ArgumentException>(() => new UserKey(tid, oid));
        Assert.Null(UserKey.TryCreate(tid, oid));
    }

    [Fact]
    public void Round_trips_session_key()
    {
        var key = new UserKey(Tid, Oid);

        Assert.Equal(key, UserKey.FromSessionKey(key.SessionKey));
        Assert.Null(UserKey.FromSessionKey("inv:x:y"));
        Assert.Null(UserKey.FromSessionKey("other:" + Tid + ":" + Oid));
    }
}

public class FileInfrastructureTests
{
    [Fact]
    public async Task Atomic_write_replaces_file_and_leaves_no_temp()
    {
        using var root = new TempRoot();
        var path = System.IO.Path.Combine(root.Path, "a", "file.txt");
        await AtomicFile.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("one"), CancellationToken.None);
        await AtomicFile.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("two"), CancellationToken.None);

        Assert.Equal("two", await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFiles(System.IO.Path.GetDirectoryName(path)!));
    }

    [Fact]
    public async Task Failed_write_keeps_original()
    {
        using var root = new TempRoot();
        var path = System.IO.Path.Combine(root.Path, "file.txt");
        await AtomicFile.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("original"), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => AtomicFile.WriteAsync(path,
            async (s, ct) => { await s.WriteAsync(Encoding.UTF8.GetBytes("partial"), ct); throw new InvalidOperationException(); },
            CancellationToken.None));

        Assert.Equal("original", await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFiles(root.Path));
    }

    [Fact]
    public async Task Lease_is_exclusive_until_released_and_stale_locks_are_taken_over()
    {
        using var root = new TempRoot();
        var lockPath = System.IO.Path.Combine(root.Path, "x.lock");

        var first = FileLease.TryAcquire(lockPath, TimeSpan.FromMinutes(1));
        Assert.NotNull(first);
        Assert.Null(FileLease.TryAcquire(lockPath, TimeSpan.FromMinutes(1)));
        await first!.DisposeAsync();
        await using (var second = FileLease.TryAcquire(lockPath, TimeSpan.FromMilliseconds(1)))
        {
            Assert.NotNull(second);
        }

        var stale = FileLease.TryAcquire(lockPath, TimeSpan.FromMilliseconds(1));
        Assert.NotNull(stale);
        await Task.Delay(20);
        Assert.Null(FileLease.TryAcquire(lockPath, TimeSpan.FromMinutes(1))); // removes the stale lock
        await using var takeover = FileLease.TryAcquire(lockPath, TimeSpan.FromMinutes(1));
        Assert.NotNull(takeover);
    }

    [Theory]
    [InlineData("36 25 0:40 / /data rw,relatime - cifs //acct.file.core.windows.net/share rw", "/data", true)]
    [InlineData("36 25 0:40 / /data rw,relatime - cifs //acct.file.core.windows.net/share rw", "/data/sub", true)]
    [InlineData("36 25 0:40 / /mnt/nfs rw shared:1 - nfs4 host:/x rw", "/mnt/nfs", true)]
    [InlineData("22 1 0:21 / / rw - overlay overlay rw", "/data", false)]
    [InlineData("36 25 0:40 / /database rw - cifs //a/b rw", "/data", false)]
    public void Detects_network_mounts(string mountInfo, string path, bool expected)
    {
        var info = "22 1 0:21 / / rw - overlay overlay rw\n" + mountInfo + "\n";
        Assert.Equal(expected, PersistentVolumeCheck.IsOnNetworkMount(info, path));
    }

    [Fact]
    public void Required_volume_without_mount_disables_persistence()
    {
        using var root = new TempRoot();
        var status = PersistentVolumeCheck.Evaluate(new PersistenceOptions { RequirePersistentVolume = true }, root.Paths, NullLogger.Instance);

        Assert.False(status.IsPersistent);
    }

    [Fact]
    public void Writable_local_folder_is_persistent_when_volume_not_required()
    {
        using var root = new TempRoot();
        Assert.True(PersistentVolumeCheck.Evaluate(new PersistenceOptions(), root.Paths, NullLogger.Instance).IsPersistent);
    }
}

public class FileUserDataStoreTests
{
    private static readonly UserKey Key = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static FileUserDataStore Store(TempRoot root, PersistenceOptions? options = null) =>
        new(root.Paths, Options.Create(options ?? new PersistenceOptions()), NullLogger<FileUserDataStore>.Instance);

    private static InventorySnapshot TaggedSnapshot(string secret = "pw")
    {
        var snapshot = Fixture.Snapshot(secret);
        return snapshot with
        {
            Resources = snapshot.Resources.Select((r, i) => i == 0
                ? r with { Tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Env"] = "prod" } }
                : r).ToList()
        };
    }

    [Fact]
    public async Task Round_trips_snapshot_with_case_insensitive_tags()
    {
        using var root = new TempRoot();
        var store = Store(root);
        var snapshot = TaggedSnapshot();

        await store.SaveSnapshotAsync(Key, snapshot, rotate: true, CancellationToken.None);
        var loaded = await store.LoadSnapshotAsync(Key, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(snapshot.ScannedUtc, loaded!.ScannedUtc);
        Assert.Equal(snapshot.Resources.Select(r => r.Id), loaded.Resources.Select(r => r.Id));
        Assert.Equal(snapshot.AppConfigs.Sum(a => a.Entries.Count), loaded.AppConfigs.Sum(a => a.Entries.Count));
        Assert.Equal("prod", loaded.Resources[0].Tags["ENV"]);
        Assert.Equal(snapshot.Resources[0].Kind, loaded.Resources[0].Kind);
    }

    [Fact]
    public async Task Saved_file_contains_no_secret_values()
    {
        using var root = new TempRoot();
        const string secret = "S3cr3t-Value-That-Must-Not-Leak";
        await Store(root).SaveSnapshotAsync(Key, Fixture.Snapshot(secret), rotate: true, CancellationToken.None);

        await using var file = File.OpenRead(root.Paths.Snapshot(Key));
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var json = await reader.ReadToEndAsync();

        Assert.DoesNotContain(secret, json);
        Assert.Contains("\"summary\"", json);
    }

    [Fact]
    public async Task Reads_summary_and_rotates_previous_only_on_new_scans()
    {
        using var root = new TempRoot();
        var store = Store(root);
        var first = Fixture.Snapshot();
        var second = first with { ScannedUtc = first.ScannedUtc.AddHours(1) };

        await store.SaveSnapshotAsync(Key, first, rotate: true, CancellationToken.None);
        Assert.False(File.Exists(root.Paths.PreviousSnapshot(Key)));
        await store.SaveSnapshotAsync(Key, second, rotate: true, CancellationToken.None);
        await store.SaveSnapshotAsync(Key, second, rotate: false, CancellationToken.None);

        var info = await store.GetInfoAsync(Key, CancellationToken.None);
        Assert.Equal(second.ScannedUtc, info.Summary!.ScannedUtc);
        Assert.Equal(second.Resources.Count, info.Summary.ResourceCount);
        Assert.Equal(first.ScannedUtc, (await store.LoadPreviousSnapshotAsync(Key, CancellationToken.None))!.ScannedUtc);
    }

    [Fact]
    public async Task Refuses_snapshot_owned_by_another_user()
    {
        using var root = new TempRoot();
        var store = Store(root);
        var other = new UserKey("cccccccc-cccc-cccc-cccc-cccccccccccc", Key.ObjectId);
        await store.SaveSnapshotAsync(other, Fixture.Snapshot(), rotate: true, CancellationToken.None);
        Directory.CreateDirectory(root.Paths.UserDirectory(Key));
        File.Copy(root.Paths.Snapshot(other), root.Paths.Snapshot(Key));

        Assert.Null(await store.LoadSnapshotAsync(Key, CancellationToken.None));
    }

    [Fact]
    public async Task Corrupt_file_is_treated_as_no_data()
    {
        using var root = new TempRoot();
        Directory.CreateDirectory(root.Paths.UserDirectory(Key));
        await File.WriteAllTextAsync(root.Paths.Snapshot(Key), "not gzip");

        Assert.Null(await Store(root).LoadSnapshotAsync(Key, CancellationToken.None));
    }

    [Fact]
    public async Task Oversized_snapshot_is_not_saved_and_keeps_previous_file()
    {
        using var root = new TempRoot();
        var store = Store(root, new PersistenceOptions { MaxSnapshotMegabytes = 1 });
        var snapshot = Fixture.Snapshot();
        await store.SaveSnapshotAsync(Key, snapshot, rotate: true, CancellationToken.None);
        var big = snapshot with
        {
            Resources = snapshot.Resources.Select(r => r with
            {
                Properties = new Dictionary<string, string> { ["blob"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(400_000)) }
            }).ToList()
        };

        await Assert.ThrowsAsync<SnapshotTooLargeException>(() => store.SaveSnapshotAsync(Key, big, rotate: true, CancellationToken.None));
        Assert.Equal(snapshot.ScannedUtc, (await store.LoadSnapshotAsync(Key, CancellationToken.None))!.ScannedUtc);
        Assert.False(File.Exists(root.Paths.PreviousSnapshot(Key)));
    }

    [Fact]
    public async Task Preferences_are_sanitized()
    {
        using var root = new TempRoot();
        var store = Store(root);
        await store.SavePreferencesAsync(Key, new UserPreferences
        {
            LastPage = "https://evil.example/",
            SelectedSubscriptionIds = ["11111111-1111-1111-1111-111111111111", "not-a-guid"],
            InventoryTab = "SQL"
        }, CancellationToken.None);

        var prefs = await store.LoadPreferencesAsync(Key, CancellationToken.None);
        Assert.Null(prefs.LastPage);
        Assert.Equal(["11111111-1111-1111-1111-111111111111"], prefs.SelectedSubscriptionIds);
        Assert.Equal("SQL", prefs.InventoryTab);
    }

    [Fact]
    public async Task Delete_removes_user_folder()
    {
        using var root = new TempRoot();
        var store = Store(root);
        await store.SaveSnapshotAsync(Key, Fixture.Snapshot(), rotate: true, CancellationToken.None);

        await store.DeleteAllAsync(Key, CancellationToken.None);

        Assert.False(Directory.Exists(root.Paths.UserDirectory(Key)));
        Assert.Null((await store.GetInfoAsync(Key, CancellationToken.None)).Summary);
    }
}

public class SnapshotAccessFilterTests
{
    [Fact]
    public void Keeps_everything_when_all_subscriptions_are_accessible()
    {
        var snapshot = Fixture.Snapshot();
        var result = SnapshotAccessFilter.Filter(snapshot, [Fixture.SubscriptionId]);

        Assert.Equal(0, result.RemovedSubscriptions);
        Assert.Same(snapshot, result.Snapshot);
    }

    [Fact]
    public void Hides_inaccessible_subscriptions_and_their_data()
    {
        var snapshot = Fixture.Snapshot();
        var result = SnapshotAccessFilter.Filter(snapshot, ["99999999-9999-9999-9999-999999999999"]);

        Assert.Equal(1, result.RemovedSubscriptions);
        Assert.Empty(result.Snapshot.Subscriptions);
        Assert.Empty(result.Snapshot.Resources);
        Assert.Empty(result.Snapshot.AppConfigs);
        Assert.Empty(result.Snapshot.Tenants);
    }
}

public class RetentionCleanupTests
{
    [Fact]
    public async Task Deletes_inactive_users_and_keeps_recent_ones()
    {
        using var root = new TempRoot();
        var paths = root.Paths;
        var options = new PersistenceOptions { RetentionDays = 90 };
        var store = new FileUserDataStore(paths, Options.Create(options), NullLogger<FileUserDataStore>.Instance);
        var oldKey = new UserKey(Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        var newKey = new UserKey(Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        await store.SaveSnapshotAsync(oldKey, Fixture.Snapshot(), rotate: true, CancellationToken.None);
        await store.SaveSnapshotAsync(newKey, Fixture.Snapshot(), rotate: true, CancellationToken.None);

        var old = DateTime.UtcNow.AddDays(-120);
        await File.WriteAllTextAsync(paths.LastAccess(oldKey), $"{{\"lastAccessUtc\":\"{old:O}\"}}");
        foreach (var f in Directory.GetFiles(paths.UserDirectory(oldKey))) File.SetLastWriteTimeUtc(f, old);
        Directory.CreateDirectory(System.IO.Path.Combine(paths.UserDataRoot, "not-a-guid", "x"));

        var service = new RetentionCleanupService(paths, new PersistenceStatus(true, "OK"), Options.Create(options), TimeProvider.System,
            NullLogger<RetentionCleanupService>.Instance);
        var deleted = await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, deleted);
        Assert.False(Directory.Exists(paths.UserDirectory(oldKey)));
        Assert.True(Directory.Exists(paths.UserDirectory(newKey)));
        Assert.True(Directory.Exists(System.IO.Path.Combine(paths.UserDataRoot, "not-a-guid")));
    }
}
