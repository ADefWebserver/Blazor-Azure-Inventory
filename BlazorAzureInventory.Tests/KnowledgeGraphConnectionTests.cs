using System.Text.Json;
using BlazorAzureInventory.Models.Configuration;
using BlazorAzureInventory.Models.Graph;
using BlazorAzureInventory.Models.Inventory;
using BlazorAzureInventory.Models.Options;
using BlazorAzureInventory.Services.Analysis;
using BlazorAzureInventory.Services.Azure;
using BlazorAzureInventory.Services.Graph;
using Microsoft.Extensions.Options;

namespace BlazorAzureInventory.Tests;

/// <summary>Config-file resolution, SCM diagnostics, storage name hints, queues, and external services.</summary>
public class KnowledgeGraphConnectionTests
{
    private static readonly string ContosoQueue = Fixture.ContosoData + "/queueservices/default/queues/upload-queue";
    private static readonly string Archive = Fixture.Id("rg-data", "microsoft.storage/storageaccounts/contosoarchive");
    private static readonly string ArchiveOutreach = Archive + "/blobservices/default/containers/outreach";

    private static readonly IReadOnlyDictionary<string, string[]> Aliases = new InventoryOptions().EffectiveEnvironmentAliases;

    private static IReadOnlyList<ConnectionReference> Classify(string key, string value) =>
        Fixture.Classifier.Classify(key, value, ConfigSource.AppSettingsEnvironmentJson);

    // Config file resolution

    [Fact]
    public void Resolver_reads_file_name_without_dot()
    {
        var s = ConfigFileResolver.Resolve(["appsettings.json", "appsettingsProduction.json", "appsettingsDev.json"], "Production", Aliases);
        Assert.Equal("appsettings.json", s.BaseFile);
        Assert.Equal("appsettingsProduction.json", s.EnvironmentFile);
        Assert.Equal(["appsettingsDev.json"], s.Skipped);
    }

    [Fact]
    public void Resolver_prefers_dotted_name()
    {
        var s = ConfigFileResolver.Resolve(["appsettingsProduction.json", "appsettings.Production.json"], "Production", Aliases);
        Assert.Equal("appsettings.Production.json", s.EnvironmentFile);
        Assert.Null(s.BaseFile);
        Assert.Equal(["appsettings.Production.json"], s.ToRead);
    }

    [Theory]
    [InlineData("Production", "appsettings.Prod.json")]
    [InlineData("Prod", "appsettings.Production.json")]
    [InlineData("production", "APPSETTINGSPROD.JSON")]
    public void Resolver_uses_aliases_both_ways(string environment, string file)
    {
        var s = ConfigFileResolver.Resolve(["appsettings.json", file], environment, Aliases);
        Assert.Equal(file, s.EnvironmentFile);
    }

    [Fact]
    public void Resolver_returns_nothing_for_unrelated_files()
    {
        var s = ConfigFileResolver.Resolve(["appsettings.Development.json"], "Production", Aliases);
        Assert.Empty(s.ToRead);
        Assert.Equal(["appsettings.Development.json"], s.Skipped);
    }

    [Fact]
    public void Default_pattern_matches_both_naming_styles()
    {
        Assert.Equal(["appsettings*.json"], new InventoryOptions().EffectiveConfigFilePatterns);
    }

    // Kudu targets

    private static KuduConfigFileReader Kudu()
    {
        var options = Options.Create(new InventoryOptions());
        return new KuduConfigFileReader(new HttpClient(), new ScmHostValidator(options), options);
    }

    [Fact]
    public void Arm_tunnel_uri_uses_the_extensions_route()
    {
        var uri = Kudu().BuildUri(KuduTarget.ArmTunnel(Fixture.ShopWeb), "appsettingsProduction.json");
        Assert.NotNull(uri);
        Assert.Equal("management.azure.com", uri!.Host);
        Assert.Contains("/providers/microsoft.web/sites/shop-web/extensions/api/vfs/site/wwwroot/appsettingsProduction.json", uri.AbsolutePath);
        Assert.Contains("api-version=", uri.Query);
    }

    [Theory]
    [InlineData("/subscriptions/x/resourcegroups/rg/providers/microsoft.web/sites/a")]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/resourcegroups/rg/providers/microsoft.web/sites/a/../../x")]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/resourcegroups/rg/providers/microsoft.storage/storageaccounts/a")]
    public void Arm_tunnel_rejects_invalid_site_ids(string siteId) =>
        Assert.Null(Kudu().BuildUri(KuduTarget.ArmTunnel(siteId), ""));

    // SCM diagnostics

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Theory]
    [InlineData("""{"properties":{"scmIpSecurityRestrictions":[{"ipAddress":"Any","action":"Allow"}]}}""", false)]
    [InlineData("""{"properties":{"scmIpSecurityRestrictions":[{"ipAddress":"10.0.0.0/8","action":"Allow"}]}}""", true)]
    [InlineData("""{"properties":{"scmIpSecurityRestrictionsDefaultAction":"Deny"}}""", true)]
    [InlineData("""{"properties":{"publicNetworkAccess":"Disabled"}}""", true)]
    [InlineData("""{"properties":{"scmIpSecurityRestrictionsUseMain":true,"ipSecurityRestrictionsDefaultAction":"Deny","scmIpSecurityRestrictionsDefaultAction":"Allow"}}""", true)]
    [InlineData("""{"properties":{}}""", false)]
    public void Scm_restrictions_are_detected(string json, bool restricted) =>
        Assert.Equal(restricted, ScmAccessDiagnostics.IsScmRestricted(Json(json)));

    [Theory]
    [InlineData(ConfigAccessReason.ScmAccessRestricted)]
    [InlineData(ConfigAccessReason.ScmUnreachable)]
    [InlineData(ConfigAccessReason.AuthRejected)]
    public void Network_reasons_are_not_reported_as_missing_roles(ConfigAccessReason reason)
    {
        var failure = new KuduException(ConfigReadStatus.AccessDenied, ScanIssueKind.AccessDenied,
            $"403: SCM access denied. {AzureRoleRequirements.ConfigFiles.Hint}", 403);
        var message = AppConfigurationReader.AccessMessage(reason, failure, fallbackEnabled: true);
        var issue = new ScanIssue(Fixture.ShopWeb, ScanProgress.ReadingConfigFiles, ScanIssueKind.AccessDenied, message);

        Assert.DoesNotContain(AzureRoleRequirements.ConfigFiles.Permission, message);
        Assert.Empty(AzureRoleRequirements.Detect([issue]));
    }

    [Fact]
    public void Missing_role_is_still_detected()
    {
        var failure = new KuduException(ConfigReadStatus.AccessDenied, ScanIssueKind.AccessDenied,
            $"403: SCM access denied. {AzureRoleRequirements.ConfigFiles.Hint}", 403);
        var message = AppConfigurationReader.AccessMessage(ConfigAccessReason.MissingRole, failure, fallbackEnabled: true);
        var issue = new ScanIssue(Fixture.ShopWeb, ScanProgress.ReadingConfigFiles, ScanIssueKind.AccessDenied, message);
        Assert.Equal(AzureRoleRequirements.WebsiteContributor, Assert.Single(AzureRoleRequirements.Detect([issue])).Role);
    }

    // Classifier

    [Theory]
    [InlineData("ApplicationSettings:AzureBlobContainer", "contososchools", ConnectionTargetKind.BlobContainer, "contososchools")]
    [InlineData("ApplicationSettings:AzureQueueName", "upload-queue", ConnectionTargetKind.StorageQueue, "upload-queue")]
    [InlineData("QueueContainer", "jobs", ConnectionTargetKind.StorageQueue, "jobs")]
    public void Storage_name_hints_are_typed(string key, string value, ConnectionTargetKind kind, string name)
    {
        var r = Assert.Single(Classify(key, value));
        Assert.Equal(kind, r.Kind);
        Assert.Equal(name, r.ContainerName);
        Assert.Equal(Confidence.Low, r.Confidence);
    }

    [Theory]
    [InlineData("ApplicationSettings:EventsRoster", "EventsRoster")]
    [InlineData("ApplicationSettings:OutreachContainer", "Outreach")]
    [InlineData("ApplicationSettings:PartnersContainer", "PartnersContainer")]
    [InlineData("BlobStorage:Timeout", "thirty")]
    [InlineData("BlobTimeout", "300")]
    [InlineData("OutreachContainer", "Bad_Name")]
    public void Non_hints_are_ignored(string key, string value) => Assert.Empty(Classify(key, value));

    [Fact]
    public void Hint_keywords_are_configurable()
    {
        var classifier = new ConnectionClassifier(Fixture.Suffixes, null, new StorageNameHintOptions { ContainerKeywords = ["Roster"] });
        var r = Assert.Single(classifier.Classify("EventsRoster", "eventsroster", ConfigSource.AppSettingsJson));
        Assert.Equal("eventsroster", r.ContainerName);
    }

    [Fact]
    public void Queue_uri_is_high_confidence()
    {
        var r = Assert.Single(Classify("Jobs:Uri", "https://contosodata.queue.core.windows.net/upload-queue"));
        Assert.Equal(ConnectionTargetKind.StorageQueue, r.Kind);
        Assert.Equal("contosodata", r.AccountOrServerName);
        Assert.Equal("upload-queue", r.ContainerName);
        Assert.Equal(Confidence.High, r.Confidence);
    }

    [Fact]
    public void Ef6_entity_connection_string_is_unwrapped()
    {
        var r = Assert.Single(Classify("ConnectionStrings:Entities",
            "metadata=res://*/Model.csdl;provider=System.Data.SqlClient;provider connection string=\"data source=contoso-sql.database.windows.net;initial catalog=ContosoSchoolsProd_Db;user id=u;password=p\""));
        Assert.Equal(ConnectionTargetKind.SqlDatabase, r.Kind);
        Assert.Equal("contoso-sql.database.windows.net", r.Host);
        Assert.Equal("ContosoSchoolsProd_Db", r.DatabaseName);
    }

    [Theory]
    [InlineData("ApplicationSettings:SmtpServer", "smtp.sendgrid.net", "smtp.sendgrid.net")]
    [InlineData("Smtp:Host", "smtp.sendgrid.net:587", "smtp.sendgrid.net")]
    [InlineData("Mail:Api", "https://api.sendgrid.com/v3/mail/send", "api.sendgrid.com")]
    [InlineData("Mail:Connection", "Server=smtp.sendgrid.net;Port=587", "smtp.sendgrid.net")]
    public void SendGrid_hosts_are_high_confidence(string key, string value, string host)
    {
        var r = Assert.Single(Classify(key, value));
        Assert.Equal(ConnectionTargetKind.ExternalService, r.Kind);
        Assert.Equal("sendgrid", r.AccountOrServerName);
        Assert.Equal(host, r.Host);
        Assert.Equal(Confidence.High, r.Confidence);
    }

    [Fact]
    public void SendGrid_key_keyword_is_medium_and_ignores_the_value()
    {
        var r = Assert.Single(Classify("Mail:SendGridApiKey", "placeholder-value"));
        Assert.Equal(ConnectionTargetKind.ExternalService, r.Kind);
        Assert.Null(r.Host);
        Assert.Equal(Confidence.Medium, r.Confidence);
    }

    [Theory]
    [InlineData("smtp.notsendgrid.net")]
    [InlineData("sendgrid.net.example.com")]
    public void SendGrid_requires_label_boundary(string host) => Assert.Empty(Classify("SmtpServer", host));

    // Analyzer attribution

    [Fact]
    public void Hint_uses_storage_account_from_the_same_section()
    {
        var entries = Fixture.Analyzer.Analyze(Fixture.ShopWeb, null, "Production",
        [
            new("Logging:Storage:AccountName", "contosodata", ConfigSource.AppSettingsJson, "appsettings.json"),
            new("ApplicationSettings:AzureStorageConnectionString", "AccountName=contosoarchive;AccountKey=x", ConfigSource.AppSettingsJson, "appsettings.json"),
            new("ApplicationSettings:AzureBlobContainer", "outreach", ConfigSource.AppSettingsJson, "appsettings.json"),
        ]);
        var hint = entries.Single(e => e.Key == "ApplicationSettings:AzureBlobContainer").References.Single();
        Assert.Equal("contosoarchive", hint.AccountOrServerName);
    }

    // Matcher

    private static (ResourceMatcher Matcher, ResourceIndex Index) MatcherWithQueueAndArchive()
    {
        var resources = Fixture.Resources();
        resources.Add(Fixture.Resource(ContosoQueue, AzureResourceKind.StorageQueue, Fixture.ContosoData));
        resources.Add(Fixture.Resource(Archive, AzureResourceKind.StorageAccount, endpoint: "https://contosoarchive.blob.core.windows.net/"));
        resources.Add(Fixture.Resource(ArchiveOutreach, AzureResourceKind.BlobContainer, Archive));
        var matcher = Fixture.Matcher;
        return (matcher, matcher.BuildIndex(resources));
    }

    private static AppConfigAnalysis Analysis(params RawSetting[] settings) =>
        new(Fixture.ShopWeb, "Production", ConfigReadStatus.Read, ConfigReadStatus.Read, [],
            Fixture.Analyzer.Analyze(Fixture.ShopWeb, null, "Production", settings));

    private static ConnectionReference RefOf(AppConfigAnalysis a, string key) => a.Entries.Single(e => e.Key == key).References.Single();

    [Fact]
    public void Confirmed_hints_are_promoted_and_unconfirmed_hints_stay_low()
    {
        var (matcher, index) = MatcherWithQueueAndArchive();
        var matched = matcher.Match(Analysis(
            new("Storage", "AccountName=contosodata;AccountKey=x", ConfigSource.AppServiceAppSetting, null),
            new("ImagesContainer", "product-images", ConfigSource.AppServiceAppSetting, null),
            new("AzureQueueName", "upload-queue", ConfigSource.AppServiceAppSetting, null),
            new("MissingContainer", "nothing-here", ConfigSource.AppServiceAppSetting, null)), index);

        var images = RefOf(matched, "ImagesContainer");
        Assert.Equal(Fixture.ProductImages, images.ResolvedResourceId);
        Assert.Equal(Confidence.Medium, images.Confidence);

        var queue = RefOf(matched, "AzureQueueName");
        Assert.Equal(ContosoQueue, queue.ResolvedResourceId);
        Assert.Equal(Confidence.Medium, queue.Confidence);

        var missing = RefOf(matched, "MissingContainer");
        Assert.Equal(Fixture.ContosoData, missing.ResolvedResourceId);
        Assert.Equal(Confidence.Low, missing.Confidence);
    }

    [Fact]
    public void Hint_is_reattributed_to_the_account_that_has_the_child()
    {
        var (matcher, index) = MatcherWithQueueAndArchive();
        var matched = matcher.Match(Analysis(
            new("Storage", "AccountName=contosodata;AccountKey=x", ConfigSource.AppServiceAppSetting, null),
            new("Archive", "AccountName=contosoarchive;AccountKey=x", ConfigSource.AppServiceAppSetting, null),
            new("OutreachContainer", "outreach", ConfigSource.AppServiceAppSetting, null)), index);

        var outreach = RefOf(matched, "OutreachContainer");
        Assert.Equal(ArchiveOutreach, outreach.ResolvedResourceId);
        Assert.Equal("contosoarchive", outreach.AccountOrServerName);
        Assert.Equal(Confidence.Medium, outreach.Confidence);
    }

    // Graph

    private static InventorySnapshot SnapshotWith(IReadOnlyList<AzureResourceInfo> extraResources, params AppConfigAnalysis[] extraConfigs)
    {
        var baseSnapshot = Fixture.Snapshot();
        var resources = baseSnapshot.Resources.Concat(extraResources).ToList();
        var matcher = Fixture.Matcher;
        var index = matcher.BuildIndex(resources);
        var configs = baseSnapshot.AppConfigs.Where(c => extraConfigs.All(x => x.AppId != c.AppId))
            .Concat(extraConfigs.Select(c => matcher.Match(c, index)))
            .ToList();
        return baseSnapshot with { Resources = resources, AppConfigs = configs };
    }

    private static AppConfigAnalysis AppWith(string appId, params RawSetting[] settings) =>
        new(appId, "Production", ConfigReadStatus.Read, ConfigReadStatus.Read, [],
            Fixture.Analyzer.Analyze(appId, null, "Production", settings));

    [Fact]
    public void Graph_links_queues_to_accounts_and_apps()
    {
        var snapshot = SnapshotWith(
            [Fixture.Resource(ContosoQueue, AzureResourceKind.StorageQueue, Fixture.ContosoData)],
            AppWith(Fixture.BillingApi,
                new("Storage", "AccountName=contosodata;AccountKey=x", ConfigSource.AppSettingsJson, "appsettings.json"),
                new("AzureQueueName", "upload-queue", ConfigSource.AppSettingsJson, "appsettings.json")));
        var graph = Fixture.Builder().Build(snapshot, new GraphBuildOptions());

        Assert.Contains(graph.Edges, e => e.Type == EdgeTypes.HasQueue && e.Source == Fixture.ContosoData && e.Target == ContosoQueue);
        var edge = Assert.Single(graph.Edges, e => e.Type == EdgeTypes.ConnectsTo && e.Source == Fixture.BillingApi && e.Target == ContosoQueue);
        Assert.Equal("Medium", edge.Data["confidence"]);
        Assert.False((bool)graph.Nodes.Single(n => n.Id == ContosoQueue).Data[TagKeys.Taggable]!);
    }

    [Fact]
    public void Queue_cap_keeps_referenced_queues_and_adds_aggregate()
    {
        var other = Fixture.ContosoData + "/queueservices/default/queues/aaa-other";
        var snapshot = SnapshotWith(
            [
                Fixture.Resource(ContosoQueue, AzureResourceKind.StorageQueue, Fixture.ContosoData),
                Fixture.Resource(other, AzureResourceKind.StorageQueue, Fixture.ContosoData)
            ],
            AppWith(Fixture.BillingApi,
                new("Storage", "AccountName=contosodata;AccountKey=x", ConfigSource.AppSettingsJson, "appsettings.json"),
                new("AzureQueueName", "upload-queue", ConfigSource.AppSettingsJson, "appsettings.json")));
        var graph = Fixture.Builder().Build(snapshot, new GraphBuildOptions(MaxQueuesPerAccount: 1));

        Assert.Contains(graph.Nodes, n => n.Id == ContosoQueue);
        Assert.DoesNotContain(graph.Nodes, n => n.Id == other);
        Assert.Equal("+1 more queues", graph.Nodes.Single(n => n.Id.EndsWith("/queues/__more")).Label);
    }

    [Fact]
    public void Apps_share_one_external_service_node()
    {
        var snapshot = SnapshotWith([],
            AppWith(Fixture.BillingApi, new RawSetting("Mail:SmtpServer", "smtp.sendgrid.net", ConfigSource.AppSettingsJson, "appsettings.json")),
            AppWith(Fixture.ShopWeb,
                new("Smtp:Host", "smtp.sendgrid.net:587", ConfigSource.AppSettingsJson, "appsettings.json"),
                new("Mail:Api", "https://api.sendgrid.com/v3", ConfigSource.AppSettingsJson, "appsettings.json")));
        var builder = Fixture.Builder();
        var graph = builder.Build(snapshot, new GraphBuildOptions());

        var node = Assert.Single(graph.Nodes, n => n.Type == nameof(AzureResourceKind.ExternalService));
        Assert.Equal("external:service:sendgrid", node.Id);
        Assert.Equal("SendGrid", node.Label);
        Assert.Equal("#1A82E2", node.Data["color"]);
        Assert.Equal(["api.sendgrid.com", "smtp.sendgrid.net"], (List<string>)node.Data["matchedHosts"]!);
        Assert.Equal(2, graph.Edges.Count(e => e.Type == EdgeTypes.ConnectsTo && e.Target == node.Id));
        Assert.All(graph.Edges, e => Assert.Contains(graph.Nodes, n => n.Id == e.Source));

        var again = builder.Build(snapshot, new GraphBuildOptions());
        Assert.Equal(graph.Edges.Select(e => e.Id), again.Edges.Select(e => e.Id));
    }

    [Fact]
    public void App_node_carries_config_file_details()
    {
        var analysis = AppWith(Fixture.BillingApi) with
        {
            ConfigFilesStatus = ConfigReadStatus.AccessDenied,
            ConfigFilesReason = ConfigAccessReason.ScmAccessRestricted
        };
        var graph = Fixture.Builder().Build(SnapshotWith([], analysis), new GraphBuildOptions());
        var node = graph.Nodes.Single(n => n.Id == Fixture.BillingApi);
        Assert.Equal("ScmAccessRestricted", node.Data["configFilesReason"]);
        Assert.Contains("access restrictions", (string)node.Data["configFilesHint"]!);
    }

    // Options and filters

    [Fact]
    public void External_service_options_are_validated()
    {
        var options = new InventoryOptions
        {
            ExternalServices =
            [
                new() { Id = "a", DisplayName = "A", HostSuffixes = ["nodot"] },
                new() { Id = "b", DisplayName = "B", Color = "red", HostSuffixes = ["database.windows.net"] },
                new() { Id = "b", DisplayName = "B2" }
            ]
        };
        var result = new ExternalServiceOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("must contain a dot"));
        Assert.Contains(result.Failures!, f => f.Contains("Azure suffix"));
        Assert.Contains(result.Failures!, f => f.Contains("Duplicate"));
        Assert.Contains(result.Failures!, f => f.Contains("External service 'b'"));
        Assert.True(new ExternalServiceOptionsValidator().Validate(null, new InventoryOptions()).Succeeded);
    }

    [Fact]
    public void Saved_filters_show_new_node_types()
    {
        var legacy = new GraphFilterState { NodeTypes = ["WebApp", "SqlDatabase"] };
        var restored = NodeTypeStyles.RestoreVisibleTypes(legacy);
        Assert.Contains("StorageQueue", restored);
        Assert.Contains("ExternalService", restored);
        Assert.DoesNotContain("StorageAccount", restored);

        var current = new GraphFilterState { NodeTypes = ["WebApp"], KnownNodeTypes = NodeTypeStyles.All.Select(t => t.Type).ToList() };
        Assert.Equal(["WebApp"], NodeTypeStyles.RestoreVisibleTypes(current));
    }
}
