using System.Text.Json;
using OpenSuperWhisper.Core;
using OpenSuperWhisper.Core.Models;

namespace OpenSuperWhisper.Storage;

/// <summary>Persists the F33 term-learning "pending candidates" list to learned-term-candidates.json,
/// next to settings.json/history.json/terms.json in the same %LOCALAPPDATA%\OpenSuperWhisper
/// directory. Same load/save shape (and same corrupt/locked-file degrade-to-empty behavior) as
/// <see cref="TermDictionaryStore"/> - this file is purely an internal counter, never shown to the
/// user directly (unlike terms.json, which TermDictionaryWindow edits), so resetting it on
/// corruption just means a few observed edits need to reoccur before they're promoted again.</summary>
public sealed class TermLearningStore
{
    private readonly string _path;

    public bool LastLoadWasReset { get; private set; }
    public bool IsDegraded { get; private set; }

    public TermLearningStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenSuperWhisper");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "learned-term-candidates.json");
    }

    public List<ObservedTermEdit> Load()
    {
        LastLoadWasReset = false;
        IsDegraded = false;

        if (!File.Exists(_path)) return new List<ObservedTermEdit>();
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<ObservedTermEdit>>(json) ?? new List<ObservedTermEdit>();
        }
        catch (Exception ex) when (ex is JsonException or FileNotFoundException)
        {
            Log.Error("术语自学习候选文件已损坏，重置为空", ex);
            LastLoadWasReset = true;
            return new List<ObservedTermEdit>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("术语自学习候选文件读取失败（可能被占用或无权限），本次以空列表运行且不会保存覆盖原文件", ex);
            IsDegraded = true;
            return new List<ObservedTermEdit>();
        }
    }

    public void Save(List<ObservedTermEdit> observed)
    {
        if (IsDegraded)
        {
            Log.Error("跳过术语自学习候选保存：上次加载时检测到文件被占用/无权限，避免覆盖磁盘上的原文件");
            return;
        }
        try
        {
            var json = JsonSerializer.Serialize(observed, new JsonSerializerOptions { WriteIndented = true });
            AtomicFile.WriteAllText(_path, json);
        }
        catch (Exception ex)
        {
            Log.Error("保存术语自学习候选失败", ex);
        }
    }
}
