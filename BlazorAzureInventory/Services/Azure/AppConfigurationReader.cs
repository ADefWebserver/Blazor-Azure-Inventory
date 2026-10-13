using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.AppContainers;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.AppService.Models;
using BlazorAzureInventory.Models.Configuration;
using BlazorAzureInventory.Models.Inventory;
using BlazorAzureInventory.Models.Options;
using BlazorAzureInventory.Services.Analysis;
using Microsoft.Extensions.Options;

namespace BlazorAzureInventory.Services.Azure;

public sealed record AppReadResult(
    AppConfigAnalysis Analysis,
    IReadOnlyList<ScanIssue> Issues,
    IReadOnlyDictionary<string, string> ExtraProperties);

/// <summary>
/// Reads App Service settings, connection strings, Kudu config files, and Container App env vars,
/// then flattens, masks, and classifies them. Raw values live only inside these methods.
/// </summary>
public sealed class AppConfigurationReader(
    ArmClientFactory armFactory,
    KuduConfigFileReader kudu,
    ScmAccessDiagnostics diagnostics,
    ConfigFlattener flattener,
    ConfigAnalyzer analyzer,
    IOptions<InventoryOptions> options,
    ILogger<AppConfigurationReader> logger)
{
    private static readonly string SettingsAccessHint = AzureRoleRequirements.AppSettings.Hint;
    private static readonly string[] EnvironmentKeys = ["ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT", "AZURE_FUNCTIONS_ENVIRONMENT"];

    public async Task<AppReadResult> ReadAppServiceAsync(AzureResourceInfo app, Action<string>? onStage, CancellationToken ct)
    {
        var issues = new List<ScanIssue>();
        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var client = await armFactory.GetAsync(app.TenantId);
        var isSlot = app.Kind == AzureResourceKind.WebAppSlot;
        var slotName = isSlot ? app.Prop("slotName") : null;

        string? scmHost = null;
        string? linuxFx = null;
        WebSiteResource? site = null;
        WebSiteSlotResource? slot = null;
        try
        {
            if (isSlot)
            {
                slot = (await client.GetWebSiteSlotResource(new ResourceIdentifier(app.Id)).GetAsync(ct)).Value;
                scmHost = ScmHostOf(slot.Data.HostNameSslStates);
                linuxFx = slot.Data.SiteConfig?.LinuxFxVersion;
            }
            else
            {
                site = (await client.GetWebSiteResource(new ResourceIdentifier(app.Id)).GetAsync(ct)).Value;
                scmHost = ScmHostOf(site.Data.HostNameSslStates);
                linuxFx = site.Data.SiteConfig?.LinuxFxVersion;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var (kind, message) = ScanErrors.Classify(ex);
            issues.Add(new ScanIssue(app.Id, "Reading app settings", kind, message));
            return new AppReadResult(
                new AppConfigAnalysis(app.Id, null, ConfigReadStatus.NotScanned, ConfigReadStatus.NotScanned, [], []),
                issues, extra);
        }

        var raw = new List<RawSetting>();
        var settingsStatus = ConfigReadStatus.Read;
        IDictionary<string, string>? appSettings = null;
        try
        {
            var sticky = await GetStickyNamesAsync(client, isSlot ? app.ParentId! : app.Id, ct);

            appSettings = isSlot
                ? (await slot!.GetApplicationSettingsSlotAsync(ct)).Value.Properties
                : (await site!.GetApplicationSettingsAsync(ct)).Value.Properties;
            foreach (var (key, value) in appSettings)
            {
                raw.Add(new RawSetting(ConfigFlattener.NormalizeEnvironmentKey(key), value,
                    ConfigSource.AppServiceAppSetting, null, sticky.AppSettings.Contains(key)));
            }

            var connectionStrings = isSlot
                ? (await slot!.GetConnectionStringsSlotAsync(ct)).Value.Properties
                : (await site!.GetConnectionStringsAsync(ct)).Value.Properties;
            foreach (var (name, pair) in connectionStrings)
            {
                raw.Add(new RawSetting(ConfigFlattener.ConnectionStringKey(name), pair.Value,
                    ConfigSource.AppServiceConnectionString, null, sticky.ConnectionStrings.Contains(name)));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var (kind, message) = ScanErrors.Classify(ex, SettingsAccessHint);
            if (kind == ScanIssueKind.Unexpected)
                logger.LogWarning(ex, "Unexpected error reading app settings for {ResourceId}", app.Id);
            settingsStatus = kind switch
            {
                ScanIssueKind.AccessDenied or ScanIssueKind.ConsentRequired => ConfigReadStatus.AccessDenied,
                ScanIssueKind.NotFound => ConfigReadStatus.NotFound,
                _ => ConfigReadStatus.Error
            };
            issues.Add(new ScanIssue(app.Id, "Reading app settings", kind, message));
        }

        var environment = EnvironmentKeys
            .Select(k => appSettings?.FirstOrDefault(kv => kv.Key.Equals(k, StringComparison.OrdinalIgnoreCase)).Value)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "Production";

        var files = ConfigFilesResult.NotScanned;
        if (options.Value.ReadConfigFiles && app.Kind != AzureResourceKind.FunctionApp
            && (scmHost is not null || options.Value.UseArmKuduFallback))
        {
            onStage?.Invoke(ScanProgress.ReadingConfigFiles);
            files = await ReadConfigFilesAsync(app, scmHost, environment, ct);
            raw.AddRange(files.Settings);
            if (files.Issue is not null) issues.Add(files.Issue);
            if (files.Via != ConfigFilesRoute.None) extra["configFilesVia"] = files.Via.ToString();
        }

        var linuxImage = ConnectionClassifier.ParseImage(linuxFx);
        if (linuxImage is not null)
        {
            extra["images"] = linuxImage.RegistryHost + "/" + linuxImage.Repository;
        }

        var entries = Analyze(app.Id, slotName, environment, raw);
        IReadOnlyList<ImageReference> images = linuxImage is null ? [] : [linuxImage];
        return new AppReadResult(
            new AppConfigAnalysis(app.Id, environment, settingsStatus, files.Status, files.Found, entries)
            {
                Images = images,
                ConfigFilesReason = files.Reason,
                ConfigFilesVia = files.Via,
                ConfigFilesRead = files.Read,
                ConfigFilesSkipped = files.Skipped
            },
            issues, extra);
    }

    public async Task<AppReadResult> ReadContainerAppAsync(AzureResourceInfo app, CancellationToken ct)
    {
        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var client = await armFactory.GetAsync(app.TenantId);
            var data = (await client.GetContainerAppResource(new ResourceIdentifier(app.Id)).GetAsync(ct)).Value.Data;

            var keyVaultSecrets = (data.Configuration?.Secrets ?? [])
                .Where(s => s.Name is not null)
                .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().KeyVaultUri, StringComparer.OrdinalIgnoreCase);

            var raw = new List<RawSetting>();
            var images = new List<ImageReference>();
            var imageNames = new List<string>();
            foreach (var container in data.Template?.Containers ?? [])
            {
                if (!string.IsNullOrWhiteSpace(container.Image))
                {
                    imageNames.Add(container.Image);
                    if (ConnectionClassifier.ParseImage(container.Image) is { } image) images.Add(image);
                }

                foreach (var env in container.Env ?? [])
                {
                    if (string.IsNullOrWhiteSpace(env.Name)) continue;
                    var key = ConfigFlattener.NormalizeEnvironmentKey(env.Name);
                    if (!string.IsNullOrEmpty(env.SecretRef))
                    {
                        // Only the Key Vault URI (an identifier) is used; secret values are never requested.
                        var kvUri = keyVaultSecrets.GetValueOrDefault(env.SecretRef);
                        raw.Add(new RawSetting(key, kvUri is null ? null : $"@Microsoft.KeyVault(SecretUri={kvUri})",
                            ConfigSource.ContainerAppEnvSecretRef, null));
                    }
                    else
                    {
                        raw.Add(new RawSetting(key, env.Value, ConfigSource.ContainerAppEnvValue, null));
                    }
                }
            }

            extra["images"] = string.Join(", ", imageNames.Distinct());
            extra["registries"] = string.Join(", ", (data.Configuration?.Registries ?? [])
                .Select(r => r.Server).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
            extra["envVarCount"] = raw.Count.ToString();
            var environmentId = (data.ManagedEnvironmentId ?? data.EnvironmentId)?.ToString();
            if (!string.IsNullOrEmpty(environmentId)) extra["environmentId"] = environmentId.ToLowerInvariant();

            var entries = Analyze(app.Id, null, null, raw);
            return new AppReadResult(
                new AppConfigAnalysis(app.Id, null, ConfigReadStatus.Read, ConfigReadStatus.NotScanned, [], entries) { Images = images },
                [], extra);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var (kind, message) = ScanErrors.Classify(ex, AzureRoleRequirements.ContainerApps.Hint);
            var status = kind is ScanIssueKind.AccessDenied or ScanIssueKind.ConsentRequired ? ConfigReadStatus.AccessDenied : ConfigReadStatus.Error;
            return new AppReadResult(
                new AppConfigAnalysis(app.Id, null, status, ConfigReadStatus.NotScanned, [], []),
                [new ScanIssue(app.Id, "Reading Container Apps", kind, message)], extra);
        }
    }

    private sealed record ConfigFilesResult(
        ConfigReadStatus Status, ScanIssue? Issue, ConfigAccessReason Reason, ConfigFilesRoute Via,
        IReadOnlyList<string> Found, IReadOnlyList<string> Read, IReadOnlyList<string> Skipped, IReadOnlyList<RawSetting> Settings)
    {
        public static readonly ConfigFilesResult NotScanned =
            new(ConfigReadStatus.NotScanned, null, ConfigAccessReason.None, ConfigFilesRoute.None, [], [], [], []);

        public KuduException? Failure { get; init; }
    }

    private static readonly ConfigAccessReason[] FallbackReasons =
        [ConfigAccessReason.ScmAccessRestricted, ConfigAccessReason.ScmUnreachable, ConfigAccessReason.AuthRejected, ConfigAccessReason.Unknown];

    private async Task<ConfigFilesResult> ReadConfigFilesAsync(AzureResourceInfo app, string? scmHost, string environment, CancellationToken ct)
    {
        var credential = await armFactory.GetCredentialAsync(app.TenantId);
        var fallback = options.Value.UseArmKuduFallback;
        if (scmHost is null)
        {
            return await ReadViaAsync(app, KuduTarget.ArmTunnel(app.Id), credential, environment, ConfigAccessReason.None, ct);
        }

        var direct = await ReadViaAsync(app, KuduTarget.Direct(scmHost), credential, environment, ConfigAccessReason.None, ct);
        if (direct.Failure is not { } failure || failure.Status != ConfigReadStatus.AccessDenied) return direct;

        var reason = await diagnostics.DiagnoseAsync(app, failure, ct);
        if (reason == ConfigAccessReason.None) reason = ConfigAccessReason.Unknown;
        if (fallback && FallbackReasons.Contains(reason))
        {
            var tunneled = await ReadViaAsync(app, KuduTarget.ArmTunnel(app.Id), credential, environment, reason, ct);
            if (tunneled.Status is ConfigReadStatus.Read or ConfigReadStatus.NotFound) return tunneled;
            logger.LogDebug("ARM tunnel config read failed for {ResourceId}: {Message}", app.Id, tunneled.Issue?.Message);
        }

        return direct with
        {
            Reason = reason,
            Issue = new ScanIssue(app.Id, ScanProgress.ReadingConfigFiles, failure.IssueKind, AccessMessage(reason, failure, fallback))
        };
    }

    /// <summary>Only <see cref="ConfigAccessReason.MissingRole"/> (and Unknown) name the permission, so network problems are not reported as missing roles.</summary>
    public static string AccessMessage(ConfigAccessReason reason, KuduException failure, bool fallbackEnabled)
    {
        var fallbackHint = fallbackEnabled ? " The ARM fallback also failed." : " Or enable Inventory:UseArmKuduFallback.";
        return reason switch
        {
            ConfigAccessReason.ScmAccessRestricted =>
                "SCM site blocks this scanner by access restrictions. Allow the scanner's outbound IP or VNet on the SCM site." + fallbackHint,
            ConfigAccessReason.ScmUnreachable =>
                "SCM site unreachable from the scanner (private endpoint or DNS). Link the scanner VNet to the privatelink.azurewebsites.net DNS zone." + fallbackHint,
            ConfigAccessReason.AuthRejected =>
                $"{failure.HttpStatus}: SCM rejected the token although the role is present (for example Conditional Access)." + fallbackHint,
            _ => failure.Message
        };
    }

    private async Task<ConfigFilesResult> ReadViaAsync(
        AzureResourceInfo app, KuduTarget target, TokenCredential credential, string environment, ConfigAccessReason reason, CancellationToken ct)
    {
        var found = new List<string>();
        var settings = new List<RawSetting>();
        string? current = null;
        try
        {
            found.AddRange(await kudu.ListConfigFilesAsync(target, credential, ct));
            var selection = ConfigFileResolver.Resolve(found, environment, options.Value.EffectiveEnvironmentAliases);
            if (selection.ToRead.Count == 0)
            {
                return new(ConfigReadStatus.NotFound, null, reason, target.Route, found, [], selection.Skipped, []);
            }

            foreach (var file in selection.ToRead)
            {
                current = file;
                var source = file == selection.BaseFile ? ConfigSource.AppSettingsJson : ConfigSource.AppSettingsEnvironmentJson;
                await using var stream = await kudu.ReadFileAsync(target, file, credential, ct);
                settings.AddRange(flattener.FlattenJson(stream, file, source));
            }
            return new(ConfigReadStatus.Read, null, reason, target.Route, found, selection.ToRead, selection.Skipped, settings);
        }
        catch (KuduException ex) when (ex.Status == ConfigReadStatus.NotFound)
        {
            return new(ConfigReadStatus.NotFound, null, reason, target.Route, found, [], [], []);
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or InvalidDataException)
        {
            // Parser messages can echo content, so only the file name is reported.
            return new(ConfigReadStatus.Error, new ScanIssue(app.Id, ScanProgress.ReadingConfigFiles, ScanIssueKind.Unexpected, $"Malformed JSON in {current}."),
                reason, ConfigFilesRoute.None, found, [], [], []);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var (kind, message) = ScanErrors.Classify(ex, AzureRoleRequirements.ConfigFiles.Hint);
            var status = ex is KuduException k ? k.Status : kind == ScanIssueKind.AccessDenied ? ConfigReadStatus.AccessDenied : ConfigReadStatus.Error;
            return new(status, new ScanIssue(app.Id, ScanProgress.ReadingConfigFiles, kind, message),
                reason, ConfigFilesRoute.None, found, [], [], []) { Failure = ex as KuduException };
        }
    }

    private List<ConfigEntry> Analyze(string appId, string? slotName, string? environment, List<RawSetting> raw) =>
        analyzer.Analyze(appId, slotName, environment, raw);

    private static async Task<(HashSet<string> AppSettings, HashSet<string> ConnectionStrings)> GetStickyNamesAsync(
        ArmClient client, string siteId, CancellationToken ct)
    {
        try
        {
            var names = (await client.GetWebSiteResource(new ResourceIdentifier(siteId)).GetSlotConfigNamesResource().GetAsync(ct)).Value.Data;
            return (new HashSet<string>(names.AppSettingNames ?? [], StringComparer.OrdinalIgnoreCase),
                    new HashSet<string>(names.ConnectionStringNames ?? [], StringComparer.OrdinalIgnoreCase));
        }
        // Sticky names are optional metadata. For Reader-only callers ARM masks the response (id: null), which the
        // SDK fails to deserialize with ArgumentNullException; that must not block the settings read itself.
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return ([], []);
        }
    }

    private static string? ScmHostOf(IList<HostNameSslState>? states) =>
        states?.FirstOrDefault(s => s.HostType == AppServiceHostType.Repository)?.Name;
}
