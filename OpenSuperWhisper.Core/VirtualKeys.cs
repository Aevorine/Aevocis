namespace OpenSuperWhisper.Core;

/// <summary>
/// A handful of Win32 virtual-key codes (from winuser.h VK_*) that F05 voice commands
/// (Enter/Backspace) and F13 macro "SendKey" actions need to pass to
/// <see cref="ITextInjector.SendVirtualKey"/>. Deliberately just the common, useful-for-dictation
/// ones - not a full VK_* table.
/// </summary>
public static class VirtualKeys
{
    public const ushort Enter = 0x0D;
    public const ushort Backspace = 0x08;
    public const ushort Tab = 0x09;
    public const ushort Escape = 0x1B;
    public const ushort Space = 0x20;

    private static readonly Dictionary<string, ushort> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Enter"] = Enter,
        ["Return"] = Enter,
        ["回车"] = Enter,
        ["换行"] = Enter,
        ["Backspace"] = Backspace,
        ["退格"] = Backspace,
        ["删除"] = Backspace,
        ["Tab"] = Tab,
        ["Escape"] = Escape,
        ["Esc"] = Escape,
        ["Space"] = Space,
        ["空格"] = Space,
    };

    /// <summary>Parses a key name (from the F13 macro text editor, e.g. "按键:Enter") into a
    /// virtual-key code. Case-insensitive; accepts both the English VK name and a couple of
    /// common Chinese aliases.</summary>
    public static bool TryParse(string name, out ushort virtualKeyCode)
    {
        virtualKeyCode = 0;
        if (string.IsNullOrWhiteSpace(name)) return false;
        return ByName.TryGetValue(name.Trim(), out virtualKeyCode);
    }
}
