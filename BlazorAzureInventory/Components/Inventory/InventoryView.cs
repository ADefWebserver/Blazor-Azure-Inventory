using BlazorAzureInventory.Models.Configuration;
using BlazorAzureInventory.Models.Graph;
using BlazorAzureInventory.Models.Inventory;
using BlazorAzureInventory.Services.Analysis;

namespace BlazorAzureInventory.Components.Inventory;

public sealed class AppRow
{
    public required AzureResourceInfo Resource { get; init; }
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Subscription { get; init; } = "";
    public string ResourceGroup => Resource.ResourceGroup;
    public string Location => Resource.Location;
    public string State => Resource.Prop("state") ?? "";
    public string DefaultHost => Resource.Endpoint ?? "";
    public ConfigReadStatus SettingsStatus { get; init; } = ConfigReadStatus.NotScanned;
    public ConfigReadStatus ConfigFilesStatus { get; init; } = ConfigReadStatus.NotScanned;
    public int Connections { get; init; }
    public List<AppRow> Slots { get; } = new();
}

public sealed class SqlServerRow
{
    public required AzureResourceInfo Resource { get; init; }
    public string Server => Resource.Name;
    public string Fqdn => Resource.Endpoint ?? "";
    public string Subscription { get; init; } = "";
    public string ResourceGroup => Resource.ResourceGroup;
    public string Location => Resource.Location;
    public int Databases => DatabaseRows.Count;
    public List<ChildRow> DatabaseRows { get; } = new();
}

public sealed class StorageRow
{
    public required AzureResourceInfo Resource { get; init; }
    public string Account => Resource.Name;
    public string Kind => Resource.Prop("kind") ?? "";
    public string Sku => Resource.Prop("sku") ?? "";
    public string HnsEnabled => Resource.Prop("hnsEnabled") ?? "No";
    public string Location => Resource.Location;
    public int Containers => ContainerRows.Count;
    public bool Truncated => Resource.Prop("containersTruncated") == "true";
    public int Consumers { get; init; }
    public List<ChildRow> ContainerRows { get; } = new();
}

public sealed class ChildRow
{
    public required AzureResourceInfo Resource { get; init; }
    public string Name => Resource.Name;
    public string Sku => Resource.Prop("sku") ?? "";
    public string Status => Resource.Prop("status") ?? "";
    public string PublicAccess => Resource.Prop("publicAccess") ?? "None";
    public string LastModified => Resource.Prop("lastModified") ?? "";
    public int Consumers { get; init; }
}

public sealed class ContainerAppRow
{
    public required AzureResourceInfo Resource { get; init; }
    public string Name => Resource.Name;
    public string Environment => (Resource.Prop("environmentId") ?? "").Split('/')[^1];
    public string Images => Resource.Prop("images") ?? "";
    public string Registry => Resource.Prop("registries") ?? "";
    public int EnvVarCount => int.TryParse(Resource.Prop("envVarCount"), out var n) ? n : 0;
    public int Connections { get; init; }
    public AppConfigAnalysis? Analysis { get; init; }
}

public sealed class SimpleRow
{
    public required AzureResourceInfo Resource { get; init; }
    public string Type { get; init; } = "";
    public string Name => Resource.Name;
    public string EndpointHost => ResourceMatcher.HostOf(Resource.Endpoint) ?? "";
    public string Sku => Resource.Prop("sku") ?? "";
    public int Consumers { get; init; }
}

/// <summary>Grid rows and summary counts derived once from a snapshot and its graph.</summary>
public sealed class InventoryView
{
    private static readonly HashSet<string> DependencyEdges =
        [EdgeTypes.ConnectsTo, EdgeTypes.ReferencesSecret, EdgeTypes.PullsImageFrom];

    public List<AppRow> Apps { get; } = new();
    public List<SqlServerRow> SqlServers { get; } = new();
    public List<StorageRow> Storage { get; } = new();
    public List<ContainerAppRow> ContainerApps { get; } = new();
    public List<SimpleRow> Registries { get; } = new();
    public List<SimpleRow> Other { get; } = new();

    public int WebAppCount { get; private set; }
    public int FunctionAppCount { get; private set; }
    public int SlotCount { get; private set; }
    public int SqlDatabaseCount { get; private set; }
    public int ContainerCount { get; private set; }

    public static InventoryView Build(InventorySnapshot snapshot, GraphIndex? index)
    {
        var view = new InventoryView();
        var subs = snapshot.Subscriptions.ToDictionary(s => s.SubscriptionId, s => s.DisplayName, StringComparer.OrdinalIgnoreCase);
        var analyses = snapshot.AppConfigs.ToDictionary(a => a.AppId, StringComparer.OrdinalIgnoreCase);
        var consumers = ConsumerCounts(index);
        int Consumers(string id) => consumers.GetValueOrDefault(id);
        string Sub(AzureResourceInfo r) => subs.GetValueOrDefault(r.SubscriptionId, r.SubscriptionId);

        AppRow ToAppRow(AzureResourceInfo r)
        {
            var a = analyses.GetValueOrDefault(r.Id);
            return new AppRow
            {
                Resource = r,
                Name = r.Kind == AzureResourceKind.WebAppSlot ? $"{r.Prop("siteName")}/{r.Name}" : r.Name,
                Kind = r.Kind switch { AzureResourceKind.FunctionApp => "Function", AzureResourceKind.WebAppSlot => "Slot", _ => "App" },
                Subscription = Sub(r),
                SettingsStatus = a?.SettingsStatus ?? ConfigReadStatus.NotScanned,
                ConfigFilesStatus = a?.ConfigFilesStatus ?? ConfigReadStatus.NotScanned,
                Connections = a?.ConnectionCount ?? 0
            };
        }

        var byId = new Dictionary<string, AppRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in snapshot.Resources.Where(r => r.Kind is AzureResourceKind.WebApp or AzureResourceKind.FunctionApp))
        {
            var row = ToAppRow(r);
            byId[r.Id] = row;
            view.Apps.Add(row);
        }
        foreach (var slot in snapshot.Resources.Where(r => r.Kind == AzureResourceKind.WebAppSlot))
        {
            var row = ToAppRow(slot);
            if (slot.ParentId is not null && byId.TryGetValue(slot.ParentId, out var parent)) parent.Slots.Add(row);
            else view.Apps.Add(row);
        }

        var servers = new Dictionary<string, SqlServerRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in snapshot.Resources.Where(r => r.Kind == AzureResourceKind.SqlServer))
        {
            var row = new SqlServerRow { Resource = r, Subscription = Sub(r) };
            servers[r.Id] = row;
            view.SqlServers.Add(row);
        }
        foreach (var db in snapshot.Resources.Where(r => r.Kind == AzureResourceKind.SqlDatabase))
        {
            if (db.ParentId is not null && servers.TryGetValue(db.ParentId, out var server))
            {
                server.DatabaseRows.Add(new ChildRow { Resource = db, Consumers = Consumers(db.Id) });
            }
        }

        var accounts = new Dictionary<string, StorageRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in snapshot.Resources.Where(r => r.Kind == AzureResourceKind.StorageAccount))
        {
            var row = new StorageRow { Resource = r, Consumers = Consumers(r.Id) };
            accounts[r.Id] = row;
            view.Storage.Add(row);
        }
        foreach (var c in snapshot.Resources.Where(r => r.Kind == AzureResourceKind.BlobContainer))
        {
            if (c.ParentId is not null && accounts.TryGetValue(c.ParentId, out var account))
            {
                account.ContainerRows.Add(new ChildRow { Resource = c, Consumers = Consumers(c.Id) });
            }
        }

        foreach (var r in snapshot.Resources.Where(r => r.Kind == AzureResourceKind.ContainerApp))
        {
            var a = analyses.GetValueOrDefault(r.Id);
            view.ContainerApps.Add(new ContainerAppRow { Resource = r, Analysis = a, Connections = a?.ConnectionCount ?? 0 });
        }

        foreach (var r in snapshot.Resources.Where(r => r.Kind == AzureResourceKind.ContainerRegistry))
        {
            view.Registries.Add(new SimpleRow { Resource = r, Type = "Container Registry", Consumers = Consumers(r.Id) });
        }

        foreach (var r in snapshot.Resources.Where(r => r.Kind is AzureResourceKind.CosmosAccount or AzureResourceKind.RedisCache
                     or AzureResourceKind.ServiceBusNamespace or AzureResourceKind.KeyVault))
        {
            var type = r.Kind switch
            {
                AzureResourceKind.CosmosAccount => "Cosmos DB",
                AzureResourceKind.RedisCache => "Redis",
                AzureResourceKind.ServiceBusNamespace => "Service Bus",
                _ => "Key Vault"
            };
            view.Other.Add(new SimpleRow { Resource = r, Type = type, Consumers = Consumers(r.Id) });
        }

        view.WebAppCount = snapshot.Resources.Count(r => r.Kind == AzureResourceKind.WebApp);
        view.FunctionAppCount = snapshot.Resources.Count(r => r.Kind == AzureResourceKind.FunctionApp);
        view.SlotCount = snapshot.Resources.Count(r => r.Kind == AzureResourceKind.WebAppSlot);
        view.SqlDatabaseCount = snapshot.Resources.Count(r => r.Kind == AzureResourceKind.SqlDatabase);
        view.ContainerCount = snapshot.Resources.Count(r => r.Kind == AzureResourceKind.BlobContainer);
        return view;
    }

    /// <summary>Distinct consuming apps per resource; accounts and servers include consumers of their children.</summary>
    private static Dictionary<string, int> ConsumerCounts(GraphIndex? index)
    {
        var sets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (index is null) return new();

        foreach (var edge in index.Graph.Edges.Where(e => DependencyEdges.Contains(e.Type)))
        {
            var targets = new List<string> { edge.Target };
            targets.AddRange(index.In(edge.Target)
                .Where(e => e.Type is EdgeTypes.HostsDatabase or EdgeTypes.HasContainer)
                .Select(e => e.Source));
            foreach (var t in targets)
            {
                if (!sets.TryGetValue(t, out var set)) sets[t] = set = new(StringComparer.OrdinalIgnoreCase);
                set.Add(edge.Source);
            }
        }
        return sets.ToDictionary(kv => kv.Key, kv => kv.Value.Count, StringComparer.OrdinalIgnoreCase);
    }
}
