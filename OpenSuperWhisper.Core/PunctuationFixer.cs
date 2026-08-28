using System.Text.RegularExpressions;

namespace OpenSuperWhisper.Core;

/// <summary>
/// Whisper's own output usually already carries internal punctuation, but a spoken utterance
/// that just trails off (no "period" spoken, mic released mid-thought) often comes back with no
/// terminal punctuation at all - it reads like a sentence with the last character cut off. This
/// appends one when it's clearly missing, so dictated text reads like something a person typed
/// rather than a live transcript. Deliberately narrow in scope: it only ever touches the very
/// end of the string, never rewrites or re-punctuates the interior.
/// </summary>
public static class PunctuationFixer
{
    // Anything already ending in sentence/clause-final punctuation (Latin or CJK, plus quotes/
    // brackets that can legally follow it) is left untouched.
    private static readonly Regex AlreadyPunctuated = new(@"[.!?。！？…、，,;:；：""'）)\]】”’]\s*$", RegexOptions.Compiled);

    // A string is "mostly CJK" if most of its non-whitespace characters fall in the common
    // Chinese/Japanese/Korean ideograph and kana blocks - good enough to pick 。 vs . without
    // pulling in a full language-detection library for a one-character decision.
    private static readonly Regex CjkChar = new(@"[一-鿿぀-ヿ가-힯]", RegexOptions.Compiled);

    public static string Apply(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var trimmed = text.TrimEnd();
        if (trimmed.Length == 0 || AlreadyPunctuated.IsMatch(trimmed)) return text;

        var nonSpace = trimmed.Replace(" ", "");
        var cjkCount = CjkChar.Matches(nonSpace).Count;
        var isMostlyCjk = nonSpace.Length > 0 && cjkCount * 2 >= nonSpace.Length;

        return trimmed + (isMostlyCjk ? "。" : ".");
    }
}
