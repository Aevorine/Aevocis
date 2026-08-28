using OpenSuperWhisper.Core.Models;

namespace OpenSuperWhisper.Core;

/// <summary>
/// F05 口头命令匹配。纯函数、无副作用——真正"做点什么"(撤销上一次注入、发送 Enter、大写后注入)
/// 是调用方(DictationController)的事，这里只回答"这句话命中了哪个命令，剩下什么内容"。
/// </summary>
public static class VoiceCommandMatcher
{
    public readonly record struct MatchResult(bool Matched, VoiceCommandAction Action, string RemainingText)
    {
        public static readonly MatchResult None = new(false, default, "");
    }

    /// <summary>
    /// <see cref="VoiceCommandAction.CancelDictation"/>/<see cref="VoiceCommandAction.SendEnter"/>
    /// only match when the *entire* (normalized) utterance equals the configured phrase - a
    /// dictation that happens to merely contain the word "换行" mid-sentence must still get typed
    /// normally. <see cref="VoiceCommandAction.UppercaseSuffix"/> instead matches when the
    /// utterance *ends with* the phrase and there's real content before it - "hello world 全部
    /// 大写" - so the command can apply to text spoken in the same breath.
    /// </summary>
    public static MatchResult Match(string text, IReadOnlyList<VoiceCommandDefinition> commands)
    {
        if (string.IsNullOrWhiteSpace(text) || commands.Count == 0) return MatchResult.None;

        var normalized = TriggerTextNormalizer.Normalize(text);
        if (normalized.Length == 0) return MatchResult.None;

        foreach (var cmd in commands)
        {
            var phrase = cmd.Phrase?.Trim();
            if (string.IsNullOrEmpty(phrase)) continue;

            if (cmd.Action == VoiceCommandAction.UppercaseSuffix)
            {
                if (normalized.Length <= phrase.Length) continue;
                if (!normalized.EndsWith(phrase, StringComparison.OrdinalIgnoreCase)) continue;

                var remaining = TriggerTextNormalizer.Normalize(normalized[..^phrase.Length]);
                if (remaining.Length == 0) continue; // nothing to uppercase - not a real match
                return new MatchResult(true, cmd.Action, remaining);
            }

            if (string.Equals(normalized, phrase, StringComparison.OrdinalIgnoreCase))
                return new MatchResult(true, cmd.Action, "");
        }

        return MatchResult.None;
    }
}
