namespace OpenSuperWhisper.Core;

/// <summary>
/// F11: when AppSettings.ShowDraftBeforeInject is on, DictationController calls this after
/// recognition (and after term-dictionary/punctuation post-processing) but before injecting text,
/// so the user can glance at - and optionally tweak - what's about to be typed.
/// </summary>
public interface IDraftConfirmation
{
    /// <summary>
    /// Presents <paramref name="text"/> for review/editing. Must not block the calling thread
    /// (the caller awaits the returned task; nothing else - including the global hotkey - is
    /// blocked while it's pending). Returns the possibly-edited text to inject, or null if the
    /// user cancelled (Esc, or dismissing the UI another way) - callers must not inject text or
    /// save history when this returns null.
    /// </summary>
    Task<string?> ConfirmAsync(string text);
}
