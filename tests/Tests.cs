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
        Check(Land(ms, new Point(2559, 720), new Point(2565, 720)) == new Point(2560, 580), "a -> b lands at the same physical height");
        Check(Land(ms, new Point(2560, 1659), new Point(2550, 1659)) == new Point(2559, 1439), "b -> a at the bottom");
        Check(Land(ms, new Point(100, 0), new Point(100, -5)) == new Point(100, 0), "nothing above a: stop at the edge");
        Check(Land(ms, new Point(3840, 1659), new Point(3840, 1665)) == new Point(9568, 9000), "b -> c across the gap");
        Check(Land(ms, new Point(2565, 1659), new Point(2565, 1665)) == new Point(2565, 1659), "nothing under b's left part");
        Check(Land(ms, new Point(9500, 9000), new Point(9500, 8990)) == new Point(3763, 1659), "c -> b");
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

        // a screen whose connection id changed still finds its saved place by EDID product code
        var moved = new List<Mon> { new Mon { Id = @"LEN68F6\7&abc&0&UID999", X = 1, Y = 1, W = 1, H = 1 } };
        r.ApplyTo(moved);
        Check(moved[0].X == -97 && moved[0].W == 797, "layout follows a screen to a new port");

        Console.WriteLine(failures == 0 ? "All tests passed." : failures + " test(s) failed.");
        return failures == 0 ? 0 : 1;
    }
}
