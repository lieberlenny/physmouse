using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

sealed class AppPickerWindow : PaintedWindow
{
    readonly HashSet<string> selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Icon> icons = new Dictionary<string, Icon>(StringComparer.OrdinalIgnoreCase);
    List<OpenApplication> apps = new List<OpenApplication>();
    int scroll;

    public string[] SelectedApps { get; private set; }

    public AppPickerWindow(string[] current, Point anchor) : base("Choose full-screen apps", 500, 460, 0, 0, false)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        var work = Screen.FromPoint(anchor).WorkingArea;
        int x = Math.Max(work.Left, Math.Min(anchor.X, work.Right - Width));
        int y = anchor.Y + Height > work.Bottom ? anchor.Y - Height : anchor.Y;
        Location = new Point(x, Math.Max(work.Top, y));
        foreach (var name in current ?? new string[0]) selected.Add(name);
        LoadApps();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) foreach (var icon in icons.Values) icon.Dispose();
        base.Dispose(disposing);
    }

    void LoadApps()
    {
        foreach (var icon in icons.Values) icon.Dispose();
        icons.Clear();
        apps = FullscreenApps.FindOpen();
        foreach (var app in apps)
        {
            try
            {
                using (var process = Process.GetProcessesByName(app.ProcessName).FirstOrDefault())
                    if (process != null)
                    {
                        var icon = Icon.ExtractAssociatedIcon(process.MainModule.FileName);
                        if (icon != null) icons[app.ProcessName] = icon;
                    }
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            catch (System.Security.SecurityException) { }
            catch (ArgumentException) { }
        }
        foreach (var name in selected.Where(n => !apps.Any(a => a.ProcessName.Equals(n, StringComparison.OrdinalIgnoreCase))))
            apps.Add(new OpenApplication { ProcessName = name, IsOpen = false });
        apps = apps.OrderBy(a => a.Title ?? a.ProcessName, StringComparer.CurrentCultureIgnoreCase).ToList();
        scroll = Math.Min(scroll, Math.Max(0, apps.Count - 1));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(t.Bg);
        hits.Clear();
        var panel = new RectangleF(1, 1, ClientSize.Width - 2, ClientSize.Height - 2);
        using (var b = new SolidBrush(t.Card)) Fill(g, b, panel, 8 * s);
        using (var p = new Pen(t.ControlStroke, 1)) Stroke(g, p, panel, 8 * s);
        using (var icon = Theme.AppIcon((int)(24 * s), false)) g.DrawImage(icon, 18 * s, 16 * s);
        Draw(g, "Choose full-screen apps", fStrong, t.Text, new RectangleF(52 * s, 12 * s, ClientSize.Width - 100 * s, 28 * s), TextFormatFlags.VerticalCenter);
        var close = new RectangleF(ClientSize.Width - 40 * s, 10 * s, 28 * s, 28 * s);
        Draw(g, "\uE8BB", fGlyphSmall, t.TextDim, close, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        hits.Add(new Hit { R = close, Click = Close });

        var area = new RectangleF(18 * s, 52 * s, ClientSize.Width - 36 * s, ClientSize.Height - 120 * s);
        Draw(g, "Select open applications to keep the cursor on their screen while full-screen. Tray-only apps are hidden.",
            fCaption, t.TextDim, new RectangleF(area.X, area.Y, area.Width, 36 * s), TextFormatFlags.WordBreak);
        float listY = area.Y + 38 * s, rowH = 40 * s;
        var list = new RectangleF(area.X, listY, area.Width, area.Bottom - listY);
        using (var b = new SolidBrush(t.Card)) Fill(g, b, list, 5 * s);
        using (var p = new Pen(t.ControlStroke, 1)) Stroke(g, p, list, 5 * s);
        int visible = Math.Max(1, (int)(list.Height / rowH));
        if (apps.Count == 0)
            Draw(g, "No open desktop applications. Open an app, then refresh.", fBody, t.TextDim, Inset(list, 16 * s, 0), TextFormatFlags.VerticalCenter);
        Action<int> scrollBy = delta => { ClearKeyboardFocus(); scroll = Math.Max(0, Math.Min(apps.Count - visible, scroll - delta * 3)); Invalidate(); };
        if (apps.Count > visible) hits.Add(new Hit { R = list, Wheel = scrollBy });
        for (int i = scroll; i < Math.Min(apps.Count, scroll + visible); i++)
        {
            var app = apps[i];
            var row = new RectangleF(list.X + 4 * s, list.Y + 4 * s + (i - scroll) * rowH, list.Width - 8 * s, rowH);
            if (Hot(row)) using (var b = new SolidBrush(t.CardHover)) Fill(g, b, row, 3 * s);
            Icon appIcon;
            if (icons.TryGetValue(app.ProcessName, out appIcon)) g.DrawIcon(appIcon, Rectangle.Round(new RectangleF(row.X + 8 * s, row.Y + 8 * s, 28 * s, 28 * s)));
            else
            {
                using (var fallback = Theme.AppIcon((int)(28 * s), false)) g.DrawImage(fallback, row.X + 8 * s, row.Y + 8 * s);
            }
            string label = app.Title == null ? app.ProcessName + ".exe" : app.Title + " (" + app.ProcessName + ".exe)";
            if (TextRenderer.MeasureText(g, label, fBody, Size.Empty, TextFormatFlags.NoPadding).Width > row.Width - 104 * s)
                label = app.ProcessName + ".exe";
            var labelRect = new RectangleF(row.X + 48 * s, row.Y, row.Width - 100 * s, app.IsOpen ? row.Height : 22 * s);
            Draw(g, label, fBody, t.Text, labelRect, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (!app.IsOpen) Draw(g, "Not currently open", fCaption, t.TextDim, new RectangleF(row.X + 48 * s, row.Y + 22 * s, row.Width - 100 * s, 16 * s), TextFormatFlags.EndEllipsis);
            var check = new RectangleF(row.Right - 34 * s, row.Y + 12 * s, 18 * s, 18 * s);
            if (selected.Contains(app.ProcessName))
            {
                using (var b = new SolidBrush(t.Accent)) Fill(g, b, check, 3 * s);
                Draw(g, "\uE73E", fGlyphSmall, t.OnAccent, check, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            else using (var p = new Pen(t.TextDim, 1)) Stroke(g, p, check, 3 * s);
            hits.Add(new Hit { R = row, Wheel = scrollBy, Click = () => { if (!selected.Add(app.ProcessName)) selected.Remove(app.ProcessName); Invalidate(); } });
        }
        float y = ClientSize.Height - 48 * s, x = 18 * s;
        x = Button(g, x, y, "Refresh", false, true, LoadApps) + 8 * s;
        float saveW = Measure(g, "Save", 96 * s), cancelW = Measure(g, "Cancel", 0);
        Button(g, ClientSize.Width - 24 * s - saveW, y, "Save", true, true, () => { SelectedApps = selected.ToArray(); DialogResult = DialogResult.OK; Close(); });
        Button(g, ClientSize.Width - 32 * s - saveW - cancelW, y, "Cancel", false, true, Close);
        PaintFocusBorder(g);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys key)
    {
        if (key == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, key);
    }
}
