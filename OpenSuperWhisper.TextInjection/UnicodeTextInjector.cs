using System.Runtime.InteropServices;
using System.Text;
using OpenSuperWhisper.Core;

namespace OpenSuperWhisper.TextInjection;

/// <summary>
/// Types text into whichever window currently owns OS focus, by synthesizing Unicode
/// keystrokes via SendInput. Works across normal apps, browsers, Electron apps, etc.
/// without needing the clipboard.
/// </summary>
public sealed class UnicodeTextInjector : ITextInjector
{
    public void InjectText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var sanitized = Sanitize(text);
        if (sanitized.Length == 0) return;

        var inputs = new List<NativeMethods.INPUT>(sanitized.Length * 2);
        foreach (var ch in sanitized)
        {
            inputs.Add(MakeKeyInput(ch, keyUp: false));
            inputs.Add(MakeKeyInput(ch, keyUp: true));
        }

        var arr = inputs.ToArray();
        uint sent = NativeMethods.SendInput((uint)arr.Length, arr, Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent != (uint)arr.Length)
        {
            // SendInput returns the number of events it actually queued - if that doesn't match
            // what we asked for, the text did not (fully) land wherever focus was. Treat this as
            // a hard failure rather than silently reporting success: the caller
            // (DictationController) only saves history / raises TranscriptionCompleted after
            // InjectText returns without throwing.
            var win32Error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"SendInput 未能注入全部按键事件（应发送 {arr.Length} 个事件，实际接受 {sent} 个），Win32 错误码 {win32Error}。文本很可能没有真正输入到目标窗口。");
        }
    }

    /// <summary>
    /// Strips characters that must never reach an arbitrary focused control unexamined:
    /// C0 control characters (\r, \n, \t, and other codes below 0x20) - a stray newline from a
    /// misheard transcript can submit a form or trigger "send" in whatever currently has focus -
    /// and Unicode bidi-control characters (U+202A-U+202E, U+2066-U+2069), which can reorder how
    /// the injected text visually renders relative to what's around it.
    /// </summary>
    private static string Sanitize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch < 0x20) continue;
            var code = (int)ch;
            if (code is >= 0x202A and <= 0x202E) continue; // bidi embedding/override controls
            if (code is >= 0x2066 and <= 0x2069) continue; // bidi isolate controls
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static NativeMethods.INPUT MakeKeyInput(char ch, bool keyUp)
    {
        return new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = 0,
                    wScan = ch,
                    dwFlags = NativeMethods.KEYEVENTF_UNICODE | (keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0),
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }
}
