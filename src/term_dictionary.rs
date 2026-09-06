//! Professional-vocabulary term corrections (F02): persisted user rules that
//! rewrite recognizer mishearings in the final transcript (e.g. SenseVoice
//! outputting "克劳德" for the spoken product name "Claude"). Storage is
//! `%LOCALAPPDATA%\Aevocis\terms.json`, same atomic write pattern as
//! `settings.rs::save`.
//!
//! Faithful port of the C# reference's `TermDictionary.Apply`
//! (`src-reference/OpenSuperWhisper.Core/TermDictionary.cs`), which used
//! `Regex` with a `(?<!...)...(?!...)` negative-lookaround word-boundary
//! pattern for Latin/alphanumeric terms. The `regex` crate this project could
//! otherwise pull in does NOT support lookaround, so that boundary check is
//! reimplemented here by hand via manual char scanning instead of adding a
//! regex dependency.

use std::path::PathBuf;

use serde::{Deserialize, Serialize};

#[derive(Serialize, Deserialize, Clone)]
pub struct TermCorrection {
    pub wrong: String,
    pub correct: String,
}

fn terms_path() -> PathBuf {
    crate::app_data_dir().join("terms.json")
}

/// Loads persisted corrections. Returns an empty list if the file does not
/// exist yet or fails to parse (e.g. hand-edited into invalid JSON) -- a
/// corrupt terms file must never prevent the app from starting, matching the
/// degrade-to-empty behavior of `settings::load` and `history::load`.
pub fn load() -> Vec<TermCorrection> {
    std::fs::read_to_string(terms_path())
        .ok()
        .and_then(|s| serde_json::from_str(&s).ok())
        .unwrap_or_default()
}

/// Atomically persists `list`: write to a sibling `.tmp` file, then rename
/// over the real path, so a crash or concurrent read mid-write can never
/// observe a half-written file -- identical pattern to `settings.rs::save`.
pub fn save(list: &[TermCorrection]) {
    let path = terms_path();
    let Ok(json) = serde_json::to_string_pretty(list) else { return };
    let tmp = path.with_extension("json.tmp");
    if std::fs::write(&tmp, json).is_ok() {
        let _ = std::fs::rename(&tmp, &path);
    }
}

/// True if `s` contains at least one ASCII letter or digit. This is the
/// switch between the two matching modes below: a "wrong" term with any
/// Latin/digit character is matched at word boundaries (so a rule for
/// "claude" doesn't also fire inside "claudette"), while a term made purely
/// of CJK/punctuation is matched as a plain substring, since CJK text has no
/// spaces to define word boundaries in the first place.
fn has_ascii_alnum(s: &str) -> bool {
    s.chars().any(|c| c.is_ascii_alphanumeric())
}

/// Two chars are "the same letter" for matching purposes, compared via full
/// Unicode case folding (`to_lowercase()`, which yields an iterator of chars
/// rather than a single char, since a few characters lowercase to more than
/// one) rather than `eq_ignore_ascii_case`, so this stays correct even though
/// `wrong` is only guaranteed to contain *at least one* ASCII alnum char --
/// it may still mix in accented Latin or other multi-byte characters.
fn chars_match_ci(a: char, b: char) -> bool {
    a.to_lowercase().eq(b.to_lowercase())
}

/// True if `c` must NOT be treated as adjacent to a word for boundary
/// purposes -- i.e. is itself part of a word. Mirrors the regex's
/// `[A-Za-z0-9]` boundary class exactly (deliberately ASCII-only, matching
/// the C# original's `[A-Za-z0-9]`, not a broader Unicode "is alphanumeric").
fn is_word_char(c: char) -> bool {
    c.is_ascii_alphanumeric()
}

/// "Latin mode" replacement: finds every case-insensitive, non-overlapping
/// occurrence of `wrong` in `text` such that the character immediately
/// before the match (if any) and the character immediately after it (if any)
/// are both NOT `[A-Za-z0-9]`, and replaces each with `correct` verbatim
/// (never case-adjusted, matching the C# original's `MatchEvaluator` which
/// always substitutes the literal `Correct` string regardless of how `Wrong`
/// was cased in the source text).
///
/// Implemented via manual char-index scanning rather than `str::replace`
/// (which has no notion of boundaries) or the `regex` crate (whose engine
/// doesn't support the lookaround the C# pattern relies on). Matches are
/// found against the original `text`, not a copy already mutated by earlier
/// replacements in this same call -- identical semantics to .NET's
/// `Regex.Replace`, which likewise scans the original string and advances
/// past each match it makes.
///
/// Operates on `Vec<char>` throughout (never byte indices) so this stays
/// correct on UTF-8 multi-byte input (e.g. a "wrong" rule mixing Latin and
/// Chinese characters) without risking a mid-codepoint slice panic.
fn apply_latin_mode(text: &str, wrong: &str, correct: &str) -> String {
    let chars: Vec<char> = text.chars().collect();
    let wrong_chars: Vec<char> = wrong.chars().collect();
    let wlen = wrong_chars.len();
    if wlen == 0 {
        return text.to_string();
    }

    let mut result = String::with_capacity(text.len());
    let mut i = 0usize;
    while i < chars.len() {
        let fits = i + wlen <= chars.len();
        let content_matches = fits
            && chars[i..i + wlen]
                .iter()
                .zip(wrong_chars.iter())
                .all(|(a, b)| chars_match_ci(*a, *b));

        if content_matches {
            let before_ok = i == 0 || !is_word_char(chars[i - 1]);
            let after_ok = i + wlen >= chars.len() || !is_word_char(chars[i + wlen]);
            if before_ok && after_ok {
                result.push_str(correct);
                i += wlen;
                continue;
            }
        }

        // Either the content didn't match at this position, or it matched
        // but failed a boundary check (e.g. "claude" inside "claudette") --
        // either way, keep this one character as-is and keep looking from
        // the next position, exactly like a failed regex match attempt
        // advancing by one.
        result.push(chars[i]);
        i += 1;
    }
    result
}

/// "CJK mode" replacement: plain substring replace, no boundary logic --
/// CJK text has no spaces, so the Latin word-boundary notion doesn't apply.
fn apply_cjk_mode(text: &str, wrong: &str, correct: &str) -> String {
    text.replace(wrong, correct)
}

/// Applies `corrections` to `text` in list order, each pass operating on the
/// *previous* pass's output (sequential, not simultaneous) -- matching the
/// C# original's `foreach` loop that reassigns `result` each iteration.
pub fn apply(text: &str, corrections: &[TermCorrection]) -> String {
    let mut result = text.to_string();
    for c in corrections {
        if c.wrong.is_empty() || c.wrong == c.correct {
            continue;
        }
        result = if has_ascii_alnum(&c.wrong) {
            apply_latin_mode(&result, &c.wrong, &c.correct)
        } else {
            apply_cjk_mode(&result, &c.wrong, &c.correct)
        };
    }
    result
}

#[cfg(test)]
mod tests {
    use super::*;

    fn correction(wrong: &str, correct: &str) -> TermCorrection {
        TermCorrection { wrong: wrong.to_string(), correct: correct.to_string() }
    }

    #[test]
    fn latin_boundary_does_not_mangle_superstring() {
        let corrections = vec![correction("claude", "Claude")];
        assert_eq!(apply("I asked claude for help", &corrections), "I asked Claude for help");
        // "claudette" must be left untouched: the char after the "claude"
        // substring inside it ('t') is alnum, so the boundary check rejects it.
        assert_eq!(apply("claudette walked in", &corrections), "claudette walked in");
    }

    #[test]
    fn latin_mode_is_case_insensitive_but_replaces_verbatim() {
        let corrections = vec![correction("claude", "Claude")];
        assert_eq!(apply("CLAUDE said hi", &corrections), "Claude said hi");
    }

    #[test]
    fn cjk_mode_is_plain_substring_replace() {
        let corrections = vec![correction("克劳德", "Claude")];
        assert_eq!(apply("我问了克劳德一个问题", &corrections), "我问了Claude一个问题");
    }

    #[test]
    fn sequential_passes_use_previous_output() {
        // Second rule's "wrong" text is produced by the first rule's output,
        // proving passes chain rather than all matching the original text.
        let corrections = vec![correction("foo", "bar"), correction("bar", "baz")];
        assert_eq!(apply("foo", &corrections), "baz");
    }

    #[test]
    fn skips_empty_or_noop_rules() {
        let corrections = vec![correction("", "x"), correction("same", "same")];
        assert_eq!(apply("same text unchanged", &corrections), "same text unchanged");
    }

    #[test]
    fn utf8_multibyte_does_not_panic_or_corrupt() {
        let corrections = vec![correction("你好", "您好")];
        assert_eq!(apply("你好，世界", &corrections), "您好，世界");
    }
}
