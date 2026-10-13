using BlazorAzureInventory.Models.Options;
using Microsoft.Extensions.Options;

namespace BlazorAzureInventory.Services.Analysis;

/// <summary>Known non-Azure services (SendGrid by default) matched by host suffix or setting-key keyword.</summary>
public sealed class ExternalServiceCatalog
{
    public static readonly ExternalServiceCatalog Default = new([ExternalServiceDefinition.SendGrid]);

    public ExternalServiceCatalog(IOptions<InventoryOptions> options) : this(options.Value.EffectiveExternalServices) { }

    public ExternalServiceCatalog(IEnumerable<ExternalServiceDefinition> services)
    {
        Services = services.ToList();
        _byId = Services.GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    private readonly Dictionary<string, ExternalServiceDefinition> _byId;

    public IReadOnlyList<ExternalServiceDefinition> Services { get; }

    public ExternalServiceDefinition? ById(string? id) => id is not null ? _byId.GetValueOrDefault(id) : null;

    /// <summary>Label-boundary match: the host equals a suffix or ends with "." + suffix.</summary>
    public ExternalServiceDefinition? MatchHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        var h = host.Trim().TrimEnd('.').ToLowerInvariant();
        return Services.FirstOrDefault(s => s.HostSuffixes.Any(suffix =>
        {
            var x = suffix.Trim().TrimStart('.').ToLowerInvariant();
            return x.Length > 0 && (h == x || h.EndsWith("." + x, StringComparison.Ordinal));
        }));
    }

    public ExternalServiceDefinition? MatchKey(string keySegment) =>
        Services.FirstOrDefault(s => s.KeyKeywords.Any(k => k.Length > 0 && keySegment.Contains(k, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Configuration errors, empty when valid. Azure suffixes are rejected so Azure resources are never shadowed.</summary>
    public static IReadOnlyList<string> Validate(IEnumerable<ExternalServiceDefinition> services, IEnumerable<string> azureSuffixes)
    {
        var errors = new List<string>();
        var azure = azureSuffixes.Select(s => s.TrimStart('.').ToLowerInvariant()).ToList();
        var list = services.ToList();
        foreach (var dup in list.GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            errors.Add($"Duplicate external service id '{dup.Key}'.");
        foreach (var s in list)
        {
            foreach (var suffix in s.HostSuffixes)
            {
                var x = suffix.Trim().TrimStart('.').ToLowerInvariant();
                if (!x.Contains('.')) errors.Add($"External service '{s.Id}': host suffix '{suffix}' must contain a dot.");
                else if (azure.Any(a => x == a || x.EndsWith("." + a, StringComparison.Ordinal)))
                    errors.Add($"External service '{s.Id}': host suffix '{suffix}' is an Azure suffix.");
            }
        }
        return errors;
    }
}

/// <summary>Validates the external-service entries, which data-annotation validation does not reach inside arrays.</summary>
public sealed class ExternalServiceOptionsValidator : IValidateOptions<InventoryOptions>
{
    public ValidateOptionsResult Validate(string? name, InventoryOptions options)
    {
        var errors = new List<string>();
        foreach (var service in options.ExternalServices)
        {
            var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
            if (!System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
                    service, new System.ComponentModel.DataAnnotations.ValidationContext(service), results, validateAllProperties: true))
            {
                errors.AddRange(results.Select(r => $"External service '{service.Id}': {r.ErrorMessage}"));
            }
        }
        errors.AddRange(ExternalServiceCatalog.Validate(options.ExternalServices, new CloudEndpointSuffixes(options.CloudEndpointSuffixes).AllAzure));
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
