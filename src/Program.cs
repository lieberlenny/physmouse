using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("physmouse")]
[assembly: AssemblyDescription("Moves the cursor between screens by where they physically sit on your desk.")]
[assembly: AssemblyProduct("physmouse")]
[assembly: AssemblyCopyright("Copyright © 2026 Lennart Blees")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

static class Program
{
    public const string RepoUrl = "https://github.com/lieberlenny/physmouse";
    public static readonly string Version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

    public static Settings Cfg { get { return Mapper.Cfg; } }
    public static event Action Changed; // monitors or settings changed; the window redraws

    static readonly int ShowMessage = Native.RegisterWindowMessage("physmouse.show");
    const string IpcTitle = "physmouse.ipc";
    static Mutex mutex;
    static IpcWindow ipc;
    static NotifyIcon tray;
    static MenuItem trayEnabled;
    static Icon iconOn, iconOff;
    static MainWindow window;
    static SynchronizationContext ui;
    static System.Windows.Forms.Timer settle;

    [STAThread]
    static int Main(string[] args)
    {
        bool background = args.Contains("--background");
        if (!TakeMutex())
        {
            // Already running, maybe elevated or hidden in the tray: ask that copy to show its window.
            var other = Native.FindWindowEx(Native.HWND_MESSAGE, IntPtr.Zero, null, IpcTitle);
            if (other != IntPtr.Zero && !background) Native.PostMessage(other, ShowMessage, IntPtr.Zero, IntPtr.Zero);
            return 0;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        ui = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(ui);
        Native.AllowDarkMenus();

        Mapper.Cfg = Settings.Load();
        bool firstRun = !Cfg.Existed;
        try { Startup.Reconcile(); } catch (Exception) { } // a locked shortcut or registry key isn't worth failing over
        Refresh();
        if (firstRun)
        {
            Cfg.Store(Mapper.Mons);
            Cfg.Save();
            try { Startup.CreateShortcut(); } catch (Exception) { } // nice to have; the app works without it
        }
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

        if (!background || firstRun) ShowWindow();
        Application.Run();

        SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        tray.Visible = false;
        tray.Dispose();
        ipc.DestroyHandle();
        if (mutex != null) mutex.Dispose();
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
        window.Activate();
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

    public static void UpdateTray()
    {
        tray.Icon = Cfg.Enabled ? iconOn : iconOff;
        tray.Text = Cfg.Enabled ? "physmouse" : "physmouse (paused)";
        trayEnabled.Checked = Cfg.Enabled;
        tray.Visible = Cfg.TrayIcon;
    }

    // False when another app already owns that shortcut.
    public static bool SetHotkey(Keys k) { return ipc.Register(k); }
    public static void SuspendHotkey() { ipc.Register(Keys.None); }

    // Hands over to the elevated scheduled task, which starts a fresh copy with administrator rights.
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
            Native.ChangeWindowMessageFilterEx(Handle, ShowMessage, 1, IntPtr.Zero); // let a non-elevated launch reach an elevated copy
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
            else if (m.Msg == Native.WM_HOTKEY) SetEnabled(!Cfg.Enabled);
            else base.WndProc(ref m);
        }
    }
}
