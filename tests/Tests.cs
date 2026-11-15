using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

// Run by build.cmd. Prints each failure and exits non-zero if there were any.
static class Tests
{
    static int failures;

    static void Check(bool ok, string what)
    {
        if (ok) return;
        failures++;
        Console.WriteLine("FAIL: " + what);
    }

    static Point? Land(List<Mon> ms, Point cur, Point p)
    {
        Move mv;
        if (!Mapper.Plan(ms, cur, p, out mv)) return null;
        return mv.To == null ? mv.Edge : mv.Target;
    }

    static int Main()
    {
        // 1440p and 4K, both 600 x 340 mm, side by side with a 100 mm gap; a portrait screen below the 4K one
        var a = new Mon { Id = "A", R = new Rectangle(0, 0, 2560, 1440), X = 0, Y = 0, W = 600, H = 340 };
        var b = new Mon { Id = "B", R = new Rectangle(2560, -500, 3840, 2160), X = 700, Y = 0, W = 600, H = 340 };
        var c = new Mon { Id = "C", R = new Rectangle(9000, 9000, 1080, 1920), X = 800, Y = 400, W = 190, H = 340 };
        var ms = new List<Mon> { a, b, c };

        Check(Land(ms, new Point(10, 10), new Point(20, 20)) == null, "move inside a screen is left to Windows");
        Check(Land(ms, new Point(2559, 720), new Point(2565, 720)) == new Point(2566, 580), "a -> b lands at the same physical height");
        Check(Land(ms, new Point(2560, 1659), new Point(2550, 1659)) == new Point(2553, 1439), "b -> a at the bottom");
        Check(Land(ms, new Point(100, 0), new Point(100, -5)) == new Point(100, 0), "nothing above a: stop at the edge");
        Check(Land(ms, new Point(3840, 1659), new Point(3840, 1665)) == new Point(9568, 9006), "b -> c across the gap");
        Check(Land(ms, new Point(2565, 1659), new Point(2565, 1665)) == new Point(2565, 1659), "nothing under b's left part");
        Check(Land(ms, new Point(9500, 9000), new Point(9500, 8990)) == new Point(3763, 1653), "c -> b");
        Check(Land(ms, new Point(-5, -5), new Point(-6, -6)) == null, "cursor off every screen is left to Windows");

        Move mv;
        Mapper.Plan(ms, new Point(100, 0), new Point(100, -5), out mv);
        Check(mv.To == null && mv.From == a, "Plan reports no neighbour above a");

        // rotated panel: EDID says 597 x 336 mm, Windows reports it as 1440 x 2560 px
        double k = Monitors.MmPerPixel(597, 336, 1440, 2560);
        Check(Math.Abs(1440 * k - 336) < 1 && Math.Abs(2560 * k - 597) < 1, "rotation is detected from the EDID size");

        // settings survive a save/load round trip
        var s = new Settings { Enabled = false, HoldAtEdge = false, Resistance = 40, BypassKey = Keys.ControlKey, LockFullscreen = true, Hotkey = Keys.Control | Keys.Alt | Keys.F12, TrayIcon = false, Inches = true };
        s.Store(new[] { new Mon { Id = @"LEN68F6\5&15fef2ea&1&UID4353", Model = "Legion 34W-2c", R = new Rectangle(0, 0, 3440, 1440), X = -97, Y = -385, W = 797, H = 334 } });
        var r = Settings.Parse(s.Format());
        Check(!r.Enabled && !r.HoldAtEdge && r.Resistance == 40 && r.BypassKey == Keys.ControlKey && r.LockFullscreen && !r.TrayIcon && r.Inches, "options round-trip");
        Check(r.Hotkey == (Keys.Control | Keys.Alt | Keys.F12), "shortcut round-trips");
        Check(r.Layout.ContainsKey(@"LEN68F6\5&15fef2ea&1&UID4353") && r.Layout[@"LEN68F6\5&15fef2ea&1&UID4353"][1] == -385, "layout round-trips");
        Check(Settings.ParseHotkey("ctrl+shift+1") == (Keys.Control | Keys.Shift | Keys.D1), "digit shortcut parses");
        Check(Settings.ParseHotkey("ctrl+banana") == Keys.None && Settings.ParseHotkey("ctrl+alt") == Keys.None, "bad shortcuts are rejected");
        Check(Settings.Parse("").LockFullscreen && Settings.Parse("lock-to-fullscreen = false").LockFullscreen == false, "full-screen lock defaults to on");
        Check(Settings.Parse("edge-resistance = -5\nbypass-key = hyper").Resistance == 0, "out-of-range values are clamped");
        Check(Settings.Parse("[layout]\nA  NaN 0 500 300\nB  0 Infinity 500 300\nC  1e308 0 500 300\nD  0 0 1e-300 300\nE  0 0 500 1e9\nF  -97 -385 797 334").Layout.Keys.Count == 1,
            "implausible layout values (NaN, infinity, huge, tiny) are dropped");
        Check(Settings.ParseHotkey("ctrl+alt+lbutton") == Keys.None, "mouse buttons are not shortcut keys");
        Check(Installer.SameSession(3, 3) && !Installer.SameSession(3, 4), "installer only targets physmouse processes in this session");
        // legacy task: \physmouse starting an exe a standard user could replace, wherever that is
        var admin = new[] { @"C:\Program Files", @"C:\Program Files (x86)", @"C:\WINDOWS" };
        Func<string, string> task = cmd => "<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Actions><Exec><Command>" + cmd + "</Command><Arguments>--background</Arguments></Exec></Actions></Task>";
        Check(Startup.IsLegacyTask(task(@"C:\Users\x\AppData\Local\Programs\physmouse\physmouse.exe"), admin), "legacy task in the per-user install folder is recognized");
        Check(Startup.IsLegacyTask(task("\"%LOCALAPPDATA%\\Programs\\physmouse\\physmouse.exe\""), admin), "quoted path with variables is recognized");
        Check(Startup.IsLegacyTask(task(@"D:\Tools\physmouse-portable\physmouse.exe"), admin), "legacy task of a portable copy is recognized");
        Check(!Startup.IsLegacyTask(task(@"C:\Program Files\physmouse\physmouse.exe"), admin), "a task starting physmouse from Program Files is kept");
        Check(Startup.IsLegacyTask(task(@"C:\Program Files Evil\physmouse.exe"), admin), "a folder that only starts like Program Files doesn't count");
        Check(!Startup.IsLegacyTask("<Task><Actions><ComHandler><ClassId>{0}</ClassId></ComHandler></Actions></Task>", admin), "a task without a command is left alone");

        // a screen whose connection id changed still finds its saved place by EDID product code
        var moved = new List<Mon> { new Mon { Id = @"LEN68F6\7&abc&0&UID999", X = 1, Y = 1, W = 1, H = 1 } };
        r.ApplyTo(moved);
        Check(moved[0].X == -97 && moved[0].W == 797, "layout follows a screen to a new port");

        // layout editor snapping: two 600 mm screens side by side at 0 and 600, dragging a 300 mm one
        var pair = new List<double[]> { new double[] { 0, 600 }, new double[] { 600, 600 } };
        var one = new List<double[]> { new double[] { 0, 600 } };
        double? guide;
        Check(Snap.Axis(148, 300, one, 5, out guide) == 150 && guide == 300, "centre snaps to another screen's centre");
        Check(Snap.Axis(452, 300, pair, 5, out guide) == 450 && guide == 600, "centre snaps to the seam between two screens");
        Check(Snap.Axis(597, 300, one, 5, out guide) == 600 && guide == 600, "edge snaps to touch another screen");
        Check(Snap.Axis(-3, 300, one, 5, out guide) == 0 && guide == 0, "edges align");
        Check(Snap.Axis(400, 300, one, 5, out guide) == 400 && guide == null, "far from everything: no snap");
        var uneven = new List<double[]> { new double[] { 0, 500 }, new double[] { 700, 500 } };
        Check(Snap.Axis(453, 300, uneven, 5, out guide) == 450 && guide == 600, "centre snaps to the middle of all screens");
        Check(Snap.Axis(98, 300, uneven, 5, out guide) == 100 && guide == 250, "nearest snap wins");

        // --- Bridge tests ---

        // Split-edge defaults: A's right edge faces B (top half) and C (bottom half)
        var ba = new Mon { Id = "BA", R = new Rectangle(0, 0, 1920, 1080), X = 0, Y = 0, W = 500, H = 280 };
        var bb = new Mon { Id = "BB", R = new Rectangle(1920, 0, 1920, 1080), X = 510, Y = 0, W = 500, H = 140 };
        var bc = new Mon { Id = "BC", R = new Rectangle(3840, 0, 1920, 1080), X = 510, Y = 140, W = 500, H = 140 };
        var bms = new List<Mon> { ba, bb, bc };
        var defaults = BridgeWindow.GenerateDefaultBridges(bms);
        // BA's right edge should have two bridges: one to BB, one to BC
        var baRight = defaults.FindAll(d => d.ScreenId == "BA" && d.Edge == "Right");
        Check(baRight.Count == 2, "split-edge defaults produce two bridges on BA right");
        var toBB = baRight.Find(d => d.TargetScreenId == "BB");
        var toBC = baRight.Find(d => d.TargetScreenId == "BC");
        Check(toBB != null && toBC != null, "split-edge defaults target BB and BC");
        // They should not overlap: BB bridge ends before BC bridge starts
        if (toBB != null && toBC != null)
        {
            int bbEnd = 1080 - toBB.BottomOffset;
            int bcStart = toBC.TopOffset;
            Check(bbEnd <= bcStart || toBB.TopOffset >= 1080 - toBC.BottomOffset, "split-edge bridges do not overlap");
        }

        // Bridge-aware routing: two bridges on the same edge with different targets
        Mapper.Cfg = new Settings();
        Mapper.Cfg.Bridges.Add(new Bridge { ScreenId = "BA", Edge = "Right", TopOffset = 0, BottomOffset = 540, TargetScreenId = "BB" });
        Mapper.Cfg.Bridges.Add(new Bridge { ScreenId = "BA", Edge = "Right", TopOffset = 540, BottomOffset = 0, TargetScreenId = "BC" });
        Move bmv;
        // Cursor in top half (y=200) should route to BB
        Mapper.Plan(bms, new Point(1919, 200), new Point(1925, 200), out bmv);
        Check(bmv.To == bb, "bridge routes top-half cursor to BB");
        // Cursor in bottom half (y=800) should route to BC
        Mapper.Plan(bms, new Point(1919, 800), new Point(1925, 800), out bmv);
        Check(bmv.To == bc, "bridge routes bottom-half cursor to BC");
        // Cursor outside all bridges on an edge with bridges is blocked
        Mapper.Cfg.Bridges.Clear();
        Mapper.Cfg.Bridges.Add(new Bridge { ScreenId = "BA", Edge = "Right", TopOffset = 100, BottomOffset = 100, TargetScreenId = "BB" });
        Mapper.Plan(bms, new Point(1919, 50), new Point(1925, 50), out bmv);
        Check(bmv.To == null, "cursor outside bridge zone is blocked");

        // Bridge settings round-trip
        var bs = new Settings();
        bs.Bridges.Add(new Bridge { ScreenId = "X", Edge = "Right", TopOffset = 10, BottomOffset = 20, TargetScreenId = "Y" });
        bs.Store(new Mon[0]);
        var bp = Settings.Parse(bs.Format());
        Check(bp.Bridges.Count == 1 && bp.Bridges[0].TargetScreenId == "Y" && bp.Bridges[0].TopOffset == 10 && bp.Bridges[0].BottomOffset == 20, "bridge with TargetScreenId round-trips");

        // Real-world 6-monitor layout: monitor 1's left edge should produce exactly 2 bridges (to mon3 and mon6)
        var m1 = new Mon { Id = "M1", R = new Rectangle(0, 0, 3840, 2160), X = 0, Y = 60.6, W = 698, H = 392.6 };
        var m2 = new Mon { Id = "M2", R = new Rectangle(3840, 0, 1920, 1080), X = 698, Y = 118.2, W = 595.6, H = 335 };
        var m3 = new Mon { Id = "M3", R = new Rectangle(-1920, 0, 1920, 1080), X = -595.6, Y = 118.2, W = 595.6, H = 335 };
        var m4 = new Mon { Id = "M4", R = new Rectangle(1000, -1080, 1920, 1080), X = 102.4, Y = -274.4, W = 595.6, H = 335 };
        var m5 = new Mon { Id = "M5", R = new Rectangle(3840, -1080, 1920, 1080), X = 772.7, Y = -174.8, W = 520.9, H = 293 };
        var m6 = new Mon { Id = "M6", R = new Rectangle(-1920, -1080, 1920, 1080), X = -595.6, Y = -174.8, W = 520.9, H = 293 };
        var realMs = new List<Mon> { m1, m2, m3, m4, m5, m6 };
        var realDefaults = BridgeWindow.GenerateDefaultBridges(realMs);
        var m1Left = realDefaults.FindAll(d => d.ScreenId == "M1" && d.Edge == "Left");
        Check(m1Left.Count == 2, "6-mon layout: M1 left edge has 2 bridges (got " + m1Left.Count + ")");
        Check(m1Left.Exists(d => d.TargetScreenId == "M3"), "6-mon layout: M1 left has bridge to M3");
        Check(m1Left.Exists(d => d.TargetScreenId == "M6"), "6-mon layout: M1 left has bridge to M6");
        // Verify non-overlapping
        if (m1Left.Count == 2)
        {
            var toM3 = m1Left.Find(d => d.TargetScreenId == "M3");
            var toM6 = m1Left.Find(d => d.TargetScreenId == "M6");
            int m3End = 2160 - toM3.BottomOffset;
            int m6End = 2160 - toM6.BottomOffset;
            Check(toM6.TopOffset + toM6.BottomOffset > 0 || toM3.TopOffset + toM3.BottomOffset > 0, "6-mon layout: bridges are not full-edge");
            Check(m6End <= toM3.TopOffset || m3End <= toM6.TopOffset, "6-mon layout: M1 left bridges do not overlap");
        }

        // Backward compat: empty TargetScreenId falls back to FindNeighbor
        Mapper.Cfg = new Settings();
        Mapper.Cfg.Bridges.Add(new Bridge { ScreenId = "A", Edge = "Right", TopOffset = 0, BottomOffset = 0, TargetScreenId = "" });
        Mapper.Plan(ms, new Point(2559, 720), new Point(2565, 720), out bmv);
        Check(bmv.To == b, "empty TargetScreenId falls back to FindNeighbor");
        Mapper.Cfg = new Settings(); // reset

        Console.WriteLine(failures == 0 ? "All tests passed." : failures + " test(s) failed.");
        return failures == 0 ? 0 : 1;
    }
}
