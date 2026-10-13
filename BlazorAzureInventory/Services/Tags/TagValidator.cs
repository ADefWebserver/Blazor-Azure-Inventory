using BlazorAzureInventory.Models.Tags;
using BlazorAzureInventory.Services.Analysis;

namespace BlazorAzureInventory.Services.Tags;

/// <summary>Validates tag names, values, and counts against ARM tag limits. Does no I/O.</summary>
public sealed class TagValidator(SecretClassifier secrets)
{
    public const int MaxTagsPerResource = 50;
    public const int MaxNameLength = 512;
    public const int MaxValueLength = 256;

    private static readonly char[] ForbiddenNameChars = ['<', '>', '%', '&', '\\', '?', '/'];
    private static readonly string[] ReservedPrefixes = ["microsoft", "azure", "windows"];

    /// <summary>Per ARM type rules. Add new resource types here.</summary>
    public static readonly IReadOnlyDictionary<string, TypeRule> TypeRules = new Dictionary<string, TypeRule>(StringComparer.OrdinalIgnoreCase)
    {
        ["microsoft.storage/storageaccounts"] = new(MaxNameLength: 128),
        ["microsoft.storage/storageaccounts/blobservices/containers"] = new(Taggable: false,
            Reason: "Blob containers do not support Azure tags (they use container metadata instead)."),
        ["blobcontainer"] = new(Taggable: false,
            Reason: "Blob containers do not support Azure tags (they use container metadata instead)."),
        ["microsoft.storage/storageaccounts/queueservices/queues"] = new(Taggable: false,
            Reason: "Storage queues do not support Azure tags (they use queue metadata instead)."),
    };

    public sealed record TypeRule(int MaxNameLength = TagValidator.MaxNameLength, bool Taggable = true, string? Reason = null);

    public static TypeRule RuleFor(string? armType) =>
        armType is not null && TypeRules.TryGetValue(armType, out var rule) ? rule : new TypeRule();

    public TagValidationResult ValidateName(string? name, string? armType)
    {
        var errors = new List<TagValidationError>();
        var warnings = new List<TagValidationError>();
        var rule = RuleFor(armType);
        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add(new(name, "Tag name is required."));
            return new(errors, warnings);
        }

        if (name != name.Trim()) warnings.Add(new(name, "Leading or trailing spaces will be trimmed."));
        var trimmed = name.Trim();
        if (trimmed.Length > rule.MaxNameLength)
            errors.Add(new(name, $"Tag name must be {rule.MaxNameLength} characters or fewer."));
        if (trimmed.IndexOfAny(ForbiddenNameChars) >= 0)
            errors.Add(new(name, "Tag name cannot contain < > % & \\ ? /"));
        if (ReservedPrefixes.Any(p => trimmed.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            errors.Add(new(name, "Tag names cannot start with 'microsoft', 'azure', or 'windows'."));
        if (trimmed.StartsWith("hidden-", StringComparison.OrdinalIgnoreCase))
            warnings.Add(new(name, "Tags starting with 'hidden-' are hidden in the Azure portal."));
        return new(errors, warnings);
    }

    public TagValidationResult ValidateValue(string name, string? value)
    {
        var errors = new List<TagValidationError>();
        var warnings = new List<TagValidationError>();
        value ??= "";
        if (value.Length > MaxValueLength) errors.Add(new(name, $"Tag value must be {MaxValueLength} characters or fewer."));
        if (value.Length > 0 && value != value.Trim()) warnings.Add(new(name, "Leading or trailing spaces in the value will be trimmed."));
        if (IsSecretLike(name, value))
            warnings.Add(new(name, "This value looks like a secret. Tags are visible to anyone with Reader access."));
        return new(errors, warnings);
    }

    /// <summary>
    /// Validates a complete tag set (the result after applying changes). Tags that are unchanged from
    /// <paramref name="existing"/> skip name and value rules, because Azure itself creates tags such as
    /// <c>hidden-link: /app-insights-resource-id</c> that would otherwise block every edit.
    /// </summary>
    public TagValidationResult ValidateSet(IEnumerable<KeyValuePair<string, string>> tags, string? armType,
        IReadOnlyDictionary<string, string>? existing = null)
    {
        var errors = new List<TagValidationError>();
        var warnings = new List<TagValidationError>();
        var rule = RuleFor(armType);
        if (!rule.Taggable)
        {
            errors.Add(new(null, rule.Reason ?? "This resource type does not support tags."));
            return new(errors, warnings);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var (name, value) in tags)
        {
            count++;
            if (!seen.Add(name.Trim())) errors.Add(new(name, $"Duplicate tag name '{name}' (names are case-insensitive)."));
            if (IsUnchanged(existing, name, value)) continue;
            var n = ValidateName(name, armType);
            var v = ValidateValue(name, value);
            errors.AddRange(n.Errors);
            errors.AddRange(v.Errors);
            warnings.AddRange(n.Warnings);
            warnings.AddRange(v.Warnings);
        }

        if (count > MaxTagsPerResource) errors.Add(new(null, $"A resource can have at most {MaxTagsPerResource} tags ({count} requested)."));
        return new(errors, warnings);
    }

    private static bool IsUnchanged(IReadOnlyDictionary<string, string>? existing, string name, string value) =>
        existing is not null && existing.Any(kv => string.Equals(kv.Key, name, StringComparison.Ordinal) && kv.Value == value);

    /// <summary>Validates only the names and values touched by a set of changes.</summary>
    public TagValidationResult ValidateChanges(IEnumerable<TagChange> changes, string? armType)
    {
        var errors = new List<TagValidationError>();
        var warnings = new List<TagValidationError>();
        var rule = RuleFor(armType);
        if (!rule.Taggable) return new([new(null, rule.Reason ?? "This resource type does not support tags.")], []);

        foreach (var c in changes.Where(c => c.Kind != TagChangeKind.Remove))
        {
            var n = ValidateName(c.Name, armType);
            var v = ValidateValue(c.Name, c.NewValue);
            errors.AddRange(n.Errors);
            errors.AddRange(v.Errors);
            warnings.AddRange(n.Warnings);
            warnings.AddRange(v.Warnings);
        }
        return new(errors, warnings);
    }

    public bool IsSecretLike(string name, string? value) => secrets.IsSecretLike(name, value);
}
