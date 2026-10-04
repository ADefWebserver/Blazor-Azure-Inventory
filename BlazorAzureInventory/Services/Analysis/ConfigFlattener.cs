using BlazorAzureInventory.Models.Configuration;
using Microsoft.Extensions.Configuration;

namespace BlazorAzureInventory.Services.Analysis;

/// <summary>
/// A transient setting with its raw value. Exists only inside the analysis pipeline and must never be stored.
/// </summary>
public readonly record struct RawSetting(
    string Key, string? Value, ConfigSource Source, string? FileName, bool IsSlotSticky = false);

/// <summary>Reproduces how ASP.NET Core builds configuration so keys line up across all sources.</summary>
public sealed class ConfigFlattener
{
    public IReadOnlyList<RawSetting> FlattenJson(Stream json, string fileName, ConfigSource source)
    {
        var config = new ConfigurationBuilder().AddJsonStream(json).Build();
        return config.AsEnumerable()
            .Where(kv => kv.Value is not null)
            .Select(kv => new RawSetting(kv.Key, kv.Value, source, fileName))
            .OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string NormalizeEnvironmentKey(string key) => key.Replace("__", ":", StringComparison.Ordinal);

    public static string ConnectionStringKey(string name) => $"ConnectionStrings:{name}";

    public static int Precedence(ConfigSource source) => source switch
    {
        ConfigSource.AppSettingsJson => 0,
        ConfigSource.AppSettingsEnvironmentJson => 1,
        ConfigSource.AppServiceAppSetting => 2,
        ConfigSource.ContainerAppEnvValue => 2,
        ConfigSource.ContainerAppEnvSecretRef => 2,
        ConfigSource.AppServiceConnectionString => 3,
        _ => 0
    };

    /// <summary>
    /// Keeps every declared entry and flags those replaced by a higher-precedence source (or a later duplicate of equal precedence).
    /// </summary>
    public IReadOnlyList<(RawSetting Setting, bool IsOverridden)> ApplyPrecedence(IReadOnlyList<RawSetting> settings)
    {
        var winners = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < settings.Count; i++)
        {
            var key = settings[i].Key;
            if (!winners.TryGetValue(key, out var current)
                || Precedence(settings[i].Source) >= Precedence(settings[current].Source))
            {
                winners[key] = i;
            }
        }

        var result = new List<(RawSetting, bool)>(settings.Count);
        for (var i = 0; i < settings.Count; i++)
        {
            result.Add((settings[i], winners[settings[i].Key] != i));
        }
        return result;
    }
}
