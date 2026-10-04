using System.Net;
using System.Net.Sockets;
using BlazorAzureInventory.Models.Options;
using Microsoft.Extensions.Options;

namespace BlazorAzureInventory.Services.AI;

/// <summary>
/// SSRF protection for user-entered AI endpoints: https only, Azure AI host suffix allowlist,
/// and no loopback, link-local, or private addresses after DNS resolution.
/// </summary>
public sealed class EndpointGuard
{
    private readonly IReadOnlyList<string> _suffixes;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;

    public EndpointGuard(IOptions<AIOptions> options)
        : this(options, Dns.GetHostAddressesAsync) { }

    public EndpointGuard(IOptions<AIOptions> options, Func<string, CancellationToken, Task<IPAddress[]>> resolve)
    {
        _suffixes = options.Value.EffectiveAzureAIHostSuffixes;
        _resolve = resolve;
    }

    public bool TryValidateFormat(string? endpoint, out Uri uri, out string? error)
    {
        uri = null!;
        error = "Endpoint must use https and be an Azure OpenAI or Foundry host.";
        if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(parsed.UserInfo) || !parsed.IsDefaultPort) return false;
        if (parsed.HostNameType != UriHostNameType.Dns) return false;

        var host = parsed.Host.TrimEnd('.');
        var allowed = _suffixes.Any(s =>
        {
            var suffix = s.StartsWith('.') ? s : "." + s;
            return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && host.Length > suffix.Length;
        });
        if (!allowed) return false;

        uri = new Uri($"https://{host.ToLowerInvariant()}/");
        error = null;
        return true;
    }

    /// <summary>Returns null when the endpoint is allowed, otherwise a user-safe error message.</summary>
    public async Task<string?> ValidateAsync(string? endpoint, CancellationToken ct = default)
    {
        if (!TryValidateFormat(endpoint, out var uri, out var error)) return error;

        IPAddress[] addresses;
        try
        {
            addresses = await _resolve(uri.Host, ct);
        }
        catch (SocketException)
        {
            return "Endpoint host could not be resolved.";
        }

        if (addresses.Length == 0) return "Endpoint host could not be resolved.";
        return addresses.Any(IsNonPublic) ? "Endpoint resolves to a private or local network address." : null;
    }

    public static bool IsNonPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10
                || b[0] == 0
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (b[0] & 0xFE) == 0xFC;
        }

        return true;
    }
}
