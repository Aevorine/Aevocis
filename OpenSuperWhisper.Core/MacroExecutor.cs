using System.Diagnostics;
using OpenSuperWhisper.Core.Models;

namespace OpenSuperWhisper.Core;

/// <summary>
/// F13 语音宏引擎："触发短语"命中即依次执行一串动作(启动程序/打字/发送按键)。Matching
/// (<see cref="Match"/>) is a pure static function with no side effects - easy to verify in
/// isolation. Execution (<see cref="Execute"/>) is an instance method because typing/sending keys
/// needs an <see cref="ITextInjector"/>; launching a program needs no such dependency, it goes
/// straight through <see cref="Process.Start(ProcessStartInfo)"/> (plain System.Diagnostics,
/// available on net8.0 with no Win32/WPF dependency).
/// </summary>
public sealed class MacroExecutor
{
    private readonly ITextInjector _injector;

    public MacroExecutor(ITextInjector injector)
    {
        _injector = injector;
    }

    /// <summary>Finds the first macro whose trigger phrase exactly matches the (trimmed,
    /// trailing-punctuation-stripped) utterance. Unlike F05's UppercaseSuffix command, a macro
    /// only ever matches the *whole* utterance - "一句话完成"切软件+打字，not a suffix tacked onto
    /// other dictated text.</summary>
    public static VoiceMacro? Match(string text, IReadOnlyList<VoiceMacro> macros)
    {
        if (string.IsNullOrWhiteSpace(text) || macros.Count == 0) return null;

        var normalized = TriggerTextNormalizer.Normalize(text);
        if (normalized.Length == 0) return null;

        foreach (var macro in macros)
        {
            var phrase = macro.TriggerPhrase?.Trim();
            if (string.IsNullOrEmpty(phrase)) continue;
            if (string.Equals(normalized, phrase, StringComparison.OrdinalIgnoreCase))
                return macro;
        }
        return null;
    }

    /// <summary>
    /// Runs a macro's action list in order. Each action's failure (bad launch path, unknown key
    /// name, SendInput rejection) is caught, logged, and recorded in the returned list - but does
    /// NOT stop the remaining actions: e.g. a macro "打开:一个装错的路径;打字:早上好" still types
    /// "早上好" even though the launch failed, instead of silently doing nothing at all. An empty
    /// returned list means every action succeeded.
    /// </summary>
    public List<string> Execute(VoiceMacro macro)
    {
        var errors = new List<string>();
        foreach (var action in macro.Actions)
        {
            try
            {
                switch (action.Type)
                {
                    case MacroActionType.LaunchApp:
                        Process.Start(new ProcessStartInfo(action.Value) { UseShellExecute = true });
                        break;

                    case MacroActionType.TypeText:
                        _injector.InjectText(action.Value);
                        break;

                    case MacroActionType.SendKey:
                        if (!VirtualKeys.TryParse(action.Value, out var vk))
                            throw new InvalidOperationException($"未知按键名：{action.Value}");
                        _injector.SendVirtualKey(vk);
                        break;

                    default:
                        throw new InvalidOperationException($"未知动作类型：{action.Type}");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"语音宏「{macro.TriggerPhrase}」的动作执行失败：{action.Type}:{action.Value}", ex);
                errors.Add($"{action.Type}:{action.Value} - {ex.Message}");
            }
        }
        return errors;
    }
}
