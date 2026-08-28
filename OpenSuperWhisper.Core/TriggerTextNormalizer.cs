using System.Text.RegularExpressions;

namespace OpenSuperWhisper.Core;

/// <summary>
/// Strips a trailing run of punctuation/whitespace so a spoken command or macro trigger phrase
/// (F05/F13) still matches even after PunctuationFixer appended a period, or Whisper itself
/// produced trailing punctuation the user never actually said out loud. Shared by
/// <see cref="VoiceCommandMatcher"/> and <see cref="MacroExecutor"/> so both apply the exact same
/// normalization. Only ever touches the end of the string - same scope restriction as
/// <see cref="PunctuationFixer"/>.
/// </summary>
internal static class TriggerTextNormalizer
{
    private static readonly Regex TrailingNoise =
        new(@"[\s.!?。！？…、，,;:；：""'）)\]】”’]+$", RegexOptions.Compiled);

    public static string Normalize(string text) => TrailingNoise.Replace(text.Trim(), "");
}
