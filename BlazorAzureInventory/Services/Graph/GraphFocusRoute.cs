namespace BlazorAzureInventory.Services.Graph;

/// <summary>The Focus view described by the <c>focus</c> and <c>expand</c> query parameters of the graph page.</summary>
public sealed record GraphFocusRoute(string Focus, IReadOnlyList<string> Expand)
{
    public const int MaxIdLength = 2048;

    /// <summary>Normalizes query values. Returns null (Overview) when <paramref name="focus"/> is blank or too long.</summary>
    public static GraphFocusRoute? Parse(string? focus, IEnumerable<string>? expand, int maxExpand, out int droppedExpand)
    {
        var raw = expand?.ToList() ?? [];
        var id = focus?.Trim();
        if (string.IsNullOrEmpty(id) || id.Length > MaxIdLength)
        {
            droppedExpand = raw.Count;
            return null;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id };
        var kept = new List<string>();
        foreach (var value in raw)
        {
            var e = value?.Trim();
            if (string.IsNullOrEmpty(e) || e.Length > MaxIdLength || kept.Count >= Math.Max(0, maxExpand) || !seen.Add(e)) continue;
            kept.Add(e);
        }

        droppedExpand = raw.Count - kept.Count;
        return new GraphFocusRoute(id, kept);
    }

    // ARM resource IDs are case-insensitive; Expand is compared in order.
    public bool Equals(GraphFocusRoute? other) =>
        other is not null
        && string.Equals(Focus, other.Focus, StringComparison.OrdinalIgnoreCase)
        && Expand.SequenceEqual(other.Expand, StringComparer.OrdinalIgnoreCase);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Focus, StringComparer.OrdinalIgnoreCase);
        foreach (var e in Expand) hash.Add(e, StringComparer.OrdinalIgnoreCase);
        return hash.ToHashCode();
    }
}
