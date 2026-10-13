using BlazorAzureInventory.Models.Configuration;

namespace BlazorAzureInventory.Services.Analysis;

/// <summary>
/// Turns transient raw settings into <see cref="ConfigEntry"/> records: precedence, masking flags, and identifier-only
/// connection references. This is the boundary after which no raw value exists.
/// </summary>
public sealed class ConfigAnalyzer(ConfigFlattener flattener, SecretClassifier secrets, ConnectionClassifier classifier)
{
    public List<ConfigEntry> Analyze(string appId, string? slotName, string? environment, IReadOnlyList<RawSetting> raw)
    {
        var entries = new List<ConfigEntry>(raw.Count);
        foreach (var (setting, overridden) in flattener.ApplyPrecedence(raw))
        {
            entries.Add(new ConfigEntry(
                appId, slotName, setting.Key, setting.Source, setting.FileName, environment,
                setting.IsSlotSticky, overridden,
                secrets.IsSecretLike(setting.Key, setting.Value),
                secrets.IsKeyVaultReference(setting.Value),
                classifier.Classify(setting.Key, setting.Value, setting.Source)));
        }

        // Container and queue name hints are only kept when the same app has a resolved-quality storage reference.
        var candidates = entries
            .Where(e => !e.IsOverridden)
            .SelectMany(e => e.References.Select(r => (e.Key, Ref: r)))
            .Where(x => x.Ref.Kind is ConnectionTargetKind.StorageAccount or ConnectionTargetKind.BlobContainer or ConnectionTargetKind.StorageQueue
                        && x.Ref.Confidence != Confidence.Low && x.Ref.AccountOrServerName is not null)
            .Select(x => (x.Key, Account: x.Ref.AccountOrServerName!))
            .ToList();

        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (!e.References.Any(IsHint)) continue;
            var account = BestAccount(e.Key, candidates);
            entries[i] = e with
            {
                References = account is null
                    ? e.References.Where(r => !IsHint(r)).ToList()
                    : e.References.Select(r => IsHint(r) ? r with { AccountOrServerName = account } : r).ToList()
            };
        }
        return entries;

        static bool IsHint(ConnectionReference r) =>
            r.Kind is ConnectionTargetKind.BlobContainer or ConnectionTargetKind.StorageQueue
            && r.Confidence == Confidence.Low && r.AccountOrServerName is null;
    }

    /// <summary>The storage account whose setting key shares the most leading sections with <paramref name="hintKey"/>; ties keep the first.</summary>
    public static string? BestAccount(string hintKey, IReadOnlyList<(string Key, string Account)> candidates)
    {
        string? best = null;
        var bestScore = -1;
        var hintSections = Sections(hintKey);
        foreach (var (key, account) in candidates)
        {
            var sections = Sections(key);
            var score = 0;
            while (score < hintSections.Length && score < sections.Length
                   && hintSections[score].Equals(sections[score], StringComparison.OrdinalIgnoreCase)) score++;
            if (score > bestScore)
            {
                best = account;
                bestScore = score;
            }
        }
        return best;

        static string[] Sections(string key) => key.Split(':')[..^1];
    }
}
