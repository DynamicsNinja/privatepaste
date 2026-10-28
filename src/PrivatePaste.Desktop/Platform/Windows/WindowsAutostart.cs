using System.Runtime.Versioning;
using Microsoft.Win32;

namespace PrivatePaste.Desktop.Platform.Windows;

/// <summary>HKCU <c>Software\Microsoft\Windows\CurrentVersion\Run</c>, value <c>PrivatePaste</c> = "&lt;exe&gt;" --minimized.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAutostart(string exePath) : IAutostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PrivatePaste";

    public string Command { get; } = $"\"{exePath}\" --minimized";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
    }

    public void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
