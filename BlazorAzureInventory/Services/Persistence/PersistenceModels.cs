using System.Text.Json;
using System.Text.Json.Serialization;
using BlazorAzureInventory.Models.Graph;
using BlazorAzureInventory.Models.Inventory;

namespace BlazorAzureInventory.Services.Persistence;

public static class PersistenceJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false
    };

    public static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };
}

public sealed record SnapshotOwner(string TenantId, string ObjectId);

public sealed record SnapshotSummary(DateTimeOffset ScannedUtc, int ResourceCount, int SubscriptionCount);

/// <summary>On-disk format of <c>snapshot.json.gz</c>. <see cref="Summary"/> is written first so it can be read without the snapshot.</summary>
public sealed record SnapshotEnvelope(
    int SchemaVersion,
    string AppVersion,
    DateTimeOffset SavedUtc,
    SnapshotOwner Owner,
    SnapshotSummary Summary,
    InventorySnapshot Snapshot)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record LastAccess(DateTimeOffset LastAccessUtc);

public sealed class UserPreferences
{
    public const int CurrentSchemaVersion = 1;

    public static readonly IReadOnlySet<string> AllowedPages =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/inventory", "/graph", "/chat", "/appconfiguration" };

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public DateTimeOffset UpdatedUtc { get; set; }
    public List<string> SelectedSubscriptionIds { get; set; } = [];
    public GraphFilterState? GraphFilter { get; set; }
    public string? InventoryTab { get; set; }
    public string? LastPage { get; set; }

    /// <summary>Drops values that are not in a known set, so a tampered file cannot inject redirects or bad state.</summary>
    public UserPreferences Sanitize()
    {
        if (LastPage is not null && !AllowedPages.Contains(LastPage)) LastPage = null;
        if (InventoryTab is { Length: > 100 }) InventoryTab = null;
        SelectedSubscriptionIds = SelectedSubscriptionIds.Where(id => Guid.TryParse(id, out _)).Distinct(StringComparer.OrdinalIgnoreCase).Take(1000).ToList();
        return this;
    }
}
