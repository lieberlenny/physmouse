using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
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
    float contentScroll, contentHeight, contentMax;
    RectangleF contentViewport, scrollTrack, scrollThumb;
    bool scrollingContent;
    float scrollStartY, scrollStartOffset;

    // inline number editing
    TextBox box;
    Action<string> boxCommit;

    // general page
    bool capturing;
    string hotkeyError, startupError;
    Startup.Mode startMode;
    bool blockedAppsExpanded;
    bool replayPromptVisible;
    int tutorialStep = -1;
    float tutorialPhase;
    readonly System.Windows.Forms.Timer tutorialTimer = new System.Windows.Forms.Timer { Interval = 30 };

    public MainWindow() : base("physmouse", 1040, 720, 820, 560, true)
    {
        edit = Copy(Mapper.Mons);
        Program.Changed += OnChanged;
        tutorialTimer.Tick += (o, e) => { tutorialPhase += 0.035f; Invalidate(); };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Program.Changed -= OnChanged;
            tutorialTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (Program.Cfg.WelcomeShown) return;
        StartTutorial();
    }

    protected override void OnDeactivated() { EndCapture(); EndEdit(true); tutorialTimer.Stop(); }
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (tutorialStep >= 0 && SystemInformation.IsMenuAnimationEnabled) tutorialTimer.Start();
    }
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
        if (!e.Cancel && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            Program.UpdateTray();
        }
    }

    // ---------------------------------------------------------------- input

    protected override void OnDown(MouseEventArgs e)
    {
        EndEdit(true);
        base.OnDown(e);
        var visibleHotkey = hotkeyRect;
        visibleHotkey.Y -= contentScroll;
        if (capturing && (pressed == null || !visibleHotkey.Contains(e.Location))) EndCapture();
    }

    protected override void OnMove(MouseEventArgs e)
    {
        if (scrollingContent)
        {
            ClearKeyboardFocus();
            contentScroll = Math.Max(0, Math.Min(contentMax,
                scrollStartOffset + (e.Y - scrollStartY) * contentMax / Math.Max(1, scrollTrack.Height - scrollThumb.Height)));
            Invalidate();
            return;
        }
        if (drag == null) { base.OnMove(e); return; }
        mouse = e.Location;
        DragTo(e.Location);
    }

    protected override void OnUp(MouseEventArgs e)
    {
        if (scrollingContent) { scrollingContent = false; pressed = null; Invalidate(); return; }
        if (drag == null) { base.OnUp(e); return; }
        pressed = null;
        drag = null;
        guideX = guideY = null;
        Invalidate();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys key)
    {
        if (tutorialStep >= 0)
        {
            if (key == Keys.Escape) { EndTutorial(); return true; }
            if (key == Keys.Left && tutorialStep > 0) { tutorialStep--; tutorialPhase = 0; Invalidate(); return true; }
            if (key == Keys.Right) { AdvanceTutorial(); return true; }
            return true;
        }
        if (key == Keys.Escape && CloseComboPopup()) return true;
        if (capturing) { CaptureKey(key); return true; }
        if (box != null) return base.ProcessCmdKey(ref msg, key);
        if (key == Keys.Escape) { if (sel != null) { sel = null; Invalidate(); } else Close(); return true; }
        if (page != Page.Layout && contentMax > 0 && (key == Keys.PageDown || key == Keys.PageUp || key == Keys.Home || key == Keys.End))
        {
            ClearKeyboardFocus();
            contentScroll = key == Keys.Home ? 0 : key == Keys.End ? contentMax :
                Math.Max(0, Math.Min(contentMax, contentScroll + (key == Keys.PageDown ? 1 : -1) * contentViewport.Height * 0.85f));
            Invalidate();
            return true;
        }
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
        ClearKeyboardFocus();
        contentScroll = contentHeight = contentMax = 0;
        hotkeyError = startupError = null;
        if (p == Page.General && !Program.Packaged) startMode = Startup.Current();
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
        float left = navW + 20 * s, right = page == Page.Layout ? ClientSize.Width - 40 * s : Math.Min(ClientSize.Width - 52 * s, left + 1000 * s);
        Draw(g, PageNames[(int)page], fTitle, t.Text, new RectangleF(left, 28 * s, right - left, 44 * s), TextFormatFlags.VerticalCenter);
        var area = new RectangleF(left, 92 * s, right - left, ClientSize.Height - 92 * s - 24 * s);
        contentViewport = area;
        if (page == Page.Layout) PaintLayout(g, area);
        else
        {
            hits.Add(new Hit { R = area, Wheel = ScrollContent });
            int firstContentHit = hits.Count;
            var state = g.Save();
            g.SetClip(area);
            g.TranslateTransform(0, -contentScroll);
            if (page == Page.Behavior) PaintBehavior(g, area);
            else if (page == Page.General) PaintGeneral(g, area);
            else PaintAbout(g, area);
            PaintComboPopup(g, area, contentScroll);
            g.Restore(state);

            for (int i = hits.Count - 1; i >= firstContentHit; i--)
            {
                var r = hits[i].R;
                r.Y -= contentScroll;
                r = RectangleF.Intersect(r, area);
                if (r.IsEmpty) hits.RemoveAt(i);
                else hits[i].R = r;
            }
            contentMax = Math.Max(0, contentHeight - area.Height);
            if (contentScroll > contentMax) { contentScroll = contentMax; Invalidate(); }
            PaintScrollBar(g, area);
        }
        if (page == Page.Layout) PaintComboPopup(g, area, 0);
        if (tutorialStep >= 0) PaintTutorial(g);
        else if (replayPromptVisible) PaintReplayPrompt(g);
        PaintFocusBorder(g);
    }

    void ScrollContent(int delta)
    {
        ClearKeyboardFocus();
        EndEdit(true);
        contentScroll = Math.Max(0, Math.Min(contentMax, contentScroll - delta * 48 * s));
        Invalidate();
    }

    void PaintScrollBar(Graphics g, RectangleF area)
    {
        if (contentMax <= 0) return;
        float x = area.Right - 8 * s;
        scrollTrack = new RectangleF(x, area.Y + 4 * s, 4 * s, area.Height - 8 * s);
        float thumbHeight = Math.Max(28 * s, scrollTrack.Height * area.Height / contentHeight);
        float travel = scrollTrack.Height - thumbHeight;
        float thumbY = scrollTrack.Y + (contentMax == 0 ? 0 : travel * contentScroll / contentMax);
        scrollThumb = new RectangleF(x - 3 * s, thumbY, 10 * s, thumbHeight);
        using (var b = new SolidBrush(t.Grid)) Fill(g, b, scrollTrack, 2 * s);
        using (var b = new SolidBrush(scrollThumb.Contains(mouse) ? t.TextDim : t.TextOff)) Fill(g, b, scrollThumb, 5 * s);
        var trackHit = RectangleF.Inflate(scrollTrack, 5 * s, 0);
        hits.Add(new Hit { R = trackHit, Cursor = Cursors.Hand, Down = pt =>
        {
            ClearKeyboardFocus();
            contentScroll = Math.Max(0, Math.Min(contentMax, contentScroll + (pt.Y < scrollThumb.Top ? -1 : 1) * contentViewport.Height * 0.85f));
            Invalidate();
        }});
        hits.Add(new Hit { R = RectangleF.Inflate(scrollThumb, 3 * s, 0), Cursor = Cursors.SizeNS, Down = pt =>
        {
            scrollingContent = true;
            scrollStartY = pt.Y;
            scrollStartOffset = contentScroll;
        }});
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
                using (var b = new SolidBrush(on ? t.NavSelected : IsDown(r) ? t.NavSelected : t.NavHover)) Fill(g, b, r, 8 * s);
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
        Button(g, bx, by, "Reset to Windows defaults", false, true, () =>
        {
            if (MessageBox.Show(this, "Reset the screen positions in this editor to Windows' layout? You can still cancel with Revert before applying.",
                "Reset screen layout", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
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
            using (var b = new SolidBrush(isSel ? (t.HighContrast ? t.Tile : Theme.Mix(t.Tile, t.Accent, t.Dark ? 0.18 : 0.10)) : hot ? Theme.Mix(t.Tile, t.Text, 0.04) : t.Tile)) Fill(g, b, r, 8 * s);
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

        float toggleW = ToggleWidth(g), chooseW = Measure(g, "Choose apps", 0);
        c = Card(g, ref y, a, "\uE740", "Lock cursor in full-screen apps", "When enabled, the cursor stays on the same display while selected apps are full-screen.", toggleW + chooseW + 16 * s);
        chooseAppsRect = new RectangleF(c.Right - 16 * s - toggleW - 8 * s - chooseW, c.Y + (c.Height - 32 * s) / 2, chooseW, 32 * s);
        Button(g, chooseAppsRect.X, chooseAppsRect.Y, "Choose apps", false, true, ChooseFullscreenApps);
        Toggle(g, c, cfg.LockFullscreen, v => { cfg.LockFullscreen = v; Program.Save(); });
        if (cfg.BlockedApps.Length > 0)
        {
            c = Card(g, ref y, a, "\uE8D7", "Apps with cursor lock", cfg.BlockedApps.Length + (cfg.BlockedApps.Length == 1 ? " selected application" : " selected applications"), 0);
            var disclosure = new RectangleF(c.X, c.Y, c.Width, c.Height);
            Draw(g, blockedAppsExpanded ? "\uE70E" : "\uE70D", fGlyphSmall, t.TextDim,
                new RectangleF(c.Right - 36 * s, c.Y, 20 * s, c.Height), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            hits.Add(new Hit { R = disclosure, Click = () => { blockedAppsExpanded = !blockedAppsExpanded; Invalidate(); } });
            if (blockedAppsExpanded)
            {
                string names = string.Join("   ·   ", cfg.BlockedApps.Select(n => n + ".exe"));
                float textHeight = TextRenderer.MeasureText(g, names, fCaption, new Size((int)(a.Width - 32 * s), int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Height;
                float panelHeight = textHeight + 20 * s;
                FillCard(g, new RectangleF(a.X, y, a.Width, panelHeight), false);
                DrawWrapped(g, names, fCaption, t.TextDim, a.X + 16 * s, y + 10 * s, a.Width - 32 * s);
                y += panelHeight + 4 * s;
            }
        }
        contentHeight = y - a.Y;
    }

    // ------------------------------------------------ General page

    RectangleF hotkeyRect;

    void PaintGeneral(Graphics g, RectangleF a)
    {
        var cfg = Program.Cfg;
        float y = a.Y;
        Group(g, ref y, a, "Startup");
        RectangleF c;
        if (Program.Packaged)
        {
            // The Store version's startup entry belongs to Windows (the package's startup task).
            float openW0 = Measure(g, "Open", 0);
            c = Card(g, ref y, a, "\uE7E8", "Start with Windows", "Windows manages this for the Store version. physmouse starts when you sign in unless you turn it off under Startup apps.", openW0);
            Button(g, c.Right - 16 * s - openW0, c.Y + (c.Height - 32 * s) / 2, "Open", false, true, () => Process.Start("ms-settings:startupapps"));
        }
        else
        {
            string[] startItems = { "Off", "On", "On, as administrator" };
            string startDesc = startMode == Startup.Mode.Off ? "physmouse only runs when you open it."
                : startMode == Startup.Mode.User ? "Starts in the background when you sign in."
                : "Starts with administrator rights when you sign in, so the cursor also follows your layout while an elevated app such as Task Manager is in front.";
            c = Card(g, ref y, a, "\uE7E8", "Start with Windows", startDesc, ComboWidth(g, startItems), startupError);
            Combo(g, c, startItems, (int)startMode, i => SetStartup((Startup.Mode)i));
        }

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
        float openW = Measure(g, "Open folder", 0), resetW = Measure(g, "Reset settings", 0);
        string settingsPath = Settings.DiskPath;
        c = Card(g, ref y, a, "\uE8B7", settingsPath, "Preferences and monitor layout are kept in AppData, so they survive updates and reinstalling.", openW + resetW + 8 * s);
        Button(g, c.Right - 16 * s - openW, c.Y + (c.Height - 32 * s) / 2, "Open folder", false, true, () =>
        {
            if (File.Exists(settingsPath)) Process.Start("explorer.exe", "/select,\"" + settingsPath + "\"");
            else Process.Start("explorer.exe", "\"" + Path.GetDirectoryName(settingsPath) + "\"");
        });
        Button(g, c.Right - 16 * s - openW - 8 * s - resetW, c.Y + (c.Height - 32 * s) / 2, "Reset settings", false, true, () =>
        {
            if (MessageBox.Show(this, "Reset physmouse preferences and monitor layout to Windows defaults? The Windows startup setting will stay as it is.",
                "Reset settings", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            EndCapture();
            Program.ResetSettings();
        });
        contentHeight = y - a.Y;
    }

    RectangleF chooseAppsRect;

    void ChooseFullscreenApps()
    {
        var anchor = PointToScreen(new Point((int)chooseAppsRect.X, (int)(chooseAppsRect.Bottom - contentScroll)));
        using (var picker = new AppPickerWindow(Program.Cfg.BlockedApps, anchor))
            if (picker.ShowDialog(this) == DialogResult.OK)
            {
                Program.Cfg.BlockedApps = picker.SelectedApps ?? new string[0];
                Program.Save();
            }
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
        bool updateAvailable = Program.UpdateVersion != null;
        Draw(g, "Version " + Program.Version, fCaption, t.TextDim, new RectangleF(head.X + 92 * s, head.Y + 50 * s, (updateAvailable ? 270 : 400) * s, 20 * s), TextFormatFlags.Left);
        if (updateAvailable)
        {
            float buttonW = Measure(g, "Update", 96 * s), buttonX = head.Right - 16 * s - buttonW;
            float noticeX = buttonX - 220 * s;
            Draw(g, "Update available", fStrong, t.Text, new RectangleF(noticeX, head.Y + 20 * s, 210 * s, 20 * s), TextFormatFlags.EndEllipsis);
            Draw(g, "Version " + Program.Version + " to Version " + Program.UpdateVersion, fCaption, t.TextDim,
                new RectangleF(noticeX, head.Y + 48 * s, 220 * s, 20 * s), TextFormatFlags.EndEllipsis);
            Button(g, buttonX, head.Y + 32 * s, "Update", true, true, () => Process.Start(Program.LatestReleaseUrl));
        }
        y = head.Bottom + 16 * s;

        y += DrawWrapped(g,
            "Windows lines screens up by their pixels. When screens differ in size or resolution, or sit at different heights, the cursor jumps as it crosses from one to the next. " +
            "physmouse knows how big each screen physically is and where it sits on your desk, so the cursor leaves one screen and enters the next at the same physical spot.",
            fBody, t.TextDim, a.X, y, Math.Min(a.Width, 720 * s)) + 24 * s;

        int screens = Mapper.Mons.Count;
        string status = (Program.Cfg.Enabled ? "Remapping is on" : "Remapping is paused") + "  ·  " + screens + (screens == 1 ? " screen" : " screens") + " connected  ·  "
            + (Startup.Elevated ? "running as administrator" : "running without administrator rights");
        var statusArea = new RectangleF(a.X, y, a.Width - 198 * s, a.Height);
        var statusCard = Card(g, ref y, statusArea, "\uE9D9", "Status", status, 0);
        var replayTile = new RectangleF(statusCard.Right + 8 * s, statusCard.Y, a.Right - statusCard.Right - 8 * s, statusCard.Height);
        FillCard(g, replayTile, Hot(replayTile) && pressed == null);
        Draw(g, "\uE72C", fGlyph, t.Text, new RectangleF(replayTile.X + 16 * s, replayTile.Y, 24 * s, replayTile.Height), TextFormatFlags.VerticalCenter);
        Draw(g, "Replay tutorial", fBody, t.Text, new RectangleF(replayTile.X + 48 * s, replayTile.Y + 7 * s, replayTile.Width - 60 * s, 22 * s), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        DrawWrapped(g, "Run setup guide again", fCaption, t.TextDim, replayTile.X + 48 * s, replayTile.Y + 31 * s, replayTile.Width - 60 * s);
        hits.Add(new Hit { R = replayTile, Click = ReplayTutorial });

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
        contentHeight = y - a.Y;
    }

    void ReplayTutorial()
    {
        replayPromptVisible = true;
        Invalidate();
    }

    void ScheduleTutorial(bool restartNow)
    {
        replayPromptVisible = false;
        Program.Cfg.WelcomeShown = false;
        Program.Save();
        if (restartNow) Program.Restart();
    }

    void StartTutorial()
    {
        replayPromptVisible = false;
        page = Page.Layout;
        tutorialStep = 0;
        tutorialPhase = 0;
        if (SystemInformation.IsMenuAnimationEnabled) tutorialTimer.Start();
        Invalidate();
    }

    void EndTutorial()
    {
        tutorialStep = -1;
        tutorialTimer.Stop();
        Program.Cfg.WelcomeShown = true;
        Program.Save();
        Invalidate();
    }

    void AdvanceTutorial()
    {
        if (tutorialStep < 2) { tutorialStep++; tutorialPhase = 0; Invalidate(); }
        else EndTutorial();
    }

    void PaintTutorial(Graphics g)
    {
        using (var b = new SolidBrush(Color.FromArgb(t.Dark ? 190 : 150, Color.Black)))
            g.FillRectangle(b, ClientRectangle);
        hits.Add(new Hit { R = new RectangleF(0, 60 * s, ClientSize.Width, ClientSize.Height - 60 * s) });
        PaintAppName(g, "physmouse", !Program.Cfg.Enabled);
        PaintCaption(g);

        float w = Math.Min(620 * s, ClientSize.Width - 40 * s), h = Math.Min(450 * s, ClientSize.Height - 76 * s);
        var panel = new RectangleF((ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);
        FillCard(g, panel, false);
        int step = tutorialStep + 1;
        Draw(g, "GETTING STARTED     " + step + " / 3", fCaption, t.Accent,
            new RectangleF(panel.X + 26 * s, panel.Y + 18 * s, w - 52 * s, 18 * s), TextFormatFlags.VerticalCenter);
        string[] titles = { "Identify your screens", "Arrange them like your desk", "Apply and move naturally" };
        string[] copy = {
            "Click Identify to flash matching numbers on each physical display.",
            "Drag the screen tiles until they match the positions and heights of your monitors.",
            "Click Apply to use the layout. physmouse lines up cursor crossings by physical height."
        };
        Draw(g, titles[tutorialStep], fTitle, t.Text, new RectangleF(panel.X + 26 * s, panel.Y + 40 * s, w - 52 * s, 40 * s), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        DrawWrapped(g, copy[tutorialStep], fBody, t.TextDim, panel.X + 28 * s, panel.Y + 84 * s, w - 56 * s);
        var demo = new RectangleF(panel.X + 24 * s, panel.Y + 132 * s, w - 48 * s, 188 * s);
        using (var b = new SolidBrush(t.Canvas)) Fill(g, b, demo, 6 * s);
        using (var p = new Pen(t.Stroke, 1)) Stroke(g, p, demo, 6 * s);
        PaintTutorialDemo(g, demo);

        float by = panel.Bottom - 48 * s;
        Button(g, panel.X + 24 * s, by, "Skip tutorial", false, true, EndTutorial);
        float nextW = Measure(g, tutorialStep == 2 ? "Finish" : "Next", 96 * s);
        Button(g, panel.Right - 24 * s - nextW, by, tutorialStep == 2 ? "Finish" : "Next", true, true, AdvanceTutorial);
        if (tutorialStep > 0)
            Button(g, panel.Right - 32 * s - nextW - Measure(g, "Back", 0), by, "Back", false, true,
                () => { tutorialStep--; tutorialPhase = 0; Invalidate(); });
    }

    void PaintTutorialDemo(Graphics g, RectangleF r)
    {
        if (tutorialStep == 0)
        {
            int count = Math.Max(1, Math.Min(4, edit.Count));
            float tw = 76 * s, th = 86 * s, gap = 20 * s;
            float x = r.X + (r.Width - count * tw - (count - 1) * gap) / 2, y = r.Y + 42 * s;
            for (int i = 0; i < count; i++)
            {
                float pulse = SystemInformation.IsMenuAnimationEnabled ? (float)(2 + 2 * (0.5 + 0.5 * Math.Sin(tutorialPhase * 4 + i))) : 3;
                var tile = new RectangleF(x + i * (tw + gap), y, tw, th);
                using (var b = new SolidBrush(t.Tile)) Fill(g, b, tile, 5 * s);
                using (var p = new Pen(t.Accent, pulse * s)) Stroke(g, p, tile, 5 * s);
                Draw(g, edit.Count > i ? edit[i].Number.ToString(CultureInfo.InvariantCulture) : (i + 1).ToString(),
                    fTitle, t.Text, new RectangleF(tile.X, tile.Y + 6 * s, tw, 50 * s), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                Draw(g, "SCREEN " + (i + 1), fCaption, t.TextDim, new RectangleF(tile.X, tile.Bottom - 25 * s, tw, 18 * s), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            Draw(g, "Match each number to the display that flashes", fCaption, t.TextDim,
                new RectangleF(r.X + 12 * s, r.Bottom - 28 * s, r.Width - 24 * s, 18 * s), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        if (tutorialStep == 1)
        {
            var mons = edit.Take(4).ToList();
            if (mons.Count == 0) return;
            double minX = mons.Min(m => m.X), minY = mons.Min(m => m.Y);
            double spanW = mons.Max(m => m.X + m.W) - minX, spanH = mons.Max(m => m.Y + m.H) - minY;
            double gapMm = Math.Max(15, spanW * 0.08), rowW = mons.Sum(m => m.W) + gapMm * (mons.Count - 1);
            float innerW = r.Width - 48 * s, innerH = r.Height - 62 * s;
            double scale = Math.Min(Math.Min(innerW / rowW, innerH / mons.Max(m => m.H)), Math.Min(innerW / spanW, innerH / spanH));
            float totalW = (float)(spanW * scale), totalH = (float)(spanH * scale);
            float tx = r.X + (r.Width - totalW) / 2, ty = r.Y + 30 * s + (innerH - totalH) / 2;
            float progress = SystemInformation.IsMenuAnimationEnabled ? Math.Min(1, tutorialPhase / 1.7f) : 1;
            float ease = 1 - (1 - progress) * (1 - progress);
            float startW = (float)(rowW * scale), sx = r.X + (r.Width - startW) / 2, sy = r.Y + 30 * s + innerH / 2;
            float cursorX = sx;
            foreach (var m in mons)
            {
                float mw = (float)(m.W * scale), mh = (float)(m.H * scale);
                var from = new RectangleF(cursorX, sy - mh / 2, mw, mh);
                var to = new RectangleF(tx + (float)((m.X - minX) * scale), ty + (float)((m.Y - minY) * scale), mw, mh);
                var tile = new RectangleF(from.X + (to.X - from.X) * ease, from.Y + (to.Y - from.Y) * ease, mw, mh);
                DrawDemoMonitor(g, tile, m.Number.ToString(CultureInfo.InvariantCulture));
                cursorX += mw + (float)(gapMm * scale);
            }
            Draw(g, "Tiles move into the same arrangement as your desk", fCaption, t.TextDim,
                new RectangleF(r.X + 12 * s, r.Bottom - 28 * s, r.Width - 24 * s, 18 * s), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        float u = SystemInformation.IsMenuAnimationEnabled ? (tutorialPhase % 2.4f) / 2.4f : 0.5f;
        var left = new RectangleF(r.X + 58 * s, r.Y + 36 * s, 150 * s, 86 * s);
        var right = new RectangleF(r.X + 290 * s, r.Y + 36 * s, 178 * s, 86 * s);
        DrawDemoMonitor(g, left, "1");
        DrawDemoMonitor(g, right, "2");
        float lineY = left.Y + left.Height * 0.62f;
        using (var p = new Pen(t.Accent, 2 * s)) g.DrawLine(p, left.X + 32 * s, lineY, right.Right - 24 * s, lineY);
        float px = left.X + 32 * s + (right.Right - 56 * s - (left.X + 32 * s)) * u;
        var pointer = new[] { new PointF(px, lineY - 8 * s), new PointF(px, lineY + 10 * s), new PointF(px + 5 * s, lineY + 5 * s), new PointF(px + 9 * s, lineY + 13 * s), new PointF(px + 12 * s, lineY + 11 * s), new PointF(px + 8 * s, lineY + 3 * s), new PointF(px + 15 * s, lineY + 3 * s) };
        using (var b = new SolidBrush(t.Text)) g.FillPolygon(b, pointer);
        using (var p = new Pen(t.Bg, 1 * s)) g.DrawPolygon(p, pointer);
        Draw(g, "Same physical height across both screens", fCaption, t.TextDim,
            new RectangleF(r.X + 12 * s, r.Bottom - 28 * s, r.Width - 24 * s, 18 * s), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    void DrawDemoMonitor(Graphics g, RectangleF r, string number)
    {
        using (var b = new SolidBrush(t.Tile)) Fill(g, b, r, 8 * s);
        using (var p = new Pen(t.TileStroke, 1)) Stroke(g, p, r, 8 * s);
        Draw(g, number, fStrong, t.Text, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    void PaintReplayPrompt(Graphics g)
    {
        using (var b = new SolidBrush(Color.FromArgb(t.Dark ? 190 : 150, Color.Black)))
            g.FillRectangle(b, ClientRectangle);
        hits.Add(new Hit { R = new RectangleF(0, 60 * s, ClientSize.Width, ClientSize.Height - 60 * s) });
        PaintAppName(g, "physmouse", !Program.Cfg.Enabled);
        PaintCaption(g);
        float w = Math.Min(520 * s, ClientSize.Width - 40 * s), h = 280 * s;
        var panel = new RectangleF((ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);
        FillCard(g, panel, false);
        Draw(g, "Replay the tutorial?", fTitle, t.Text, new RectangleF(panel.X + 24 * s, panel.Y + 20 * s, w - 48 * s, 40 * s), TextFormatFlags.VerticalCenter);
        var message = new RectangleF(panel.X + 24 * s, panel.Y + 72 * s, w - 48 * s, 96 * s);
        FillCard(g, message, false);
        DrawWrapped(g, "The tutorial will appear the next time physmouse opens. Close and reopen now to see it immediately.",
            fBody, t.TextDim, message.X + 16 * s, message.Y + 14 * s, message.Width - 32 * s);
        float by = panel.Bottom - 48 * s, okW = Measure(g, "OK", 96 * s), restartW = Measure(g, "Close and reopen", 0);
        Button(g, panel.Right - 24 * s - okW, by, "OK", false, true, () => ScheduleTutorial(false));
        Button(g, panel.Right - 32 * s - okW - restartW, by, "Close and reopen", true, true, () => ScheduleTutorial(true));
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
        hits.Add(new Hit { R = r, Cursor = Cursors.IBeam, Click = () =>
        {
            textArea.Y -= contentScroll;
            BeginEdit(textArea, text, commit);
        }, Wheel = wheel });
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
