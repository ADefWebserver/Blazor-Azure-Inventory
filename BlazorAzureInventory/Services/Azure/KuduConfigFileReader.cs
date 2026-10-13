using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Core;
using BlazorAzureInventory.Models.Configuration;
using BlazorAzureInventory.Models.Inventory;
using BlazorAzureInventory.Models.Options;
using Microsoft.Extensions.Options;

namespace BlazorAzureInventory.Services.Azure;

public sealed class KuduException(ConfigReadStatus status, ScanIssueKind issueKind, string message, int? httpStatus = null, bool isNetworkFailure = false)
    : Exception(message)
{
    public ConfigReadStatus Status { get; } = status;
    public ScanIssueKind IssueKind { get; } = issueKind;
    public int? HttpStatus { get; } = httpStatus;
    public bool IsNetworkFailure { get; } = isNetworkFailure;
}

/// <summary>Where Kudu VFS requests go: the SCM host directly, or the ARM <c>extensions</c> tunnel for the site.</summary>
public sealed record KuduTarget(ConfigFilesRoute Route, string Value)
{
    public static KuduTarget Direct(string scmHost) => new(ConfigFilesRoute.Direct, scmHost);
    public static KuduTarget ArmTunnel(string siteId) => new(ConfigFilesRoute.ArmTunnel, siteId);
}

/// <summary>
/// Reads <c>appsettings*.json</c> through the Kudu VFS API with the user's ARM-audience token.
/// Hosts are validated, redirects are disabled, and size and time are bounded.
/// </summary>
public sealed partial class KuduConfigFileReader(HttpClient http, ScmHostValidator validator, IOptions<InventoryOptions> options)
{
    public const string ArmEndpoint = "https://management.azure.com";
    private const string ArmTunnelApiVersion = "2023-12-01";
    private const string VfsRoot = "api/vfs/site/wwwroot/";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly string[] ArmScope = ["https://management.azure.com/.default"];

    [GeneratedRegex(@"^/subscriptions/[0-9a-f-]{36}/resourcegroups/[A-Za-z0-9._()-]{1,90}/providers/microsoft\.web/sites/[A-Za-z0-9-]{1,60}(/slots/[A-Za-z0-9-]{1,60})?$", RegexOptions.IgnoreCase)]
    private static partial Regex SiteIdRegex();

    public Task<IReadOnlyList<string>> ListConfigFilesAsync(string scmHost, TokenCredential credential, CancellationToken ct) =>
        ListConfigFilesAsync(KuduTarget.Direct(scmHost), credential, ct);

    /// <summary>Lists config files in <c>site/wwwroot/</c> that match <see cref="InventoryOptions.ConfigFilePatterns"/>.</summary>
    public async Task<IReadOnlyList<string>> ListConfigFilesAsync(KuduTarget target, TokenCredential credential, CancellationToken ct)
    {
        using var response = await SendAsync(target, "", credential, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var patterns = options.Value.EffectiveConfigFilePatterns.Select(GlobToRegex).ToList();
        var names = new List<string>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return names;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
            var mime = item.TryGetProperty("mime", out var m) ? m.GetString() : null;
            if (name is null || mime == "inode/directory") continue;
            if (patterns.Any(p => p.IsMatch(name))) names.Add(name);
        }
        return names;
    }

    public Task<MemoryStream> ReadFileAsync(string scmHost, string fileName, TokenCredential credential, CancellationToken ct) =>
        ReadFileAsync(KuduTarget.Direct(scmHost), fileName, credential, ct);

    /// <summary>Returns the file content as a transient stream. The caller must dispose it immediately after flattening.</summary>
    public async Task<MemoryStream> ReadFileAsync(KuduTarget target, string fileName, TokenCredential credential, CancellationToken ct)
    {
        var patterns = options.Value.EffectiveConfigFilePatterns.Select(GlobToRegex);
        if (!patterns.Any(p => p.IsMatch(fileName)))
        {
            throw new KuduException(ConfigReadStatus.Error, ScanIssueKind.Unexpected, "File name is not an allowed config file pattern.");
        }

        var max = options.Value.MaxConfigFileBytes;
        using var response = await SendAsync(target, Uri.EscapeDataString(fileName), credential, ct);
        if (response.Content.Headers.ContentLength > max)
        {
            throw new KuduException(ConfigReadStatus.Error, ScanIssueKind.Unexpected, $"{fileName} exceeds the {max} byte limit.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await source.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > max)
            {
                await buffer.DisposeAsync();
                throw new KuduException(ConfigReadStatus.Error, ScanIssueKind.Unexpected, $"{fileName} exceeds the {max} byte limit.");
            }
            buffer.Write(chunk, 0, read);
        }
        buffer.Position = 0;
        return buffer;
    }

    /// <summary>The VFS URI for a path under <c>site/wwwroot/</c>, or null when the target fails validation.</summary>
    public Uri? BuildUri(KuduTarget target, string escapedPath)
    {
        if (target.Route == ConfigFilesRoute.ArmTunnel)
        {
            return SiteIdRegex().IsMatch(target.Value)
                ? new Uri($"{ArmEndpoint}{target.Value}/extensions/{VfsRoot}{escapedPath}?api-version={ArmTunnelApiVersion}")
                : null;
        }
        return validator.TryCreateBaseUri(target.Value, out var baseUri) ? new Uri(baseUri, VfsRoot + escapedPath) : null;
    }

    private async Task<HttpResponseMessage> SendAsync(KuduTarget target, string escapedPath, TokenCredential credential, CancellationToken ct)
    {
        var uri = BuildUri(target, escapedPath)
            ?? throw new KuduException(ConfigReadStatus.Error, ScanIssueKind.Unexpected,
                target.Route == ConfigFilesRoute.ArmTunnel ? "Site ID failed validation." : "SCM host failed validation.");
        var token = await credential.GetTokenAsync(new TokenRequestContext(ArmScope), ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new KuduException(ConfigReadStatus.Error, ScanIssueKind.Timeout, "Kudu request timed out after 15 s.");
        }
        catch (HttpRequestException ex)
        {
            throw new KuduException(ConfigReadStatus.AccessDenied, ScanIssueKind.AccessDenied,
                $"SCM site unreachable ({ex.HttpRequestError}). It may be restricted to a private endpoint.", isNetworkFailure: true);
        }

        if (response.IsSuccessStatusCode) return response;

        var status = (int)response.StatusCode;
        response.Dispose();
        throw status switch
        {
            401 or 403 => new KuduException(ConfigReadStatus.AccessDenied, ScanIssueKind.AccessDenied,
                $"{status}: SCM access denied. {AzureRoleRequirements.ConfigFiles.Hint}", status),
            404 => new KuduException(ConfigReadStatus.NotFound, ScanIssueKind.NotFound, "404: File or folder not found.", status),
            429 => new KuduException(ConfigReadStatus.Error, ScanIssueKind.Throttled, "429: SCM requests throttled.", status),
            >= 300 and < 400 => new KuduException(ConfigReadStatus.Error, ScanIssueKind.Unexpected, $"{status}: Redirects are not followed.", status),
            _ => new KuduException(ConfigReadStatus.Error, ScanIssueKind.Unexpected, $"{status}: {(HttpStatusCode)status}", status)
        };
    }

    private static Regex GlobToRegex(string glob) =>
        new("^" + Regex.Escape(glob).Replace("\\*", "[^/\\\\]*") + "$", RegexOptions.IgnoreCase);
}
