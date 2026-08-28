using System.Text.RegularExpressions;
using OpenSuperWhisper.Core.Models;

namespace OpenSuperWhisper.Core;

/// <summary>
/// Applies the user's professional-vocabulary corrections (F02) to a transcript. A "wrong" term
/// made only of CJK characters is matched as a plain substring (CJK text has no spaces to define
/// word boundaries); one containing any Latin letter/digit is matched case-insensitively at word
/// boundaries only, so e.g. a rule for "claude" doesn't also fire inside "claudette".
/// </summary>
public static class TermDictionary
{
    private static readonly Regex LatinChar = new(@"[A-Za-z0-9]", RegexOptions.Compiled);

    public static string Apply(string text, IReadOnlyList<TermCorrection> corrections)
    {
        if (string.IsNullOrEmpty(text) || corrections.Count == 0) return text;

        var result = text;
        foreach (var c in corrections)
        {
            if (string.IsNullOrEmpty(c.Wrong) || c.Wrong == c.Correct) continue;

            if (LatinChar.IsMatch(c.Wrong))
            {
                var pattern = $@"(?<![A-Za-z0-9]){Regex.Escape(c.Wrong)}(?![A-Za-z0-9])";
                // Substitute via a MatchEvaluator (not a literal replacement string) so a
                // "correct" term that happens to contain '$' can't be misread as a backreference.
                result = Regex.Replace(result, pattern, _ => c.Correct, RegexOptions.IgnoreCase);
            }
            else
            {
                result = result.Replace(c.Wrong, c.Correct);
            }
        }
        return result;
    }
}
