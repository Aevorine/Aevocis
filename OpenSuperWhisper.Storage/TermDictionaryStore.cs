using System.Text.Json;
using OpenSuperWhisper.Core;
using OpenSuperWhisper.Core.Models;

namespace OpenSuperWhisper.Storage;

/// <summary>Persists the user's professional-vocabulary corrections (F02) to terms.json, next to
/// settings.json and history.json. Same load/save shape as <see cref="SettingsStore"/> -
/// corrupt/locked files degrade to an empty list rather than losing user data on save.</summary>
public sealed class TermDictionaryStore
{
    private readonly string _path;

    public bool LastLoadWasReset { get; private set; }
    public bool IsDegraded { get; private set; }

    public TermDictionaryStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenSuperWhisper");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "terms.json");
    }

    public List<TermCorrection> Load()
    {
        LastLoadWasReset = false;
        IsDegraded = false;

        if (!File.Exists(_path)) return new List<TermCorrection>();
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<TermCorrection>>(json) ?? new List<TermCorrection>();
        }
        catch (Exception ex) when (ex is JsonException or FileNotFoundException)
        {
            Log.Error("专业词典文件已损坏，重置为空", ex);
            LastLoadWasReset = true;
            return new List<TermCorrection>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("专业词典文件读取失败（可能被占用或无权限），本次以空词典运行且不会保存覆盖原文件", ex);
            IsDegraded = true;
            return new List<TermCorrection>();
        }
    }

    public void Save(List<TermCorrection> corrections)
    {
        if (IsDegraded)
        {
            Log.Error("跳过词典保存：上次加载时检测到词典文件被占用/无权限，避免覆盖磁盘上的原文件");
            return;
        }
        try
        {
            var json = JsonSerializer.Serialize(corrections, new JsonSerializerOptions { WriteIndented = true });
            AtomicFile.WriteAllText(_path, json);
        }
        catch (Exception ex)
        {
            Log.Error("保存专业词典失败", ex);
        }
    }
}
