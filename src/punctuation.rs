//! Terminal-punctuation auto-correction (F03): a spoken utterance that just
//! trails off (no "period" spoken, mic released mid-thought) often comes back
//! from the recognizer with no terminal punctuation at all -- it reads like a
//! sentence with the last character cut off. This appends one when it's
//! clearly missing, so dictated text reads like something a person typed
//! rather than a live transcript. Deliberately narrow in scope: it only ever
//! touches the very end of the string, never rewrites or re-punctuates the
//! interior.
//!
//! Faithful port of the C# reference's `PunctuationFixer.Apply`
//! (`src-reference/OpenSuperWhisper.Core/PunctuationFixer.cs`), which used a
//! `Regex` purely for character-class membership at a fixed string position
//! (no lookaround, no quantified backtracking) -- reimplemented here as plain
//! char classification, no regex dependency needed.

/// Characters that count as "already terminated" when they are the very last
/// character of the (right-trimmed) text -- Latin/CJK terminal punctuation,
/// plus closing quotes/brackets that can legally follow terminal punctuation
/// one position earlier (e.g. a sentence ending `"already said."` ). Ported
/// character-for-character from the C# original's regex character class
/// `[.!?。！？…、，,;:；："'）)\]】"']` (verified against the compiled source,
/// not retyped from memory): note this deliberately does NOT include CJK
/// closing brackets like `」`/`』`/`》` -- per the literal C# regex, only
/// exactly these characters count, so text ending in one of those unlisted
/// brackets is treated as still needing punctuation.
const TERMINAL_CHARS: &[char] = &[
    '.', '!', '?', '。', '！', '？', '…', '、', '，', ',', ';', ':', '；', '：', '"', '\'', '）', ')', ']', '】',
    '\u{201D}', // ” right double quotation mark
    '\u{2019}', // ' right single quotation mark
];

/// A char is CJK for the purposes of the "mostly CJK" ratio below if it falls
/// in the common Chinese ideograph, Japanese kana, or Korean hangul blocks --
/// good enough to pick `。` vs `.` without a full language-detection library
/// for a one-character decision. Ranges match the C# original's regex
/// (`[一-鿿぀-ヿ가-힯]`), expressed here as explicit Unicode scalar ranges.
fn is_cjk(c: char) -> bool {
    let cp = c as u32;
    (0x4E00..=0x9FFF).contains(&cp) // CJK unified ideographs
        || (0x3041..=0x30FF).contains(&cp) // hiragana + katakana
        || (0xAC00..=0xD7A3).contains(&cp) // hangul syllables
}

/// Appends one terminal punctuation mark to `text` if it looks like it's
/// missing one, otherwise returns it unchanged.
///
/// Note: the "non-whitespace" count below uses Rust's general Unicode
/// `char::is_whitespace` rather than the C# original's narrower
/// `.Replace(" ", "")` (which strips only the ASCII space character before
/// counting) -- a deliberate, documented widening rather than a silent
/// deviation: it makes the CJK-ratio decision robust to tabs/full-width
/// spaces/newlines embedded mid-text as well, which can only make the ratio
/// more accurate, never less.
pub fn apply(text: &str) -> String {
    if text.trim().is_empty() {
        return text.to_string();
    }

    let trimmed = text.trim_end();

    if let Some(last) = trimmed.chars().next_back()
        && TERMINAL_CHARS.contains(&last)
    {
        return trimmed.to_string();
    }

    let non_whitespace: Vec<char> = trimmed.chars().filter(|c| !c.is_whitespace()).collect();
    let cjk_count = non_whitespace.iter().filter(|c| is_cjk(**c)).count();
    let is_mostly_cjk = !non_whitespace.is_empty() && cjk_count * 2 >= non_whitespace.len();

    let suffix = if is_mostly_cjk { '。' } else { '.' };
    format!("{trimmed}{suffix}")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn whitespace_only_is_returned_unchanged() {
        assert_eq!(apply("   "), "   ");
        assert_eq!(apply(""), "");
    }

    #[test]
    fn cjk_text_with_no_terminal_punctuation_gets_cjk_period() {
        assert_eq!(apply("今天天气很好"), "今天天气很好。");
    }

    #[test]
    fn english_text_with_no_terminal_punctuation_gets_latin_period() {
        assert_eq!(apply("this is a test"), "this is a test.");
    }

    #[test]
    fn text_already_ending_in_exclamation_is_unchanged() {
        assert_eq!(apply("watch out!"), "watch out!");
    }

    #[test]
    fn text_ending_in_unlisted_cjk_closing_bracket_still_gets_punctuated() {
        // `」` is a CJK closing bracket but is NOT in the literal terminal set
        // ported from the C# regex, so it must still receive a trailing mark.
        assert_eq!(apply("他说「你好」"), "他说「你好」。");
    }

    #[test]
    fn trailing_whitespace_is_dropped_before_appending() {
        assert_eq!(apply("no terminator here   "), "no terminator here.");
    }

    #[test]
    fn mixed_text_uses_majority_script_for_the_mark() {
        // 4 CJK chars vs 3 Latin chars (spaces excluded) -> mostly CJK.
        assert_eq!(apply("你好 abc 世界啊"), "你好 abc 世界啊。");
    }
}
