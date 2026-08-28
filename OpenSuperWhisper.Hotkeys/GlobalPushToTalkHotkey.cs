using System.Runtime.InteropServices;
using OpenSuperWhisper.Core;
using OpenSuperWhisper.Core.Models;

namespace OpenSuperWhisper.Hotkeys;

/// <summary>
/// System-wide push-to-talk for one virtual-key code, regardless of which window has focus. Uses
/// a WH_KEYBOARD_LL hook because RegisterHotKey only reports the final combo, never key-up
/// separately. Supports two interpretations of the same raw key-down/key-up stream (see
/// <see cref="PushToTalkMode"/>): Hold (key-down starts, key-up stops - the original behavior)
/// and Toggle (first key-down starts, next key-down stops; key-up is ignored) - F09.
///
/// F12: also supports per-app overrides (AppSettings.AppSpecificHotkeys) - while a given app is
/// focused, only its dedicated key (not the global default) arms PressStarted. Empty overrides
/// (the default) means every key-down is checked against the global key only, identical to
/// pre-F12 behavior. Known limitation where F09 Toggle mode and F12 per-app keys combine: if a
/// toggle-started recording is still active when the user switches to an app with a *different*
/// dedicated key, the "stop" tap must be that app's key, not the one that started it - switching
/// apps mid-toggle-recording can strand it until the right key is found. Rare enough (three
/// features stacking at once) not to special-case here; SetMode already recovers a stuck session
/// on an explicit mode change.
/// </summary>
public sealed class GlobalPushToTalkHotkey : IHotkeyListener
{
    /// <summary>Minimum gap between two toggle-mode key-down transitions we act on. Real
    /// hardware key-bounce produces extra electrical transitions within a few, up to a few tens
    /// of, milliseconds of the real one; a human's fastest plausible *intentional* second tap is
    /// well above that. This sits comfortably between the two so a bounce right after the
    /// "start" press can't be mistaken for the "stop" press - same spirit as
    /// DictationController's ~1600-sample (~100ms) accidental-tap guard, just applied at the
    /// key-edge level instead of after capturing audio. [估计值，未做真实硬件按键去抖动测量]</summary>
    private const long ToggleDebounceMs = 300;

    private uint _vkCode;
    private Dictionary<string, int> _appSpecificVkCodes = new();
    private PushToTalkMode _mode = PushToTalkMode.Hold;
    private NativeMethods.LowLevelKeyboardProc? _proc;
    private IntPtr _hookId = IntPtr.Zero;
    private bool _isDown;

    /// <summary>The vk code that was actually matched on the key-down currently in progress
    /// (either an app-specific override or the global default) - latched so the corresponding
    /// key-up is matched against the same code even if the foreground app changed mid-hold
    /// (e.g. the user alt-tabbed while still holding the key). Without this latch, a resolved
    /// target that changes between the down and up events would leave _isDown stuck true
    /// forever, silently breaking every future press.</summary>
    private uint? _activeMatchVk;

    /// <summary>Toggle mode only: whether a toggle-started recording is currently "on" (waiting
    /// for the next accepted key-down to end it).</summary>
    private bool _toggleActive;

    /// <summary>Toggle mode only: Environment.TickCount64 of the last key-down transition we
    /// accepted (started or stopped a recording), for the debounce check above.</summary>
    private long _lastToggleTransitionMs;

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
        // A session in progress under the old key must be closed out the same way SetMode does,
        // not just silently forgotten - otherwise DictationController's _isRecording stays true
        // forever with no PressEnded ever coming to clear it (Hold: key held down when rebound;
        // Toggle: a toggle-started recording waiting for its "stop" tap on the now-defunct key).
        var sessionInProgress = _mode == PushToTalkMode.Hold ? _isDown : _toggleActive;
        _isDown = false;
        _activeMatchVk = null;
        _toggleActive = false;
        if (sessionInProgress)
            PressEnded?.Invoke();
    }

    /// <summary>
    /// F12: swaps the per-app push-to-talk overrides live, without stopping or recreating the
    /// hook - same pattern as SetVirtualKeyCode. Pass an empty dictionary to restore "global key
    /// everywhere" behavior. Does not touch a key-down currently in progress (see
    /// <see cref="_activeMatchVk"/>).
    /// </summary>
    public void SetAppSpecificHotkeys(Dictionary<string, int> appSpecificVkCodes)
    {
        _appSpecificVkCodes = appSpecificVkCodes;
    }

    /// <summary>
    /// Switches between Hold and Toggle interpretation of the same key, live, without touching
    /// the hook itself. If a recording is currently in progress under the mode being switched
    /// away from, its PressEnded is fired now so the switch can't orphan it - under the new
    /// mode's rules that in-progress session's key-up (Hold -> ignored once we're in Toggle) or
    /// "next key-down" (Toggle -> read as a fresh Hold press, not a stop) would otherwise never
    /// arrive to close it out.
    /// </summary>
    public void SetMode(PushToTalkMode mode)
    {
        if (mode == _mode) return;
        var sessionInProgress = _mode == PushToTalkMode.Hold ? _isDown : _toggleActive;
        _mode = mode;
        _isDown = false;
        _toggleActive = false;
        if (sessionInProgress)
            PressEnded?.Invoke();
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

            if ((msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN) && !_isDown)
            {
                var targetVk = ResolveTargetVkCode();
                if (data.vkCode == targetVk)
                {
                    // !_isDown above already filters Windows' own key-repeat (held key re-fires
                    // WM_KEYDOWN with the physical key never having gone up) in both modes.
                    _isDown = true;
                    _activeMatchVk = targetVk;
                    if (_mode == PushToTalkMode.Hold)
                        PressStarted?.Invoke();
                    else
                        HandleToggleKeyDown();
                }
            }
            else if ((msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP)
                     && _isDown && data.vkCode == _activeMatchVk)
            {
                _isDown = false;
                _activeMatchVk = null;
                if (_mode == PushToTalkMode.Hold)
                    PressEnded?.Invoke();
                // Toggle mode: only key-down transitions carry meaning; key-up is ignored.
            }
        }
        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    /// <summary>F12: which vk code should currently arm PressStarted - the focused app's
    /// dedicated key if AppSpecificHotkeys has one for it (matched case-insensitively via
    /// AppSpecificLookup), else the global default. With no overrides configured this always
    /// returns _vkCode, i.e. zero behavior change from pre-F12.</summary>
    private uint ResolveTargetVkCode()
    {
        if (_appSpecificVkCodes.Count > 0)
        {
            var activeProcess = ActiveWindowInfo.GetActiveProcessName();
            if (AppSpecificLookup.TryGet(_appSpecificVkCodes, activeProcess, out var vk))
                return (uint)vk;
        }
        return _vkCode;
    }

    /// <summary>
    /// Toggle-mode key-down edge: first press starts recording, the next one stops it (no matter
    /// how long it's held). Debounced (see ToggleDebounceMs) so a key-bounce blip immediately
    /// after starting can't be misread as the stopping press.
    /// </summary>
    private void HandleToggleKeyDown()
    {
        var now = Environment.TickCount64;
        if (now - _lastToggleTransitionMs < ToggleDebounceMs)
            return; // too soon to be a deliberate separate press - ignore, state unchanged
        _lastToggleTransitionMs = now;

        _toggleActive = !_toggleActive;
        if (_toggleActive)
            PressStarted?.Invoke();
        else
            PressEnded?.Invoke();
    }

    public void Dispose() => Stop();
}
