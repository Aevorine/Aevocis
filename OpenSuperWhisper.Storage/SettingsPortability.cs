using System.Text.Json;
using OpenSuperWhisper.Core;
using OpenSuperWhisper.Core.Models;

namespace OpenSuperWhisper.Storage;

/// <summary>F31: bundles everything a user would want to carry to a new PC - settings plus the
/// custom term dictionary - into one JSON file, and reads it back. A mismatched/missing
/// microphone device ID from the old machine is harmless: <c>MicRecorder</c>'s existing device
/// resolution already falls back to the system default when a pinned device ID doesn't exist.</summary>
public sealed class SettingsBundle
{
    public AppSettings Settings { get; set; } = new();
    public List<TermCorrection> Terms { get; set; } = new();
}

public static class SettingsPortability
{
    public static void Export(string path, AppSettings settings, List<TermCorrection> terms)
    {
        var bundle = new SettingsBundle { Settings = settings, Terms = terms };
        var json = JsonSerializer.Serialize(bundle, new JsonSerializerOptions { WriteIndented = true });
        AtomicFile.WriteAllText(path, json);
    }

    /// <summary>Throws on a missing/corrupt file - unlike the app's own settings/history stores,
    /// this is a user-initiated one-shot action, so the caller (UI) should surface the failure
    /// directly rather than silently falling back to defaults.</summary>
    public static SettingsBundle Import(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<SettingsBundle>(json)
               ?? throw new InvalidDataException("导入的设置文件内容为空或格式不对");
    }
}
