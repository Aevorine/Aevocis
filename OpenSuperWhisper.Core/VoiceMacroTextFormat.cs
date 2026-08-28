using OpenSuperWhisper.Core.Models;

namespace OpenSuperWhisper.Core;

/// <summary>
/// Converts between a <see cref="VoiceMacro"/> list and the simple one-line-per-macro plain-text
/// format shown in the Settings window's F13 editor:
/// <c>触发短语|动作类型:值;动作类型:值;...</c>, e.g. <c>打开微信说早上好|打开:WeChat.exe;打字:早上好</c>.
/// Kept in Core (not SettingsWindow.xaml.cs) so it's plain string logic with no WPF dependency -
/// testable head-less, and reused as-is by the settings UI.
/// </summary>
public static class VoiceMacroTextFormat
{
    private static readonly Dictionary<string, MacroActionType> LabelToType = new()
    {
        ["打开"] = MacroActionType.LaunchApp,
        ["打字"] = MacroActionType.TypeText,
        ["按键"] = MacroActionType.SendKey,
    };

    private static readonly Dictionary<MacroActionType, string> TypeToLabel =
        LabelToType.ToDictionary(kv => kv.Value, kv => kv.Key);

    public static string Format(IEnumerable<VoiceMacro> macros) =>
        string.Join(Environment.NewLine, macros.Select(m =>
            $"{m.TriggerPhrase}|{string.Join(";", m.Actions.Select(a => $"{TypeToLabel[a.Type]}:{a.Value}"))}"));

    /// <summary>
    /// Parses the text box content, one macro per line. A malformed action token is skipped
    /// (not the whole line); a line that ends up with zero valid actions, or has no trigger
    /// phrase, is skipped entirely - same "one typo doesn't lose everything else" behavior as
    /// <see cref="VoiceCommandTextFormat.Parse"/>.
    /// </summary>
    public static List<VoiceMacro> Parse(string? text)
    {
        var result = new List<VoiceMacro>();
        foreach (var rawLine in (text ?? "").Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            var idx = line.IndexOf('|');
            if (idx < 0) continue;

            var trigger = line[..idx].Trim();
            var actionsPart = line[(idx + 1)..].Trim();
            if (trigger.Length == 0 || actionsPart.Length == 0) continue;

            var actions = new List<MacroAction>();
            foreach (var actionToken in actionsPart.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var colonIdx = actionToken.IndexOf(':');
                if (colonIdx < 0) continue;

                var typeLabel = actionToken[..colonIdx].Trim();
                var value = actionToken[(colonIdx + 1)..].Trim();
                if (value.Length == 0) continue;
                if (!LabelToType.TryGetValue(typeLabel, out var type)) continue;

                actions.Add(new MacroAction(type, value));
            }
            if (actions.Count == 0) continue;

            result.Add(new VoiceMacro(trigger, actions));
        }
        return result;
    }
}
