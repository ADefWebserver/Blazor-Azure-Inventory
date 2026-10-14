using BlazorAzureInventory.Models.Configuration;
using BlazorAzureInventory.Models.Inventory;
using BlazorAzureInventory.Models.Options;
using BlazorAzureInventory.Services.Analysis;
using BlazorAzureInventory.Services.Graph;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlazorAzureInventory.Tests;

/// <summary>The Contoso sample estate from the screen descriptions, run through the real analysis pipeline.</summary>
internal static class Fixture
{
    public const string TenantId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    public const string SubscriptionId = "11111111-1111-1111-1111-111111111111";

    public static readonly string ShopWeb = Id("rg-shop", "microsoft.web/sites/shop-web");
    public static readonly string ShopWebStaging = ShopWeb + "/slots/staging";
    public static readonly string OrdersFunc = Id("rg-shop", "microsoft.web/sites/orders-func");
    public static readonly string BillingApi = Id("rg-shop", "microsoft.web/sites/billing-api");
    public static readonly string CatalogApi = Id("rg-shop", "microsoft.app/containerapps/catalog-api");
    public static readonly string ContosoSql = Id("rg-data", "microsoft.sql/servers/contoso-sql");
    public static readonly string OrdersDb = ContosoSql + "/databases/orders";
    public static readonly string CustomersDb = ContosoSql + "/databases/customers";
    public static readonly string ContosoData = Id("rg-data", "microsoft.storage/storageaccounts/contosodata");
    public static readonly string Uploads = ContosoData + "/blobservices/default/containers/uploads";
    public static readonly string ProductImages = ContosoData + "/blobservices/default/containers/product-images";
    public static readonly string ContosoAcr = Id("rg-shop", "microsoft.containerregistry/registries/contosoacr");
    public static readonly string ContosoKv = Id("rg-data", "microsoft.keyvault/vaults/contoso-kv");
    public static readonly string ContosoCache = Id("rg-data", "microsoft.cache/redis/contoso-cache");
    public static readonly string ContosoBus = Id("rg-data", "microsoft.servicebus/namespaces/contoso-bus");

    public static CloudEndpointSuffixes Suffixes => new(new CloudEndpointSuffixOptions());
    public static ConnectionClassifier Classifier => new(Suffixes);
    public static ConfigAnalyzer Analyzer => new(new ConfigFlattener(), new SecretClassifier(), Classifier);
    public static ResourceMatcher Matcher => new(Suffixes);

    public static InventoryGraphBuilder Builder(ILogger<InventoryGraphBuilder>? logger = null) =>
        new(Matcher, logger ?? NullLogger<InventoryGraphBuilder>.Instance);

    public static string Id(string rg, string path) =>
        $"/subscriptions/{SubscriptionId}/resourcegroups/{rg}/providers/{path}".ToLowerInvariant();

    public static AzureResourceInfo Resource(
        string id, AzureResourceKind kind, string? parentId = null, string? endpoint = null, Dictionary<string, string>? props = null)
    {
        var rg = id.Split('/')[4];
        return new AzureResourceInfo(id, id.Split('/')[^1], kind, kind.ToString().ToLowerInvariant(), TenantId, SubscriptionId, rg,
            "eastus", parentId, endpoint, props ?? new Dictionary<string, string>());
    }

    public static List<AzureResourceInfo> Resources() =>
    [
        Resource(ShopWeb, AzureResourceKind.WebApp, endpoint: "shop-web.azurewebsites.net"),
        Resource(ShopWebStaging, AzureResourceKind.WebAppSlot, ShopWeb, "shop-web-staging.azurewebsites.net",
            new() { ["siteName"] = "shop-web", ["slotName"] = "staging" }),
        Resource(OrdersFunc, AzureResourceKind.FunctionApp, endpoint: "orders-func.azurewebsites.net"),
        Resource(BillingApi, AzureResourceKind.WebApp, endpoint: "billing-api.azurewebsites.net"),
        Resource(CatalogApi, AzureResourceKind.ContainerApp, endpoint: "catalog-api.example.eastus.azurecontainerapps.io"),
        Resource(ContosoSql, AzureResourceKind.SqlServer, endpoint: "contoso-sql.database.windows.net"),
        Resource(OrdersDb, AzureResourceKind.SqlDatabase, ContosoSql, props: new() { ["sku"] = "S1", ["status"] = "Online" }),
        Resource(CustomersDb, AzureResourceKind.SqlDatabase, ContosoSql),
        Resource(ContosoData, AzureResourceKind.StorageAccount, endpoint: "https://contosodata.blob.core.windows.net/"),
        Resource(Uploads, AzureResourceKind.BlobContainer, ContosoData),
        Resource(ProductImages, AzureResourceKind.BlobContainer, ContosoData),
        Resource(ContosoAcr, AzureResourceKind.ContainerRegistry, endpoint: "contosoacr.azurecr.io"),
        Resource(ContosoKv, AzureResourceKind.KeyVault, endpoint: "https://contoso-kv.vault.azure.net/"),
        Resource(ContosoCache, AzureResourceKind.RedisCache, endpoint: "contoso-cache.redis.cache.windows.net"),
        Resource(ContosoBus, AzureResourceKind.ServiceBusNamespace, endpoint: "https://contoso-bus.servicebus.windows.net:443/"),
    ];

    /// <summary>Builds the sample scan. <paramref name="secret"/> is placed in every secret part of every value.</summary>
    public static InventorySnapshot Snapshot(string secret = "pw", IReadOnlyList<RawSetting>? extraShopWebSettings = null)
    {
        var analyzer = Analyzer;
        var shopWeb = new List<RawSetting>
        {
            new("ConnectionStrings:Orders", $"Server=tcp:contoso-sql.database.windows.net,1433;Initial Catalog=orders;User ID=u{secret};Password={secret};", ConfigSource.AppSettingsJson, "appsettings.json"),
            new("ConnectionStrings:Orders", $"Server=tcp:contoso-sql.database.windows.net,1433;Initial Catalog=orders;User ID=u{secret};Password={secret};", ConfigSource.AppServiceConnectionString, null),
            new("Storage", $"DefaultEndpointsProtocol=https;AccountName=contosodata;AccountKey={secret};EndpointSuffix=core.windows.net", ConfigSource.AppServiceAppSetting, null),
            new("Uploads:Uri", $"https://contosodata.blob.core.windows.net/uploads?sv=2024-01-01&sig={secret}", ConfigSource.AppServiceAppSetting, null),
            new("ApiKey", $"@Microsoft.KeyVault(SecretUri=https://contoso-kv.vault.azure.net/secrets/api-key/{secret})", ConfigSource.AppServiceAppSetting, null),
            new("Logging:LogLevel:Default", $"Information{secret}", ConfigSource.AppSettingsJson, "appsettings.json"),
        };
        if (extraShopWebSettings is not null) shopWeb.AddRange(extraShopWebSettings);

        var configs = new List<AppConfigAnalysis>
        {
            new(ShopWeb, "Production", ConfigReadStatus.Read, ConfigReadStatus.Read, ["appsettings.json"],
                analyzer.Analyze(ShopWeb, null, "Production", shopWeb)),
            new(OrdersFunc, "Production", ConfigReadStatus.Read, ConfigReadStatus.NotScanned, [],
                analyzer.Analyze(OrdersFunc, null, "Production",
                [
                    new("SqlConnection", $"Server=tcp:contoso-sql.database.windows.net;Database=orders;Password={secret}", ConfigSource.AppServiceAppSetting, null),
                    new("AzureWebJobsStorage:accountName", "contosodata", ConfigSource.AppServiceAppSetting, null),
                    new("Bus", $"Endpoint=sb://contoso-bus.servicebus.windows.net/;SharedAccessKeyName=a;SharedAccessKey={secret}", ConfigSource.AppServiceAppSetting, null),
                ])),
            new(BillingApi, "Production", ConfigReadStatus.Read, ConfigReadStatus.NotFound, [],
                analyzer.Analyze(BillingApi, null, "Production",
                [
                    new("ConnectionStrings:Legacy", $"Data Source=10.0.0.5;Initial Catalog=hr;Integrated Security=true;Password={secret}", ConfigSource.AppServiceConnectionString, null),
                ])),
            new(CatalogApi, null, ConfigReadStatus.Read, ConfigReadStatus.NotScanned, [],
                analyzer.Analyze(CatalogApi, null, null,
                [
                    new("Redis", $"contoso-cache.redis.cache.windows.net:6380,password={secret},ssl=True", ConfigSource.ContainerAppEnvValue, null),
                ]))
            {
                Images = [ConnectionClassifier.ParseImage("contosoacr.azurecr.io/catalog-api:1.4.2")!]
            },
        };

        var resources = Resources();
        var matcher = Matcher;
        var index = matcher.BuildIndex(resources);
        return new InventorySnapshot(
            new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero),
            [new TenantInfo(TenantId, "Contoso", "contoso.onmicrosoft.com")],
            [new SubscriptionInfo(SubscriptionId, "Contoso Production", TenantId, "Enabled")],
            [],
            resources,
            configs.Select(c => matcher.Match(c, index)).ToList(),
            [new ScanIssue(BillingApi, "Reading config files", ScanIssueKind.AccessDenied, "403: SCM access denied.")]);
    }

    public const int LargeContainersPerAccount = 10;
    public const int LargeQueuesPerAccount = 2;

    /// <summary>
    /// Deterministic large estate for graph performance work (plan section 11.1): web apps with slots, SQL servers with
    /// orphan databases, storage children, key vaults, one SendGrid hub, and low-confidence edges.
    /// </summary>
    public static InventorySnapshot LargeSnapshot(int apps = 400, int seed = 1)
    {
        var rng = new Random(seed);
        var sqlServers = Math.Max(1, apps / 10);
        var databases = apps * 95 / 100;
        var consumedDatabases = databases - apps * 15 / 100;
        var storageAccounts = Math.Max(1, apps * 3 / 40);
        var containers = storageAccounts * LargeContainersPerAccount;
        var queues = storageAccounts * LargeQueuesPerAccount;
        var vaults = Math.Max(1, apps / 20);

        var withSlot = Pick(rng, apps, apps / 10);
        var withDatabase = Pick(rng, apps, apps - apps / 20);
        var withSendGrid = Pick(rng, apps, apps * 30 / 100);
        // Gives about 5% of the dependency edges.
        var withLowConfidence = Pick(rng, apps, apps * 18 / 100);

        var resources = new List<AzureResourceInfo>();

        var serverIds = new string[sqlServers];
        for (var s = 0; s < sqlServers; s++)
        {
            var name = $"contoso-sql-{s + 1:00}";
            serverIds[s] = Id("rg-sql", $"microsoft.sql/servers/{name}");
            resources.Add(Resource(serverIds[s], AzureResourceKind.SqlServer, endpoint: $"{name}.database.windows.net"));
        }
        for (var d = 0; d < databases; d++)
        {
            resources.Add(Resource($"{serverIds[d % sqlServers]}/databases/{LargeDatabaseName(d)}", AzureResourceKind.SqlDatabase, serverIds[d % sqlServers]));
        }

        var accountIds = new string[storageAccounts];
        for (var a = 0; a < storageAccounts; a++)
        {
            accountIds[a] = Id("rg-storage", $"microsoft.storage/storageaccounts/{LargeAccountName(a)}");
            resources.Add(Resource(accountIds[a], AzureResourceKind.StorageAccount, endpoint: $"https://{LargeAccountName(a)}.blob.core.windows.net/"));
        }
        for (var c = 0; c < containers; c++)
        {
            var account = accountIds[c / LargeContainersPerAccount];
            resources.Add(Resource($"{account}/blobservices/default/containers/files-{c + 1:000}", AzureResourceKind.BlobContainer, account));
        }
        for (var q = 0; q < queues; q++)
        {
            var account = accountIds[q / LargeQueuesPerAccount];
            resources.Add(Resource($"{account}/queueservices/default/queues/jobs-{q + 1:000}", AzureResourceKind.StorageQueue, account));
        }

        for (var v = 0; v < vaults; v++)
        {
            var name = $"contoso-kv-{v + 1:00}";
            resources.Add(Resource(Id("rg-security", $"microsoft.keyvault/vaults/{name}"), AzureResourceKind.KeyVault, endpoint: $"https://{name}.vault.azure.net/"));
        }

        var queueConsumers = new Dictionary<int, int>();
        for (var q = 0; q < queues; q++) queueConsumers[q * apps / queues] = q;

        var analyzer = Analyzer;
        var configs = new List<AppConfigAnalysis>(apps);
        var dbConsumer = 0;
        for (var i = 0; i < apps; i++)
        {
            var name = $"contoso-app-{i + 1:000}";
            var appId = Id($"rg-apps-{i % 10 + 1:00}", $"microsoft.web/sites/{name}");
            resources.Add(Resource(appId, AzureResourceKind.WebApp, endpoint: $"{name}.azurewebsites.net"));
            if (withSlot.Contains(i))
            {
                resources.Add(Resource(appId + "/slots/staging", AzureResourceKind.WebAppSlot, appId, $"{name}-staging.azurewebsites.net",
                    new() { ["siteName"] = name, ["slotName"] = "staging" }));
            }

            var c = i % containers;
            var settings = new List<RawSetting>
            {
                new("Files:Uri", $"https://{LargeAccountName(c / LargeContainersPerAccount)}.blob.core.windows.net/files-{c + 1:000}", ConfigSource.AppServiceAppSetting, null),
                new("ApiKey", $"@Microsoft.KeyVault(SecretUri=https://contoso-kv-{i % vaults + 1:00}.vault.azure.net/secrets/api-key/)", ConfigSource.AppServiceAppSetting, null),
            };
            if (withDatabase.Contains(i))
            {
                var d = dbConsumer++ % consumedDatabases;
                settings.Add(new("ConnectionStrings:Main",
                    $"Server=tcp:contoso-sql-{d % sqlServers + 1:00}.database.windows.net,1433;Initial Catalog={LargeDatabaseName(d)};Authentication=Active Directory Default;",
                    ConfigSource.AppServiceConnectionString, null));
            }
            if (queueConsumers.TryGetValue(i, out var q))
            {
                settings.Add(new("Jobs:Uri", $"https://{LargeAccountName(q / LargeQueuesPerAccount)}.queue.core.windows.net/jobs-{q + 1:000}", ConfigSource.AppServiceAppSetting, null));
            }
            if (withSendGrid.Contains(i)) settings.Add(new("Email:SmtpHost", "smtp.sendgrid.net", ConfigSource.AppServiceAppSetting, null));
            // No such container exists, so the hint stays Low and resolves to the storage account.
            if (withLowConfidence.Contains(i)) settings.Add(new("Archive:ContainerName", "archive-missing", ConfigSource.AppServiceAppSetting, null));

            configs.Add(new AppConfigAnalysis(appId, "Production", ConfigReadStatus.Read, ConfigReadStatus.NotScanned, [],
                analyzer.Analyze(appId, null, "Production", settings)));
        }

        var matcher = Matcher;
        var index = matcher.BuildIndex(resources);
        return new InventorySnapshot(
            new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero),
            [new TenantInfo(TenantId, "Contoso", "contoso.onmicrosoft.com")],
            [new SubscriptionInfo(SubscriptionId, "Contoso Production", TenantId, "Enabled")],
            [],
            resources,
            configs.Select(c => matcher.Match(c, index)).ToList(),
            []);

        static string LargeDatabaseName(int d) => $"db-{d + 1:000}";
        static string LargeAccountName(int a) => $"contosost{a + 1:00}";

        static HashSet<int> Pick(Random rng, int total, int count)
        {
            var order = Enumerable.Range(0, total).ToArray();
            rng.Shuffle(order);
            return order.Take(count).ToHashSet();
        }
    }
}
