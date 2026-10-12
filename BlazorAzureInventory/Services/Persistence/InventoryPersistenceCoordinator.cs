using System.Collections.Concurrent;
using BlazorAzureInventory.Services.Graph;

namespace BlazorAzureInventory.Services.Persistence;

/// <summary>Saves the user's snapshot a few seconds after tag writes, so saved data keeps the new tags.</summary>
public sealed class InventoryPersistenceCoordinator(
    InventorySessionStore store,
    IUserDataStore dataStore,
    ILogger<InventoryPersistenceCoordinator> logger) : IHostedService
{
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new(StringComparer.Ordinal);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (dataStore.IsPersistent) store.TagsChanged += OnTagsChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        store.TagsChanged -= OnTagsChanged;
        foreach (var cts in _pending.Values) cts.Cancel();
        return Task.CompletedTask;
    }

    private void OnTagsChanged(string sessionKey, string resourceId)
    {
        if (UserKey.FromSessionKey(sessionKey) is not { } key) return;
        var cts = new CancellationTokenSource();
        if (_pending.TryRemove(sessionKey, out var previous)) previous.Cancel();
        _pending[sessionKey] = cts;
        _ = SaveLaterAsync(sessionKey, key, cts);
    }

    private async Task SaveLaterAsync(string sessionKey, UserKey key, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(Debounce, cts.Token);
            _pending.TryRemove(new KeyValuePair<string, CancellationTokenSource>(sessionKey, cts));
            if (store.GetByKey(sessionKey)?.Snapshot is { } snapshot)
            {
                await dataStore.SaveSnapshotAsync(key, snapshot, rotate: false, CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning("Saving tag changes for {UserKey} failed ({ExceptionType})", key, ex.GetType().Name);
        }
        finally
        {
            cts.Dispose();
        }
    }
}
