namespace OpenSuperWhisper.Core;

/// <summary>Types text into whatever window currently has OS focus.</summary>
public interface ITextInjector
{
    void InjectText(string text);

    /// <summary>
    /// Sends a virtual-key press (key-down then key-up) <paramref name="times"/> times, as a
    /// single input batch. Used by F05 voice commands (e.g. Enter for "换行", repeated Backspace
    /// as a best-effort undo of this app's own last injection for "删除这段") and F13 macro
    /// "按键" actions. This is a real OS-level keystroke - like a physical key press - so it can
    /// only ever affect whatever currently has OS focus; it has no way to "know" what's already
    /// in the target control, so it can only undo what OpenSuperWhisper itself just typed, never
    /// arbitrary pre-existing content in another app.
    /// </summary>
    void SendVirtualKey(ushort virtualKeyCode, int times = 1);
}
