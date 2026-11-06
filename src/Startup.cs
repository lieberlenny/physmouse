using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Xml;
using System.Windows.Forms;
using Microsoft.Win32;
using Windows.ApplicationModel;
using Windows.Foundation;

// Start-at-sign-in and the Start menu entry. The startup state lives in Windows (registry), not in
// settings.ini, so what the app shows is always what Windows will do.
static class Startup
{
    public enum Mode { Off, User }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const string Name = "physmouse";
    public static string Exe = Application.ExecutablePath; // the installer points this at the installed copy
    public static readonly string StartMenuShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "physmouse.lnk");
    public static readonly string DesktopShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "physmouse.lnk");

    public static bool Elevated
    {
        get { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
    }

    public static Mode Current()
    {
        if (LegacyTaskExists()) return Mode.User; // it still starts physmouse until it's removed
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey))
        using (var ok = Registry.CurrentUser.OpenSubKey(ApprovedKey))
        {
            if (run == null || run.GetValue(Name) == null) return Mode.Off;
            var flag = ok == null ? null : ok.GetValue(Name) as byte[];
            return flag == null || flag.Length == 0 || flag[0] % 2 == 0 ? Mode.User : Mode.Off; // odd = turned off in Task Manager
        }
    }

    // The mode the organization's policy allows in place of the wanted one.
    public static Mode Policed(Mode mode)
    {
        if (Policy.DisableAutostart) return Mode.Off;
        if (mode == Mode.Off && Policy.ForceAutostart) return Mode.User;
        return mode;
    }

    // Brings the startup entry in line with policy. No UAC here.
    public static void Enforce()
    {
        var now = Current();
        if (Policed(now) != now) Set(Policed(now), false);
    }

    // Returns null on success, otherwise a message for the user. With elevate, a legacy task is
    // explained first and removed through UAC; without, it must already be gone (or we're elevated).
    public static string Set(Mode mode, bool elevate = true)
    {
        if (Policed(mode) != mode) return "Your organization manages this setting.";
        string err = elevate ? RemoveLegacyTaskAsking("Remove it now?") : RemoveLegacyTask(false);
        if (err != null) return err;
        SetRunEntry(mode == Mode.User);
        return null;
    }

    // For uninstalling: removes the startup entry even where policy forces it on, since the program
    // it would start is about to go. The legacy task still has to go first (see Installer.Uninstall).
    public static string Remove(bool interactive)
    {
        string err = interactive ? RemoveLegacyTaskAsking("Remove it as part of uninstalling?") : RemoveLegacyTask(false);
        if (err != null) return err;
        SetRunEntry(false);
        return null;
    }

    // Up to 1.3.0, "On, as administrator" registered a logon task "\physmouse" that ran physmouse.exe with
    // highest privileges from the per-user install folder (or wherever a portable copy sat). Anything
    // running as the user can replace that exe, so the task was a way around UAC.
    // When the task itself started us elevated, it's removed silently. Otherwise only a start by hand
    // asks (explained, then UAC); a start at sign-in (--background) never prompts.
    public static void MigrateLegacyTask(bool interactive)
    {
        if (!LegacyTaskExists()) return;
        if (!Elevated && !interactive) return;
        string err = Elevated ? Schtasks(DeleteTask, false) : RemoveLegacyTaskAsking("Remove it now?");
        if (err == null) { if (Policed(Mode.User) == Mode.User) SetRunEntry(true); }
        else if (err != Declined && !Elevated && LegacyTaskExists())
            MessageBox.Show("The task is still there, so physmouse will ask again next time you open it. " + err, "physmouse", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    const string DeleteTask = "/Delete /TN physmouse /F";
    public const string Declined = "The old administrator startup task is still there.";
    const string LegacyExplained = "An older version of physmouse (1.3.0 or earlier) registered a task that starts it with administrator rights when you sign in. "
        + "It's no longer used, and other programs could misuse it to get administrator rights without asking.";

    // The task named \physmouse, if it starts an exe that a standard user could replace. Checked by name
    // and folder rather than by our own path, so it also finds tasks of portable copies and works as SYSTEM.
    // A task that starts physmouse from Program Files was set up by an administrator on purpose; it stays.
    public static bool LegacyTaskExists()
    {
        var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), "/Query /TN physmouse /XML") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
        string xml = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 && IsLegacyTask(xml, ProtectedFolders());
    }

    static string[] ProtectedFolders()
    {
        return new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows }
            .Select(f => Environment.GetFolderPath(f)).Where(f => f != "").ToArray();
    }

    public static bool IsLegacyTask(string xml, string[] protectedFolders)
    {
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var command = doc.SelectSingleNode("//*[local-name()='Command']");
            if (command == null) return false;
            string exe = Path.GetFullPath(Environment.ExpandEnvironmentVariables(command.InnerText.Trim().Trim('"')));
            return !protectedFolders.Any(f => exe.StartsWith(Path.GetFullPath(f).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception) { return true; } // a \physmouse task we can't read is treated as the old one
    }

    // Removes the legacy task if there is one. Null when it's gone (or never existed).
    public static string RemoveLegacyTask(bool elevate)
    {
        return LegacyTaskExists() ? Schtasks(DeleteTask, elevate) : null;
    }

    // Like RemoveLegacyTask with UAC, but says why before Windows asks for administrator permission.
    public static string RemoveLegacyTaskAsking(string question)
    {
        if (!LegacyTaskExists()) return null;
        if (Elevated) return Schtasks(DeleteTask, false);
        if (MessageBox.Show(LegacyExplained + "\n\n" + question + " Windows will ask for administrator permission.",
            "physmouse", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return Declined;
        return Schtasks(DeleteTask, true);
    }

    // Windows only runs a Run entry at sign-in when StartupApproved marks it enabled (Task Manager's
    // Startup apps list), so write both.
    static void SetRunEntry(bool on)
    {
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        using (var ok = Registry.CurrentUser.CreateSubKey(ApprovedKey))
        {
            if (on)
            {
                run.SetValue(Name, "\"" + Exe + "\" --background");
                ok.SetValue(Name, new byte[12] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
            }
            else
            {
                run.DeleteValue(Name, false);
                ok.DeleteValue(Name, false);
            }
        }
    }


    static string Schtasks(string args, bool elevate)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args) { WindowStyle = ProcessWindowStyle.Hidden };
        if (elevate && !Elevated) { psi.Verb = "runas"; psi.UseShellExecute = true; }
        else { psi.UseShellExecute = false; psi.CreateNoWindow = true; }
        try
        {
            using (var p = Process.Start(psi))
            {
                p.WaitForExit();
                return p.ExitCode == 0 ? null : "Task Scheduler reported error " + p.ExitCode + ".";
            }
        }
        catch (Win32Exception e)
        {
            return e.NativeErrorCode == 1223 ? "Administrator permission was not given, so nothing changed." : e.Message;
        }
    }

    public static void CreateShortcut(string path)
    {
        object lnk = OpenShortcut(path);
        var t = lnk.GetType();
        t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { Exe });
        t.InvokeMember("Arguments", BindingFlags.SetProperty, null, lnk, new object[] { "" });
        t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(Exe) });
        t.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "Move the cursor between screens by where they sit on your desk" });
        t.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
    }

    static object OpenShortcut(string path)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        object shell = Activator.CreateInstance(shellType);
        return shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
    }

    // The Store version starts through the package's startup task, which the manifest leaves off.
    // Only called when packaged: the task needs package identity.
    public static class Store
    {
        public static StartupTaskState State() { return Wait(StartupTask.GetAsync(Name)).State; }

        public static StartupTaskState Set(bool on)
        {
            var task = Wait(StartupTask.GetAsync(Name));
            if (!on) { task.Disable(); return task.State; }
            return Wait(task.RequestEnableAsync()); // stays DisabledByUser if it was turned off in Settings > Apps > Startup
        }

        // On or off for the settings page; locked says why it can't be changed there (null when it can).
        public static bool Read(out string locked)
        {
            var st = State();
            locked = st == StartupTaskState.DisabledByUser ? "Turned off under Settings > Apps > Startup. Turn it on there."
                : st == StartupTaskState.DisabledByPolicy || st == StartupTaskState.EnabledByPolicy || Policy.DisableAutostart || Policy.ForceAutostart ? "Set by your organization."
                : null;
            return st == StartupTaskState.Enabled || st == StartupTaskState.EnabledByPolicy;
        }

        public static void Enforce()
        {
            if (Policy.DisableAutostart) Set(false);
            else if (Policy.ForceAutostart) Set(true);
        }

        // No System.Runtime.WindowsRuntime (AsTask) without the Windows SDK, so wait by polling.
        static T Wait<T>(IAsyncOperation<T> op)
        {
            while (op.Status == AsyncStatus.Started) { Application.DoEvents(); System.Threading.Thread.Sleep(10); }
            return op.GetResults();
        }
    }

    // The exe may have moved since the entries were written; point them back at this copy.
    public static void Reconcile()
    {
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true))
            if (run != null && run.GetValue(Name) != null && (string)run.GetValue(Name) != "\"" + Exe + "\" --background")
                run.SetValue(Name, "\"" + Exe + "\" --background");
        foreach (var path in new[] { StartMenuShortcut, DesktopShortcut })
        {
            if (!File.Exists(path)) continue;
            object lnk = OpenShortcut(path);
            var t = lnk.GetType();
            string target = (string)t.InvokeMember("TargetPath", BindingFlags.GetProperty, null, lnk, null);
            if (!string.Equals(Path.GetFileName(target), "physmouse.exe", StringComparison.OrdinalIgnoreCase)) continue; // someone else's shortcut
            if (!string.Equals(target, Exe, StringComparison.OrdinalIgnoreCase) || (string)t.InvokeMember("Arguments", BindingFlags.GetProperty, null, lnk, null) != "")
                CreateShortcut(path);
        }
    }
}
