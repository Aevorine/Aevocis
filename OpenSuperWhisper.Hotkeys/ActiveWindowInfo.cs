using System.ComponentModel;
using System.Diagnostics;

namespace OpenSuperWhisper.Hotkeys;

/// <summary>
/// Reads which process owns the currently focused (foreground) window, via the standard
/// GetForegroundWindow + GetWindowThreadProcessId + Process.GetProcessById combo (the documented
/// MSDN approach). Used by F06 (per-app transcription prompt) and F12 (per-app push-to-talk key)
/// to know "what app is the user dictating into right now".
/// </summary>
public static class ActiveWindowInfo
{
    /// <summary>
    /// Returns the process name (e.g. "Code", "WeChat", "chrome" - no ".exe" suffix, same value
    /// as Process.ProcessName) of whichever window currently has OS input focus, or null if there
    /// is no foreground window or its owning process couldn't be resolved (window just closed
    /// mid-call, the process is protected/elevated and access is denied, etc.). Callers must
    /// treat null as "no per-app override available - use the default", never as an error.
    /// </summary>
    public static string? GetActiveProcessName()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return null;

        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            // No process with that id anymore - it exited between GetWindowThreadProcessId and
            // GetProcessById (both racy by nature: the foreground window can close/switch at any
            // moment on a background hook thread).
            return null;
        }
        catch (InvalidOperationException)
        {
            // Process exited while its properties were being read.
            return null;
        }
        catch (Win32Exception)
        {
            // Access denied - e.g. an elevated/protected process (Task Manager as admin, some
            // system processes) when this app isn't itself elevated.
            return null;
        }
    }
}
