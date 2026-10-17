using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

static class Native
{
    public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    public const int WH_MOUSE_LL = 14, WM_MOUSEMOVE = 0x200, WM_HOTKEY = 0x312, WM_DPICHANGED = 0x02E0, WM_SETICON = 0x80;
    public static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT { public Point pt; public int mouseData, flags, time; public IntPtr extraInfo; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint thread);
    [DllImport("user32")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32")] public static extern bool GetCursorPos(out Point p);
    [DllImport("user32")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32")] public static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    struct TRACKMOUSEEVENT { public int cbSize, dwFlags; public IntPtr hwndTrack; public int dwHoverTime; }
    [DllImport("user32")] static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);

    // Asks for WM_NCMOUSELEAVE when the mouse leaves the window's non-client area.
    public static void TrackNonClientLeave(IntPtr hwnd)
    {
        var e = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf(typeof(TRACKMOUSEEVENT)), dwFlags = 0x12, hwndTrack = hwnd }; // TME_LEAVE | TME_NONCLIENT
        TrackMouseEvent(ref e);
    }
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern int RegisterWindowMessage(string name);
    [DllImport("user32")] public static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32")] public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32")] public static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, int msg, int action, IntPtr info);
    [DllImport("user32")] public static extern int GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern bool EnumDisplayDevices(string device, int index, ref DISPLAY_DEVICE dd, int flags);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
    [DllImport("gdi32", CharSet = CharSet.Unicode)] public static extern IntPtr CreateDC(string driver, string device, string output, IntPtr init);
    [DllImport("gdi32")] public static extern int GetDeviceCaps(IntPtr dc, int index);
    [DllImport("gdi32")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("dwmapi")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // Undocumented, but what Explorer and most tray apps use to get dark context menus.
    [DllImport("uxtheme", EntryPoint = "#135")] static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme", EntryPoint = "#136")] static extern void FlushMenuThemes();

    public static void AllowDarkMenus()
    {
        try { SetPreferredAppMode(1); FlushMenuThemes(); } catch (EntryPointNotFoundException) { }
    }
}
