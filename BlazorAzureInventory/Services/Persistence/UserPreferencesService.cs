using BlazorAzureInventory.Services.Auth;

namespace BlazorAzureInventory.Services.Persistence;

/// <summary>Per-circuit cache of the user's preferences. Changes are written after a short delay to batch rapid edits.</summary>
public sealed class UserPreferencesService(IUserDataStore dataStore, UserContextAccessor userContext, ILogger<UserPreferencesService> logger)
    : IAsyncDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);
    private UserPreferences? _prefs;
    private UserKey? _key;
    private CancellationTokenSource? _pendingSave;
    private volatile bool _dirty;

    public async Task<UserPreferences> GetAsync(CancellationToken ct = default)
    {
        if (_prefs is not null) return _prefs;
        _key = UserKey.FromPrincipal(await userContext.GetUserAsync());
        _prefs = _key is { } key ? await dataStore.LoadPreferencesAsync(key, ct) : new UserPreferences();
        return _prefs;
    }

    public async Task UpdateAsync(Action<UserPreferences> change)
    {
        var prefs = await GetAsync();
        change(prefs);
        if (_key is not { } key || !dataStore.IsPersistent) return;

        _dirty = true;
        _pendingSave?.Cancel();
        var cts = _pendingSave = new CancellationTokenSource();
        _ = SaveLaterAsync(key, prefs, cts.Token);
    }

    private async Task SaveLaterAsync(UserKey key, UserPreferences prefs, CancellationToken ct)
    {
        try
        {
            await Task.Delay(Debounce, ct);
            _dirty = false;
            await dataStore.SavePreferencesAsync(key, prefs, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning("Saving preferences for {UserKey} failed ({ExceptionType})", key, ex.GetType().Name);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Flush a pending save when the circuit closes so the last change is not lost.
        if (_dirty && _key is { } key && _prefs is not null)
        {
            _pendingSave?.Cancel();
            _dirty = false;
            try { await dataStore.SavePreferencesAsync(key, _prefs, CancellationToken.None); }
            catch (Exception ex) { logger.LogWarning("Saving preferences for {UserKey} failed ({ExceptionType})", key, ex.GetType().Name); }
        }
        _pendingSave?.Dispose();
    }
}
