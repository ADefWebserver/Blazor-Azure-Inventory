using System.Net;
using BlazorAzureInventory.Models.Options;
using Microsoft.Extensions.Options;

namespace BlazorAzureInventory.Services.Azure;

/// <summary>
/// SSRF guard for Kudu. The SCM host must come from ARM, use https, be a DNS name (not an IP literal),
/// contain an <c>scm</c> label, and end with an allowlisted suffix.
/// </summary>
public sealed class ScmHostValidator(IOptions<InventoryOptions> options)
{
    public bool TryCreateBaseUri(string? scmHost, out Uri baseUri)
    {
        baseUri = null!;
        if (string.IsNullOrWhiteSpace(scmHost)) return false;

        var host = scmHost.Trim().TrimEnd('.').ToLowerInvariant();
        if (Uri.CheckHostName(host) != UriHostNameType.Dns || IPAddress.TryParse(host, out _)) return false;

        var labels = host.Split('.');
        if (!labels.Contains("scm")) return false;

        var suffixOk = options.Value.EffectiveScmHostSuffixes.Any(s =>
        {
            var suffix = s.StartsWith('.') ? s : "." + s;
            return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && host.Length > suffix.Length;
        });
        if (!suffixOk) return false;

        return Uri.TryCreate($"https://{host}/", UriKind.Absolute, out baseUri!) && IsAllowed(baseUri);
    }

    public bool IsAllowed(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo)
        && uri.IsDefaultPort
        && uri.HostNameType == UriHostNameType.Dns;
}
