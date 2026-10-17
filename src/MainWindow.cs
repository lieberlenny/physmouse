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

// The settings window. Everything is painted here in the style of the Windows 11 Settings app; each
// paint records clickable regions ("hits"), and mouse input is dispatched to those.
class MainWindow : Form
{
    enum Page { Layout, Behavior, General, About }
    static readonly string[] PageNames = { "Layout", "Behavior", "General", "About" };
    static readonly string[] PageGlyphs = { "\uE7F4", "\uE962", "\uE713", "\uE946" };

    class Hit { public RectangleF R; public Action Click; public Action<Point> Down; public Action<int> Wheel; public Cursor Cursor; }

    Theme t = Theme.Current();
    Page page;
    readonly List<Hit> hits = new List<Hit>();
    Point mouse = new Point(-9999, -9999);
    Hit pressed;
    float s = 1; // DPI scale
    Font fTitle, fBody, fStrong, fCaption, fGlyph, fGlyphSmall;

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

    public MainWindow()
    {
        Text = "physmouse";
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        ResizeRedraw = true;
        KeyPreview = true;
        BackColor = t.Bg;
        s = DeviceDpi / 96f;
        MakeFonts();
        ClientSize = new Size((int)(1040 * s), (int)(720 * s));
        MinimumSize = new Size((int)(820 * s), (int)(560 * s));
        edit = Copy(Mapper.Mons);

        Program.Changed += OnChanged;
        SystemEvents.UserPreferenceChanged += OnThemeChanged;
        cursorTimer.Tick += (o, e) =>
        {
            Point p;
            if (page != Page.Layout || !Visible || WindowState == FormWindowState.Minimized || !Native.GetCursorPos(out p) || p == lastCursor) return;
            lastCursor = p;
            Invalidate(Rectangle.Ceiling(canvas));
        };
        cursorTimer.Start();

        MouseMove += OnMouseMove;
        MouseDown += OnMouseDown;
        MouseUp += OnMouseUp;
        MouseWheel += (o, e) => { var h = HitAt(e.Location); if (h != null && h.Wheel != null) h.Wheel(e.Delta > 0 ? 1 : -1); };
        MouseLeave += (o, e) => { mouse = new Point(-9999, -9999); Invalidate(); };
        Deactivate += (o, e) => { active = false; EndCapture(); EndEdit(true); Invalidate(); };
        Activated += (o, e) => { active = true; Invalidate(); };
        Resize += (o, e) => Invalidate(); // maximize/restore swaps the max button glyph
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Program.Changed -= OnChanged;
            SystemEvents.UserPreferenceChanged -= OnThemeChanged;
            cursorTimer.Dispose();
            foreach (var f in new[] { fTitle, fBody, fStrong, fCaption, fGlyph, fGlyphSmall }) f.Dispose();
            if (bigIcon != null) { bigIcon.Dispose(); smallIcon.Dispose(); }
        }
        base.Dispose(disposing);
    }

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

    void OnThemeChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General && e.Category != UserPreferenceCategory.Color && e.Category != UserPreferenceCategory.VisualStyle) return;
        if (InvokeRequired) { BeginInvoke((Action)(() => OnThemeChanged(sender, e))); return; }
        t = Theme.Current();
        BackColor = t.Bg;
        ApplyTitleBar();
        Invalidate();
    }

    // ---------------------------------------------------------------- window plumbing

    void MakeFonts()
    {
        foreach (var f in new[] { fTitle, fBody, fStrong, fCaption, fGlyph, fGlyphSmall }) if (f != null) f.Dispose();
        fTitle = new Font(Theme.DisplayFont, 28 * s, GraphicsUnit.Pixel);
        fBody = new Font(Theme.TextFont, 14 * s, GraphicsUnit.Pixel);
        fStrong = new Font(Theme.StrongFont, 14 * s, GraphicsUnit.Pixel);
        fCaption = new Font(Theme.TextFont, 12 * s, GraphicsUnit.Pixel);
        fGlyph = new Font(Theme.IconFont, 16 * s, GraphicsUnit.Pixel);
        fGlyphSmall = new Font(Theme.IconFont, 10 * s, GraphicsUnit.Pixel);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        float real = Native.GetDpiForWindow(Handle) / 96f;
        if (real > 0 && Math.Abs(real - s) > 0.01f)
        {
            float k = real / s;
            s = real;
            MakeFonts();
            ClientSize = new Size((int)(ClientSize.Width * k), (int)(ClientSize.Height * k));
            MinimumSize = new Size((int)(820 * s), (int)(560 * s));
        }
        ApplyTitleBar();
        Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x37); // SWP_FRAMECHANGED | NOMOVE | NOSIZE | NOZORDER | NOACTIVATE: recalc without caption
    }

    void ApplyTitleBar()
    {
        int dark = t.Dark ? 1 : 0;
        Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    // WinForms sets its default icon after OnHandleCreated, so ours goes on once the window is shown.
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        SetIcons();
    }

    Icon bigIcon, smallIcon;
    void SetIcons()
    {
        if (bigIcon != null) { bigIcon.Dispose(); smallIcon.Dispose(); }
        bigIcon = Theme.MakeIcon((int)(32 * s), false);
        smallIcon = Theme.MakeIcon((int)(16 * s), false);
        Native.SendMessage(Handle, Native.WM_SETICON, (IntPtr)1, bigIcon.Handle);
        Native.SendMessage(Handle, Native.WM_SETICON, IntPtr.Zero, smallIcon.Handle);
    }

    // ------------------------------------------------ custom title bar
    // The standard frame stays (resize borders, shadow, snapping, rounded corners); only the caption is
    // folded into the client area, where the window draws its own buttons level with the app icon.

    const int HTCLIENT = 1, HTCAPTION = 2, HTMAXBUTTON = 9, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;
    const int WM_NCCALCSIZE = 0x83, WM_NCHITTEST = 0x84, WM_NCMOUSEMOVE = 0xA0, WM_NCLBUTTONDOWN = 0xA1, WM_NCLBUTTONUP = 0xA2,
        WM_NCLBUTTONDBLCLK = 0xA3, WM_NCMOUSELEAVE = 0x2A2;
    RectangleF minRect, maxRect, closeRect;
    bool maxHot, maxDown, active = true;
    float CaptionHeight { get { return 60 * s; } }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_NCCALCSIZE:
                if (m.WParam == IntPtr.Zero) break;
                var before = (Native.RECT)Marshal.PtrToStructure(m.LParam, typeof(Native.RECT)); // first RECT of NCCALCSIZE_PARAMS
                base.WndProc(ref m);
                var after = (Native.RECT)Marshal.PtrToStructure(m.LParam, typeof(Native.RECT));
                // drop the caption; a maximized window hangs its frame off-screen, so inset by the same amount as the sides
                after.Top = before.Top + (WindowState == FormWindowState.Maximized ? after.Left - before.Left : 0);
                Marshal.StructureToPtr(after, m.LParam, false);
                m.Result = IntPtr.Zero;
                return;

            case WM_NCHITTEST:
                base.WndProc(ref m);
                if (m.Result.ToInt32() != HTCLIENT) return; // side and bottom resize borders
                long lp = m.LParam.ToInt64();
                var pt = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                if (WindowState != FormWindowState.Maximized && pt.Y < 5 * s)
                    m.Result = (IntPtr)(pt.X < 16 * s ? HTTOPLEFT : pt.X > ClientSize.Width - 16 * s ? HTTOPRIGHT : HTTOP);
                else if (maxRect.Contains(pt))
                    m.Result = (IntPtr)HTMAXBUTTON; // lets Windows 11 show snap layouts on hover
                else if (pt.Y < CaptionHeight && !minRect.Contains(pt) && !closeRect.Contains(pt))
                    m.Result = (IntPtr)HTCAPTION;
                return;

            // The maximize button is non-client (for snap layouts), so its hover and click are handled here.
            case WM_NCMOUSEMOVE:
                bool hot = m.WParam.ToInt32() == HTMAXBUTTON;
                if (hot != maxHot) { maxHot = hot; Invalidate(Rectangle.Ceiling(maxRect)); }
                if (hot) Native.TrackNonClientLeave(Handle);
                break;
            case WM_NCMOUSELEAVE:
                if (maxHot || maxDown) { maxHot = maxDown = false; Invalidate(Rectangle.Ceiling(maxRect)); }
                break;
            case WM_NCLBUTTONDOWN:
                if (m.WParam.ToInt32() != HTMAXBUTTON) break;
                maxDown = true;
                Invalidate(Rectangle.Ceiling(maxRect));
                return;
            case WM_NCLBUTTONUP:
                if (m.WParam.ToInt32() != HTMAXBUTTON) break;
                if (maxDown) ToggleMaximize();
                maxDown = false;
                Invalidate(Rectangle.Ceiling(maxRect));
                return;
            case WM_NCLBUTTONDBLCLK:
                if (m.WParam.ToInt32() == HTMAXBUTTON) return;
                break;
        }
        base.WndProc(ref m);
        if (m.Msg == Native.WM_DPICHANGED)
        {
            EndEdit(true);
            s = (m.WParam.ToInt32() & 0xFFFF) / 96f;
            MakeFonts();
            var r = (Native.RECT)Marshal.PtrToStructure(m.LParam, typeof(Native.RECT));
            MinimumSize = new Size((int)(820 * s), (int)(560 * s));
            Bounds = new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            SetIcons();
            Invalidate();
        }
    }

    void ToggleMaximize()
    {
        WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
    }

    // Three buttons, level with the app icon in the top-left corner.
    void PaintCaption(Graphics g)
    {
        float w = 46 * s, h = 32 * s, top = 16 * s, x = ClientSize.Width - 8 * s - 3 * w;
        minRect = new RectangleF(x, top, w, h);
        maxRect = new RectangleF(x + w, top, w, h);
        closeRect = new RectangleF(x + 2 * w, top, w, h);
        CaptionButton(g, minRect, "", Hot(minRect), IsDown(minRect), false);
        CaptionButton(g, maxRect, WindowState == FormWindowState.Maximized ? "" : "", maxHot, maxDown, false);
        CaptionButton(g, closeRect, "", Hot(closeRect), IsDown(closeRect), true);
        hits.Add(new Hit { R = minRect, Click = () => WindowState = FormWindowState.Minimized });
        hits.Add(new Hit { R = closeRect, Click = Close });
    }

    void CaptionButton(Graphics g, RectangleF r, string glyph, bool hot, bool down, bool close)
    {
        Color fg = active ? t.Text : t.TextOff;
        if (close && (hot || down))
        {
            using (var b = new SolidBrush(down ? Color.FromArgb(0xC4, 0x2B, 0x1C) : Color.FromArgb(0xE8, 0x11, 0x23))) Fill(g, b, r, 4 * s);
            fg = Color.White;
        }
        else if (hot || down)
            using (var b = new SolidBrush(down ? t.NavSelected : t.NavHover)) Fill(g, b, r, 4 * s);
        Draw(g, glyph, fGlyphSmall, fg, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
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

    Hit HitAt(Point p)
    {
        for (int i = hits.Count - 1; i >= 0; i--) if (hits[i].R.Contains(p)) return hits[i];
        return null;
    }

    void OnMouseDown(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        EndEdit(true);
        pressed = HitAt(e.Location);
        if (capturing && (pressed == null || pressed.Click == null || pressed.R != hotkeyRect)) EndCapture();
        if (pressed != null && pressed.Down != null) pressed.Down(e.Location);
        Invalidate();
    }

    void OnMouseMove(object sender, MouseEventArgs e)
    {
        mouse = e.Location;
        if (drag != null) { DragTo(e.Location); return; }
        var h = HitAt(e.Location);
        Cursor = h != null && h.Cursor != null ? h.Cursor : Cursors.Default;
        Invalidate();
    }

    void OnMouseUp(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var h = pressed;
        pressed = null;
        if (drag != null) { drag = null; guideX = guideY = null; }
        else if (h != null && h.Click != null && h.R.Contains(e.Location)) h.Click();
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
        using (var icon = Theme.AppIcon((int)(20 * s), !Program.Cfg.Enabled)) g.DrawImage(icon, 20 * s, 22 * s);
        Draw(g, "physmouse", fCaption, t.Text, new RectangleF(52 * s, 20 * s, navW, 24 * s), TextFormatFlags.VerticalCenter);

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
    }

    // ---------------------------------------------------------------- building blocks

    bool Hot(RectangleF r) { return r.Contains(mouse) && (pressed == null || pressed.R == r); }
    bool IsDown(RectangleF r) { return pressed != null && pressed.R == r && r.Contains(mouse); }

    static RectangleF Inset(RectangleF r, float x, float y) { return new RectangleF(r.X + x, r.Y + y, r.Width - 2 * x, r.Height - 2 * y); }

    void Draw(Graphics g, string text, Font f, Color c, RectangleF r, TextFormatFlags flags)
    {
        TextRenderer.DrawText(g, text, f, Rectangle.Round(r), c, flags | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | (flags.HasFlag(TextFormatFlags.WordBreak) ? 0 : TextFormatFlags.SingleLine));
    }

    float DrawWrapped(Graphics g, string text, Font f, Color c, float x, float y, float w)
    {
        var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        var size = TextRenderer.MeasureText(g, text, f, new Size((int)w, int.MaxValue), flags);
        TextRenderer.DrawText(g, text, f, new Rectangle((int)x, (int)y, (int)w, size.Height), c, flags);
        return size.Height;
    }

    static void Fill(Graphics g, Brush b, RectangleF r, float rad) { using (var p = Theme.Round(r, rad)) g.FillPath(b, p); }
    static void Stroke(Graphics g, Pen pen, RectangleF r, float rad)
    {
        var half = pen.Width / 2;
        using (var p = Theme.Round(new RectangleF(r.X + half, r.Y + half, r.Width - pen.Width, r.Height - pen.Width), rad)) g.DrawPath(pen, p);
    }

    void FillCard(Graphics g, RectangleF r, bool hover)
    {
        using (var b = new SolidBrush(hover ? t.CardHover : t.Card)) Fill(g, b, r, 4 * s);
        using (var p = new Pen(t.Stroke, 1)) Stroke(g, p, r, 4 * s);
    }

    void Group(Graphics g, ref float y, RectangleF a, string title)
    {
        if (y > a.Y) y += 20 * s;
        Draw(g, title, fStrong, t.Text, new RectangleF(a.X + 2 * s, y, a.Width, 20 * s), TextFormatFlags.Left);
        y += 28 * s;
    }

    // A settings row: icon, title, description on the left; the caller puts a control at the right.
    RectangleF Card(Graphics g, ref float y, RectangleF a, string glyph, string title, string desc, float controlW, string error = null)
    {
        float textX = a.X + 56 * s, textW = a.Width - 56 * s - 16 * s - (controlW > 0 ? controlW + 32 * s : 0);
        var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        int descH = desc == "" ? 0 : TextRenderer.MeasureText(g, desc, fCaption, new Size((int)textW, int.MaxValue), flags).Height;
        int errH = error == null ? 0 : TextRenderer.MeasureText(g, error, fCaption, new Size((int)textW, int.MaxValue), flags).Height + (int)(4 * s);
        float h = Math.Max(68 * s, 14 * s + 20 * s + descH + errH + 14 * s);
        var r = new RectangleF(a.X, y, a.Width, h);
        FillCard(g, r, Hot(r) && pressed == null);
        float ty = r.Y + (h - (20 * s + descH + errH)) / 2;
        Draw(g, glyph, fGlyph, t.Text, new RectangleF(a.X + 20 * s, r.Y, 20 * s, h), TextFormatFlags.VerticalCenter);
        Draw(g, title, fBody, t.Text, new RectangleF(textX, ty, textW, 20 * s), TextFormatFlags.EndEllipsis | TextFormatFlags.PathEllipsis);
        if (descH > 0) TextRenderer.DrawText(g, desc, fCaption, new Rectangle((int)textX, (int)(ty + 20 * s), (int)textW, descH), t.TextDim, flags);
        if (error != null) TextRenderer.DrawText(g, error, fCaption, new Rectangle((int)textX, (int)(ty + 20 * s + descH + 4 * s), (int)textW, errH), t.Danger, flags);
        y += h + 4 * s;
        return r;
    }

    float ToggleWidth(Graphics g) { return 40 * s + 12 * s + TextRenderer.MeasureText(g, "Off", fBody, Size.Empty, TextFormatFlags.NoPadding).Width; }

    void Toggle(Graphics g, RectangleF card, bool on, Action<bool> set)
    {
        var sw = new RectangleF(card.Right - 16 * s - 40 * s, card.Y + (card.Height - 20 * s) / 2, 40 * s, 20 * s);
        float labelW = TextRenderer.MeasureText(g, "Off", fBody, Size.Empty, TextFormatFlags.NoPadding).Width;
        var hit = new RectangleF(sw.X - 12 * s - labelW, card.Y + (card.Height - 32 * s) / 2, sw.Right - (sw.X - 12 * s - labelW), 32 * s);
        bool hot = Hot(hit), down = IsDown(hit);
        Draw(g, on ? "On" : "Off", fBody, t.Text, new RectangleF(hit.X, hit.Y, labelW + 4 * s, hit.Height), TextFormatFlags.VerticalCenter);
        float knob = down ? 14 * s : hot ? 14 * s : 12 * s;
        if (on)
        {
            using (var b = new SolidBrush(hot ? t.AccentHover : t.Accent)) Fill(g, b, sw, 10 * s);
            using (var b = new SolidBrush(t.OnAccent)) g.FillEllipse(b, sw.Right - 10 * s - knob / 2, sw.Y + 10 * s - knob / 2, knob, knob);
        }
        else
        {
            if (hot) using (var b = new SolidBrush(t.ControlHover)) Fill(g, b, sw, 10 * s);
            using (var p = new Pen(t.TextDim, 1 * s)) Stroke(g, p, sw, 10 * s);
            using (var b = new SolidBrush(t.TextDim)) g.FillEllipse(b, sw.X + 10 * s - knob / 2, sw.Y + 10 * s - knob / 2, knob, knob);
        }
        hits.Add(new Hit { R = hit, Click = () => set(!on) });
    }

    void PaintControlBox(Graphics g, RectangleF r, bool focused)
    {
        bool hot = Hot(r), down = IsDown(r);
        using (var b = new SolidBrush(down ? t.ControlDown : hot ? t.ControlHover : t.Control)) Fill(g, b, r, 4 * s);
        using (var p = new Pen(t.ControlStroke, 1)) Stroke(g, p, r, 4 * s);
        if (focused)
        {
            var state = g.Save();
            using (var clip = Theme.Round(r, 4 * s)) g.SetClip(clip);
            using (var b = new SolidBrush(t.Accent)) g.FillRectangle(b, r.X, r.Bottom - 2 * s, r.Width, 2 * s);
            g.Restore(state);
        }
    }

    float ComboWidth(Graphics g, string[] items)
    {
        return Math.Max(160 * s, items.Max(i => TextRenderer.MeasureText(g, i, fBody, Size.Empty, TextFormatFlags.NoPadding).Width) + 52 * s);
    }

    void Combo(Graphics g, RectangleF card, string[] items, int index, Action<int> set)
    {
        float w = ComboWidth(g, items);
        var r = new RectangleF(card.Right - 16 * s - w, card.Y + (card.Height - 32 * s) / 2, w, 32 * s);
        PaintControlBox(g, r, false);
        Draw(g, items[index], fBody, t.Text, new RectangleF(r.X + 12 * s, r.Y, r.Width - 40 * s, r.Height), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        Draw(g, "\uE70D", fGlyphSmall, t.TextDim, new RectangleF(r.Right - 28 * s, r.Y, 16 * s, r.Height), TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        hits.Add(new Hit
        {
            R = r,
            Click = () =>
            {
                var menu = new ContextMenu(items.Select((label, i) => new MenuItem(label, (o, e) => { set(i); Invalidate(); }) { RadioCheck = true, Checked = i == index }).ToArray());
                menu.Show(this, new Point((int)r.X, (int)r.Bottom + (int)(2 * s)));
            }
        });
    }

    float Measure(Graphics g, string text, float min)
    {
        return Math.Max(min * s, TextRenderer.MeasureText(g, text, fBody, Size.Empty, TextFormatFlags.NoPadding).Width + 40 * s);
    }

    // Returns the button's right edge.
    float Button(Graphics g, float x, float y, string text, bool accent, bool enabled, Action click)
    {
        var r = new RectangleF(x, y, Measure(g, text, accent ? 96 : 0), 32 * s);
        bool hot = enabled && Hot(r), down = enabled && IsDown(r);
        if (accent && enabled)
        {
            using (var b = new SolidBrush(down ? Theme.Mix(t.Accent, t.Bg, 0.2) : hot ? t.AccentHover : t.Accent)) Fill(g, b, r, 4 * s);
            Draw(g, text, fBody, t.OnAccent, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        else
        {
            using (var b = new SolidBrush(down ? t.ControlDown : hot ? t.ControlHover : t.Control)) Fill(g, b, r, 4 * s);
            using (var p = new Pen(t.ControlStroke, 1)) Stroke(g, p, r, 4 * s);
            Draw(g, text, fBody, enabled ? t.Text : t.TextOff, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        if (enabled) hits.Add(new Hit { R = r, Click = click });
        return r.Right;
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
