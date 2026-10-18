using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

// Base for physmouse's windows. Everything is painted in the style of the Windows 11 Settings app; each
// paint records clickable regions ("hits"), and mouse input is dispatched to those. The standard frame
// stays (shadow, snapping, rounded corners); only the caption is folded into the client area, where the
// window draws its own buttons level with the app icon.
abstract class PaintedWindow : Form
{
    protected class Hit { public RectangleF R; public Action Click; public Action<Point> Down; public Action<int> Wheel; public Cursor Cursor; }

    protected Theme t = Theme.Current();
    protected readonly List<Hit> hits = new List<Hit>();
    protected Point mouse = new Point(-9999, -9999);
    protected Hit pressed;
    protected float s = 1; // DPI scale
    RectangleF openCombo, openComboMenu;
    string[] openComboItems;
    Action<int> openComboSet;
    int openComboIndex;
    bool openComboSeen;
    int keyboardFocusIndex = -1;
    bool keyboardNavigation;
    protected Font fTitle, fBody, fStrong, fCaption, fGlyph, fGlyphSmall;
    protected bool active = true;
    readonly bool resizable;
    readonly Size minSize; // at 96 dpi

    protected PaintedWindow(string title, int width, int height, int minWidth, int minHeight, bool resizable)
    {
        this.resizable = resizable;
        minSize = new Size(minWidth, minHeight);
        Text = title;
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        ResizeRedraw = true;
        KeyPreview = true;
        MaximizeBox = resizable;
        if (!resizable) FormBorderStyle = FormBorderStyle.FixedSingle;
        BackColor = t.Bg;
        s = DeviceDpi / 96f;
        MakeFonts();
        ClientSize = new Size((int)(width * s), (int)(height * s));
        ApplyMinimum();

        SystemEvents.UserPreferenceChanged += OnThemeChanged;
        MouseMove += (o, e) => OnMove(e);
        MouseDown += (o, e) => { if (e.Button == MouseButtons.Left) OnDown(e); };
        MouseUp += (o, e) => { if (e.Button == MouseButtons.Left) OnUp(e); };
        MouseWheel += (o, e) => { var h = HitAt(e.Location); if (h != null && h.Wheel != null) h.Wheel(e.Delta > 0 ? 1 : -1); };
        MouseLeave += (o, e) => { mouse = new Point(-9999, -9999); Invalidate(); };
        Deactivate += (o, e) => { active = false; OnDeactivated(); Invalidate(); };
        Activated += (o, e) => { active = true; Invalidate(); };
        Resize += (o, e) => Invalidate(); // maximize/restore swaps the max button glyph
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= OnThemeChanged;
            foreach (var f in new[] { fTitle, fBody, fStrong, fCaption, fGlyph, fGlyphSmall }) f.Dispose();
            if (bigIcon != null) { bigIcon.Dispose(); smallIcon.Dispose(); }
        }
        base.Dispose(disposing);
    }

    protected virtual void OnDeactivated() { }
    protected virtual void OnScaleChanged() { }
    protected void ClearKeyboardFocus() { keyboardFocusIndex = -1; keyboardNavigation = false; }
    protected bool CloseComboPopup()
    {
        if (openComboItems == null) return false;
        openComboItems = null;
        Invalidate();
        return true;
    }

    void ApplyMinimum()
    {
        if (resizable) MinimumSize = new Size((int)(minSize.Width * s), (int)(minSize.Height * s));
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

    void MakeFonts()
    {
        foreach (var f in new[] { fTitle, fBody, fStrong, fCaption, fGlyph, fGlyphSmall }) if (f != null) f.Dispose();
        fTitle = new Font(Theme.DisplayFont, 30 * s, GraphicsUnit.Pixel);
        fBody = new Font(Theme.TextFont, 15 * s, GraphicsUnit.Pixel);
        fStrong = new Font(Theme.StrongFont, 15 * s, GraphicsUnit.Pixel);
        fCaption = new Font(Theme.TextFont, 13 * s, GraphicsUnit.Pixel);
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
            ApplyMinimum();
        }
        ApplyTitleBar();
        Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x37); // SWP_FRAMECHANGED | NOMOVE | NOSIZE | NOZORDER | NOACTIVATE: recalc without caption
    }

    void ApplyTitleBar()
    {
        int dark = t.Dark ? 1 : 0;
        Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    protected void PaintFocusBorder(Graphics g)
    {
        if (active)
        {
            var r = new RectangleF(s, s, ClientSize.Width - 2 * s, ClientSize.Height - 2 * s);
            using (var p = new Pen(Theme.Mix(t.Bg, t.Text, t.Dark ? 0.20 : 0.08), s)) Stroke(g, p, r, 8 * s);
        }
        if (keyboardNavigation)
        {
            var targets = hits.Where(h => h.Click != null).ToList();
            if (keyboardFocusIndex >= 0 && keyboardFocusIndex < targets.Count)
            {
                var r = targets[keyboardFocusIndex].R;
                r.Inflate(2 * s, 2 * s);
                using (var p = new Pen(t.Accent, 2 * s)) Stroke(g, p, r, 6 * s);
            }
        }
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

    // ---------------------------------------------------------------- custom title bar

    const int HTCLIENT = 1, HTCAPTION = 2, HTMAXBUTTON = 9, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;
    const int WM_NCCALCSIZE = 0x83, WM_NCHITTEST = 0x84, WM_NCMOUSEMOVE = 0xA0, WM_NCLBUTTONDOWN = 0xA1, WM_NCLBUTTONUP = 0xA2,
        WM_NCLBUTTONDBLCLK = 0xA3, WM_NCMOUSELEAVE = 0x2A2;
    RectangleF minRect, maxRect, closeRect;
    bool maxHot, maxDown;
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
                if (m.Result.ToInt32() != HTCLIENT) return; // side and bottom borders
                long lp = m.LParam.ToInt64();
                var pt = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                if (resizable && WindowState != FormWindowState.Maximized && pt.Y < 5 * s)
                    m.Result = (IntPtr)(pt.X < 16 * s ? HTTOPLEFT : pt.X > ClientSize.Width - 16 * s ? HTTOPRIGHT : HTTOP);
                else if (resizable && maxRect.Contains(pt))
                    m.Result = (IntPtr)HTMAXBUTTON; // lets Windows 11 show snap layouts on hover
                else if (pt.Y < CaptionHeight && !minRect.Contains(pt) && !closeRect.Contains(pt) && !InCaptionControl(pt))
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
                if (maxDown) WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
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
            s = (m.WParam.ToInt32() & 0xFFFF) / 96f;
            MakeFonts();
            ApplyMinimum();
            var r = (Native.RECT)Marshal.PtrToStructure(m.LParam, typeof(Native.RECT));
            Bounds = new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            SetIcons();
            OnScaleChanged();
            Invalidate();
        }
    }

    // A clickable region drawn inside the caption band, which must not start a window drag.
    bool InCaptionControl(Point pt)
    {
        return hits.Any(h => h.R.Contains(pt));
    }

    // Window buttons, level with the app icon in the top-left corner.
    protected void PaintCaption(Graphics g)
    {
        float w = 46 * s, h = 32 * s, top = 16 * s, n = resizable ? 3 : 2, x = ClientSize.Width - 8 * s - n * w;
        minRect = new RectangleF(x, top, w, h);
        maxRect = resizable ? new RectangleF(x + w, top, w, h) : RectangleF.Empty;
        closeRect = new RectangleF(x + (n - 1) * w, top, w, h);
        CaptionButton(g, minRect, "\uE921", Hot(minRect), IsDown(minRect), false);
        if (resizable) CaptionButton(g, maxRect, WindowState == FormWindowState.Maximized ? "\uE923" : "\uE922", maxHot, maxDown, false);
        CaptionButton(g, closeRect, "\uE8BB", Hot(closeRect), IsDown(closeRect), true);
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

    // The small icon and name in the top-left corner.
    protected void PaintAppName(Graphics g, string name, bool paused)
    {
        using (var icon = Theme.AppIcon((int)(20 * s), paused)) g.DrawImage(icon, 20 * s, 22 * s);
        Draw(g, name, fCaption, t.Text, new RectangleF(52 * s, 20 * s, 300 * s, 24 * s), TextFormatFlags.VerticalCenter);
    }

    // ---------------------------------------------------------------- input

    protected Hit HitAt(Point p)
    {
        for (int i = hits.Count - 1; i >= 0; i--) if (hits[i].R.Contains(p)) return hits[i];
        return null;
    }

    protected virtual void OnDown(MouseEventArgs e)
    {
        keyboardNavigation = false;
        if (openComboItems != null && !openComboMenu.Contains(e.Location) && !openCombo.Contains(e.Location))
        { openComboItems = null; Invalidate(); }
        pressed = HitAt(e.Location);
        keyboardFocusIndex = hits.Where(h => h.Click != null).ToList().IndexOf(pressed);
        if (pressed != null && pressed.Down != null) pressed.Down(e.Location);
        Invalidate();
    }

    protected virtual void OnMove(MouseEventArgs e)
    {
        mouse = e.Location;
        var h = HitAt(e.Location);
        Cursor = h != null && h.Cursor != null ? h.Cursor : Cursors.Default;
        Invalidate();
    }

    protected virtual void OnUp(MouseEventArgs e)
    {
        var h = pressed;
        pressed = null;
        if (h != null && h.Click != null && h.R.Contains(e.Location)) h.Click();
        Invalidate();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys key)
    {
        if (ActiveControl == null)
        {
            if ((key & Keys.KeyCode) == Keys.Tab)
            {
                var targets = hits.Where(h => h.Click != null).ToList();
                if (targets.Count == 0) return true;
                bool reverse = (key & Keys.Shift) != 0;
                keyboardFocusIndex = keyboardFocusIndex < 0 ? (reverse ? targets.Count - 1 : 0)
                    : (keyboardFocusIndex + (reverse ? targets.Count - 1 : 1)) % targets.Count;
                keyboardNavigation = true;
                Invalidate();
                return true;
            }
            if (openComboItems != null && ((key & Keys.KeyCode) == Keys.Up || (key & Keys.KeyCode) == Keys.Down))
            {
                openComboIndex = Math.Max(0, Math.Min(openComboItems.Length - 1, openComboIndex + ((key & Keys.KeyCode) == Keys.Down ? 1 : -1)));
                Invalidate();
                return true;
            }
            if (keyboardNavigation && ((key & Keys.KeyCode) == Keys.Enter || (key & Keys.KeyCode) == Keys.Space))
            {
                ActivateKeyboardFocus();
                Invalidate();
                return true;
            }
        }
        return base.ProcessCmdKey(ref msg, key);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (ActiveControl == null && keyboardNavigation && e.KeyCode == Keys.Space)
        {
            ActivateKeyboardFocus();
            e.Handled = e.SuppressKeyPress = true;
            Invalidate();
            return;
        }
        base.OnKeyDown(e);
    }

    void ActivateKeyboardFocus()
    {
        if (openComboItems != null)
        {
            openComboSet(openComboIndex);
            openComboItems = null;
        }
        else
        {
            var targets = hits.Where(h => h.Click != null).ToList();
            if (keyboardFocusIndex >= 0 && keyboardFocusIndex < targets.Count) targets[keyboardFocusIndex].Click();
        }
    }

    // ---------------------------------------------------------------- building blocks

    protected bool Hot(RectangleF r) { return r.Contains(mouse) && (pressed == null || pressed.R == r); }
    protected bool IsDown(RectangleF r) { return pressed != null && pressed.R == r && r.Contains(mouse); }

    protected static RectangleF Inset(RectangleF r, float x, float y) { return new RectangleF(r.X + x, r.Y + y, r.Width - 2 * x, r.Height - 2 * y); }

    protected void Draw(Graphics g, string text, Font f, Color c, RectangleF r, TextFormatFlags flags)
    {
        DrawNative(g, text, f, Rectangle.Round(r), c, flags | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | (flags.HasFlag(TextFormatFlags.WordBreak) ? 0 : TextFormatFlags.SingleLine));
    }

    protected float DrawWrapped(Graphics g, string text, Font f, Color c, float x, float y, float w)
    {
        var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        var size = TextRenderer.MeasureText(g, text, f, new Size((int)w, int.MaxValue), flags);
        DrawNative(g, text, f, new Rectangle((int)x, (int)y, (int)w, size.Height), c, flags);
        return size.Height;
    }

    void DrawNative(Graphics g, string text, Font f, Rectangle r, Color c, TextFormatFlags flags)
    {
        TextRenderer.DrawText(g, text, f, r, c,
            flags | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform);
    }

    protected static void Fill(Graphics g, Brush b, RectangleF r, float rad) { using (var p = Theme.Round(r, rad)) g.FillPath(b, p); }
    protected static void Stroke(Graphics g, Pen pen, RectangleF r, float rad)
    {
        var half = pen.Width / 2;
        using (var p = Theme.Round(new RectangleF(r.X + half, r.Y + half, r.Width - pen.Width, r.Height - pen.Width), rad)) g.DrawPath(pen, p);
    }

    protected void FillCard(Graphics g, RectangleF r, bool hover)
    {
        using (var b = new SolidBrush(hover ? t.CardHover : t.Card)) Fill(g, b, r, 8 * s);
        using (var p = new Pen(t.Stroke, 1)) Stroke(g, p, r, 8 * s);
    }

    protected void Group(Graphics g, ref float y, RectangleF a, string title)
    {
        if (y > a.Y) y += 20 * s;
        Draw(g, title, fStrong, t.Text, new RectangleF(a.X + 2 * s, y, a.Width, 24 * s), TextFormatFlags.Left);
        y += 32 * s;
    }

    // A settings row: icon, title, description on the left; the caller puts a control at the right.
    protected RectangleF Card(Graphics g, ref float y, RectangleF a, string glyph, string title, string desc, float controlW, string error = null)
    {
        float textX = a.X + 56 * s, textW = a.Width - 56 * s - 16 * s - (controlW > 0 ? controlW + 32 * s : 0);
        var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        int descH = desc == "" ? 0 : TextRenderer.MeasureText(g, desc, fCaption, new Size((int)textW, int.MaxValue), flags).Height;
        int errH = error == null ? 0 : TextRenderer.MeasureText(g, error, fCaption, new Size((int)textW, int.MaxValue), flags).Height + (int)(4 * s);
        float h = Math.Max(72 * s, 14 * s + 22 * s + descH + errH + 14 * s);
        var r = new RectangleF(a.X, y, a.Width, h);
        FillCard(g, r, Hot(r) && pressed == null);
        float ty = r.Y + (h - (22 * s + descH + errH)) / 2;
        Draw(g, glyph, fGlyph, t.Text, new RectangleF(a.X + 20 * s, r.Y, 20 * s, h), TextFormatFlags.VerticalCenter);
        Draw(g, title, fBody, t.Text, new RectangleF(textX, ty, textW, 22 * s), TextFormatFlags.EndEllipsis | TextFormatFlags.PathEllipsis);
        if (descH > 0) DrawNative(g, desc, fCaption, new Rectangle((int)textX, (int)(ty + 22 * s), (int)textW, descH), t.TextDim, flags);
        if (error != null) DrawNative(g, error, fCaption, new Rectangle((int)textX, (int)(ty + 22 * s + descH + 4 * s), (int)textW, errH), t.Danger, flags);
        y += h + 4 * s;
        return r;
    }

    protected float ToggleWidth(Graphics g) { return 40 * s + 12 * s + TextRenderer.MeasureText(g, "Off", fBody, Size.Empty, TextFormatFlags.NoPadding).Width; }

    protected void Toggle(Graphics g, RectangleF card, bool on, Action<bool> set)
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

    protected void PaintControlBox(Graphics g, RectangleF r, bool focused)
    {
        bool hot = Hot(r), down = IsDown(r);
        using (var b = new SolidBrush(down ? t.ControlDown : hot ? t.ControlHover : t.Control)) Fill(g, b, r, 6 * s);
        using (var p = new Pen(t.ControlStroke, 1)) Stroke(g, p, r, 6 * s);
        if (focused)
        {
            var state = g.Save();
            using (var clip = Theme.Round(r, 4 * s)) g.SetClip(clip);
            using (var b = new SolidBrush(t.Accent)) g.FillRectangle(b, r.X, r.Bottom - 2 * s, r.Width, 2 * s);
            g.Restore(state);
        }
    }

    protected float ComboWidth(Graphics g, string[] items)
    {
        return Math.Max(160 * s, items.Max(i => TextRenderer.MeasureText(g, i, fBody, Size.Empty, TextFormatFlags.NoPadding).Width) + 52 * s);
    }

    protected void Combo(Graphics g, RectangleF card, string[] items, int index, Action<int> set)
    {
        float w = ComboWidth(g, items);
        var r = new RectangleF(card.Right - 16 * s - w, card.Y + (card.Height - 32 * s) / 2, w, 32 * s);
        PaintControlBox(g, r, false);
        Draw(g, items[index], fBody, t.Text, new RectangleF(r.X + 12 * s, r.Y, r.Width - 40 * s, r.Height), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        Draw(g, "\uE70D", fGlyphSmall, t.TextDim, new RectangleF(r.Right - 28 * s, r.Y, 16 * s, r.Height), TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        if (openComboItems != null && openCombo == r) openComboSeen = true;
        hits.Add(new Hit
        {
            R = r,
            Click = () =>
            {
                if (openComboItems != null && openCombo == r) openComboItems = null;
                else { openCombo = r; openComboItems = items; openComboSet = set; openComboIndex = index; }
                Invalidate();
            }
        });
    }

    protected void PaintComboPopup(Graphics g, RectangleF viewport, float scroll)
    {
        if (openComboItems == null) return;
        if (!openComboSeen) { openComboItems = null; return; }
        openComboSeen = false;
        float row = 30 * s, pad = 4 * s, h = openComboItems.Length * row + pad * 2;
        float y = openCombo.Bottom + 2 * s;
        float viewTop = viewport.Y + scroll, viewBottom = viewport.Bottom + scroll;
        if (y + h > viewBottom) y = openCombo.Top - h - 2 * s;
        if (y < viewTop) y = viewTop;
        openComboMenu = new RectangleF(openCombo.X, y, openCombo.Width, h);
        using (var b = new SolidBrush(t.Card)) Fill(g, b, openComboMenu, 8 * s);
        using (var p = new Pen(t.ControlStroke, 1)) Stroke(g, p, openComboMenu, 8 * s);
        for (int i = 0; i < openComboItems.Length; i++)
        {
            var r = new RectangleF(openComboMenu.X + pad, y + pad + i * row, openComboMenu.Width - pad * 2, row);
            bool hot = r.Contains(mouse);
            if (hot || i == openComboIndex)
                using (var b = new SolidBrush(hot ? t.ControlHover : t.NavSelected)) Fill(g, b, r, 5 * s);
            if (i == openComboIndex) Draw(g, "•", fBody, t.Accent, new RectangleF(r.X + 8 * s, r.Y, 14 * s, r.Height), TextFormatFlags.VerticalCenter);
            Draw(g, openComboItems[i], fBody, t.Text, new RectangleF(r.X + 28 * s, r.Y, r.Width - 36 * s, r.Height), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            int selected = i;
            hits.Add(new Hit { R = r, Click = () => { openComboSet(selected); openComboItems = null; Invalidate(); } });
        }
    }

    protected float Measure(Graphics g, string text, float min)
    {
        return Math.Max(min * s, TextRenderer.MeasureText(g, text, fBody, Size.Empty, TextFormatFlags.NoPadding).Width + 40 * s);
    }

    // Returns the button's right edge.
    protected float Button(Graphics g, float x, float y, string text, bool accent, bool enabled, Action click)
    {
        var r = new RectangleF(x, y, Measure(g, text, accent ? 96 : 0), 32 * s);
        bool hot = enabled && Hot(r), down = enabled && IsDown(r);
        if (accent && enabled)
        {
            using (var b = new SolidBrush(down ? Theme.Mix(t.Accent, t.Bg, 0.2) : hot ? t.AccentHover : t.Accent)) Fill(g, b, r, 6 * s);
            Draw(g, text, fBody, t.OnAccent, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        else
        {
            using (var b = new SolidBrush(down ? t.ControlDown : hot ? t.ControlHover : t.Control)) Fill(g, b, r, 6 * s);
            using (var p = new Pen(t.ControlStroke, 1)) Stroke(g, p, r, 6 * s);
            Draw(g, text, fBody, enabled ? t.Text : t.TextOff, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        if (enabled) hits.Add(new Hit { R = r, Click = click });
        return r.Right;
    }
}
