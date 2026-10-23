using System;
using Microsoft.Win32;

// Settings an administrator pushes under HKLM\SOFTWARE\Policies\physmouse (e.g. through Intune).
// They override the user's choices and are read once at startup.
static class Policy
{
    const string Key = @"SOFTWARE\Policies\physmouse";

    public static readonly bool DisableUpdateCheck = Flag("DisableUpdateCheck");
    public static readonly bool DisableAutostart = Flag("DisableAutostart"); // wins over ForceAutostart
    public static readonly bool ForceAutostart = Flag("ForceAutostart");
    public static readonly bool DisableSelfInstaller = Flag("DisableSelfInstaller");
    public static readonly string DefaultSettings = Text("DefaultSettings"); // settings.ini to start from when the user has none

    static object Value(string name)
    {
        try { using (var k = Registry.LocalMachine.OpenSubKey(Key)) return k == null ? null : k.GetValue(name); }
        catch (Exception) { return null; } // an unreadable policy key is treated as absent
    }

    static bool Flag(string name)
    {
        var v = Value(name);
        return v is int ? (int)v != 0 : v is string && ((string)v).Trim() == "1";
    }

    static string Text(string name)
    {
        var v = Value(name) as string;
        return string.IsNullOrWhiteSpace(v) ? null : Environment.ExpandEnvironmentVariables(v.Trim());
    }
}
