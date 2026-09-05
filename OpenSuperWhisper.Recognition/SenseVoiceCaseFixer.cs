using System.Text;

namespace OpenSuperWhisper.Recognition;

/// <summary>
/// SenseVoice's tokenizer emits English in ALL CAPS ("帮我 COMMIT 一下这段代码") - an artifact of
/// its uppercase BPE vocabulary, not a transcription of shouting. This fixer is engine-specific
/// post-processing owned by SenseVoiceTranscriptionEngine (never applied to Whisper output, which
/// already has natural casing).
///
/// Deliberately conservative rules:
/// - A run of 2+ uppercase A-Z letters forming a whole word is lowercased ("COMMIT" -> "commit").
///   Real acronyms ("API", "GPU") get lowercased too - that's the accepted tradeoff, and the
///   existing term dictionary (applied later in DictationController's chain) is the user's tool
///   to force specific words back ("api" -> "API").
/// - Single letters are left alone ("I" stays "I").
/// - After punctuation restoration, the first Latin letter of the text and of each sentence that
///   follows a Latin sentence ender (. ! ?) is re-capitalized. CJK enders (。！？) deliberately
///   do NOT trigger capitalization - "好的。ok 收到" reads naturally as lowercase.
/// </summary>
public static class SenseVoiceCaseFixer
{
    /// <summary>Step 1, run before punctuation restoration.</summary>
    public static string LowercaseAllCapsWords(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c is >= 'A' and <= 'Z')
            {
                int start = i;
                while (i < text.Length && text[i] is >= 'A' and <= 'Z') i++;
                // Whole-word check: the run must not be followed by more letters (e.g. "McDonald"
                // has an uppercase run followed by lowercase - leave mixed-case words untouched).
                bool followedByLetter = i < text.Length && char.IsLetter(text[i]);
                int len = i - start;
                if (len >= 2 && !followedByLetter)
                {
                    for (int j = start; j < i; j++) sb.Append(char.ToLowerInvariant(text[j]));
                }
                else
                {
                    sb.Append(text, start, len);
                }
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
        return sb.ToString();
    }

    /// <summary>Step 2, run after punctuation restoration.</summary>
    public static string CapitalizeSentenceStarts(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var chars = text.ToCharArray();
        bool atSentenceStart = true;
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (atSentenceStart && c is >= 'a' and <= 'z')
            {
                chars[i] = char.ToUpperInvariant(c);
                atSentenceStart = false;
            }
            else if (c is '.' or '!' or '?')
            {
                atSentenceStart = true;
            }
            else if (!char.IsWhiteSpace(c))
            {
                atSentenceStart = false;
            }
        }
        return new string(chars);
    }
}
