using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace OpenSuperWhisper.Hotkeys;

/// <summary>
/// System-wide "show/hide the main window" hotkey (F32) - independent of, and running alongside,
/// <see cref="GlobalPushToTalkHotkey"/> (push-to-talk dictation). Built on Win32
/// RegisterHotKey/WM_HOTKEY rather than a WH_KEYBOARD_LL hook: RegisterHotKey only reports the
/// finished combo as a single "pressed" edge (all modifiers already held, main key just went
/// down) with no separate up-transition - exactly what a single toggle action needs.
/// GlobalPushToTalkHotkey needs the heavier hook instead because it also has to see the matching
/// key-up (Hold mode) and raw per-key transitions (Toggle mode); neither applies to a one-shot
/// toggle, so the simpler, OS-arbitrated RegisterHotKey mechanism is the better fit here - the OS
/// itself enforces that only one owner can hold a given combo at a time, which is exactly the
/// semantics a "one dedicated shortcut" feature wants.
///
/// RegisterHotKey delivers WM_HOTKEY to a specific window handle, so this class owns a tiny
/// message-only <see cref="HwndSource"/> (parented to HWND_MESSAGE) purely as a mailbox for that
/// message - it is never shown, has no size, and does not appear in the taskbar or Alt-Tab. It is
/// deliberately NOT tied to MainWindow's handle: MainWindow is hidden/shown by this very feature,
/// and WPF can recreate a window's underlying HWND across certain lifetime transitions, which
/// would silently drop the registration if it were parented there. A dedicated, permanently-alive
/// window has no such lifetime coupling and needs no message-pump of its own - it rides the WPF
/// Dispatcher's existing one, same as every other window in the app.
/// </summary>
public sealed class GlobalToggleWindowHotkey : IDisposable
{
    // Arbitrary but fixed - only one instance of this class is ever created per process (wired up
    // once in App.xaml.cs), so there is no risk of two live registrations colliding on the same id.
    private const int HotkeyId = 0x0B32; // F32

    /// <summary>Public re-exports of the Win32 MOD_* flags (winuser.h) for callers outside this
    /// assembly (AppSettings' default value, SettingsWindow's key-capture UI) that need to
    /// compose a modifier mask without duplicating the raw Win32 magic numbers themselves.</summary>
    public const uint ModAlt = NativeMethods.MOD_ALT;
    public const uint ModControl = NativeMethods.MOD_CONTROL;
    public const uint ModShift = NativeMethods.MOD_SHIFT;
    public const uint ModWin = NativeMethods.MOD_WIN;

    private uint _modifiers;
    private uint _vkCode;
    private HwndSource? _source;
    private bool _registered;

    public event Action? Pressed;

    /// <summary>The Win32 error code from the last failed <see cref="Start"/>/<see cref="SetHotkey"/>
    /// call (via GetLastWin32Error right after RegisterHotKey returned false), or 0 if the last
    /// attempt succeeded or none has been made yet. By far the most common non-zero value is
    /// ERROR_HOTKEY_ALREADY_REGISTERED (1409): some other running application already owns that
    /// exact modifier+key combo - Windows enforces one owner per combo system-wide and gives no
    /// way to ask who the other owner is, and there is no way to "steal" it back. The only real
    /// recovery is telling the user and letting them pick a different combo in Settings, which is
    /// exactly what SettingsWindow does with this value (mirrors how
    /// <see cref="GlobalPushToTalkHotkey.LastWin32Error"/> surfaces its own registration failure).</summary>
    public int LastWin32Error { get; private set; }

    /// <param name="modifiers">Win32 MOD_* flags (e.g. <see cref="ModControl"/> | <see cref="ModAlt"/>).
    /// MOD_NOREPEAT is added automatically on top of whatever is passed here - see its doc comment
    /// in NativeMethods for why.</param>
    /// <param name="virtualKeyCode">The non-modifier key, as a Win32 VK_* code.</param>
    public GlobalToggleWindowHotkey(int modifiers, int virtualKeyCode)
    {
        _modifiers = (uint)modifiers;
        _vkCode = (uint)virtualKeyCode;
    }

    /// <summary>
    /// Creates the message-only window (first call only) and registers the hotkey. Returns false
    /// - and sets <see cref="LastWin32Error"/> - if RegisterHotKey failed, most commonly because
    /// another running application already owns that exact combo. On failure the message-only
    /// window is still left running (harmless - it simply never receives WM_HOTKEY) so a later
    /// <see cref="SetHotkey"/> retry with a different combo doesn't need to recreate it. Safe to
    /// call more than once: a second call while already registered is a no-op that returns true.
    /// </summary>
    public bool Start()
    {
        if (_source is null)
        {
            var parameters = new HwndSourceParameters("OpenSuperWhisperToggleHotkeyWindow")
            {
                ParentWindow = NativeMethods.HWND_MESSAGE,
            };
            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
        }

        return Register();
    }

    private bool Register()
    {
        if (_registered) return true;
        _registered = NativeMethods.RegisterHotKey(_source!.Handle, HotkeyId, _modifiers | NativeMethods.MOD_NOREPEAT, _vkCode);
        LastWin32Error = _registered ? 0 : Marshal.GetLastWin32Error();
        return _registered;
    }

    private void Unregister()
    {
        if (!_registered || _source is null) return;
        NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
    }

    /// <summary>
    /// Swaps the modifier+key combo this listener watches for, live - used by the Settings window
    /// to rebind the show/hide hotkey without an app restart, the same live-rebind pattern as
    /// <see cref="GlobalPushToTalkHotkey.SetVirtualKeyCode"/>. Unregisters the old combo first (a
    /// no-op if it was never successfully registered, e.g. it was already owned by another app at
    /// the time) and registers the new one. Returns false - with <see cref="LastWin32Error"/> set
    /// - if the new combo can't be registered; the old combo is deliberately not restored in that
    /// case, so what's "in effect" always matches what the user just chose (even if that means
    /// nothing is currently active) rather than silently keeping a stale registration alive
    /// behind their back.
    /// </summary>
    public bool SetHotkey(int modifiers, int virtualKeyCode)
    {
        Unregister();
        _modifiers = (uint)modifiers;
        _vkCode = (uint)virtualKeyCode;
        return _source is null || Register(); // Start() hasn't run yet - applied whenever it does
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Stop() => Unregister();

    public void Dispose()
    {
        Unregister();
        if (_source is null) return;
        _source.RemoveHook(WndProc);
        _source.Dispose();
        _source = null;
    }
}
