using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

// The settings window: layout editor, behaviour, general options and about.
class MainWindow : PaintedWindow
{
    enum Page { Layout, Behavior, General, About }
    static readonly string[] PageNames = { "Layout", "Behavior", "General", "About" };
    static readonly string[] PageGlyphs = { "\uE7F4", "\uE962", "\uE713", "\uE946" };

    Page page;

    // layout editor
    List<Mon> edit;
    bool dirty;
    Mon sel, drag;
    double grabX, grabY, scale = 1, ox, oy;
    PointF origin;
    double? guideX, guideY;
    RectangleF canvas;
    Point lastCursor;
    readonly System.Windows.Forms.Timer cursorTimer = new System.Windows.Forms.Timer { Interval = 30 };

    // inline number editing
    TextBox box;
    Action<string> boxCommit;

    // general page
    bool capturing;
    string hotkeyError, startupError;
    Startup.Mode startMode;

    public MainWindow() : base("physmouse", 1040, 720, 820, 560, true)
    {
        edit = Copy(Mapper.Mons);
        Program.Changed += OnChanged;
        cursorTimer.Tick += (o, e) =>
        {
            Point p;
            if (page != Page.Layout || !Visible || WindowState == FormWindowState.Minimized || !Native.GetCursorPos(out p) || p == lastCursor) return;
            lastCursor = p;
            Invalidate(Rectangle.Ceiling(canvas));
        };
        cursorTimer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Program.Changed -= OnChanged;
            cursorTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnDeactivated() { EndCapture(); EndEdit(true); }
    protected override void OnScaleChanged() { EndEdit(true); }

    static List<Mon> Copy(IEnumerable<Mon> ms) { return ms.Select(m => m.Clone()).ToList(); }

    void OnChanged()
    {
        // Screens were plugged in or out, or settings changed. Keep unsaved edits for screens still present.
        var fresh = Copy(Mapper.Mons);
        if (dirty)
            foreach (var m in fresh)
            {
                var old = edit.FirstOrDefault(o => o.Id == m.Id);
                if (old != null) { m.X = old.X; m.Y = old.Y; m.W = old.W; m.H = old.H; }
            }
        string selId = sel == null ? null : sel.Id;
        edit = fresh;
        sel = edit.FirstOrDefault(m => m.Id == selId);
        drag = null;
        Invalidate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        EndEdit(true);
        if (dirty && e.CloseReason == CloseReason.UserClosing)
        {
            var answer = MessageBox.Show(this, "Apply the layout changes you made?", "physmouse", MessageBoxButtons.YesNoCancel, MessageBoxIcon.None);
            if (answer == DialogResult.Cancel) { e.Cancel = true; return; }
            if (answer == DialogResult.Yes) Apply();
        }
        base.OnFormClosing(e);
    }

    // ---------------------------------------------------------------- input

    protected override void OnDown(MouseEventArgs e)
    {
        EndEdit(true);
        base.OnDown(e);
        if (capturing && (pressed == null || pressed.R != hotkeyRect)) EndCapture();
    }

    protected override void OnMove(MouseEventArgs e)
    {
        if (drag == null) { base.OnMove(e); return; }
        mouse = e.Location;
        DragTo(e.Location);
    }

    protected override void OnUp(MouseEventArgs e)
    {
        if (drag == null) { base.OnUp(e); return; }
        pressed = null;
        drag = null;
        guideX = guideY = null;
        Invalidate();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys key)
    {
        if (capturing) { CaptureKey(key); return true; }
        if (box != null) return base.ProcessCmdKey(ref msg, key);
        if (key == Keys.Escape) { if (sel != null) { sel = null; Invalidate(); } else Close(); return true; }
        if (page == Page.Layout)
        {
            if (key == (Keys.Control | Keys.S)) { if (dirty) Apply(); return true; }
            if (sel != null)
            {
                double step = (key & Keys.Shift) != 0 ? 10 : 1;
                switch (key & Keys.KeyCode)
                {
                    case Keys.Left: sel.X -= step; break;
                    case Keys.Right: sel.X += step; break;
                    case Keys.Up: sel.Y -= step; break;
                    case Keys.Down: sel.Y += step; break;
                    default: return base.ProcessCmdKey(ref msg, key);
                }
                dirty = true;
                Invalidate();
                return true;
            }
        }
        return base.ProcessCmdKey(ref msg, key);
    }

    void Go(Page p)
    {
        EndEdit(true);
        EndCapture();
        page = p;
        hotkeyError = startupError = null;
        if (p == Page.General) startMode = Startup.Current();
        Invalidate();
    }

    // ---------------------------------------------------------------- painting

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(t.Bg);
        hits.Clear();

        float navW = 260 * s;
        PaintNav(g, navW);
        PaintCaption(g);

        // settings pages keep a readable width; the layout canvas uses whatever space there is
        float left = navW + 20 * s, right = page == Page.Layout ? ClientSize.Width - 40 * s : Math.Min(ClientSize.Width - 40 * s, left + 1000 * s);
        Draw(g, PageNames[(int)page], fTitle, t.Text, new RectangleF(left, 28 * s, right - left, 44 * s), TextFormatFlags.VerticalCenter);
        var area = new RectangleF(left, 92 * s, right - left, ClientSize.Height - 92 * s - 24 * s);
        switch (page)
        {
            case Page.Layout: PaintLayout(g, area); break;
            case Page.Behavior: PaintBehavior(g, area); break;
            case Page.General: PaintGeneral(g, area); break;
            case Page.About: PaintAbout(g, area); break;
        }
    }

    void PaintNav(Graphics g, float navW)
    {
        PaintAppName(g, "physmouse", !Program.Cfg.Enabled);

        float y = 92 * s;
        for (int i = 0; i < PageNames.Length; i++)
        {
            var r = new RectangleF(8 * s, y, navW - 16 * s, 36 * s);
            bool on = (int)page == i;
            if (on || Hot(r))
                using (var b = new SolidBrush(on ? t.NavSelected : IsDown(r) ? t.NavSelected : t.NavHover)) Fill(g, b, r, 4 * s);
            if (on)
                using (var b = new SolidBrush(t.Accent)) Fill(g, b, new RectangleF(r.X, r.Y + 10 * s, 3 * s, 16 * s), 1.5f * s);
            Draw(g, PageGlyphs[i], fGlyph, t.Text, new RectangleF(r.X + 16 * s, r.Y, 20 * s, r.Height), TextFormatFlags.VerticalCenter);
            Draw(g, PageNames[i], fBody, t.Text, new RectangleF(r.X + 48 * s, r.Y, r.Width - 48 * s, r.Height), TextFormatFlags.VerticalCenter);
            var p = (Page)i;
            hits.Add(new Hit { R = r, Click = () => Go(p) });
            y += 40 * s;
        }
    }

    // ------------------------------------------------ Layout page

    void PaintLayout(Graphics g, RectangleF a)
    {
        float y = a.Y;
        string intro = "Drag each screen to where it sits on your desk. The cursor then crosses between screens at the same physical height, so offsets and gaps are fine.";
        y += DrawWrapped(g, intro, fBody, t.TextDim, a.X, y, a.Width) + 16 * s;

        // inspector for the selected screen
        var card = new RectangleF(a.X, y, a.Width, 64 * s);
        FillCard(g, card, false);
        if (sel == null)
            Draw(g, "Select a screen to type in its exact position and size.", fBody, t.TextDim, Inset(card, 20 * s, 0), TextFormatFlags.VerticalCenter);
        else
        {
            float fx = card.Right - 16 * s;
            foreach (var f in new[] { "H", "W", "Y", "X" })
            {
                string label = f == "H" ? "Height" : f == "W" ? "Width" : f;
                var field = new RectangleF(fx - 92 * s, card.Y + 16 * s, 92 * s, 32 * s);
                string key = f;
                double mm = f == "X" ? sel.X : f == "Y" ? sel.Y : f == "W" ? sel.W : sel.H;
                Number(g, field, Units(mm), Program.Cfg.Inches ? "in" : "mm",
                    v => SetField(key, v),
                    d => { SetFieldMm(key, (f == "X" ? sel.X : f == "Y" ? sel.Y : f == "W" ? sel.W : sel.H) + d * (Program.Cfg.Inches ? 2.54 : 1)); });
                float lw = TextRenderer.MeasureText(g, label, fBody, Size.Empty, TextFormatFlags.NoPadding).Width;
                Draw(g, label, fBody, t.TextDim, new RectangleF(field.X - lw - 8 * s, field.Y, lw + 2 * s, field.Height), TextFormatFlags.VerticalCenter);
                fx = field.X - lw - 24 * s;
            }
            var info = new RectangleF(card.X + 20 * s, card.Y + 12 * s, fx - card.X - 24 * s, 20 * s);
            Draw(g, "Screen " + sel.Number, fStrong, t.Text, info, TextFormatFlags.EndEllipsis);
            info.Y += 20 * s;
            Draw(g, SizeText(sel), fCaption, t.TextDim, info, TextFormatFlags.EndEllipsis);
        }
        y = card.Bottom + 12 * s;

        float barH = 32 * s;
        canvas = new RectangleF(a.X, y, a.Width, a.Bottom - y - barH - 16 * s);
        PaintCanvas(g);

        // bottom bar
        float by = a.Bottom - barH;
        float bx = a.X;
        bx = Button(g, bx, by, "Identify", false, true, () => Identify.Show(edit, t)) + 8 * s;
        Button(g, bx, by, "Reset to detected", false, true, () =>
        {
            string selId = sel == null ? null : sel.Id;
            edit = Monitors.Detect();
            sel = edit.FirstOrDefault(m => m.Id == selId);
            dirty = true;
            Invalidate();
        });
        float rx = a.Right - Measure(g, "Apply", 96) ;
        Button(g, rx, by, "Apply", true, dirty, Apply);
        float rx2 = rx - 8 * s - Measure(g, "Revert", 96);
        Button(g, rx2, by, "Revert", false, dirty, Revert);
        if (dirty)
            Draw(g, "Unsaved changes", fBody, t.TextDim, new RectangleF(rx2 - 220 * s, by, 208 * s, barH), TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
    }

    static string SizeText(Mon m)
    {
        return (m.Diagonal / 25.4).ToString("0.0", CultureInfo.CurrentCulture) + "″  ·  " + m.R.Width + " × " + m.R.Height;
    }

    string Units(double mm)
    {
        return Program.Cfg.Inches ? (mm / 25.4).ToString("0.0", CultureInfo.CurrentCulture) : Math.Round(mm).ToString(CultureInfo.CurrentCulture);
    }

    void SetField(string key, string text)
    {
        double v;
        text = text.Trim().Replace(',', '.');
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return;
        SetFieldMm(key, Program.Cfg.Inches ? v * 25.4 : v);
    }

    void SetFieldMm(string key, double mm)
    {
        if (sel == null) return;
        if (key == "X") sel.X = mm;
        else if (key == "Y") sel.Y = mm;
        else if (key == "W") sel.W = Math.Max(20, mm);
        else sel.H = Math.Max(20, mm);
        dirty = true;
        Invalidate();
    }

    void Apply()
    {
        Program.Cfg.Store(edit);
        dirty = false;
        Program.Save();
        Program.Refresh();
    }

    void Revert()
    {
        dirty = false;
        OnChanged();
    }

    double MmX(float x) { return (x - origin.X) / scale + ox; }
    double MmY(float y) { return (y - origin.Y) / scale + oy; }
    float SX(double mm) { return (float)((mm - ox) * scale + origin.X); }
    float SY(double mm) { return (float)((mm - oy) * scale + origin.Y); }
    RectangleF View(Mon m) { return new RectangleF(SX(m.X), SY(m.Y), (float)(m.W * scale), (float)(m.H * scale)); }

    void PaintCanvas(Graphics g)
    {
        using (var b = new SolidBrush(t.Canvas)) Fill(g, b, canvas, 8 * s);
        using (var p = new Pen(t.Stroke, 1)) Stroke(g, p, canvas, 8 * s);
        hits.Add(new Hit { R = canvas, Down = pt => { sel = null; } });
        if (edit.Count == 0) return;

        if (drag == null) // refit only when not dragging, so the view doesn't slide under the mouse
        {
            float pad = 40 * s;
            ox = edit.Min(m => m.X); oy = edit.Min(m => m.Y);
            double w = edit.Max(m => m.X + m.W) - ox, h = edit.Max(m => m.Y + m.H) - oy;
            float cw = canvas.Width - 2 * pad, ch = canvas.Height - 2 * pad;
            scale = Math.Max(0.01, Math.Min(cw / w, ch / h));
            origin = new PointF(canvas.X + pad + (float)((cw - w * scale) / 2), canvas.Y + pad + (float)((ch - h * scale) / 2));
        }

        var state = g.Save();
        using (var clip = Theme.Round(canvas, 8 * s)) g.SetClip(clip);

        // grid every 10 cm (4 in), for a sense of scale
        double step = Program.Cfg.Inches ? 101.6 : 100;
        using (var grid = new Pen(t.Grid, 1))
        {
            for (double x = Math.Ceiling(MmX(canvas.Left) / step) * step; SX(x) < canvas.Right; x += step) g.DrawLine(grid, SX(x), canvas.Top, SX(x), canvas.Bottom);
            for (double y = Math.Ceiling(MmY(canvas.Top) / step) * step; SY(y) < canvas.Bottom; y += step) g.DrawLine(grid, canvas.Left, SY(y), canvas.Right, SY(y));
        }

        using (var guide = new Pen(t.Accent, 1.5f * s) { DashStyle = DashStyle.Dash })
        {
            if (guideX != null) g.DrawLine(guide, SX(guideX.Value), canvas.Top, SX(guideX.Value), canvas.Bottom);
            if (guideY != null) g.DrawLine(guide, canvas.Left, SY(guideY.Value), canvas.Right, SY(guideY.Value));
        }

        foreach (var m in edit.OrderBy(x => x == sel))
        {
            var r = View(m);
            r.Inflate(-2 * s, -2 * s); // touching screens still read as two
            bool isSel = m == sel, hot = Hot(r) && drag == null;
            using (var b = new SolidBrush(isSel ? Theme.Mix(t.Tile, t.Accent, t.Dark ? 0.18 : 0.10) : hot ? Theme.Mix(t.Tile, t.Text, 0.04) : t.Tile)) Fill(g, b, r, 6 * s);
            using (var p = new Pen(isSel ? t.Accent : t.TileStroke, (isSel ? 2 : 1) * s)) Stroke(g, p, r, 6 * s);

            float numSize = Math.Max(10, Math.Min(40 * s, Math.Min(r.Height * 0.36f, r.Width * 0.3f)));
            bool roomy = r.Height > 110 * s && r.Width > 110 * s;
            float mid = r.Y + r.Height / 2 - (roomy ? 18 * s : 0);
            using (var big = new Font(Theme.DisplayFont, numSize, GraphicsUnit.Pixel))
                Draw(g, m.Number.ToString(CultureInfo.InvariantCulture), big, t.Text, new RectangleF(r.X, mid - numSize, r.Width, numSize * 2), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (roomy)
            {
                var line = new RectangleF(r.X + 8 * s, mid + numSize * 0.75f, r.Width - 16 * s, 18 * s);
                Draw(g, m.Model != "" ? m.Model : "Screen " + m.Number, fCaption, t.Text, line, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
                line.Y += 18 * s;
                Draw(g, SizeText(m) + (m.Primary ? "  ·  main display" : ""), fCaption, t.TextDim, line, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
            }

            var mon = m;
            hits.Add(new Hit { R = r, Cursor = Cursors.SizeAll, Down = pt => { sel = drag = mon; grabX = MmX(pt.X) - mon.X; grabY = MmY(pt.Y) - mon.Y; } });
        }

        // where the cursor is right now, in this (possibly unsaved) layout
        Point c;
        Native.GetCursorPos(out c);
        var on = edit.FirstOrDefault(m => m.R.Contains(c));
        if (on != null)
        {
            float cx = SX(on.X + (c.X - on.R.Left + 0.5) * on.W / on.R.Width), cy = SY(on.Y + (c.Y - on.R.Top + 0.5) * on.H / on.R.Height);
            // a small arrow pointer, tip at the cursor position
            float u = 1.1f * s;
            var arrow = new[] { new PointF(0, 0), new PointF(0, 16), new PointF(4, 12.5f), new PointF(7, 18.5f), new PointF(9.5f, 17.5f), new PointF(6.5f, 11.5f), new PointF(11.5f, 11.5f) }
                .Select(q => new PointF(cx + q.X * u, cy + q.Y * u)).ToArray();
            g.FillPolygon(Brushes.White, arrow);
            using (var p = new Pen(Color.Black, 1.2f * s) { LineJoin = LineJoin.Round }) g.DrawPolygon(p, arrow);
        }
        g.Restore(state);
    }

    void DragTo(Point p)
    {
        var others = edit.Where(n => n != drag).ToList();
        drag.X = Snap(MmX(p.X) - grabX, drag.W, others.SelectMany(n => new[] { n.X, n.X + n.W }), out guideX);
        drag.Y = Snap(MmY(p.Y) - grabY, drag.H, others.SelectMany(n => new[] { n.Y, n.Y + n.H }), out guideY);
        dirty = true;
        Invalidate();
    }

    // Snap either edge of [pos, pos+len] onto the nearest edge of another screen when within 8 px on screen.
    // Hold Alt to place freely.
    double Snap(double pos, double len, IEnumerable<double> edges, out double? guide)
    {
        guide = null;
        if ((ModifierKeys & Keys.Alt) != 0) return pos;
        double best = pos, bestD = 8 * s / scale;
        foreach (var e in edges)
            foreach (var cand in new[] { e, e - len })
                if (Math.Abs(cand - pos) < bestD) { best = cand; bestD = Math.Abs(cand - pos); guide = e; }
        return best;
    }

    // ------------------------------------------------ Behavior page

    void PaintBehavior(Graphics g, RectangleF a)
    {
        var cfg = Program.Cfg;
        float y = a.Y;
        string toggleHint = cfg.Hotkey != Keys.None ? " Shortcut: " + Settings.HotkeyText(cfg.Hotkey) + "." : "";
        var c = Card(g, ref y, a, "\uE962", "Remap cursor", "Move the cursor by where your screens sit on the desk. When off, Windows' own pixel layout is used." + toggleHint, ToggleWidth(g));
        Toggle(g, c, cfg.Enabled, Program.SetEnabled);

        string[] edgeItems = { "Stop at the edge", "Use Windows' layout" };
        c = Card(g, ref y, a, "\uE81E", "When there's no screen beyond an edge", "Where the cursor goes when you push it toward a side that has no screen next to it on the desk.", ComboWidth(g, edgeItems));
        Combo(g, c, edgeItems, cfg.HoldAtEdge ? 0 : 1, i => { cfg.HoldAtEdge = i == 0; Program.Save(); });

        c = Card(g, ref y, a, "\uE8B0", "Edge resistance", "How far you need to keep pushing past an edge before the cursor moves to the next screen. Useful for scroll bars and panels docked to a screen edge. 0 turns it off.", 100 * s);
        var field = new RectangleF(c.Right - 16 * s - 100 * s, c.Y + (c.Height - 32 * s) / 2, 100 * s, 32 * s);
        Number(g, field, cfg.Resistance.ToString(CultureInfo.CurrentCulture), "px",
            v => { int n; if (int.TryParse(v.Trim(), out n)) { cfg.Resistance = Math.Max(0, Math.Min(500, n)); Program.Save(); } },
            d => { cfg.Resistance = Math.Max(0, Math.Min(500, cfg.Resistance + d * 10)); Program.Save(); });

        c = Card(g, ref y, a, "\uE765", "Bypass key", "Hold this key while moving the cursor to cross screens the way Windows normally would.", ComboWidth(g, Settings.BypassNames));
        Combo(g, c, Settings.BypassNames, Math.Max(0, Array.IndexOf(Settings.BypassKeys, cfg.BypassKey)), i => { cfg.BypassKey = Settings.BypassKeys[i]; Program.Save(); });

        c = Card(g, ref y, a, "\uE740", "Keep the cursor on full-screen apps", "While a game or video fills a screen, the cursor can't leave that screen." + (cfg.BypassKey != Keys.None ? " Hold the bypass key to get out." : " Switch apps with Alt+Tab, or set a bypass key, to get out."), ToggleWidth(g));
        Toggle(g, c, cfg.LockFullscreen, v => { cfg.LockFullscreen = v; Program.Save(); });
    }

    // ------------------------------------------------ General page

    RectangleF hotkeyRect;

    void PaintGeneral(Graphics g, RectangleF a)
    {
        var cfg = Program.Cfg;
        float y = a.Y;
        Group(g, ref y, a, "Startup");
        string[] startItems = { "Off", "On", "On, as administrator" };
        string startDesc = startMode == Startup.Mode.Off ? "physmouse only runs when you open it."
            : startMode == Startup.Mode.User ? "Starts in the background when you sign in."
            : "Starts with administrator rights when you sign in, so the cursor also follows your layout while an elevated app such as Task Manager is in front.";
        var c = Card(g, ref y, a, "\uE7E8", "Start with Windows", startDesc, ComboWidth(g, startItems), startupError);
        Combo(g, c, startItems, (int)startMode, i => SetStartup((Startup.Mode)i));

        Group(g, ref y, a, "Controls");
        c = Card(g, ref y, a, "\uE75B", "Notification area icon", "Shows physmouse next to the clock. If you hide it, open physmouse from Start to get back here.", ToggleWidth(g));
        Toggle(g, c, cfg.TrayIcon, v => { cfg.TrayIcon = v; Program.Save(); Program.UpdateTray(); });

        c = Card(g, ref y, a, "\uE765", "On/off shortcut", "Turns cursor remapping on or off from any app. Click the box and press the keys; Backspace removes the shortcut.", 200 * s, hotkeyError);
        hotkeyRect = new RectangleF(c.Right - 16 * s - 200 * s, c.Y + (c.Height - 32 * s) / 2, 200 * s, 32 * s);
        PaintControlBox(g, hotkeyRect, capturing);
        Draw(g, capturing ? "Press a shortcut…" : Settings.HotkeyText(cfg.Hotkey), fBody, capturing || cfg.Hotkey == Keys.None ? t.TextDim : t.Text,
            Inset(hotkeyRect, 12 * s, 0), TextFormatFlags.VerticalCenter);
        hits.Add(new Hit { R = hotkeyRect, Click = BeginCapture });

        string[] unitItems = { "Millimetres", "Inches" };
        c = Card(g, ref y, a, "\uECC6", "Units", "Used for positions and sizes in the layout editor.", ComboWidth(g, unitItems));
        Combo(g, c, unitItems, cfg.Inches ? 1 : 0, i => { cfg.Inches = i == 1; Program.Save(); });

        Group(g, ref y, a, "Settings file");
        c = Card(g, ref y, a, "\uE8B7", Settings.FilePath, "Everything physmouse remembers is in this plain-text file. To make a portable copy, put a settings.ini next to physmouse.exe.", Measure(g, "Open folder", 0));
        Button(g, c.Right - 16 * s - Measure(g, "Open folder", 0), c.Y + (c.Height - 32 * s) / 2, "Open folder", false, true, () =>
        {
            if (File.Exists(Settings.FilePath)) Process.Start("explorer.exe", "/select,\"" + Settings.FilePath + "\"");
            else Process.Start("explorer.exe", "\"" + Path.GetDirectoryName(Settings.FilePath) + "\"");
        });
    }

    void SetStartup(Startup.Mode mode)
    {
        startupError = Startup.Set(mode);
        startMode = Startup.Current();
        Invalidate();
        if (startupError == null && mode == Startup.Mode.Admin && !Startup.Elevated)
        {
            var answer = MessageBox.Show(this, "Restart physmouse with administrator rights now? Otherwise this takes effect the next time you sign in.",
                "physmouse", MessageBoxButtons.YesNo, MessageBoxIcon.None);
            if (answer == DialogResult.Yes && !Program.RestartElevated())
                startupError = "Couldn't start the administrator copy now. It will start the next time you sign in.";
        }
    }

    void BeginCapture()
    {
        capturing = true;
        hotkeyError = null;
        Program.SuspendHotkey(); // otherwise pressing the current shortcut toggles instead of being typed
        Invalidate();
    }

    void EndCapture()
    {
        if (!capturing) return;
        capturing = false;
        Program.SetHotkey(Program.Cfg.Hotkey);
        Invalidate();
    }

    void CaptureKey(Keys key)
    {
        var code = key & Keys.KeyCode;
        var mods = key & Keys.Modifiers;
        if (code == Keys.ShiftKey || code == Keys.ControlKey || code == Keys.Menu || code == Keys.LWin || code == Keys.RWin) return;
        if (code == Keys.Escape && mods == Keys.None) { EndCapture(); return; }
        if ((code == Keys.Back || code == Keys.Delete) && mods == Keys.None)
        {
            Program.Cfg.Hotkey = Keys.None;
            Program.Save();
            EndCapture();
            return;
        }
        bool fkey = code >= Keys.F1 && code <= Keys.F24;
        if (!fkey && (mods & (Keys.Control | Keys.Alt)) == 0)
        {
            hotkeyError = "Use Ctrl or Alt together with a key, or a function key on its own.";
            Invalidate();
            return;
        }
        capturing = false;
        if (Program.SetHotkey(key)) { Program.Cfg.Hotkey = key; hotkeyError = null; Program.Save(); }
        else { hotkeyError = Settings.HotkeyText(key) + " is already used by another app."; Program.SetHotkey(Program.Cfg.Hotkey); }
        Invalidate();
    }

    // ------------------------------------------------ About page

    void PaintAbout(Graphics g, RectangleF a)
    {
        float y = a.Y;
        var head = new RectangleF(a.X, y, a.Width, 96 * s);
        FillCard(g, head, false);
        using (var icon = Theme.AppIcon((int)(56 * s), false)) g.DrawImage(icon, head.X + 20 * s, head.Y + 20 * s);
        Draw(g, "physmouse", fStrong, t.Text, new RectangleF(head.X + 92 * s, head.Y + 26 * s, 400 * s, 22 * s), TextFormatFlags.Left);
        Draw(g, "Version " + Program.Version, fCaption, t.TextDim, new RectangleF(head.X + 92 * s, head.Y + 50 * s, 400 * s, 20 * s), TextFormatFlags.Left);
        y = head.Bottom + 16 * s;

        y += DrawWrapped(g,
            "Windows lines screens up by their pixels. When screens differ in size or resolution, or sit at different heights, the cursor jumps as it crosses from one to the next. " +
            "physmouse knows how big each screen physically is and where it sits on your desk, so the cursor leaves one screen and enters the next at the same physical spot.",
            fBody, t.TextDim, a.X, y, Math.Min(a.Width, 720 * s)) + 24 * s;

        int screens = Mapper.Mons.Count;
        string status = (Program.Cfg.Enabled ? "Remapping is on" : "Remapping is paused") + "  ·  " + screens + (screens == 1 ? " screen" : " screens") + " connected  ·  "
            + (Startup.Elevated ? "running as administrator" : "running without administrator rights");
        Card(g, ref y, a, "\uE9D9", "Status", status, 0);

        var c = Card(g, ref y, a, "\uE943", "Source code", Program.RepoUrl, Measure(g, "Open", 0));
        Button(g, c.Right - 16 * s - Measure(g, "Open", 0), c.Y + (c.Height - 32 * s) / 2, "Open", false, true, () => Process.Start(Program.RepoUrl));
        c = Card(g, ref y, a, "\uEBE8", "Report a problem", "Describe what happened and attach your settings.ini.", Measure(g, "Open", 0));
        Button(g, c.Right - 16 * s - Measure(g, "Open", 0), c.Y + (c.Height - 32 * s) / 2, "Open", false, true, () => Process.Start(Program.RepoUrl + "/issues"));
        Card(g, ref y, a, "\uE8D7", "License", "MIT. Free to use, change and share.", 0);
        if (Installer.RunningInstalled)
        {
            c = Card(g, ref y, a, "\uE74D", "Uninstall", "Removes physmouse, its shortcuts and its startup entry. You can also do this from Settings > Apps.", Measure(g, "Uninstall", 0));
            Button(g, c.Right - 16 * s - Measure(g, "Uninstall", 0), c.Y + (c.Height - 32 * s) / 2, "Uninstall", false, true,
                () => Process.Start(Application.ExecutablePath, "--uninstall"));
        }
    }


    // A number box; click to type, scroll to step.
    void Number(Graphics g, RectangleF r, string text, string unit, Action<string> commit, Action<int> wheel)
    {
        bool editing = box != null && Rectangle.Round(r).Contains(box.Bounds.Location);
        PaintControlBox(g, r, editing);
        float unitW = TextRenderer.MeasureText(g, unit, fBody, Size.Empty, TextFormatFlags.NoPadding).Width;
        Draw(g, unit, fBody, t.TextDim, new RectangleF(r.Right - 10 * s - unitW, r.Y, unitW + 2 * s, r.Height), TextFormatFlags.VerticalCenter);
        if (!editing)
            Draw(g, text, fBody, t.Text, new RectangleF(r.X + 8 * s, r.Y, r.Width - 24 * s - unitW, r.Height), TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        var textArea = new RectangleF(r.X + 8 * s, r.Y, r.Width - 24 * s - unitW, r.Height);
        hits.Add(new Hit { R = r, Cursor = Cursors.IBeam, Click = () => BeginEdit(textArea, text, commit), Wheel = wheel });
    }

    void BeginEdit(RectangleF area, string text, Action<string> commit)
    {
        EndEdit(true);
        int h = fBody.Height;
        box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            Font = fBody,
            BackColor = t.Control,
            ForeColor = t.Text,
            TextAlign = HorizontalAlignment.Right,
            Text = text,
            Bounds = new Rectangle((int)area.X, (int)(area.Y + (area.Height - h) / 2), (int)area.Width, h),
        };
        boxCommit = commit;
        box.KeyDown += (o, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; EndEdit(true); }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; EndEdit(false); }
        };
        box.Leave += (o, e) => EndEdit(true);
        Controls.Add(box);
        box.Focus();
        box.SelectAll();
        Invalidate();
    }

    void EndEdit(bool keep)
    {
        if (box == null) return;
        var b = box;
        var commit = boxCommit;
        box = null;
        boxCommit = null;
        if (keep) commit(b.Text);
        Controls.Remove(b);
        b.Dispose();
        Invalidate();
    }
}

// Big screen numbers, like Windows' own "Identify", so the tiles can be matched to real screens.
class Identify : Form
{
    readonly Mon mon;
    readonly Theme theme;

    public static void Show(IEnumerable<Mon> ms, Theme t)
    {
        foreach (var m in ms) new Identify(m, t).Show();
    }

    Identify(Mon m, Theme t)
    {
        mon = m;
        theme = t;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        int size = Math.Max(120, m.R.Height / 6);
        Bounds = new Rectangle(m.R.Left + size / 4, m.R.Top + size / 4, size, size);
        BackColor = t.Accent;
        var timer = new System.Windows.Forms.Timer { Interval = 2500 };
        timer.Tick += (o, e) => { timer.Dispose(); Close(); };
        timer.Start();
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x80; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2; // DWMWCP_ROUND
        Native.DwmSetWindowAttribute(Handle, 33, ref round, 4);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using (var f = new Font(Theme.DisplayFont, Height * 0.55f, GraphicsUnit.Pixel))
            TextRenderer.DrawText(e.Graphics, mon.Number.ToString(CultureInfo.InvariantCulture), f, ClientRectangle, theme.OnAccent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }
}
