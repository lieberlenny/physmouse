using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// A cursor move that leaves its screen: where it left (Edge) and where it should land (Target on To).
// To is null when no screen lies beyond that edge.
struct Move { public Mon From, To; public Point Edge, Target; }

static class Mapper
{
    public static volatile List<Mon> Mons = new List<Mon>();
    public static volatile Settings Cfg = new Settings();

    static readonly Native.HookProc proc = Hook; // held so the delegate isn't collected
    static IntPtr hook;
    static SynchronizationContext hookThread;
    static int pushed; // edge-resistance progress, only touched on the hook thread
    const int Inset = 6; // pixels past the border where a crossing lands

    // The hook gets its own thread and message loop, so a busy UI can never stall the cursor.
    public static void Start()
    {
        var ready = new ManualResetEvent(false);
        var t = new Thread(() =>
        {
            hookThread = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(hookThread);
            Install();
            // Windows silently drops a low-level hook whose callback ever times out; re-installing is cheap.
            var timer = new System.Windows.Forms.Timer { Interval = 60000 };
            timer.Tick += (s, e) => Install();
            timer.Start();
            ready.Set();
            Application.Run();
            timer.Dispose();
            if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
        });
        t.Name = "physmouse hook";
        t.IsBackground = true;
        t.Priority = ThreadPriority.Highest;
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        ready.WaitOne();
    }

    public static void Rehook()
    {
        if (hookThread != null) hookThread.Post(_ => Install(), null);
    }

    static void Install()
    {
        var old = hook;
        hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, proc, Native.GetModuleHandle(null), 0); // new one first: no gap
        if (old != IntPtr.Zero) Native.UnhookWindowsHookEx(old);
    }

    static IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        var cfg = Cfg;
        if (code >= 0 && wParam.ToInt32() == Native.WM_MOUSEMOVE && cfg.Enabled
            && !(cfg.BypassKey != Keys.None && (Native.GetAsyncKeyState((int)cfg.BypassKey) & 0x8000) != 0))
        {
            var info = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
            Point cur;
            Native.GetCursorPos(out cur);
            Move mv;
            if (!Plan(Mons, cur, info.pt, out mv)) pushed = 0;
            else
            {
                // An app that confines the cursor (exclusive full-screen games) wins: Windows would clamp
                // SetCursorPos into its area, landing the cursor in a corner instead of on the other screen.
                Native.RECT clip;
                if (mv.To != null && Native.GetClipCursor(out clip)
                    && !Rectangle.FromLTRB(clip.Left, clip.Top, clip.Right, clip.Bottom).Contains(mv.Target)) mv.To = null;
                bool locked = cfg.LockFullscreen && FullscreenOn(mv.From, cfg.BlockedApps);
                if (mv.To != null || locked || cfg.HoldAtEdge)
                {
                    bool cross = mv.To != null && !locked;
                    Point dest = cross ? mv.Target : mv.Edge;
                    if (cross && cfg.Resistance > 0)
                    {
                        pushed += Math.Max(Math.Abs(info.pt.X - mv.Edge.X), Math.Abs(info.pt.Y - mv.Edge.Y));
                        if (pushed < cfg.Resistance) dest = mv.Edge; else pushed = 0;
                    }
                    Native.SetCursorPos(dest.X, dest.Y);
                    return (IntPtr)1;
                }
            }
        }
        return Native.CallNextHookEx(hook, code, wParam, lParam);
    }

    // A window covering the whole screen (game, video, presentation), other than the desktop itself.
    // Maximized windows keep their title bar, so they don't count, even on a screen without a taskbar.
    static bool FullscreenOn(Mon m, string[] selectedApps)
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || !FullscreenApps.IsSelectedWindow(fg, selectedApps)
            || (Native.GetWindowLong(fg, -16) & 0x00C00000) == 0x00C00000) return false; // GWL_STYLE, WS_CAPTION
        var cls = new StringBuilder(64);
        Native.GetClassName(fg, cls, cls.Capacity);
        var c = cls.ToString();
        if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd") return false;
        Native.RECT r;
        return Native.GetWindowRect(fg, out r) && r.Left <= m.R.Left && r.Top <= m.R.Top && r.Right >= m.R.Right && r.Bottom >= m.R.Bottom;
    }

    // The cursor at cur wants to go to p. False when that's a move within one screen (or off every
    // screen), which Windows handles. Otherwise mv says where it crosses, in physical terms.
    public static bool Plan(List<Mon> ms, Point cur, Point p, out Move mv)
    {
        mv = new Move();
        var m = ms.FirstOrDefault(x => x.R.Contains(cur));
        if (m == null || m.R.Contains(p)) return false;
        var r = m.R;
        int cx = Math.Max(r.Left, Math.Min(r.Right - 1, p.X)), cy = Math.Max(r.Top, Math.Min(r.Bottom - 1, p.Y));
        mv.From = m;
        mv.Edge = new Point(cx, cy);
        double mx = m.X + (cx - r.Left + 0.5) * m.W / r.Width, my = m.Y + (cy - r.Top + 0.5) * m.H / r.Height;
        int dir = p.X >= r.Right ? 0 : p.X < r.Left ? 1 : p.Y >= r.Bottom ? 2 : 3; // right, left, down, up
        bool horiz = dir < 2;

        // Bridge filtering: once any bridges are configured, every crossing must go through a bridge zone.
        // An edge with no bridges is fully blocked. A matched bridge routes to its TargetScreenId directly.
        var bridges = Cfg.Bridges;
        if (bridges.Count > 0)
        {
            string edgeName = dir == 0 ? "Right" : dir == 1 ? "Left" : dir == 2 ? "Bottom" : "Top";
            int edgeLen = horiz ? r.Height : r.Width;
            int posAlongEdge = horiz ? (cy - r.Top) : (cx - r.Left);
            bool hasEdgeBridge = false;
            Bridge matched = null;
            for (int i = 0; i < bridges.Count; i++)
            {
                var br = bridges[i];
                if (!br.ScreenId.Equals(m.Id, StringComparison.OrdinalIgnoreCase) || !br.Edge.Equals(edgeName, StringComparison.OrdinalIgnoreCase)) continue;
                hasEdgeBridge = true;
                int start = br.TopOffset;
                int end = edgeLen - br.BottomOffset;
                if (posAlongEdge >= start && posAlongEdge < end) { matched = br; break; }
            }
            if (!hasEdgeBridge)
            {
                mv.To = null;
                return true;
            }
            if (matched == null)
            {
                mv.To = null;
                return true;
            }
            // Route to the target specified by the bridge, or fall back to FindNeighbor for legacy bridges
            if (!string.IsNullOrEmpty(matched.TargetScreenId))
            {
                Mon target = null;
                for (int i = 0; i < ms.Count; i++)
                    if (ms[i].Id != null && ms[i].Id.Equals(matched.TargetScreenId, StringComparison.OrdinalIgnoreCase)) { target = ms[i]; break; }
                mv.To = target;
            }
            else
            {
                mv.To = FindNeighbor(ms, m, dir, mx, my);
            }
        }
        else
        {
            mv.To = FindNeighbor(ms, m, dir, mx, my);
        }
        if (mv.To == null) return true;
        var b2 = mv.To.R;
        var t2 = mv.To;
        int tx = dir == 0 ? b2.Left + Inset : dir == 1 ? b2.Right - 1 - Inset : b2.Left + (int)((mx - t2.X) / t2.W * b2.Width);
        int ty = dir == 2 ? b2.Top + Inset : dir == 3 ? b2.Bottom - 1 - Inset : b2.Top + (int)((my - t2.Y) / t2.H * b2.Height);
        mv.Target = new Point(Math.Max(b2.Left, Math.Min(b2.Right - 1, tx)), Math.Max(b2.Top, Math.Min(b2.Bottom - 1, ty)));
        return true;
    }

    // Find the closest monitor across from m's edge in direction dir (0=Right, 1=Left, 2=Down, 3=Up)
    // at the given physical mm position along the edge. Returns null if no neighbor is reachable.
    public static Mon FindNeighbor(List<Mon> ms, Mon m, int dir, double physX, double physY)
    {
        bool horiz = dir < 2;
        Mon best = null;
        double bestGap = double.MaxValue;
        foreach (var n in ms)
        {
            if (n == m) continue;
            double along = horiz ? physY : physX, lo = horiz ? n.Y : n.X, len = horiz ? n.H : n.W;
            if (along < lo || along >= lo + len) continue;
            double gap = dir == 0 ? n.X - (m.X + m.W) : dir == 1 ? m.X - (n.X + n.W)
                       : dir == 2 ? n.Y - (m.Y + m.H) : m.Y - (n.Y + n.H);
            if (gap + (horiz ? n.W : n.H) / 2 <= 0) continue;
            if (gap < bestGap) { best = n; bestGap = gap; }
        }
        return best;
    }
}
