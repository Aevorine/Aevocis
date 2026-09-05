using System.Linq;
using System.Text.RegularExpressions;
using OpenSuperWhisper.Core.Models;

namespace OpenSuperWhisper.Core;

/// <summary>
/// F33 term-dictionary self-learning. The ONLY input this ever sees is a (draft text shown in the
/// F11 confirm window, text the user actually confirmed) pair - see DictationController.OnPressEnded's
/// ShowDraftBeforeInject branch, the sole call site. There is no other entry point, no keystroke/
/// clipboard hook, and nothing here ever runs when ShowDraftBeforeInject is off or when the user
/// didn't edit anything: the whole feature is "notice when the user keeps fixing the same thing in
/// this app's own confirm window, and offer to fix it automatically next time."
/// </summary>
public static class TermLearning
{
    /// <summary>Number of separate dictations the same (original -> replacement) edit must show up
    /// in before it's auto-added to the real term dictionary.</summary>
    public const int PromotionThreshold = 3;

    /// <summary>A token is either a maximal run of ASCII letters/digits (so "Claude" or "F31" is
    /// one token, not one per character) or any single other character (so CJK text - which has no
    /// spaces to define word boundaries - is diffed per character and merged back into whole spans
    /// by <see cref="Diff"/> below). Whitespace/punctuation are tokens too, purely so the alignment
    /// lines up correctly around them; they're filtered out of anything actually recorded by
    /// <see cref="RecordAndPromote"/>.</summary>
    private static readonly Regex TokenPattern = new(@"[A-Za-z0-9]+|.", RegexOptions.Singleline | RegexOptions.Compiled);

    private static List<string> Tokenize(string text) =>
        TokenPattern.Matches(text).Select(m => m.Value).ToList();

    /// <summary>
    /// Aligns <paramref name="original"/> against <paramref name="edited"/> via a standard
    /// longest-common-subsequence (LCS) token alignment - dictated sentences are short (well under
    /// a few hundred tokens), so the O(n*m) DP table this uses is trivial - and returns every
    /// contiguous span that changed on both sides as (originalSpan, editedSpan) pairs. A pure
    /// insertion or deletion (tokens added/removed with nothing on the other side to pair them
    /// with) is intentionally NOT returned: a term-dictionary rule needs a non-empty "wrong" text
    /// to match against (see <see cref="TermDictionary.Apply"/>), so there's nothing to learn from
    /// "the user just deleted a word".
    /// </summary>
    public static List<(string Original, string Replacement)> Diff(string original, string edited)
    {
        var origTokens = Tokenize(original ?? "");
        var editTokens = Tokenize(edited ?? "");
        int n = origTokens.Count, m = editTokens.Count;

        var dp = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                dp[i, j] = origTokens[i] == editTokens[j]
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        // Walk the table forward from (0,0), greedily following whichever direction the DP table
        // says the LCS goes through, collecting the matched (i, j) anchor pairs along the way.
        var anchors = new List<(int I, int J)>();
        int a = 0, b = 0;
        while (a < n && b < m)
        {
            if (origTokens[a] == editTokens[b])
            {
                anchors.Add((a, b));
                a++;
                b++;
            }
            else if (dp[a + 1, b] >= dp[a, b + 1])
            {
                a++;
            }
            else
            {
                b++;
            }
        }
        anchors.Add((n, m)); // virtual trailing anchor closes out the final gap, if any

        var result = new List<(string, string)>();
        int prevI = -1, prevJ = -1;
        foreach (var (i, j) in anchors)
        {
            var origGapLen = i - (prevI + 1);
            var editGapLen = j - (prevJ + 1);
            if (origGapLen > 0 && editGapLen > 0)
            {
                var origSpan = string.Concat(origTokens.Skip(prevI + 1).Take(origGapLen));
                var editSpan = string.Concat(editTokens.Skip(prevJ + 1).Take(editGapLen));
                result.Add((origSpan, editSpan));
            }
            prevI = i;
            prevJ = j;
        }
        return result;
    }

    /// <summary>
    /// Records one dictation's worth of diff pairs (from <paramref name="draftText"/> -&gt;
    /// <paramref name="confirmedText"/>) into <paramref name="observed"/> - mutated in place:
    /// an existing (original, replacement) pair's <see cref="ObservedTermEdit.Count"/> is bumped,
    /// a new one is appended with Count 1 - and returns whichever pairs just reached
    /// <see cref="PromotionThreshold"/> in THIS call. A pair that reaches the threshold is removed
    /// from <paramref name="observed"/> - its job is done; from here on it lives in the real term
    /// dictionary, where <c>TermDictionaryWindow</c> already lets the user see/edit/undo it like
    /// any manually-added entry.
    /// </summary>
    public static List<(string Original, string Replacement)> RecordAndPromote(
        string draftText, string confirmedText, List<ObservedTermEdit> observed)
    {
        var promoted = new List<(string, string)>();
        if (draftText == confirmedText) return promoted;

        foreach (var (rawOriginal, rawReplacement) in Diff(draftText, confirmedText))
        {
            var original = rawOriginal.Trim();
            var replacement = rawReplacement.Trim();
            // Nothing to learn from a span that trims to nothing, or that trims down to the same
            // text on both sides (e.g. a whitespace-only difference the tokenizer still paired up).
            if (original.Length == 0 || replacement.Length == 0 || original == replacement)
                continue;

            var existing = observed.Find(o => o.Original == original && o.Replacement == replacement);
            if (existing is null)
            {
                existing = new ObservedTermEdit { Original = original, Replacement = replacement, Count = 0 };
                observed.Add(existing);
            }
            existing.Count++;

            if (existing.Count >= PromotionThreshold)
            {
                promoted.Add((existing.Original, existing.Replacement));
                observed.Remove(existing);
            }
        }
        return promoted;
    }
}
