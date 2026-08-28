namespace OpenSuperWhisper.Core;

/// <summary>
/// Writes a file's full content atomically: write to a temp file beside the target, then swap
/// it into place with <see cref="File.Replace(string, string, string?)"/> (or a move, if the
/// target doesn't exist yet). A crash or power loss mid-write can therefore never leave a
/// half-written, corrupt file at the real path - the real path only ever sees a complete write.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) dir = ".";
        var tempPath = Path.Combine(dir, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        File.WriteAllText(tempPath, content);
        try
        {
            if (File.Exists(path))
                File.Replace(tempPath, path, null);
            else
                File.Move(tempPath, path);
        }
        catch
        {
            // Don't leave the temp file behind on failure.
            try { File.Delete(tempPath); } catch { /* best effort cleanup */ }
            throw;
        }
    }
}
