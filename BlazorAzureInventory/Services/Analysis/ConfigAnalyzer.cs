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

        // Container-name hints are only kept when the same app has a resolved-quality storage reference.
        var storageAccount = entries
            .Where(e => !e.IsOverridden)
            .SelectMany(e => e.References)
            .Where(r => r.Kind is ConnectionTargetKind.StorageAccount or ConnectionTargetKind.BlobContainer
                        && r.Confidence != Confidence.Low && r.AccountOrServerName is not null)
            .Select(r => r.AccountOrServerName)
            .FirstOrDefault();

        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (!e.References.Any(IsHint)) continue;
            entries[i] = e with
            {
                References = storageAccount is null
                    ? e.References.Where(r => !IsHint(r)).ToList()
                    : e.References.Select(r => IsHint(r) ? r with { AccountOrServerName = storageAccount } : r).ToList()
            };
        }
        return entries;

        static bool IsHint(ConnectionReference r) =>
            r.Kind == ConnectionTargetKind.BlobContainer && r.Confidence == Confidence.Low && r.AccountOrServerName is null;
    }
}
