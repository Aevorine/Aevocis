using System.Runtime.InteropServices;
using OpenSuperWhisper.Core;

namespace OpenSuperWhisper.Hotkeys;

/// <summary>
/// System-wide push-to-talk: fires PressStarted on key-down and PressEnded on key-up for one
/// virtual-key code, regardless of which window has focus. Uses a WH_KEYBOARD_LL hook because
/// RegisterHotKey only reports the final combo, never key-up separately.
/// </summary>
public sealed class GlobalPushToTalkHotkey : IHotkeyListener
{
    private uint _vkCode;
    private NativeMethods.LowLevelKeyboardProc? _proc;
    private IntPtr _hookId = IntPtr.Zero;
    private bool _isDown;

    public event Action? PressStarted;
    public event Action? PressEnded;

    /// <summary>The Win32 error code from the last failed <see cref="Start"/> call (via
    /// GetLastWin32Error right after SetWindowsHookEx returned NULL), or 0 if the last Start
    /// succeeded / hasn't been called yet.</summary>
    public int LastWin32Error { get; private set; }

    public GlobalPushToTalkHotkey(int virtualKeyCode = 0xA3) // default: VK_RCONTROL
    {
        _vkCode = (uint)virtualKeyCode;
    }

    /// <summary>
    /// Swaps the virtual-key code this listener watches for, live, without stopping or
    /// recreating the underlying hook. Used by the Settings window to rebind the
    /// push-to-talk key without an app restart. If the previously bound key is currently
    /// held down, its key-up will simply be ignored (matched against the new code instead).
    /// </summary>
    public void SetVirtualKeyCode(int virtualKeyCode)
    {
        _vkCode = (uint)virtualKeyCode;
        _isDown = false;
    }

    /// <summary>Registers the WH_KEYBOARD_LL hook. Returns false - and sets
    /// <see cref="LastWin32Error"/> - if SetWindowsHookEx failed (its documented failure value
    /// is IntPtr.Zero, the same value this field starts at, so the return had to be checked
    /// explicitly rather than trusted).</summary>
    public bool Start()
    {
        if (_hookId != IntPtr.Zero) return true;
        _proc = HookCallback;
        using var curModule = System.Diagnostics.Process.GetCurrentProcess().MainModule;
        _hookId = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL,
            _proc,
            NativeMethods.GetModuleHandle(curModule!.ModuleName!),
            0);

        if (_hookId == IntPtr.Zero)
        {
            LastWin32Error = Marshal.GetLastWin32Error();
            return false;
        }
        LastWin32Error = 0;
        return true;
    }

    public void Stop()
    {
        if (_hookId == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_hookId);
        _hookId = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = wParam.ToInt32();
            var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            if (data.vkCode == _vkCode)
            {
                if ((msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN) && !_isDown)
                {
                    _isDown = true;
                    PressStarted?.Invoke();
                }
                else if (msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP)
                {
                    _isDown = false;
                    PressEnded?.Invoke();
                }
            }
        }
        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose() => Stop();
}
