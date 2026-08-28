using System.Text.Json.Serialization;

namespace OpenSuperWhisper.Core.Models;

/// <summary>One step in a F13 语音宏的动作序列。故意只做这三种，够用就好，不追求"万能脚本"。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MacroActionType
{
    /// <summary>启动一个程序 - <see cref="MacroAction.Value"/> 是可执行文件的完整路径，或者一个
    /// 能被 Windows Shell 直接解析的名字（在 PATH 上的话）。这个应用不会替用户瞎猜"微信在哪"之类
    /// 的安装路径——猜错了比不猜更糟；找不到就让用户自己在设置里填。</summary>
    LaunchApp,

    /// <summary>打字 - <see cref="MacroAction.Value"/> 是要注入到当前聚焦窗口的文本。</summary>
    TypeText,

    /// <summary>发送一个按键 - <see cref="MacroAction.Value"/> 是键名，见 <see cref="VirtualKeys"/>
    /// 支持的名字列表（如 "Enter"、"Tab"）。</summary>
    SendKey,
}

public sealed record MacroAction(MacroActionType Type, string Value);

/// <summary>
/// 一条用户自定义语音宏："TriggerPhrase 命中 -> 依次执行 Actions"。比如"打开微信说早上好" =
/// 一条 LaunchApp("WeChat.exe") + 一条 TypeText("早上好")。
/// </summary>
public sealed record VoiceMacro(string TriggerPhrase, List<MacroAction> Actions);
