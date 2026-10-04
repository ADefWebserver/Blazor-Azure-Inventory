using System.Data.Common;
using System.Text.RegularExpressions;
using BlazorAzureInventory.Models.Configuration;

namespace BlazorAzureInventory.Services.Analysis;

/// <summary>
/// Extracts identifiers (hosts, account, database, container, vault and secret names) from a transient
/// setting value. The raw value is never returned or stored.
/// </summary>
public sealed partial class ConnectionClassifier(CloudEndpointSuffixes suffixes)
{
    private static readonly string[] SqlServerKeys = ["Server", "Data Source", "Address", "Addr", "Network Address"];
    private static readonly string[] SqlDatabaseKeys = ["Initial Catalog", "Database"];
    private static readonly string[] StorageEndpointKeys = ["BlobEndpoint", "QueueEndpoint", "TableEndpoint", "FileEndpoint"];
    private static readonly string[] IdentityStorageUriSuffixes = ["blobServiceUri", "queueServiceUri", "tableServiceUri", "serviceUri"];

    [GeneratedRegex(@"^@Microsoft\.KeyVault\((?<body>.*)\)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex KeyVaultReferenceRegex();

    [GeneratedRegex(@"^(?<host>[A-Za-z0-9.-]+):(?<port>\d{1,5})$")]
    private static partial Regex HostPortRegex();

    [GeneratedRegex(@"(?:https?|sb)://[^\s;,'""<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex UriRegex();

    [GeneratedRegex(@"^(?<acct>[a-z0-9]{3,24})\.(?<svc>blob|queue|table|file|dfs)\.", RegexOptions.IgnoreCase)]
    private static partial Regex StorageHostRegex();

    [GeneratedRegex(@"^[a-z0-9](?!.*--)[a-z0-9-]{1,61}[a-z0-9]$")]
    private static partial Regex BlobContainerNameRegex();

    [GeneratedRegex(@"^[a-z0-9]{3,24}$")]
    private static partial Regex StorageAccountNameRegex();

    public IReadOnlyList<ConnectionReference> Classify(string key, string? transientValue, ConfigSource source)
    {
        // Container App secretRef entries only carry a value when the secret is Key Vault-backed (a reference, not a secret).
        if (string.IsNullOrWhiteSpace(transientValue))
        {
            return [];
        }

        var value = transientValue.Trim();

        if (TryKeyVaultReference(value, out var kvRef)) return [kvRef];
        if (TryRedis(value, out var redis)) return [redis];

        var results = new List<ConnectionReference>();

        if (!IsAbsoluteUri(value) && TryParseConnectionString(value, out var builder))
        {
            results.AddRange(ClassifyConnectionString(builder));
            if (results.Count > 0) return results;
        }

        foreach (Match match in UriRegex().Matches(value))
        {
            if (Uri.TryCreate(match.Value, UriKind.Absolute, out var uri))
            {
                if (TryStorageUri(uri, out var storage)) results.Add(storage);
                else if (TryKeyVaultUri(uri, out var vault)) results.Add(vault);
            }
        }

        if (TryIdentityBased(key, value, out var identity)) results.Add(identity);

        if (results.Count == 0 && TryContainerHint(key, value, out var hint)) results.Add(hint);

        return Dedupe(results);
    }

    public static bool IsIdentityBasedKey(string key) =>
        EndsWithSegment(key, "accountName") || EndsWithSegment(key, "fullyQualifiedNamespace")
        || IdentityStorageUriSuffixes.Any(s => EndsWithSegment(key, s));

    /// <summary>Parses a container image or <c>DOCKER|</c> LinuxFxVersion into registry host and repository (no tag).</summary>
    public static ImageReference? ParseImage(string? image)
    {
        if (string.IsNullOrWhiteSpace(image)) return null;
        var s = image.Trim();
        if (s.StartsWith("DOCKER|", StringComparison.OrdinalIgnoreCase)) s = s[7..];
        else if (s.Contains('|')) return null;

        var slash = s.IndexOf('/');
        if (slash <= 0) return null;
        var registry = s[..slash].ToLowerInvariant();
        if (!registry.Contains('.') && !registry.Contains(':') && registry != "localhost") return null;

        var repo = s[(slash + 1)..];
        var at = repo.IndexOf('@');
        if (at >= 0) repo = repo[..at];
        var colon = repo.LastIndexOf(':');
        if (colon >= 0) repo = repo[..colon];
        if (repo.Length == 0) return null;

        return new ImageReference(registry.Split(':')[0], repo.ToLowerInvariant(), null);
    }

    private bool TryKeyVaultReference(string value, out ConnectionReference reference)
    {
        reference = null!;
        var m = KeyVaultReferenceRegex().Match(value);
        if (!m.Success) return false;

        var body = m.Groups["body"].Value;
        string? vault = null, secret = null, host = null;

        var parts = body.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var name = part[..eq].Trim();
            var val = part[(eq + 1)..].Trim();
            if (name.Equals("SecretUri", StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate(val, UriKind.Absolute, out var uri))
            {
                host = uri.Host.ToLowerInvariant();
                vault = host.Split('.')[0];
                var segments = uri.AbsolutePath.Trim('/').Split('/');
                if (segments.Length >= 2 && segments[0].Equals("secrets", StringComparison.OrdinalIgnoreCase))
                {
                    secret = segments[1];
                }
            }
            else if (name.Equals("VaultName", StringComparison.OrdinalIgnoreCase)) vault = val.ToLowerInvariant();
            else if (name.Equals("SecretName", StringComparison.OrdinalIgnoreCase)) secret = val;
        }

        if (vault is null) return false;
        reference = new ConnectionReference(ConnectionTargetKind.KeyVault, host, vault, null, null, secret,
            false, Confidence.High, null);
        return true;
    }

    private bool TryRedis(string value, out ConnectionReference reference)
    {
        reference = null!;
        var first = value.Split(',')[0].Trim();
        var m = HostPortRegex().Match(first);
        if (!m.Success) return false;
        var host = NormalizeHost(m.Groups["host"].Value);
        if (!CloudEndpointSuffixes.EndsWithAny(host, suffixes.Redis)) return false;

        reference = new ConnectionReference(ConnectionTargetKind.RedisCache, host, host.Split('.')[0], null, null, null,
            false, Confidence.High, null);
        return true;
    }

    private IEnumerable<ConnectionReference> ClassifyConnectionString(DbConnectionStringBuilder b)
    {
        // SQL
        var server = FirstValue(b, SqlServerKeys);
        if (!string.IsNullOrWhiteSpace(server))
        {
            var host = NormalizeSqlHost(server);
            if (host.Length > 0)
            {
                var db = FirstValue(b, SqlDatabaseKeys);
                var isAzure = CloudEndpointSuffixes.EndsWithAny(host, suffixes.Sql);
                yield return new ConnectionReference(
                    string.IsNullOrWhiteSpace(db) ? ConnectionTargetKind.SqlServer : ConnectionTargetKind.SqlDatabase,
                    host, host.Split('.')[0], string.IsNullOrWhiteSpace(db) ? null : db.Trim(), null, null,
                    false, isAzure ? Confidence.High : Confidence.Medium, null);
                yield break;
            }
        }

        // Storage emulator
        if (FirstValue(b, ["UseDevelopmentStorage"]) is { } dev && dev.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            yield return new ConnectionReference(ConnectionTargetKind.External, null, null, null, null, null,
                true, Confidence.High, null);
            yield break;
        }

        // Storage connection string
        var account = FirstValue(b, ["AccountName"]);
        string? storageHost = null;
        foreach (var endpointKey in StorageEndpointKeys)
        {
            if (FirstValue(b, [endpointKey]) is { } ep && Uri.TryCreate(ep, UriKind.Absolute, out var epUri))
            {
                storageHost ??= epUri.Host.ToLowerInvariant();
                account ??= StorageHostRegex().Match(epUri.Host) is { Success: true } sm
                    ? sm.Groups["acct"].Value
                    : epUri.Host.Split('.')[0];
            }
        }
        if (!string.IsNullOrWhiteSpace(account))
        {
            yield return new ConnectionReference(ConnectionTargetKind.StorageAccount, storageHost,
                account.Trim().ToLowerInvariant(), null, null, null, false, Confidence.High, null);
            yield break;
        }

        // Cosmos DB
        if (FirstValue(b, ["AccountEndpoint"]) is { } cosmosEp && Uri.TryCreate(cosmosEp, UriKind.Absolute, out var cosmosUri))
        {
            var host = cosmosUri.Host.ToLowerInvariant();
            var isLocal = IsLocalHost(host);
            var isAzure = CloudEndpointSuffixes.EndsWithAny(host, suffixes.Cosmos);
            var db = FirstValue(b, ["Database"]);
            yield return new ConnectionReference(ConnectionTargetKind.CosmosAccount, host, host.Split('.')[0],
                string.IsNullOrWhiteSpace(db) ? null : db, null, null, isLocal,
                isAzure || isLocal ? Confidence.High : Confidence.Medium, null);
            yield break;
        }

        // Service Bus
        if (FirstValue(b, ["Endpoint"]) is { } sbEp
            && Uri.TryCreate(sbEp, UriKind.Absolute, out var sbUri)
            && sbUri.Scheme.Equals("sb", StringComparison.OrdinalIgnoreCase))
        {
            var host = sbUri.Host.ToLowerInvariant();
            yield return new ConnectionReference(ConnectionTargetKind.ServiceBusNamespace, host, host.Split('.')[0],
                null, null, null, false,
                CloudEndpointSuffixes.EndsWithAny(host, suffixes.ServiceBus) ? Confidence.High : Confidence.Medium, null);
        }
    }

    private bool TryStorageUri(Uri uri, out ConnectionReference reference)
    {
        reference = null!;
        var host = uri.Host.ToLowerInvariant();
        var m = StorageHostRegex().Match(host);
        if (!m.Success || !CloudEndpointSuffixes.EndsWithAny(host, suffixes.Storage)) return false;

        var account = m.Groups["acct"].Value.ToLowerInvariant();
        var service = m.Groups["svc"].Value.ToLowerInvariant();
        string? container = null;
        if (service is "blob" or "dfs")
        {
            var first = uri.AbsolutePath.Trim('/').Split('/')[0];
            if (BlobContainerNameRegex().IsMatch(first) || first is "$web" or "$root") container = first;
        }

        reference = new ConnectionReference(
            container is null ? ConnectionTargetKind.StorageAccount : ConnectionTargetKind.BlobContainer,
            host, account, null, container, null, false, Confidence.High, null);
        return true;
    }

    private bool TryKeyVaultUri(Uri uri, out ConnectionReference reference)
    {
        reference = null!;
        var host = uri.Host.ToLowerInvariant();
        if (!CloudEndpointSuffixes.EndsWithAny(host, suffixes.KeyVault)) return false;

        var segments = uri.AbsolutePath.Trim('/').Split('/');
        var secret = segments.Length >= 2 && segments[0].Equals("secrets", StringComparison.OrdinalIgnoreCase)
            ? segments[1] : null;
        reference = new ConnectionReference(ConnectionTargetKind.KeyVault, host, host.Split('.')[0], null, null, secret,
            false, Confidence.High, null);
        return true;
    }

    private bool TryIdentityBased(string key, string value, out ConnectionReference reference)
    {
        reference = null!;
        if (EndsWithSegment(key, "accountName"))
        {
            var account = value.ToLowerInvariant();
            if (!StorageAccountNameRegex().IsMatch(account)) return false;
            reference = new ConnectionReference(ConnectionTargetKind.StorageAccount, null, account, null, null, null,
                false, Confidence.High, null);
            return true;
        }

        if (EndsWithSegment(key, "fullyQualifiedNamespace"))
        {
            var host = NormalizeHost(value.Replace("sb://", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/'));
            if (!CloudEndpointSuffixes.EndsWithAny(host, suffixes.ServiceBus)) return false;
            reference = new ConnectionReference(ConnectionTargetKind.ServiceBusNamespace, host, host.Split('.')[0],
                null, null, null, false, Confidence.High, null);
            return true;
        }

        return false;
    }

    private static bool TryContainerHint(string key, string value, out ConnectionReference reference)
    {
        reference = null!;
        if (!key.Contains("Container", StringComparison.OrdinalIgnoreCase) || !BlobContainerNameRegex().IsMatch(value))
        {
            return false;
        }
        reference = new ConnectionReference(ConnectionTargetKind.BlobContainer, null, null, null, value, null,
            false, Confidence.Low, null);
        return true;
    }

    private static bool TryParseConnectionString(string value, out DbConnectionStringBuilder builder)
    {
        builder = new DbConnectionStringBuilder();
        if (!value.Contains('=')) return false;
        try
        {
            builder.ConnectionString = value;
            return builder.Count > 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string? FirstValue(DbConnectionStringBuilder b, IEnumerable<string> keys)
    {
        foreach (var k in keys)
        {
            if (b.TryGetValue(k, out var v) && v is not null && v.ToString() is { Length: > 0 } s)
            {
                return s;
            }
        }
        return null;
    }

    private static string NormalizeSqlHost(string server)
    {
        var host = server.Trim();
        var colon = host.IndexOf(':');
        if (colon >= 0 && colon < 4) host = host[(colon + 1)..]; // tcp:, np:, lpc:
        var comma = host.IndexOf(',');
        if (comma >= 0) host = host[..comma];
        var backslash = host.IndexOf('\\');
        if (backslash >= 0) host = host[..backslash];
        return host.Trim().TrimEnd('.').ToLowerInvariant();
    }

    private static string NormalizeHost(string host)
    {
        var h = host.Trim().ToLowerInvariant();
        var colon = h.LastIndexOf(':');
        if (colon > 0 && h.IndexOf(':') == colon) h = h[..colon];
        return h.TrimEnd('.');
    }

    private static bool IsAbsoluteUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps || u.Scheme == "sb");

    private static bool IsLocalHost(string host) =>
        host is "localhost" or "127.0.0.1" or "::1" or "[::1]" or "host.docker.internal";

    private static bool EndsWithSegment(string key, string segment) =>
        key.EndsWith(":" + segment, StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("__" + segment, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<ConnectionReference> Dedupe(List<ConnectionReference> refs) =>
        refs.GroupBy(r => (r.Kind, r.AccountOrServerName?.ToLowerInvariant(), r.DatabaseName, r.ContainerName, r.SecretName))
            .Select(g => g.OrderByDescending(r => r.Host is not null).First())
            .ToList();
}
