namespace BlazorAzureInventory.Services.Analysis;

public sealed record ConfigFileSelection(string? BaseFile, string? EnvironmentFile, IReadOnlyList<string> Skipped)
{
    public IReadOnlyList<string> ToRead => new[] { BaseFile, EnvironmentFile }.OfType<string>().ToList();
}

/// <summary>Picks the base and environment config files, accepting <c>appsettings.{env}.json</c>, <c>appsettings{env}.json</c>, and aliases.</summary>
public static class ConfigFileResolver
{
    public const string BaseFileName = "appsettings.json";

    public static ConfigFileSelection Resolve(
        IReadOnlyList<string> found, string environment, IReadOnlyDictionary<string, string[]> aliases)
    {
        var byName = found
            .GroupBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var baseFile = byName.GetValueOrDefault(BaseFileName);
        string? envFile = null;
        foreach (var name in CandidateNames(environment, aliases))
        {
            envFile = byName.GetValueOrDefault($"appsettings.{name}.json") ?? byName.GetValueOrDefault($"appsettings{name}.json");
            if (envFile is not null) break;
        }

        var skipped = found
            .Where(f => !f.Equals(baseFile, StringComparison.OrdinalIgnoreCase) && !f.Equals(envFile, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ConfigFileSelection(baseFile, envFile, skipped);
    }

    /// <summary>The environment first, then its aliases; aliases work in both directions (Prod also finds Production).</summary>
    public static IReadOnlyList<string> CandidateNames(string environment, IReadOnlyDictionary<string, string[]> aliases)
    {
        var names = new List<string> { environment };
        foreach (var (canonical, alternatives) in aliases)
        {
            var group = new[] { canonical }.Concat(alternatives ?? []).ToList();
            if (!group.Contains(environment, StringComparer.OrdinalIgnoreCase)) continue;
            names.AddRange(group);
        }
        return names
            .Where(n => !string.IsNullOrWhiteSpace(n) && n.IndexOfAny(['/', '\\']) < 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
