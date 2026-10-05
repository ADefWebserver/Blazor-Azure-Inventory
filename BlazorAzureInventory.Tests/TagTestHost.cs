using System.Security.Claims;
using Azure;
using BlazorAzureInventory.Models.Graph;
using BlazorAzureInventory.Models.Inventory;
using BlazorAzureInventory.Models.Options;
using BlazorAzureInventory.Services.Analysis;
using BlazorAzureInventory.Services.Auth;
using BlazorAzureInventory.Services.Graph;
using BlazorAzureInventory.Services.Tags;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BlazorAzureInventory.Tests;

/// <summary>A signed-in user with the Contoso scan in session, real tag services, and a fake ARM tag store.</summary>
internal sealed class TagTestHost
{
    public TagTestHost(InventorySnapshot? snapshot = null, TaggingOptions? options = null)
    {
        Options = options ?? new TaggingOptions();
        User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("tid", Fixture.TenantId), new Claim("oid", "22222222-2222-2222-2222-222222222222")], "test"));
        Store = new InventorySessionStore(Microsoft.Extensions.Options.Options.Create(new GraphSessionOptions()));
        UserContext = new UserContextAccessor(new FakeAuthProvider(User));

        snapshot ??= Fixture.Snapshot();
        var graph = Fixture.Builder().Build(snapshot, new GraphBuildOptions());
        Session = new InventorySession { Snapshot = snapshot, Graph = graph, Index = new GraphIndex(graph) };
        Store.Set(User, Session);

        foreach (var r in snapshot.Resources) TagStore.Tags[r.Id] = new(r.Tags, StringComparer.OrdinalIgnoreCase);

        var opts = Microsoft.Extensions.Options.Options.Create(Options);
        Validator = new TagValidator(new SecretClassifier());
        Generator = new ConnectionTagGenerator(opts);
        Proposals = new TagProposalStore(opts);
        Sync = new TagSyncCoordinator(Store, UserContext);
        Writer = new TagWriteService(TagStore, Validator, Sync, opts, NullLogger<TagWriteService>.Instance)
        {
            Delay = (_, _) => Task.CompletedTask
        };
    }

    public ClaimsPrincipal User { get; }
    public InventorySessionStore Store { get; }
    public UserContextAccessor UserContext { get; }
    public InventorySession Session { get; }
    public GraphIndex Index => Session.Index!;
    public TaggingOptions Options { get; }
    public FakeTagStore TagStore { get; } = new();
    public TagValidator Validator { get; }
    public ConnectionTagGenerator Generator { get; }
    public TagProposalStore Proposals { get; }
    public TagSyncCoordinator Sync { get; }
    public TagWriteService Writer { get; }

    public static InventorySnapshot SnapshotWithTags(params (string Id, Dictionary<string, string> Tags)[] tags)
    {
        var snapshot = Fixture.Snapshot();
        var map = tags.ToDictionary(t => t.Id, t => t.Tags, StringComparer.OrdinalIgnoreCase);
        return snapshot with
        {
            Resources = snapshot.Resources
                .Select(r => map.TryGetValue(r.Id, out var t) ? r with { Tags = new Dictionary<string, string>(t, StringComparer.OrdinalIgnoreCase) } : r)
                .ToList()
        };
    }

    public static Dictionary<string, string> Tags(params (string Name, string Value)[] tags) =>
        tags.ToDictionary(t => t.Name, t => t.Value, StringComparer.OrdinalIgnoreCase);

    private sealed class FakeAuthProvider(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user));
    }
}

internal sealed class FakeTagStore : ITagStore
{
    public Dictionary<string, Dictionary<string, string>> Tags { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Queue<Exception> GetFailures { get; } = new();
    public Queue<Exception> PutFailures { get; } = new();
    public int Gets { get; private set; }
    public int Puts { get; private set; }

    /// <summary>Every tag name sent to Azure in a Merge patch.</summary>
    public List<string> MergedNames { get; } = new();

    /// <summary>Simulates a policy <c>modify</c> effect or a concurrent edit applied by Azure on write.</summary>
    public Action<Dictionary<string, string>>? OnPut { get; set; }

    public Task<IReadOnlyDictionary<string, string>> GetAsync(string tenantId, string resourceId, CancellationToken ct)
    {
        Gets++;
        if (GetFailures.TryDequeue(out var ex)) throw ex;
        if (!Tags.TryGetValue(resourceId, out var tags)) throw new RequestFailedException(404, "Not found", "ResourceNotFound", null);
        return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(tags, StringComparer.OrdinalIgnoreCase));
    }

    public Task<IReadOnlyDictionary<string, string>> PatchAsync(string tenantId, string resourceId,
        IReadOnlyDictionary<string, string> merge, IReadOnlyDictionary<string, string> delete, CancellationToken ct)
    {
        Puts++;
        if (PutFailures.TryDequeue(out var ex)) throw ex;
        var stored = new Dictionary<string, string>(Tags.GetValueOrDefault(resourceId) ?? new(), StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in delete)
        {
            if (stored.TryGetValue(k, out var existing) && existing == v) stored.Remove(k);
        }
        foreach (var (k, v) in merge)
        {
            stored.Remove(k);
            stored[k] = v;
            MergedNames.Add(k);
        }
        OnPut?.Invoke(stored);
        Tags[resourceId] = stored;
        return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(stored, StringComparer.OrdinalIgnoreCase));
    }
}
