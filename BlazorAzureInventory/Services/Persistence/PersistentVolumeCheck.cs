using BlazorAzureInventory.Models.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace BlazorAzureInventory.Services.Persistence;

/// <summary>Whether saved data survives restarts. Decided once at startup by <see cref="PersistentVolumeCheck"/>.</summary>
public sealed class PersistenceStatus(bool isPersistent, string reason)
{
    public bool IsPersistent { get; } = isPersistent;
    public string Reason { get; } = reason;
}

public static class PersistentVolumeCheck
{
    private static readonly HashSet<string> NetworkFileSystems = new(StringComparer.Ordinal) { "cifs", "smb3", "nfs", "nfs4" };

    public static PersistenceStatus Evaluate(PersistenceOptions options, PersistencePaths paths, ILogger logger)
    {
        if (!options.Enabled) return new PersistenceStatus(false, "Persistence is disabled in configuration.");

        if (options.RequirePersistentVolume)
        {
            var mountInfo = OperatingSystem.IsLinux() && File.Exists("/proc/self/mountinfo") ? File.ReadAllText("/proc/self/mountinfo") : null;
            if (mountInfo is null || !IsOnNetworkMount(mountInfo, paths.Root))
            {
                logger.LogCritical("Persistent storage is required but {Root} is not on a mounted network share; saved data is disabled", paths.Root);
                return new PersistenceStatus(false, $"{paths.Root} is not a mounted persistent volume.");
            }
        }

        try
        {
            Probe(paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogCritical(ex, "Persistent storage at {Root} is not writable; saved data is disabled", paths.Root);
            return new PersistenceStatus(false, $"{paths.Root} is not writable.");
        }

        logger.LogInformation("Saved data is stored under {Root}", paths.Root);
        return new PersistenceStatus(true, "OK");
    }

    /// <summary>True when <paramref name="path"/> is on (or under) a cifs/smb3/nfs mount point listed in mountinfo.</summary>
    public static bool IsOnNetworkMount(string mountInfo, string path)
    {
        var target = Path.TrimEndingDirectorySeparator(path);
        string? best = null;
        string? bestType = null;
        foreach (var line in mountInfo.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // Format: id parent major:minor root mountpoint options [optional fields] - fstype source superoptions
            var fields = line.Split(' ');
            var separator = Array.IndexOf(fields, "-");
            if (fields.Length < 5 || separator < 0 || separator + 1 >= fields.Length) continue;
            var mountPoint = fields[4].Replace("\\040", " ");
            var isMatch = target == mountPoint || target.StartsWith(mountPoint.TrimEnd('/') + "/", StringComparison.Ordinal);
            if (isMatch && (best is null || mountPoint.Length > best.Length))
            {
                best = mountPoint;
                bestType = fields[separator + 1];
            }
        }
        return bestType is not null && NetworkFileSystems.Contains(bestType);
    }

    public static void Probe(PersistencePaths paths)
    {
        Directory.CreateDirectory(paths.HealthDirectory);
        var probe = Path.Combine(paths.HealthDirectory, $"{FileLease.ReplicaName}.probe");
        File.WriteAllText(probe, DateTimeOffset.UtcNow.ToString("O"));
        File.Delete(probe);
    }
}

public sealed class PersistenceHealthCheck(PersistenceStatus status, PersistencePaths paths) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!status.IsPersistent) return Task.FromResult(HealthCheckResult.Degraded(status.Reason));
        try
        {
            PersistentVolumeCheck.Probe(paths);
            return Task.FromResult(HealthCheckResult.Healthy());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Persistent storage is not writable.", ex));
        }
    }
}
