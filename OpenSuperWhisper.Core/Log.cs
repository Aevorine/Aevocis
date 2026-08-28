namespace OpenSuperWhisper.Core;

/// <summary>
/// Minimal rolling file logger. No logging framework / NuGet package - just appends lines to
/// %LOCALAPPDATA%\OpenSuperWhisper\log.txt. This app has no logging anywhere else, so this is
/// the only trace left behind when something fails silently (a swallowed exception, a
/// fallback-to-defaults, an unhandled exception the process-wide handlers caught on the way
/// down). Rotates once the file gets too big so it can never grow unbounded across a
/// long-running background process.
/// </summary>
public static class Log
{
    private const long MaxBytes = 2 * 1024 * 1024; // 2 MB per file, one rotated backup kept.
    private static readonly string LogPath;
    private static readonly object Gate = new();

    static Log()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenSuperWhisper");
        Directory.CreateDirectory(dir);
        LogPath = Path.Combine(dir, "log.txt");
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                RotateIfNeeded();
                var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
                if (ex != null) line += Environment.NewLine + ex;
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never itself take down the app - this is a best-effort trace, not a
            // guaranteed audit log.
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > MaxBytes)
            {
                var backupPath = LogPath + ".old";
                if (File.Exists(backupPath)) File.Delete(backupPath);
                File.Move(LogPath, backupPath);
            }
        }
        catch
        {
            // Best effort - if rotation fails, the append below will just keep growing the
            // current file rather than crash anything.
        }
    }
}
