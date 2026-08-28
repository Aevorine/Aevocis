namespace OpenSuperWhisper.Core;

/// <summary>
/// F30: writes a sanitized, self-contained crash report file whenever an unhandled exception
/// takes the app down - separate from the rolling log.txt so there's one obvious file to attach
/// when asking for help. Deliberately never touches history.json/settings.json - the exception
/// object and stack trace are the only inputs, so there is no path for dictated speech content to
/// end up in a report. Keeps only the most recent <see cref="MaxReports"/> files so a machine that
/// crashes repeatedly doesn't accumulate reports forever.
/// </summary>
public static class CrashReporter
{
    private const int MaxReports = 10;
    private static readonly string ReportsDir;

    static CrashReporter()
    {
        ReportsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenSuperWhisper", "crash-reports");
    }

    /// <summary>Returns the path written to, or null if writing the report itself failed (in
    /// which case the exception is still caught, not rethrown - a crash reporter must never be
    /// the thing that crashes the crash handler).</summary>
    public static string? Write(Exception ex, string context)
    {
        try
        {
            Directory.CreateDirectory(ReportsDir);
            // Timestamp plus a short random suffix (not just milliseconds): a tight crash loop
            // can produce more than one report within the same millisecond, and a collision
            // would silently overwrite the earlier report instead of keeping both.
            var suffix = Guid.NewGuid().ToString("N")[..6];
            var path = Path.Combine(ReportsDir, $"crash-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{suffix}.txt");

            var text =
                $"OpenSuperWhisper 崩溃报告{Environment.NewLine}" +
                $"时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}{Environment.NewLine}" +
                $"版本：{AppVersion.Current}{Environment.NewLine}" +
                $"系统：{Environment.OSVersion}{Environment.NewLine}" +
                $"场景：{context}{Environment.NewLine}" +
                $"{Environment.NewLine}--- 异常详情（不含任何听写内容，只有程序自身的报错信息）---{Environment.NewLine}" +
                FormatException(ex);

            File.WriteAllText(path, text);
            RotateOldReports();
            return path;
        }
        catch (Exception writeEx)
        {
            Log.Error("写入崩溃报告本身失败", writeEx);
            return null;
        }
    }

    private static string FormatException(Exception ex)
    {
        var lines = new List<string>();
        var current = ex;
        var depth = 0;
        while (current != null)
        {
            lines.Add($"[{(depth == 0 ? "异常" : "内层异常")}] {current.GetType().FullName}: {current.Message}");
            lines.Add(current.StackTrace ?? "（无堆栈信息）");
            current = current.InnerException;
            depth++;
        }
        return string.Join(Environment.NewLine + Environment.NewLine, lines);
    }

    private static void RotateOldReports()
    {
        try
        {
            var files = new DirectoryInfo(ReportsDir).GetFiles("crash-*.txt");
            if (files.Length <= MaxReports) return;

            foreach (var f in files.OrderByDescending(f => f.CreationTimeUtc).Skip(MaxReports))
                f.Delete();
        }
        catch (Exception ex)
        {
            Log.Error("清理旧崩溃报告失败", ex);
        }
    }
}
