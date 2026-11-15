using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

// Fullscreen overlay per monitor for configuring bridge zones (cursor crossing restrictions).
// One form covers each monitor. The user adds bridges on each edge, resizes them, then saves or cancels.
class BridgeWindow : Form
{
    readonly Mon mon;
    readonly List<Mon> allMons;
    readonly Theme theme;
    readonly float dpiScale;
    readonly List<BridgeBar> bars = new List<BridgeBar>();
    static List<BridgeWindow> openWindows = new List<BridgeWindow>();
    static bool saved;

    // A single bridge zone drawn on an edge.
    class BridgeBar
    {
        public string Edge;          // Top, Right, Bottom, Left
        public int StartOffset;      // px from the start of the edge
        public int EndOffset;        // px from the end of the edge
        public string TargetScreenId;
        public string PairId;        // shared GUID linking this bar to its reciprocal on the target monitor
    }

    // Dragging state
    BridgeBar dragBar;
    int dragHandle; // 0 = move whole bar, -1 = start handle, 1 = end handle
    int dragStartMouse;
    int dragStartVal, dragEndVal;

    // Hover state
    BridgeBar hoverBar;
    int hoverHandle;
    string hoverEdge;

    BridgeWindow(Mon m, List<Mon> mons, List<Bridge> existing, Theme t)
    {
        mon = m;
        allMons = mons;
        theme = t;
        saved = false;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Bounds = mon.R;
        BackColor = Color.FromArgb(30, 30, 30);
        DoubleBuffered = true;
        Opacity = 0.92;
        dpiScale = Math.Max(1, mon.R.Width / 1920f);

        foreach (var b in existing.Where(b => b.ScreenId == mon.Id))
            bars.Add(new BridgeBar { Edge = b.Edge, StartOffset = b.TopOffset, EndOffset = b.BottomOffset, TargetScreenId = b.TargetScreenId });

        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
        KeyDown += (o, e) => { if (e.KeyCode == Keys.Escape) CancelAll(); };
    }

    protected override bool ShowWithoutActivation { get { return false; } }

    // Show one window per monitor. Returns the new bridge list if saved, null if cancelled.
    public static List<Bridge> ShowSetup(List<Mon> mons, List<Bridge> existing, Theme t)
    {
        saved = false;
        openWindows.Clear();
        foreach (var m in mons)
        {
            var w = new BridgeWindow(m, mons, existing, t);
            openWindows.Add(w);
        }
        PairExistingBars();

        // Show all windows; the first one that gets focus is the primary
        foreach (var w in openWindows) w.Show();
        if (openWindows.Count > 0) openWindows[0].Activate();
        // Run a modal loop: process events until all windows are closed
        while (openWindows.Any(w => w.Visible))
            Application.DoEvents();

        if (!saved) return null;
        var result = new List<Bridge>();
        foreach (var w in openWindows)
            foreach (var bar in w.bars)
                result.Add(new Bridge { ScreenId = w.mon.Id, Edge = bar.Edge, TopOffset = bar.StartOffset, BottomOffset = bar.EndOffset, TargetScreenId = bar.TargetScreenId ?? "" });
        return result;
    }

    void SaveAll()
    {
        saved = true;
        foreach (var w in openWindows) w.Close();
    }

    void CancelAll()
    {
        saved = false;
        foreach (var w in openWindows) w.Close();
    }

    int EdgeLength(string edge)
    {
        return (edge == "Top" || edge == "Bottom") ? mon.R.Width : mon.R.Height;
    }

    // Convert bar to pixel rect on this window's coordinate space (relative to mon.R)
    RectangleF BarRect(BridgeBar bar)
    {
        int len = EdgeLength(bar.Edge);
        int thickness = (int)(24 * dpiScale);
        int start = bar.StartOffset;
        int end = len - bar.EndOffset;
        if (end <= start) end = start + 1;
        switch (bar.Edge)
        {
            case "Top": return new RectangleF(start, 0, end - start, thickness);
            case "Bottom": return new RectangleF(start, mon.R.Height - thickness, end - start, thickness);
            case "Left": return new RectangleF(0, start, thickness, end - start);
            case "Right": return new RectangleF(mon.R.Width - thickness, start, thickness, end - start);
            default: return RectangleF.Empty;
        }
    }

    // Handle rects at each end of the bar
    RectangleF StartHandle(BridgeBar bar)
    {
        var r = BarRect(bar);
        float hs = 12 * dpiScale;
        bool horiz = bar.Edge == "Top" || bar.Edge == "Bottom";
        if (horiz) return new RectangleF(r.X - hs / 2, r.Y, hs, r.Height);
        return new RectangleF(r.X, r.Y - hs / 2, r.Width, hs);
    }

    RectangleF EndHandle(BridgeBar bar)
    {
        var r = BarRect(bar);
        float hs = 12 * dpiScale;
        bool horiz = bar.Edge == "Top" || bar.Edge == "Bottom";
        if (horiz) return new RectangleF(r.Right - hs / 2, r.Y, hs, r.Height);
        return new RectangleF(r.X, r.Bottom - hs / 2, r.Width, hs);
    }

    // Plus button rects on each edge
    RectangleF PlusRect(string edge)
    {
        float bs = 32 * dpiScale;
        switch (edge)
        {
            case "Top": return new RectangleF((mon.R.Width - bs) / 2, 4 * dpiScale, bs, bs);
            case "Bottom": return new RectangleF((mon.R.Width - bs) / 2, mon.R.Height - bs - 4 * dpiScale, bs, bs);
            case "Left": return new RectangleF(4 * dpiScale, (mon.R.Height - bs) / 2, bs, bs);
            case "Right": return new RectangleF(mon.R.Width - bs - 4 * dpiScale, (mon.R.Height - bs) / 2, bs, bs);
            default: return RectangleF.Empty;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float sc = dpiScale;

        // Semi-transparent background
        using (var b = new SolidBrush(Color.FromArgb(220, 20, 20, 20)))
            g.FillRectangle(b, ClientRectangle);

        // Draw monitor outline
        float inset = 40 * sc;
        var outline = new RectangleF(inset, inset, mon.R.Width - 2 * inset, mon.R.Height - 2 * inset);
        using (var p = new Pen(Color.FromArgb(80, 255, 255, 255), 2 * sc))
        {
            p.DashStyle = DashStyle.Dash;
            g.DrawRectangle(p, outline.X, outline.Y, outline.Width, outline.Height);
        }

        // Monitor number in center
        string label = "Screen " + mon.Number;
        using (var f = new Font(Theme.DisplayFont, 48 * sc, GraphicsUnit.Pixel))
            TextRenderer.DrawText(g, label, f, new Rectangle(0, (int)(mon.R.Height / 2 - 80 * sc), mon.R.Width, (int)(60 * sc)),
                Color.FromArgb(120, 255, 255, 255), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        // Draw + buttons on each edge
        foreach (var edge in new[] { "Top", "Right", "Bottom", "Left" })
        {
            var pr = PlusRect(edge);
            bool hot = hoverEdge == edge && dragBar == null;
            using (var b = new SolidBrush(hot ? Color.FromArgb(180, theme.Accent) : Color.FromArgb(120, theme.Accent)))
                FillRound(g, b, pr, 6 * sc);
            using (var p = new Pen(Color.White, 2 * sc))
            {
                float cx = pr.X + pr.Width / 2, cy = pr.Y + pr.Height / 2, arm = 8 * sc;
                g.DrawLine(p, cx - arm, cy, cx + arm, cy);
                g.DrawLine(p, cx, cy - arm, cx, cy + arm);
            }
        }

        // Draw bridge bars
        foreach (var bar in bars)
        {
            var r = BarRect(bar);
            bool isHover = bar == hoverBar;
            float triLen = Math.Min(r.Width, r.Height);

            // Build the full bridge polygon: taper + rectangle + taper.
            // The bridge shape tapers from a point on the edge to full bar width, then back to a point.
            PointF[] shape;
            if (bar.Edge == "Top")
            {
                // Edge side = r.Y. Inner side = r.Bottom. Tapers along X axis.
                shape = new[] {
                    new PointF(r.X, r.Y),                     // start point on edge
                    new PointF(r.X + triLen, r.Y),             // start taper meets edge
                    new PointF(r.X + triLen, r.Bottom),        // start taper inner corner
                    new PointF(r.Right - triLen, r.Bottom),    // end taper inner corner
                    new PointF(r.Right - triLen, r.Y),         // end taper meets edge
                    new PointF(r.Right, r.Y),                  // end point on edge
                };
            }
            else if (bar.Edge == "Bottom")
            {
                shape = new[] {
                    new PointF(r.X, r.Bottom),
                    new PointF(r.X + triLen, r.Bottom),
                    new PointF(r.X + triLen, r.Y),
                    new PointF(r.Right - triLen, r.Y),
                    new PointF(r.Right - triLen, r.Bottom),
                    new PointF(r.Right, r.Bottom),
                };
            }
            else if (bar.Edge == "Left")
            {
                // Edge side = r.X. Inner side = r.Right. Tapers along Y axis.
                shape = new[] {
                    new PointF(r.X, r.Y),                     // start point on edge
                    new PointF(r.X, r.Y + triLen),             // start taper meets edge
                    new PointF(r.Right, r.Y + triLen),         // start taper inner corner
                    new PointF(r.Right, r.Bottom - triLen),    // end taper inner corner
                    new PointF(r.X, r.Bottom - triLen),        // end taper meets edge
                    new PointF(r.X, r.Bottom),                 // end point on edge
                };
            }
            else // Right
            {
                shape = new[] {
                    new PointF(r.Right, r.Y),
                    new PointF(r.Right, r.Y + triLen),
                    new PointF(r.X, r.Y + triLen),
                    new PointF(r.X, r.Bottom - triLen),
                    new PointF(r.Right, r.Bottom - triLen),
                    new PointF(r.Right, r.Bottom),
                };
            }

            // Fill the full bridge shape (blue)
            using (var b = new SolidBrush(isHover ? Color.FromArgb(200, theme.Accent) : Color.FromArgb(160, theme.Accent)))
                g.FillPolygon(b, shape);

            // Fill the taper triangles red
            var triColor = isHover ? Color.FromArgb(180, 220, 40, 40) : Color.FromArgb(210, 50, 50);
            using (var tb = new SolidBrush(triColor))
            {
                g.FillPolygon(tb, new[] { shape[0], shape[1], shape[2] }); // start taper
                g.FillPolygon(tb, new[] { shape[5], shape[4], shape[3] }); // end taper
            }

            // Handles
            var sh = StartHandle(bar);
            var eh = EndHandle(bar);
            bool startHot = isHover && hoverHandle == -1;
            bool endHot = isHover && hoverHandle == 1;
            using (var b = new SolidBrush(startHot ? Color.White : Color.FromArgb(200, 255, 255, 255)))
                g.FillEllipse(b, sh.X, sh.Y + sh.Height / 2 - 5 * sc, 10 * sc, 10 * sc);
            using (var b = new SolidBrush(endHot ? Color.White : Color.FromArgb(200, 255, 255, 255)))
                g.FillEllipse(b, eh.X + (eh.Width > eh.Height ? eh.Width - 10 * sc : 0), eh.Y + (eh.Height > eh.Width ? eh.Height - 10 * sc : 0), 10 * sc, 10 * sc);

            // Label: target monitor number + pixel span
            int len = EdgeLength(bar.Edge);
            int span = len - bar.StartOffset - bar.EndOffset;
            var targetMon = allMons.FirstOrDefault(m => m.Id != null && m.Id.Equals(bar.TargetScreenId, StringComparison.OrdinalIgnoreCase));
            string info = (targetMon != null ? "\u2192 " + targetMon.Number + "  " : "") + span + "px";
            using (var f = new Font(Theme.TextFont, 12 * sc, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, info, f, Rectangle.Round(r), Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            // Connection indicator: dotted line + numbered badge pointing inward from the edge
            if (targetMon != null)
            {
                float lineLen = 40 * sc;
                float badgeDia = 20 * sc;
                float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                float lx, ly; // endpoint of dotted line (away from edge)
                if (bar.Edge == "Top") { lx = cx; ly = cy + lineLen; }
                else if (bar.Edge == "Bottom") { lx = cx; ly = cy - lineLen; }
                else if (bar.Edge == "Left") { lx = cx + lineLen; ly = cy; }
                else { lx = cx - lineLen; ly = cy; } // Right
                using (var dp = new Pen(Color.FromArgb(140, theme.Accent), 2 * sc) { DashStyle = DashStyle.Dot })
                    g.DrawLine(dp, cx, cy, lx, ly);
                using (var bb = new SolidBrush(theme.Accent))
                    g.FillEllipse(bb, lx - badgeDia / 2, ly - badgeDia / 2, badgeDia, badgeDia);
                string num = targetMon.Number.ToString(CultureInfo.InvariantCulture);
                using (var bf = new Font(Theme.TextFont, 11 * sc, GraphicsUnit.Pixel))
                    TextRenderer.DrawText(g, num, bf, new Rectangle((int)(lx - badgeDia / 2), (int)(ly - badgeDia / 2), (int)badgeDia, (int)badgeDia),
                        Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        // Instruction text
        string instr = "Click + on an edge to add a bridge zone. Drag handles to resize. Right-click a bridge to remove it.";
        using (var f = new Font(Theme.TextFont, 14 * sc, GraphicsUnit.Pixel))
            TextRenderer.DrawText(g, instr, f, new Rectangle(0, (int)(mon.R.Height / 2 - 20 * sc), mon.R.Width, (int)(30 * sc)),
                Color.FromArgb(160, 255, 255, 255), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        // Save and Close buttons
        float bw = 100 * sc, bh = 36 * sc, gap = 12 * sc;
        float bx = (mon.R.Width - 2 * bw - gap) / 2;
        float by = mon.R.Height / 2 + 30 * sc;
        var saveRect = new RectangleF(bx, by, bw, bh);
        var closeRect = new RectangleF(bx + bw + gap, by, bw, bh);
        using (var b = new SolidBrush(theme.Accent))
            FillRound(g, b, saveRect, 6 * sc);
        using (var f = new Font(Theme.TextFont, 15 * sc, GraphicsUnit.Pixel))
            TextRenderer.DrawText(g, "Save", f, Rectangle.Round(saveRect), theme.OnAccent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        using (var b = new SolidBrush(Color.FromArgb(60, 255, 255, 255)))
            FillRound(g, b, closeRect, 6 * sc);
        using (var pen = new Pen(Color.FromArgb(100, 255, 255, 255), 1))
            StrokeRound(g, pen, closeRect, 6 * sc);
        using (var f = new Font(Theme.TextFont, 15 * sc, GraphicsUnit.Pixel))
            TextRenderer.DrawText(g, "Cancel", f, Rectangle.Round(closeRect), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    static void FillRound(Graphics g, Brush b, RectangleF r, float rad)
    {
        using (var p = Theme.Round(r, rad)) g.FillPath(b, p);
    }

    static void StrokeRound(Graphics g, Pen pen, RectangleF r, float rad)
    {
        float half = pen.Width / 2;
        using (var p = Theme.Round(new RectangleF(r.X + half, r.Y + half, r.Width - pen.Width, r.Height - pen.Width), rad))
            g.DrawPath(pen, p);
    }

    void OnMouseDown(object sender, MouseEventArgs e)
    {
        float sc = dpiScale;
        float bw = 100 * sc, bh = 36 * sc, gap = 12 * sc;
        float bx = (mon.R.Width - 2 * bw - gap) / 2;
        float by = mon.R.Height / 2 + 30 * sc;
        var saveRect = new RectangleF(bx, by, bw, bh);
        var closeRect = new RectangleF(bx + bw + gap, by, bw, bh);

        if (e.Button == MouseButtons.Left && saveRect.Contains(e.Location)) { SaveAll(); return; }
        if (e.Button == MouseButtons.Left && closeRect.Contains(e.Location)) { CancelAll(); return; }

        // Right-click to remove a bridge (and its pair on the other monitor)
        if (e.Button == MouseButtons.Right)
        {
            for (int i = bars.Count - 1; i >= 0; i--)
            {
                var r = BarRect(bars[i]);
                r.Inflate(6 * sc, 6 * sc);
                if (r.Contains(e.Location))
                {
                    var removed = bars[i];
                    bars.RemoveAt(i);
                    // Remove the paired bar
                    bool foundPair = false;
                    if (removed.PairId != null)
                    {
                        foreach (var w in openWindows)
                        {
                            if (foundPair) break;
                            for (int j = w.bars.Count - 1; j >= 0; j--)
                                if (w.bars[j].PairId == removed.PairId && w.bars[j] != removed) { w.bars.RemoveAt(j); foundPair = true; break; }
                        }
                    }
                    if (!foundPair && removed.TargetScreenId != null)
                    {
                        // Legacy bridge without PairId: match by opposite edge + target
                        string oppEdge = OppositeEdge(removed.Edge);
                        foreach (var w in openWindows)
                        {
                            if (foundPair) break;
                            if (w.mon.Id == null || !w.mon.Id.Equals(removed.TargetScreenId, StringComparison.OrdinalIgnoreCase)) continue;
                            for (int j = w.bars.Count - 1; j >= 0; j--)
                                if (w.bars[j].Edge == oppEdge && w.bars[j].TargetScreenId != null && w.bars[j].TargetScreenId.Equals(mon.Id, StringComparison.OrdinalIgnoreCase))
                                { w.bars.RemoveAt(j); foundPair = true; break; }
                        }
                    }
                    InvalidateAll();
                    return;
                }
            }
            return;
        }

        if (e.Button != MouseButtons.Left) return;

        // Check + buttons -- show a context menu to pick which monitor to bridge to
        foreach (var edge in new[] { "Top", "Right", "Bottom", "Left" })
        {
            if (PlusRect(edge).Contains(e.Location))
            {
                var neighbors = FindEdgeNeighbors(edge);
                if (neighbors.Count == 0) break;
                if (neighbors.Count == 1)
                {
                    AddBridge(edge, neighbors[0]);
                }
                else
                {
                    var menu = new ContextMenuStrip();
                    foreach (var n in neighbors)
                    {
                        var target = n;
                        menu.Items.Add("Screen " + target.Number + (target.Model != "" ? " (" + target.Model + ")" : ""),
                            null, (o2, e2) => AddBridge(edge, target));
                    }
                    menu.Show(this, e.Location);
                }
                return;
            }
        }

        // Check drag on bar handles
        foreach (var bar in bars)
        {
            var sh = StartHandle(bar);
            sh.Inflate(4 * sc, 4 * sc);
            var eh = EndHandle(bar);
            eh.Inflate(4 * sc, 4 * sc);
            bool horiz = bar.Edge == "Top" || bar.Edge == "Bottom";
            if (sh.Contains(e.Location))
            {
                dragBar = bar; dragHandle = -1;
                dragStartMouse = horiz ? e.X : e.Y;
                dragStartVal = bar.StartOffset; dragEndVal = bar.EndOffset;
                return;
            }
            if (eh.Contains(e.Location))
            {
                dragBar = bar; dragHandle = 1;
                dragStartMouse = horiz ? e.X : e.Y;
                dragStartVal = bar.StartOffset; dragEndVal = bar.EndOffset;
                return;
            }
            var br = BarRect(bar);
            br.Inflate(2 * sc, 2 * sc);
            if (br.Contains(e.Location))
            {
                dragBar = bar; dragHandle = 0;
                dragStartMouse = horiz ? e.X : e.Y;
                dragStartVal = bar.StartOffset; dragEndVal = bar.EndOffset;
                return;
            }
        }
    }

    void OnMouseMove(object sender, MouseEventArgs e)
    {
        float sc = dpiScale;
        if (dragBar != null)
        {
            bool horiz = dragBar.Edge == "Top" || dragBar.Edge == "Bottom";
            int pos = horiz ? e.X : e.Y;
            int delta = pos - dragStartMouse;
            int len = EdgeLength(dragBar.Edge);
            int minSize = (int)(20 * sc);

            if (dragHandle == -1) // start handle
            {
                int newStart = Math.Max(0, Math.Min(len - dragEndVal - minSize, dragStartVal + delta));
                dragBar.StartOffset = newStart;
            }
            else if (dragHandle == 1) // end handle
            {
                int newEnd = Math.Max(0, Math.Min(len - dragStartVal - minSize, dragEndVal - delta));
                dragBar.EndOffset = newEnd;
            }
            else // move whole bar
            {
                int span = len - dragStartVal - dragEndVal;
                int newStart = dragStartVal + delta;
                if (newStart < 0) newStart = 0;
                if (newStart + span > len) newStart = len - span;
                dragBar.StartOffset = newStart;
                dragBar.EndOffset = len - newStart - span;
            }
            InvalidateAll();
            return;
        }

        // Hover detection
        hoverBar = null;
        hoverHandle = 0;
        hoverEdge = null;

        foreach (var bar in bars)
        {
            var sh = StartHandle(bar);
            sh.Inflate(4 * sc, 4 * sc);
            var eh = EndHandle(bar);
            eh.Inflate(4 * sc, 4 * sc);
            if (sh.Contains(e.Location)) { hoverBar = bar; hoverHandle = -1; break; }
            if (eh.Contains(e.Location)) { hoverBar = bar; hoverHandle = 1; break; }
            var br = BarRect(bar);
            br.Inflate(2 * sc, 2 * sc);
            if (br.Contains(e.Location)) { hoverBar = bar; hoverHandle = 0; break; }
        }

        if (hoverBar != null)
        {
            bool horiz = hoverBar.Edge == "Top" || hoverBar.Edge == "Bottom";
            Cursor = hoverHandle != 0 ? (horiz ? Cursors.SizeWE : Cursors.SizeNS)
                : (horiz ? Cursors.SizeWE : Cursors.SizeNS);
        }
        else
        {
            Cursor = Cursors.Default;
            foreach (var edge in new[] { "Top", "Right", "Bottom", "Left" })
                if (PlusRect(edge).Contains(e.Location)) { hoverEdge = edge; break; }
        }
        Invalidate();
    }

    void OnMouseUp(object sender, MouseEventArgs e)
    {
        dragBar = null;
        Invalidate();
    }

    static string OppositeEdge(string edge)
    {
        switch (edge)
        {
            case "Left": return "Right";
            case "Right": return "Left";
            case "Top": return "Bottom";
            case "Bottom": return "Top";
            default: return edge;
        }
    }

    // After all windows are created, scan bars across windows and pair up reciprocal bridges.
    // A bar on window A targeting screen B pairs with a bar on window B targeting screen A on the opposite edge.
    static void PairExistingBars()
    {
        foreach (var w in openWindows)
        {
            foreach (var bar in w.bars)
            {
                if (bar.PairId != null) continue;
                if (string.IsNullOrEmpty(bar.TargetScreenId)) continue;
                string oppEdge = OppositeEdge(bar.Edge);
                foreach (var other in openWindows)
                {
                    if (!other.mon.Id.Equals(bar.TargetScreenId, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var ob in other.bars)
                    {
                        if (ob.PairId != null) continue;
                        if (ob.Edge != oppEdge) continue;
                        if (ob.TargetScreenId == null || !ob.TargetScreenId.Equals(w.mon.Id, StringComparison.OrdinalIgnoreCase)) continue;
                        string id = Guid.NewGuid().ToString("N");
                        bar.PairId = id;
                        ob.PairId = id;
                        break;
                    }
                    break;
                }
            }
        }
    }

    // Convert a bridge's pixel range on the source edge to physical mm, then map onto the target's opposite edge.
    static int[] CalculateReciprocalOffsets(Mon source, string srcEdge, int startOff, int endOff, Mon target)
    {
        bool srcHoriz = srcEdge == "Left" || srcEdge == "Right";
        int srcLen = srcHoriz ? source.R.Height : source.R.Width;
        double srcPhyLen = srcHoriz ? source.H : source.W;
        double srcPhyOrigin = srcHoriz ? source.Y : source.X;
        // Physical start and end of the bridge zone on the source
        double physStart = srcPhyOrigin + (startOff + 0.5) * srcPhyLen / srcLen;
        double physEnd = srcPhyOrigin + (srcLen - endOff - 0.5) * srcPhyLen / srcLen;

        string tgtEdge = OppositeEdge(srcEdge);
        bool tgtHoriz = tgtEdge == "Left" || tgtEdge == "Right";
        int tgtLen = tgtHoriz ? target.R.Height : target.R.Width;
        double tgtPhyLen = tgtHoriz ? target.H : target.W;
        double tgtPhyOrigin = tgtHoriz ? target.Y : target.X;
        // Map physical range to target pixels
        int tgtStart = Math.Max(0, (int)((physStart - tgtPhyOrigin) / tgtPhyLen * tgtLen));
        int tgtEnd = Math.Max(0, tgtLen - 1 - (int)((physEnd - tgtPhyOrigin) / tgtPhyLen * tgtLen));
        // Clamp so the bridge has at least 1px
        if (tgtStart + tgtEnd >= tgtLen) { tgtStart = 0; tgtEnd = 0; }
        return new int[] { tgtStart, tgtEnd };
    }

    void AddBridge(string edge, Mon target)
    {
        int len = EdgeLength(edge);
        int margin = len / 4;
        string pairId = Guid.NewGuid().ToString("N");
        var bar = new BridgeBar { Edge = edge, StartOffset = margin, EndOffset = margin, TargetScreenId = target.Id, PairId = pairId };
        bars.Add(bar);

        // Create the reciprocal bridge on the target monitor's window
        var targetWindow = openWindows.Find(w => w.mon.Id != null && w.mon.Id.Equals(target.Id, StringComparison.OrdinalIgnoreCase));
        if (targetWindow != null)
        {
            var offsets = CalculateReciprocalOffsets(mon, edge, margin, margin, target);
            targetWindow.bars.Add(new BridgeBar { Edge = OppositeEdge(edge), StartOffset = offsets[0], EndOffset = offsets[1], TargetScreenId = mon.Id, PairId = pairId });
        }
        InvalidateAll();
    }

    // Find all monitors reachable from the given edge of this monitor.
    List<Mon> FindEdgeNeighbors(string edge)
    {
        int dir = edge == "Right" ? 0 : edge == "Left" ? 1 : edge == "Bottom" ? 2 : 3;
        bool horiz = dir < 2;
        int edgeLen = horiz ? mon.R.Height : mon.R.Width;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Mon>();
        int samples = Math.Max(10, edgeLen / 20);
        for (int i = 0; i <= samples; i++)
        {
            double frac = (double)i / samples;
            int px = horiz ? 0 : (int)(frac * mon.R.Width);
            int py = horiz ? (int)(frac * mon.R.Height) : 0;
            double physX = mon.X + (px + 0.5) * mon.W / mon.R.Width;
            double physY = mon.Y + (py + 0.5) * mon.H / mon.R.Height;
            var neighbor = Mapper.FindNeighbor(allMons, mon, dir, physX, physY);
            if (neighbor != null && seen.Add(neighbor.Id))
                result.Add(neighbor);
        }
        return result;
    }

    void InvalidateAll()
    {
        foreach (var w in openWindows) w.Invalidate();
    }

    // Generate non-overlapping bridges for every (screen, edge, neighbor) pair.
    // Samples along each edge to find the exact pixel range that faces each neighbor.
    public static List<Bridge> GenerateDefaultBridges(List<Mon> mons)
    {
        var result = new List<Bridge>();
        string[] edgeNames = { "Right", "Left", "Bottom", "Top" };
        foreach (var m in mons)
        {
            for (int dir = 0; dir < 4; dir++)
            {
                bool horiz = dir < 2;
                int edgeLen = horiz ? m.R.Height : m.R.Width;
                int step = Math.Max(1, edgeLen / 500);
                // Track per-neighbor pixel ranges: minPx, maxPx along the edge
                var ranges = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
                var neighborById = new Dictionary<string, Mon>(StringComparer.OrdinalIgnoreCase);
                for (int px = 0; px < edgeLen; px += step)
                {
                    int pixX = horiz ? 0 : px;
                    int pixY = horiz ? px : 0;
                    double physX = m.X + (pixX + 0.5) * m.W / m.R.Width;
                    double physY = m.Y + (pixY + 0.5) * m.H / m.R.Height;
                    var neighbor = Mapper.FindNeighbor(mons, m, dir, physX, physY);
                    if (neighbor == null) continue;
                    int[] range;
                    if (!ranges.TryGetValue(neighbor.Id, out range))
                    {
                        range = new int[] { px, px };
                        ranges[neighbor.Id] = range;
                        neighborById[neighbor.Id] = neighbor;
                    }
                    else
                    {
                        if (px < range[0]) range[0] = px;
                        if (px > range[1]) range[1] = px;
                    }
                }
                foreach (var kv in ranges)
                    result.Add(new Bridge { ScreenId = m.Id, Edge = edgeNames[dir], TopOffset = kv.Value[0], BottomOffset = edgeLen - kv.Value[1] - 1, TargetScreenId = kv.Key });
            }
        }
        return result;
    }
}
