using System.Text.Json.Serialization;

namespace OpenSuperWhisper.Core.Models;

/// <summary>
/// What a matched F05 口头命令 does. Deliberately scoped to what SendInput can actually
/// achieve: Windows has no general API to read or edit text another app already has on screen
/// (every app implements its own text control differently), so "删除这段" can never mean
/// "delete an arbitrary chunk of text sitting in some other app" - see
/// <see cref="CancelDictation"/>'s own doc comment for the honest, narrower thing it does
/// instead.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VoiceCommandAction
{
    /// <summary>
    /// "删除这段"类命令：整句话本身被识别为这个命令时，这次听写直接作废——不打字、不存历史，
    /// 等于这句话没说过。如果 OpenSuperWhisper 自己上一次成功注入过文字（还没被别的操作覆盖），
    /// 还会尝试发送等量的退格键把那次内容也撤销掉。做不到"删除目标软件里任意已有内容"这种通用
    /// 能力——那需要读懂目标软件的文本框，Windows 没有这样的通用 API。
    /// </summary>
    CancelDictation,

    /// <summary>"换行"：发送一次 Enter 按键，而不是把"换行"两个字打出来。</summary>
    SendEnter,

    /// <summary>
    /// "……全部大写"：命令词必须出现在整句话的末尾，且前面还有实际内容才算命中——命中后，把命令词
    /// 前面那部分文字转成大写再注入（命令词本身不会被打出来）。只对英文字母有意义，纯中文没有
    /// 大小写之分，这是正常情况。
    /// </summary>
    UppercaseSuffix,
}

/// <summary>One configured voice command: <see cref="Phrase"/> is what the user says out loud,
/// <see cref="Action"/> is what happens instead of typing the phrase itself.</summary>
public sealed record VoiceCommandDefinition(string Phrase, VoiceCommandAction Action);
