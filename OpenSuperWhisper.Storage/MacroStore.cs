using System.Text.Json;
using OpenSuperWhisper.Core;
using OpenSuperWhisper.Core.Models;

namespace OpenSuperWhisper.Storage;

/// <summary>Persists the user's F13 语音宏("触发短语 -> 一串动作")到 macros.json，和
/// settings.json/terms.json 放在一起。空列表是正确的默认值——和 TermDictionaryStore 一样，这个应用
/// 不知道用户装了哪些程序、装在哪，猜一个默认宏出来只会是错的。Load/Save 行为与
/// TermDictionaryStore 同一套模式（损坏重置、占用降级）。</summary>
public sealed class MacroStore
{
    private readonly string _path;

    public bool LastLoadWasReset { get; private set; }
    public bool IsDegraded { get; private set; }

    public MacroStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenSuperWhisper");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "macros.json");
    }

    public List<VoiceMacro> Load()
    {
        LastLoadWasReset = false;
        IsDegraded = false;

        if (!File.Exists(_path)) return new List<VoiceMacro>();
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<VoiceMacro>>(json) ?? new List<VoiceMacro>();
        }
        catch (Exception ex) when (ex is JsonException or FileNotFoundException)
        {
            Log.Error("语音宏配置文件已损坏，重置为空", ex);
            LastLoadWasReset = true;
            return new List<VoiceMacro>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("语音宏配置文件读取失败（可能被占用或无权限），本次以空宏列表运行且不会保存覆盖原文件", ex);
            IsDegraded = true;
            return new List<VoiceMacro>();
        }
    }

    public void Save(List<VoiceMacro> macros)
    {
        if (IsDegraded)
        {
            Log.Error("跳过语音宏保存：上次加载时检测到配置文件被占用/无权限，避免覆盖磁盘上的原文件");
            return;
        }
        try
        {
            var json = JsonSerializer.Serialize(macros, new JsonSerializerOptions { WriteIndented = true });
            AtomicFile.WriteAllText(_path, json);
        }
        catch (Exception ex)
        {
            Log.Error("保存语音宏配置失败", ex);
        }
    }
}
