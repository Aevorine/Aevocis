using System.Text.Json;
using OpenSuperWhisper.Core;
using OpenSuperWhisper.Core.Models;

namespace OpenSuperWhisper.Storage;

public sealed class SettingsStore
{
    private readonly string _path;

    /// <summary>True if the most recent <see cref="Load"/> found a settings file that was
    /// present but unreadable as JSON, and reset to defaults because of it. The caller should
    /// tell the user about this once (it silently discards their settings otherwise).</summary>
    public bool LastLoadWasReset { get; private set; }

    /// <summary>True if the most recent <see cref="Load"/> hit an IOException/UnauthorizedAccessException
    /// (file locked by another process, permission denied, etc.) rather than corrupt JSON.
    /// While degraded, <see cref="Save"/> is a no-op - saving over an in-memory default in this
    /// state would permanently wipe an intact-but-temporarily-locked file.</summary>
    public bool IsDegraded { get; private set; }

    public SettingsStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenSuperWhisper");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
    }

    public AppSettings Load()
    {
        LastLoadWasReset = false;
        IsDegraded = false;

        if (!File.Exists(_path)) return new AppSettings();
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or FileNotFoundException)
        {
            // Genuinely corrupt (or vanished mid-read) - resetting to defaults is the right
            // call, but it must not happen silently.
            Log.Error("设置文件已损坏，重置为默认值", ex);
            LastLoadWasReset = true;
            return new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // File is intact but transiently locked (OneDrive/AV) or permission-denied - do NOT
            // conflate this with corruption. Falling back to defaults here without also
            // suppressing Save() would let the next Save() silently overwrite/wipe the real file
            // once the lock clears.
            Log.Error("设置文件读取失败（可能被占用或无权限），本次使用默认设置且不会保存覆盖原文件", ex);
            IsDegraded = true;
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        if (IsDegraded)
        {
            Log.Error("跳过设置保存：上次加载时检测到设置文件被占用/无权限，避免覆盖磁盘上的原文件");
            return;
        }
        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            AtomicFile.WriteAllText(_path, json);
        }
        catch (Exception ex)
        {
            // A save failure must not propagate as an unhandled exception - this is called
            // directly from OnStartup (before the tray icon/hotkey even exist) and from the
            // Settings window's Save button.
            Log.Error("保存设置失败", ex);
        }
    }
}
