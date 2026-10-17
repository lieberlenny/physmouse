using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

// Start-at-sign-in and the Start menu entry. The startup state lives in Windows (registry, Task
// Scheduler), not in settings.ini, so what the app shows is always what Windows will do.
static class Startup
{
    public enum Mode { Off, User, Admin }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const string Name = "physmouse";
    static readonly string Exe = Application.ExecutablePath;
    static readonly string Shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "physmouse.lnk");

    public static bool Elevated
    {
        get { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
    }

    public static Mode Current()
    {
        if (TaskExists()) return Mode.Admin;
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey))
        using (var ok = Registry.CurrentUser.OpenSubKey(ApprovedKey))
        {
            if (run == null || run.GetValue(Name) == null) return Mode.Off;
            var flag = ok == null ? null : ok.GetValue(Name) as byte[];
            return flag == null || flag.Length == 0 || flag[0] % 2 == 0 ? Mode.User : Mode.Off; // odd = turned off in Task Manager
        }
    }

    // Returns null on success, otherwise a message for the user.
    public static string Set(Mode mode)
    {
        var was = Current();
        if (mode == Mode.Admin && was != Mode.Admin)
        {
            string err = CreateTask();
            if (err != null) return err;
        }
        if (mode != Mode.Admin && was == Mode.Admin)
        {
            string err = Schtasks("/Delete /TN physmouse /F", true);
            if (err != null) return err;
        }
        SetRunEntry(mode == Mode.User);
        return null;
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

    static bool TaskExists()
    {
        var p = Process.Start(new ProcessStartInfo("schtasks.exe", "/Query /TN physmouse") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0;
    }

    // A logon task with highest privileges is the supported way to start elevated without a UAC
    // prompt at every sign-in. Written as XML because schtasks' flags can't lift the default
    // 72-hour run limit or the below-normal priority, both wrong for something that runs all day.
    static string CreateTask()
    {
        string file = Path.Combine(Path.GetTempPath(), "physmouse-task.xml");
        File.WriteAllText(file, TaskXml(), Encoding.Unicode);
        try { return Schtasks("/Create /TN physmouse /XML \"" + file + "\" /F", true); }
        finally { File.Delete(file); }
    }

    public static string TaskXml()
    {
        string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        return
"<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
"<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
"  <RegistrationInfo><Description>Starts physmouse with administrator rights when you sign in.</Description></RegistrationInfo>\r\n" +
"  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId></LogonTrigger></Triggers>\r\n" +
"  <Principals><Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
"  <Settings>\r\n" +
"    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
"    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
"    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
"    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
"    <Priority>4</Priority>\r\n" +
"  </Settings>\r\n" +
"  <Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(Exe) + "</Command><Arguments>--background</Arguments></Exec></Actions>\r\n" +
"</Task>\r\n";
    }

    // Starts the elevated task now, so switching to administrator mode doesn't wait for the next sign-in.
    public static bool RunTask() { return Schtasks("/Run /TN physmouse", false) == null; }

    static string Schtasks(string args, bool elevate)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args) { WindowStyle = ProcessWindowStyle.Hidden };
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

    // Created on first run; there's no installer, so that run is the install.
    public static void CreateShortcut()
    {
        object lnk = OpenShortcut();
        var t = lnk.GetType();
        t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { Exe });
        t.InvokeMember("Arguments", BindingFlags.SetProperty, null, lnk, new object[] { "" });
        t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(Exe) });
        t.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "Move the cursor between screens by where they sit on your desk" });
        t.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
    }

    static object OpenShortcut()
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        object shell = Activator.CreateInstance(shellType);
        return shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { Shortcut });
    }

    // The exe may have moved since the entries were written; point them back at this copy.
    public static void Reconcile()
    {
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true))
            if (run != null && run.GetValue(Name) != null && (string)run.GetValue(Name) != "\"" + Exe + "\" --background")
                run.SetValue(Name, "\"" + Exe + "\" --background");
        if (!File.Exists(Shortcut)) return;
        object lnk = OpenShortcut();
        var t = lnk.GetType();
        if (!string.Equals((string)t.InvokeMember("TargetPath", BindingFlags.GetProperty, null, lnk, null), Exe, StringComparison.OrdinalIgnoreCase)
            || (string)t.InvokeMember("Arguments", BindingFlags.GetProperty, null, lnk, null) != "")
            CreateShortcut();
    }
}
