using System.Text.Json;
using OpenSuperWhisper.Core;
using OpenSuperWhisper.Core.Models;

namespace OpenSuperWhisper.Storage;

/// <summary>
/// Persists F05 口头命令配置到 voice_commands.json，和 settings.json/terms.json 放在一起。和
/// TermDictionaryStore 不同的是：那里"空列表"才是正确的默认值（没有哪个术语纠错是普适的），这里
/// 却有一组普适、开箱即用的默认命令（"删除这段"/"换行"/"全部大写"），所以文件不存在时返回
/// <see cref="DefaultCommands"/> 而不是空列表——符合需求里"识别到就执行对应动作"要求零配置也能用。
/// 其余 Load/Save 行为（损坏重置、占用降级）与 SettingsStore/TermDictionaryStore 同一套模式。
/// </summary>
public sealed class VoiceCommandStore
{
    private readonly string _path;

    public bool LastLoadWasReset { get; private set; }
    public bool IsDegraded { get; private set; }

    public static IReadOnlyList<VoiceCommandDefinition> DefaultCommands { get; } = new List<VoiceCommandDefinition>
    {
        new("删除这段", VoiceCommandAction.CancelDictation),
        new("算了不要了", VoiceCommandAction.CancelDictation),
        new("换行", VoiceCommandAction.SendEnter),
        new("全部大写", VoiceCommandAction.UppercaseSuffix),
    };

    public VoiceCommandStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenSuperWhisper");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "voice_commands.json");
    }

    public List<VoiceCommandDefinition> Load()
    {
        LastLoadWasReset = false;
        IsDegraded = false;

        if (!File.Exists(_path)) return DefaultCommands.ToList();
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<VoiceCommandDefinition>>(json) ?? DefaultCommands.ToList();
        }
        catch (Exception ex) when (ex is JsonException or FileNotFoundException)
        {
            Log.Error("语音命令配置文件已损坏，重置为默认命令", ex);
            LastLoadWasReset = true;
            return DefaultCommands.ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("语音命令配置文件读取失败（可能被占用或无权限），本次以默认命令运行且不会保存覆盖原文件", ex);
            IsDegraded = true;
            return DefaultCommands.ToList();
        }
    }

    public void Save(List<VoiceCommandDefinition> commands)
    {
        if (IsDegraded)
        {
            Log.Error("跳过语音命令保存：上次加载时检测到配置文件被占用/无权限，避免覆盖磁盘上的原文件");
            return;
        }
        try
        {
            var json = JsonSerializer.Serialize(commands, new JsonSerializerOptions { WriteIndented = true });
            AtomicFile.WriteAllText(_path, json);
        }
        catch (Exception ex)
        {
            Log.Error("保存语音命令配置失败", ex);
        }
    }
}
