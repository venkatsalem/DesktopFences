using Microsoft.Win32;

namespace DesktopFences.Core;

/// <summary>
/// Manages the "Start with Windows" registry key.
/// Reads/writes HKCU\Software\Microsoft\Windows\CurrentVersion\Run only.
/// Zero network access — purely local registry operations.
/// </summary>
internal static class RegistryStartup
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "DesktopFences";

    public static void SetStartWithWindows(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null) return;

            if (enable)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    key.SetValue(AppName, $"\"{exePath}\"");
                }
            }
            else
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Silent fail — user may not have permission
        }
    }

    public static bool IsStartWithWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(AppName) != null;
        }
        catch
        {
            return false;
        }
    }
}
