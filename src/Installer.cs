using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

// physmouse installs itself: running the downloaded exe offers to copy it to a per-user folder and
// register it under Settings > Apps. No administrator rights are needed.
static class Installer
{
    public enum Offer { None, Install, Update }

    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\physmouse";
    public static readonly string DefaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "physmouse");
    static readonly string SettingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "physmouse");
    static readonly Version Current = typeof(Installer).Assembly.GetName().Version;

    // The installed copy according to Settings > Apps, or null.
    public static string InstalledExe
    {
        get
        {
            using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey))
            {
                var dir = k == null ? null : k.GetValue("InstallLocation") as string;
                return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "physmouse.exe");
            }
        }
    }

    public static Version InstalledVersion
    {
        get
        {
            using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey))
            {
                Version v;
                return k != null && Version.TryParse(k.GetValue("DisplayVersion") as string ?? "", out v) ? v : null;
            }
        }
    }

    public static bool RunningInstalled
    {
        get { return InstalledExe != null && SamePath(InstalledExe, Application.ExecutablePath); }
    }

    static bool SamePath(string a, string b)
    {
        return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    }

    // What to offer when this exe is started by hand.
    public static Offer Check()
    {
        if (RunningInstalled) return Offer.None;
        if (File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini"))) return Offer.None; // portable copy
        var installed = InstalledExe;
        if (installed == null || !File.Exists(installed)) return Offer.Install;
        var v = InstalledVersion;
        return v == null || Current > v ? Offer.Update : Offer.None;
    }

    // Copies this exe into dir and registers it. Returns null on success, otherwise a message for the user.
    public static string Install(string dir, bool startWithWindows, bool desktopShortcut, bool update)
    {
        if (!StopRunning()) return "physmouse is still running. Quit it from its icon next to the clock, then try again.";
        string exe = Path.Combine(dir, "physmouse.exe");
        try
        {
            Directory.CreateDirectory(dir);
            if (!SamePath(exe, Application.ExecutablePath)) File.Copy(Application.ExecutablePath, exe, true);

            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "physmouse");
                k.SetValue("DisplayVersion", Current.ToString(3));
                k.SetValue("Publisher", "Lennart Blees");
                k.SetValue("DisplayIcon", exe);
                k.SetValue("InstallLocation", dir);
                k.SetValue("UninstallString", "\"" + exe + "\" --uninstall");
                k.SetValue("URLInfoAbout", Program.RepoUrl);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
                k.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }

            Startup.Exe = exe;
            Startup.CreateShortcut(Startup.StartMenuShortcut);
            if (desktopShortcut) Startup.CreateShortcut(Startup.DesktopShortcut);
            if (update) Startup.Reconcile();
            else if (Startup.Current() != Startup.Mode.Admin) // an elevated task already points here and needs no change
            {
                string err = Startup.Set(startWithWindows ? Startup.Mode.User : Startup.Mode.Off);
                if (err != null) return err;
            }
            return null;
        }
        catch (Exception e)
        {
            return "Installing failed: " + e.Message;
        }
    }

    // Removes everything physmouse put on this PC, except its own exe, which is still running;
    // RemoveFiles deletes that once this process has exited.
    public static string Uninstall(bool keepSettings)
    {
        if (!StopRunning()) return "physmouse is still running. Quit it from its icon next to the clock, then try again.";
        string err = Startup.Set(Startup.Mode.Off); // asks for administrator permission if it starts as administrator
        if (err != null) return err;
        try
        {
            foreach (var lnk in new[] { Startup.StartMenuShortcut, Startup.DesktopShortcut })
                if (File.Exists(lnk)) File.Delete(lnk);
            pendingDir = InstalledExe == null ? null : Path.GetDirectoryName(InstalledExe);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            if (!keepSettings && Directory.Exists(SettingsDir)) Directory.Delete(SettingsDir, true);
            return null;
        }
        catch (Exception e)
        {
            return "Uninstalling failed: " + e.Message;
        }
    }

    static string pendingDir;

    // Deletes the installed exe after this process exits, then its folder if nothing else is in it.
    public static void RemoveFiles()
    {
        if (pendingDir == null) return;
        string exe = Path.Combine(pendingDir, "physmouse.exe");
        Process.Start(new ProcessStartInfo("cmd.exe",
            "/c ping -n 3 127.0.0.1 >nul & del /f /q \"" + exe + "\" & rmdir \"" + pendingDir + "\"")
        { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
    }

    // Asks a running physmouse to quit, so its exe can be replaced or removed.
    static bool StopRunning()
    {
        int self = Process.GetCurrentProcess().Id;
        var others = Process.GetProcessesByName("physmouse").Where(p => p.Id != self).ToList();
        if (others.Count == 0) return true;
        Program.AskRunningCopyToExit();
        foreach (var p in others)
        {
            try
            {
                if (p.WaitForExit(5000)) continue;
                p.Kill();
                if (!p.WaitForExit(2000)) return false;
            }
            catch (Exception) { return false; } // e.g. an elevated copy we may not end
        }
        return true;
    }

    public static string FormatVersion(Version v) { return v == null ? "?" : v.ToString(3); }
    public static string CurrentVersion { get { return Current.ToString(3); } }
}
