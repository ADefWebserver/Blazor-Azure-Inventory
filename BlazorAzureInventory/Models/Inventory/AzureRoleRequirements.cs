namespace BlazorAzureInventory.Models.Inventory;

/// <summary>An Azure RBAC role the signed-in user needs (on the scanned subscriptions) for one scan stage.</summary>
public sealed record RoleRequirement(string Stage, string Purpose, string Role, string Permission)
{
    public string Hint => $"Requires the {Role} role ({Permission}).";
}

/// <summary>A role detected as missing, the stages it blocks, and the subscriptions where it is needed.</summary>
public sealed record MissingRole(string Role, IReadOnlyList<RoleRequirement> Requirements, IReadOnlyList<string> SubscriptionIds, int IssueCount);

/// <summary>
/// Single source for the Azure roles the app needs. Every Azure call uses the signed-in user's identity, so these roles
/// are assigned to users (not to the app) on each subscription or resource group to scan.
/// Website Contributor does not include */read, so full results need Reader and Website Contributor together.
/// Contributor or Owner cover everything.
/// </summary>
public static class AzureRoleRequirements
{
    public const string Reader = "Reader";
    public const string WebsiteContributor = "Website Contributor";
    public const string Alternatives = "Contributor or Owner";

    public static readonly RoleRequirement ResourceGraph = new(ScanProgress.QueryingResourceGraph,
        "List subscriptions, resource groups, and resources", Reader, "*/read");

    public static readonly RoleRequirement BlobContainers = new(ScanProgress.ListingBlobContainers,
        "List blob containers", Reader, "Microsoft.Storage/storageAccounts/blobServices/containers/read");

    public static readonly RoleRequirement StorageQueues = new(ScanProgress.ListingStorageQueues,
        "List storage queues", Reader, "Microsoft.Storage/storageAccounts/queueServices/queues/read");

    public static readonly RoleRequirement AppSettings = new(ScanProgress.ReadingAppSettings,
        "Read App Service app settings and connection strings", WebsiteContributor, "Microsoft.Web/sites/config/list/action");

    public static readonly RoleRequirement ConfigFiles = new(ScanProgress.ReadingConfigFiles,
        "Read appsettings*.json files through the SCM (Kudu) site", WebsiteContributor, "Microsoft.Web/sites/publish/action");

    public static readonly RoleRequirement ContainerApps = new(ScanProgress.ReadingContainerApps,
        "Read Container Apps environment variables", Reader, "Microsoft.App/containerApps/read");

    public const string TagContributor = "Tag Contributor";

    /// <summary>Optional: only needed to edit tags. Not a scan stage, so it never appears in <see cref="Detect"/>.</summary>
    public static readonly RoleRequirement EditTags = new("Editing tags",
        "Edit tags (optional)", TagContributor, "Microsoft.Resources/tags/write");

    public static IReadOnlyList<RoleRequirement> All { get; } = [ResourceGraph, BlobContainers, StorageQueues, AppSettings, ConfigFiles, ContainerApps, EditTags];

    public static RoleRequirement? ForStage(string stage) =>
        All.FirstOrDefault(r => string.Equals(r.Stage, stage, StringComparison.Ordinal));

    /// <summary>
    /// Roles missing according to access-denied scan issues, grouped by role with the affected subscriptions.
    /// Only issues whose message names the stage's permission count, so network failures (for example private-endpoint-only
    /// SCM sites, also reported as access denied) are not mistaken for missing roles.
    /// </summary>
    public static IReadOnlyList<MissingRole> Detect(IEnumerable<ScanIssue> issues) =>
        issues
            .Where(i => i.Kind == ScanIssueKind.AccessDenied)
            .Select(i => (Requirement: ForStage(i.Stage), Issue: i))
            .Where(x => x.Requirement is not null && x.Issue.Message.Contains(x.Requirement.Permission, StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x.Requirement!.Role)
            .Select(g => new MissingRole(
                g.Key,
                g.Select(x => x.Requirement!).Distinct().ToList(),
                g.Select(x => SubscriptionIdOf(x.Issue.ResourceId)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                g.Count()))
            .OrderBy(m => m.Role == Reader ? 0 : 1)
            .ToList();

    /// <summary>Subscription ID parsed from an ARM resource ID, or null for non-resource scopes such as "tenant:...".</summary>
    public static string? SubscriptionIdOf(string? resourceId)
    {
        if (resourceId is null) return null;
        var parts = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[0].Equals("subscriptions", StringComparison.OrdinalIgnoreCase) ? parts[1] : null;
    }
}
