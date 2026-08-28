using Microsoft.Win32;
using OpenSuperWhisper.Core;

namespace OpenSuperWhisper.App;

/// <summary>
/// Toggles "start with Windows" via the per-user Run registry key (HKCU, not HKLM - never
/// needs admin rights and only affects the current Windows account). This only ever runs when
/// the user explicitly checks/unchecks the box in Settings; it never runs on its own.
/// </summary>
public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OpenSuperWhisper";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string;
        }
        catch (Exception ex)
        {
            Log.Error("读取开机自启注册表项失败", ex);
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                             ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                {
                    Log.Error("设置开机自启失败：拿不到当前 exe 路径");
                    return;
                }
                key.SetValue(ValueName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            Log.Info($"开机自启已{(enabled ? "开启" : "关闭")}");
        }
        catch (Exception ex)
        {
            Log.Error("设置开机自启注册表项失败", ex);
        }
    }
}
