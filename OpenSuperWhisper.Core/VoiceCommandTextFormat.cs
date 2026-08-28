using OpenSuperWhisper.Core.Models;

namespace OpenSuperWhisper.Core;

/// <summary>
/// Converts between a <see cref="VoiceCommandDefinition"/> list and the simple one-line-per-command
/// plain-text format shown in the Settings window's F05 editor: <c>动作|触发词</c>, e.g.
/// <c>换行|换行</c>. Kept in Core (not SettingsWindow.xaml.cs) so it's plain string logic with no
/// WPF dependency - testable head-less, and reused as-is by the settings UI.
/// </summary>
public static class VoiceCommandTextFormat
{
    private static readonly Dictionary<string, VoiceCommandAction> LabelToAction = new()
    {
        ["取消"] = VoiceCommandAction.CancelDictation,
        ["换行"] = VoiceCommandAction.SendEnter,
        ["大写后缀"] = VoiceCommandAction.UppercaseSuffix,
    };

    private static readonly Dictionary<VoiceCommandAction, string> ActionToLabel =
        LabelToAction.ToDictionary(kv => kv.Value, kv => kv.Key);

    public static string Format(IEnumerable<VoiceCommandDefinition> commands) =>
        string.Join(Environment.NewLine, commands.Select(c => $"{ActionToLabel[c.Action]}|{c.Phrase}"));

    /// <summary>
    /// Parses the text box content, one command per line. A line that's blank, or doesn't parse
    /// (missing '|', empty phrase, unknown action label), is silently skipped rather than
    /// aborting the whole parse - a typo on one line must not lose every other line the user
    /// already configured.
    /// </summary>
    public static List<VoiceCommandDefinition> Parse(string? text)
    {
        var result = new List<VoiceCommandDefinition>();
        foreach (var rawLine in (text ?? "").Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            var idx = line.IndexOf('|');
            if (idx < 0) continue;

            var label = line[..idx].Trim();
            var phrase = line[(idx + 1)..].Trim();
            if (phrase.Length == 0) continue;
            if (!LabelToAction.TryGetValue(label, out var action)) continue;

            result.Add(new VoiceCommandDefinition(phrase, action));
        }
        return result;
    }
}
