using System.Runtime.InteropServices;

namespace OpenSuperWhisper.Hotkeys;

internal static class NativeMethods
{
    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string lpModuleName);

    // Used by ActiveWindowInfo (F06/F12) to find which process owns the focused window - the
    // standard GetForegroundWindow + GetWindowThreadProcessId combo documented on MSDN.
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // Used by GlobalToggleWindowHotkey (F32, show/hide the main window) - RegisterHotKey/WM_HOTKEY
    // is a simpler, OS-arbitrated alternative to the WH_KEYBOARD_LL hook above, appropriate here
    // because that feature only needs one "pressed" edge, not press/release semantics.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const int WM_HOTKEY = 0x0312;

    // MOD_* fsModifiers flags for RegisterHotKey (winuser.h).
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;

    /// <summary>Added automatically to every RegisterHotKey call GlobalToggleWindowHotkey makes.
    /// Without it (Windows Vista+ only, which is a non-issue since this app targets modern
    /// Windows anyway) holding the combo down would re-fire WM_HOTKEY on the OS's own key-repeat
    /// cadence, toggling the window open/closed several times per second for as long as it's
    /// held instead of once per deliberate press.</summary>
    public const uint MOD_NOREPEAT = 0x4000;

    /// <summary>Parenting a window to this well-known (not a real handle) value makes it
    /// message-only: never shown, no taskbar/Alt-Tab entry, doesn't receive broadcast messages -
    /// exactly the mailbox GlobalToggleWindowHotkey needs for WM_HOTKEY and nothing more.</summary>
    public static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);
}
