using BlazorAzureInventory.Models.Tags;

namespace BlazorAzureInventory.Services.Tags;

/// <summary>An editable tag row. <see cref="OriginalName"/> is empty for new rows.</summary>
public sealed class TagRow
{
    public string OriginalName { get; init; } = "";
    public string OriginalValue { get; init; } = "";
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public bool IsRemoved { get; set; }
    public bool IsEditing { get; set; }
    public bool IsGenerated { get; set; }
    public bool IsNew => OriginalName.Length == 0;
    public bool IsChanged => !IsNew && !IsRemoved && (Name != OriginalName || Value != OriginalValue);
}

/// <summary>Builds and applies tag diffs. Names are case-insensitive; values are case-sensitive.</summary>
public static class TagDiffBuilder
{
    public static List<TagRow> ToRows(IReadOnlyDictionary<string, string> tags) =>
        tags.OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
            .Select(t => new TagRow { OriginalName = t.Key, OriginalValue = t.Value, Name = t.Key, Value = t.Value })
            .ToList();

    /// <summary>The tag set the rows describe (removed rows excluded; names and values trimmed).</summary>
    public static Dictionary<string, string> Desired(IEnumerable<TagRow> rows)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Where(r => !r.IsRemoved && !string.IsNullOrWhiteSpace(r.Name)))
        {
            result[row.Name.Trim()] = (row.Value ?? "").Trim();
        }
        return result;
    }

    public static IReadOnlyList<TagChange> Build(IReadOnlyDictionary<string, string> baseline, IEnumerable<TagRow> rows) =>
        Diff(baseline, Desired(rows));

    /// <summary>
    /// Changes needed to turn <paramref name="baseline"/> into <paramref name="desired"/>. A rename becomes Remove plus Add;
    /// a case-only rename becomes a single Update that carries the new casing.
    /// </summary>
    public static IReadOnlyList<TagChange> Diff(IReadOnlyDictionary<string, string> baseline, IReadOnlyDictionary<string, string> desired)
    {
        var baseMap = new Dictionary<string, (string Name, string Value)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in baseline) baseMap[k] = (k, v);

        var changes = new List<TagChange>();
        foreach (var (name, value) in desired.OrderBy(d => d.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!baseMap.TryGetValue(name, out var old))
            {
                changes.Add(new TagChange(name, TagChangeKind.Add, null, value));
            }
            else if (old.Value != value || !string.Equals(old.Name, name, StringComparison.Ordinal))
            {
                changes.Add(new TagChange(name, TagChangeKind.Update, old.Value, value));
            }
        }

        var desiredNames = new HashSet<string>(desired.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in baseMap.Values.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!desiredNames.Contains(name)) changes.Add(new TagChange(name, TagChangeKind.Remove, value, null));
        }
        return changes;
    }

    /// <summary>Applies <paramref name="changes"/> to <paramref name="current"/>, keeping every untouched tag as it is.</summary>
    public static Dictionary<string, string> Apply(IReadOnlyDictionary<string, string> current, IEnumerable<TagChange> changes)
    {
        var result = new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase);
        foreach (var c in changes)
        {
            result.Remove(c.Name);
            if (c.Kind != TagChangeKind.Remove) result[c.Name] = c.NewValue ?? "";
        }
        return result;
    }

    /// <summary>
    /// Tags whose current Azure value differs from the baseline the changes were computed against. Untouched tags are ignored.
    /// </summary>
    public static IReadOnlyList<TagConflict> FindConflicts(
        IReadOnlyDictionary<string, string> baseline, IReadOnlyDictionary<string, string> current, IEnumerable<TagChange> changes)
    {
        var conflicts = new List<TagConflict>();
        foreach (var c in changes)
        {
            var hasBase = TryGet(baseline, c.Name, out var baseValue);
            var hasCurrent = TryGet(current, c.Name, out var currentValue);
            if (hasBase == hasCurrent && baseValue == currentValue) continue;

            // Someone else already made the same change: not a conflict.
            if (c.Kind == TagChangeKind.Remove && !hasCurrent) continue;
            if (c.Kind != TagChangeKind.Remove && hasCurrent && currentValue == c.NewValue) continue;

            conflicts.Add(new TagConflict(c.Name, hasBase ? baseValue : null, hasCurrent ? currentValue : null, c.NewValue));
        }
        return conflicts;
    }

    public static bool SameTags(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => TryGet(b, kv.Key, out var v) && v == kv.Value);

    private static bool TryGet(IReadOnlyDictionary<string, string> tags, string name, out string? value)
    {
        if (tags.TryGetValue(name, out var v)) { value = v; return true; }
        foreach (var (k, val) in tags)
        {
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) { value = val; return true; }
        }
        value = null;
        return false;
    }
}
