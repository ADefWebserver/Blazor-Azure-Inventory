using System.Collections.Concurrent;
using BlazorAzureInventory.Models.Graph;
using BlazorAzureInventory.Models.Options;
using BlazorAzureInventory.Services.Auth;
using BlazorAzureInventory.Services.Azure;
using BlazorAzureInventory.Services.Graph;
using Microsoft.Extensions.Options;

namespace BlazorAzureInventory.Services.Persistence;

public enum SessionLoadOutcome { NoData, FromMemory, FromSaved, AccessCheckFailed }

/// <summary>Returns the user's in-memory session, loading the saved scan from disk on a cache miss instead of rescanning Azure.</summary>
public sealed class InventorySessionLoader(
    InventorySessionStore store,
    IUserDataStore dataStore,
    AzureContextService azureContext,
    InventoryGraphBuilder graphBuilder,
    GraphLayoutService layouts,
    UserContextAccessor userContext,
    IOptions<InventoryOptions> inventoryOptions,
    ILogger<InventorySessionLoader> logger)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);

    public SessionLoadOutcome LastOutcome { get; private set; } = SessionLoadOutcome.NoData;

    public async Task<InventorySession?> EnsureLoadedAsync(CancellationToken ct = default)
    {
        var user = await userContext.GetUserAsync();
        if (store.Get(user) is { Snapshot: not null } cached)
        {
            LastOutcome = SessionLoadOutcome.FromMemory;
            return cached;
        }
        if (!dataStore.IsPersistent || UserKey.FromPrincipal(user) is not { } key)
        {
            LastOutcome = SessionLoadOutcome.NoData;
            return store.Get(user);
        }

        var gate = Locks.GetOrAdd(key.SessionKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (store.Get(user) is { Snapshot: not null } loaded)
            {
                LastOutcome = SessionLoadOutcome.FromMemory;
                return loaded;
            }

            var snapshot = await dataStore.LoadSnapshotAsync(key, ct);
            if (snapshot is null)
            {
                LastOutcome = SessionLoadOutcome.NoData;
                return store.Get(user);
            }

            IReadOnlyList<string> accessible;
            try
            {
                accessible = (await azureContext.GetContextAsync(ct: ct)).Subscriptions.Select(s => s.SubscriptionId).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Fail closed: saved data is only shown after the user's current access is confirmed.
                logger.LogWarning("Could not confirm subscription access for {UserKey} ({ExceptionType}); saved scan not shown", key, ex.GetType().Name);
                LastOutcome = SessionLoadOutcome.AccessCheckFailed;
                return store.Get(user);
            }

            var filtered = SnapshotAccessFilter.Filter(snapshot, accessible);
            var graph = graphBuilder.Build(filtered.Snapshot, GraphBuildOptions.From(inventoryOptions.Value));
            var session = store.Get(user) ?? new InventorySession();
            session.Snapshot = filtered.Snapshot;
            session.Graph = graph;
            session.Index = new GraphIndex(graph);
            session.Source = SessionDataSource.Saved;
            session.HiddenSubscriptions = filtered.RemovedSubscriptions;
            store.Set(user, session);
            layouts.WarmUp(session);
            await dataStore.TouchAsync(key, ct);
            LastOutcome = SessionLoadOutcome.FromSaved;
            logger.LogInformation("Loaded saved scan for {UserKey}: {ResourceCount} resources, {Hidden} subscriptions hidden",
                key, filtered.Snapshot.Resources.Count, filtered.RemovedSubscriptions);
            return session;
        }
        finally
        {
            gate.Release();
        }
    }
}
