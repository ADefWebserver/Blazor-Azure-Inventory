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

public sealed class KuduException(ConfigReadStatus status, ScanIssueKind issueKind, string message) : Exception(message)
{
    public ConfigReadStatus Status { get; } = status;
    public ScanIssueKind IssueKind { get; } = issueKind;
}

/// <summary>
/// Reads <c>appsettings*.json</c> through the Kudu VFS API with the user's ARM-audience token.
/// Hosts are validated, redirects are disabled, and size and time are bounded.
/// </summary>
public sealed class KuduConfigFileReader(HttpClient http, ScmHostValidator validator, IOptions<InventoryOptions> options)
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly string[] ArmScope = ["https://management.azure.com/.default"];

    /// <summary>Lists config files in <c>site/wwwroot/</c> that match <see cref="InventoryOptions.ConfigFilePatterns"/>.</summary>
    public async Task<IReadOnlyList<string>> ListConfigFilesAsync(string scmHost, TokenCredential credential, CancellationToken ct)
    {
        using var response = await SendAsync(scmHost, "api/vfs/site/wwwroot/", credential, ct);
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

    /// <summary>Returns the file content as a transient stream. The caller must dispose it immediately after flattening.</summary>
    public async Task<MemoryStream> ReadFileAsync(string scmHost, string fileName, TokenCredential credential, CancellationToken ct)
    {
        var patterns = options.Value.EffectiveConfigFilePatterns.Select(GlobToRegex);
        if (!patterns.Any(p => p.IsMatch(fileName)))
        {
            throw new KuduException(ConfigReadStatus.Error, ScanIssueKind.Unexpected, "File name is not an allowed config file pattern.");
        }

        var max = options.Value.MaxConfigFileBytes;
        using var response = await SendAsync(scmHost, "api/vfs/site/wwwroot/" + Uri.EscapeDataString(fileName), credential, ct);
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

    private async Task<HttpResponseMessage> SendAsync(string scmHost, string relativePath, TokenCredential credential, CancellationToken ct)
    {
        if (!validator.TryCreateBaseUri(scmHost, out var baseUri))
        {
            throw new KuduException(ConfigReadStatus.Error, ScanIssueKind.Unexpected, "SCM host failed validation.");
        }

        var uri = new Uri(baseUri, relativePath);
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
                $"SCM site unreachable ({ex.HttpRequestError}). It may be restricted to a private endpoint.");
        }

        if (response.IsSuccessStatusCode) return response;

        var status = (int)response.StatusCode;
        response.Dispose();
        throw status switch
        {
            401 or 403 => new KuduException(ConfigReadStatus.AccessDenied, ScanIssueKind.AccessDenied,
                $"{status}: SCM access denied. Requires Website Contributor and SCM access."),
            404 => new KuduException(ConfigReadStatus.NotFound, ScanIssueKind.NotFound, "404: File or folder not found."),
            429 => new KuduException(ConfigReadStatus.Error, ScanIssueKind.Throttled, "429: SCM requests throttled."),
            >= 300 and < 400 => new KuduException(ConfigReadStatus.Error, ScanIssueKind.Unexpected, $"{status}: Redirects are not followed."),
            _ => new KuduException(ConfigReadStatus.Error, ScanIssueKind.Unexpected, $"{status}: {(HttpStatusCode)status}")
        };
    }

    private static Regex GlobToRegex(string glob) =>
        new("^" + Regex.Escape(glob).Replace("\\*", "[^/\\\\]*") + "$", RegexOptions.IgnoreCase);
}
