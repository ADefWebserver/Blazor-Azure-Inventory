namespace BlazorAzureInventory.Services.Persistence;

/// <summary>Writes a file through a temporary sibling and a rename, so readers see the old file or the new one, never a partial file.</summary>
public static class AtomicFile
{
    public const string TempSuffix = ".tmp";

    /// <param name="beforeCommit">Runs with the completed temporary file just before it replaces <paramref name="path"/>; throw to abort.</param>
    public static async Task WriteAsync(string path, Func<Stream, CancellationToken, Task> write, CancellationToken ct,
        Action<string>? beforeCommit = null)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}{TempSuffix}");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await write(stream, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            beforeCommit?.Invoke(temp);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    public static Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken ct) =>
        WriteAsync(path, (s, token) => s.WriteAsync(bytes, token).AsTask(), ct);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
