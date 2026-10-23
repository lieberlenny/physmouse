using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using System.Net;
using Microsoft.Win32;

[assembly: AssemblyTitle("physmouse")]
[assembly: AssemblyDescription("Moves the cursor between screens by where they physically sit on your desk.")]
[assembly: AssemblyProduct("physmouse")]
[assembly: AssemblyCopyright("Copyright © 2026 Lennart Blees")]
[assembly: AssemblyVersion("1.3.0.0")]
[assembly: AssemblyFileVersion("1.3.0.0")]

static class Program
{
    public const string RepoUrl = "https://github.com/lieberlenny/physmouse";
    public const string LatestReleaseUrl = RepoUrl + "/releases/latest";
    public static readonly string Version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
    public static string UpdateVersion { get; private set; }

    // Set when running as the Microsoft Store (MSIX) package. The Store installs, updates and removes the app,
    // and Windows owns its startup entry, so the self-installer, update check and startup options stay off.
    public static readonly string PackageFamily = GetPackageFamily();
    public static bool Packaged { get { return PackageFamily != null; } }

    static string GetPackageFamily()
    {
        try
        {
            int len = 0;
            if (Native.GetCurrentPackageFamilyName(ref len, null) != 122) return null; // ERROR_INSUFFICIENT_BUFFER; 15700 = no package
            var sb = new System.Text.StringBuilder(len);
            return Native.GetCurrentPackageFamilyName(ref len, sb) == 0 ? sb.ToString() : null;
        }
        catch (EntryPointNotFoundException) { return null; }
    }

    public static Settings Cfg { get { return Mapper.Cfg; } }
    public static event Action Changed; // monitors or settings changed; the window redraws

    static readonly int ShowMessage = Native.RegisterWindowMessage("physmouse.show");
    static readonly int ExitMessage = Native.RegisterWindowMessage("physmouse.exit");
    const string IpcTitle = "physmouse.ipc";
    static Mutex mutex;
    static IpcWindow ipc;
    static NotifyIcon tray;
    static MenuItem trayEnabled;
    static Icon iconOn, iconOff;
    static MainWindow window;
    static SynchronizationContext ui;
    static System.Windows.Forms.Timer settle;
    static bool restartAfterExit;

    [STAThread]
    static int Main(string[] args)
    {
        bool background = args.Contains("--background");
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Native.AllowDarkMenus();

        // --quiet runs setup without any window, for package managers such as winget. Exit code 0 means success.
        bool quiet = args.Contains("--quiet");
        if (args.Contains("--uninstall") && !Packaged)
        {
            if (quiet)
            {
                bool ok = Installer.Uninstall(true, true) == null; // keeps settings: a silent uninstall shouldn't lose a layout
                if (ok) Installer.RemoveFiles();
                return ok ? 0 : 1;
            }
            var w = new SetupWindow(SetupWindow.Mode.Uninstall);
            Application.Run(w);
            if (w.Result == SetupWindow.Outcome.Done) Installer.RemoveFiles();
            return 0;
        }
        if (args.Contains("--install") && quiet && !Packaged)
        {
            var installed = Installer.InstalledExe;
            string dir = installed != null ? Path.GetDirectoryName(installed) : Installer.DefaultDir;
            // Start-at-sign-in is opt-in (--autostart); --no-autostart is still accepted from older setups. Upgrades keep the current state.
            if (Installer.Install(dir, args.Contains("--autostart"), false, installed != null) != null) return 1;
            if (Installer.StoppedRunningCopy) Process.Start(Installer.InstalledExe, "--background"); // an upgrade shouldn't leave it off
            return 0;
        }

        // Started by hand from a download: offer to install (or update) before anything else.
        var offer = background || Packaged ? Installer.Offer.None
            : args.Contains("--install") ? (Installer.InstalledExe != null ? Installer.Offer.Update : Installer.Offer.Install)
            : Installer.Check();
        if (offer != Installer.Offer.None)
        {
            var w = new SetupWindow(offer == Installer.Offer.Install ? SetupWindow.Mode.Install : SetupWindow.Mode.Update);
            Application.Run(w);
            // An update stopped the running copy; bring it back even if the window was closed without "Open physmouse".
            if (w.Result == SetupWindow.Outcome.Done && Installer.StoppedRunningCopy) Process.Start(Installer.InstalledExe, "--background");
            if (w.Result != SetupWindow.Outcome.Portable) return 0; // installed (the installed copy is starting) or cancelled
            // Portable: settings live next to the exe from now on. Settings hasn't been touched yet, so it picks this up.
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini"), "");
        }

        if (!TakeMutex())
        {
            // Already running, maybe elevated or hidden in the tray: ask that copy to show its window.
            var other = Native.FindWindowEx(Native.HWND_MESSAGE, IntPtr.Zero, null, IpcTitle);
            if (other != IntPtr.Zero && !background) Native.PostMessage(other, ShowMessage, IntPtr.Zero, IntPtr.Zero);
            return 0;
        }

        ui = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(ui);

        Mapper.Cfg = Settings.Load();
        bool firstRun = !Cfg.Existed;
        // Keep startup entries and shortcuts pointing at this copy, unless it's a stray copy next to an installed one.
        if (!Packaged && (Installer.InstalledExe == null || Installer.RunningInstalled))
            try { Startup.Reconcile(); Startup.Enforce(); } catch (Exception) { } // a locked shortcut or registry key isn't worth failing over
        if (Packaged) try { Startup.Store.Enforce(); } catch (Exception) { }
        Refresh();
        if (firstRun) { Cfg.Store(Mapper.Mons); Cfg.Save(); }
        Mapper.Start();

        ipc = new IpcWindow();
        SetHotkey(Cfg.Hotkey);
        SystemEvents.DisplaySettingsChanged += DisplayChanged;

        int small = SystemInformation.SmallIconSize.Width;
        iconOn = Theme.MakeIcon(small, false);
        iconOff = Theme.MakeIcon(small, true);
        trayEnabled = new MenuItem("Remap cursor", (s, e) => SetEnabled(!Cfg.Enabled));
        tray = new NotifyIcon
        {
            ContextMenu = new ContextMenu(new[] {
                new MenuItem("Open physmouse", (s, e) => ShowWindow()) { DefaultItem = true },
                trayEnabled,
                new MenuItem("-"),
                new MenuItem("Exit", (s, e) => Application.Exit()) })
        };
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
        tray.BalloonTipClicked += (s, e) => ShowWindow();
        UpdateTray();
        if (!Packaged && !Policy.DisableUpdateCheck) CheckForUpdates();

        if (!background) ShowWindow();
        Application.Run();

        SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        tray.Visible = false;
        tray.Dispose();
        ipc.DestroyHandle();
        if (mutex != null) mutex.Dispose();
        if (restartAfterExit) Process.Start(Application.ExecutablePath);
        return 0;
    }

    static bool TakeMutex()
    {
        bool created;
        try { mutex = new Mutex(true, @"Local\physmouse", out created); }
        catch (UnauthorizedAccessException) { return false; } // held by an elevated copy
        if (!created) { mutex.Dispose(); mutex = null; }
        return created;
    }

    public static void ShowWindow()
    {
        if (window == null || window.IsDisposed) window = new MainWindow();
        window.Show();
        if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
        window.BringToFront();
        window.Activate();
        Native.SetForegroundWindow(window.Handle);
        UpdateTray();
    }

    public static void Restart()
    {
        restartAfterExit = true;
        Application.Exit();
    }

    // Re-read the connected screens and apply the saved layout to them.
    public static void Refresh()
    {
        var mons = Monitors.Detect();
        var unknown = Cfg.ApplyTo(mons);
        Mapper.Mons = mons;
        if (unknown.Count > 0 && Cfg.Existed)
        {
            // Remember the guess so this only comes up once per new screen.
            Cfg.Store(unknown);
            Save();
            if (tray != null && tray.Visible)
                tray.ShowBalloonTip(0, unknown.Count == 1 ? "New screen connected" : "New screens connected",
                    "Open physmouse to place " + (unknown.Count == 1 ? "it" : "them") + " where " + (unknown.Count == 1 ? "it sits" : "they sit") + " on your desk.", ToolTipIcon.None);
        }
        Mapper.Rehook();
        var h = Changed;
        if (h != null) h();
    }

    static void DisplayChanged(object sender, EventArgs e)
    {
        ui.Post(_ =>
        {
            Refresh();
            // Bounds can still shift for a moment after a display change; look again once it settles.
            if (settle != null) settle.Dispose();
            settle = new System.Windows.Forms.Timer { Interval = 2000 };
            settle.Tick += (s, a) => { settle.Dispose(); settle = null; Refresh(); };
            settle.Start();
        }, null);
    }

    public static void SetEnabled(bool on)
    {
        Cfg.Enabled = on;
        Save();
        UpdateTray();
    }

    public static void Save()
    {
        try { Cfg.Save(); }
        catch (Exception e) { MessageBox.Show("physmouse couldn't save its settings:\n\n" + e.Message, "physmouse", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        var h = Changed;
        if (h != null) h();
    }

    public static void ResetSettings()
    {
        SetHotkey(Keys.None);
        var settings = Settings.Defaults();
        settings.Store(Monitors.Detect());
        Mapper.Cfg = settings;
        Save();
        Refresh();
        UpdateTray();
    }

    public static void UpdateTray()
    {
        tray.Icon = Cfg.Enabled ? iconOn : iconOff;
        tray.Text = Cfg.Enabled ? "physmouse" : "physmouse (paused)";
        trayEnabled.Checked = Cfg.Enabled;
        tray.Visible = Cfg.TrayIcon || (window != null && !window.Visible);
    }

    static void CheckForUpdates()
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                // csc builds without a target framework get .NET 4.0's SSL3/TLS 1.0 default, which GitHub refuses
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                var request = (HttpWebRequest)WebRequest.Create(LatestReleaseUrl);
                request.Method = "HEAD";
                request.UserAgent = "physmouse/" + Version;
                request.Timeout = 6000;
                using (var response = request.GetResponse())
                {
                    string[] parts = response.ResponseUri.AbsolutePath.Trim('/').Split('/');
                    System.Version latest;
                    if (parts.Length < 1 || !System.Version.TryParse(parts[parts.Length - 1].TrimStart('v'), out latest)
                        || latest <= new System.Version(Version)) return;
                    ui.Post(__ =>
                    {
                        UpdateVersion = latest.ToString(3);
                        var h = Changed;
                        if (h != null) h();
                    }, null);
                }
            }
            catch (WebException) { }
            catch (InvalidOperationException) { }
        });
    }

    // False when another app already owns that shortcut.
    public static bool SetHotkey(Keys k) { return ipc.Register(k); }
    public static void SuspendHotkey() { ipc.Register(Keys.None); }

    // Hands over to the elevated scheduled task, which starts a fresh copy with administrator rights.
    // Used by the installer before it replaces or removes the exe.
    public static void AskRunningCopyToExit()
    {
        var other = Native.FindWindowEx(Native.HWND_MESSAGE, IntPtr.Zero, null, IpcTitle);
        if (other != IntPtr.Zero) Native.PostMessage(other, ExitMessage, IntPtr.Zero, IntPtr.Zero);
    }

    public static bool RestartElevated()
    {
        mutex.ReleaseMutex();
        mutex.Dispose();
        mutex = null;
        if (Startup.RunTask()) { tray.Visible = false; Application.Exit(); return true; }
        TakeMutex();
        return false;
    }

    // Message-only window: receives the "show yourself" request from a second launch and the hotkey.
    class IpcWindow : NativeWindow
    {
        Keys current = Keys.None;

        public IpcWindow()
        {
            CreateHandle(new CreateParams { Caption = IpcTitle, Parent = Native.HWND_MESSAGE });
            // let a non-elevated launch or installer reach an elevated copy
            Native.ChangeWindowMessageFilterEx(Handle, ShowMessage, 1, IntPtr.Zero);
            Native.ChangeWindowMessageFilterEx(Handle, ExitMessage, 1, IntPtr.Zero);
        }

        public bool Register(Keys k)
        {
            if (current != Keys.None) Native.UnregisterHotKey(Handle, 1);
            current = Keys.None;
            if (k == Keys.None) return true;
            uint mods = 0x4000; // MOD_NOREPEAT
            if ((k & Keys.Alt) != 0) mods |= 1;
            if ((k & Keys.Control) != 0) mods |= 2;
            if ((k & Keys.Shift) != 0) mods |= 4;
            if (!Native.RegisterHotKey(Handle, 1, mods, (uint)(k & Keys.KeyCode))) return false;
            current = k;
            return true;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == ShowMessage) ShowWindow();
            else if (m.Msg == ExitMessage) Application.Exit();
            else if (m.Msg == Native.WM_HOTKEY) SetEnabled(!Cfg.Enabled);
            else base.WndProc(ref m);
        }
    }
}
