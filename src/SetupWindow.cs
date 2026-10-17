using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

// The install, update and uninstall wizard: one page of choices, then one page saying it's done.
class SetupWindow : PaintedWindow
{
    public enum Mode { Install, Update, Uninstall }
    public enum Outcome { Cancelled, Done, Portable }
    public Outcome Result = Outcome.Cancelled;

    readonly Mode mode;
    bool finished;
    string error;
    string dir = Installer.InstalledExe != null ? Path.GetDirectoryName(Installer.InstalledExe) : Installer.DefaultDir;
    bool autostart = true, desktop, keepSettings = true;

    public SetupWindow(Mode mode)
        : base(mode == Mode.Uninstall ? "Uninstall physmouse" : "physmouse setup", 600, 540, 0, 0, false)
    {
        this.mode = mode;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys key)
    {
        if (key == Keys.Escape) { Close(); return true; }
        if (key == Keys.Enter) { Primary(); return true; }
        return base.ProcessCmdKey(ref msg, key);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(t.Bg);
        hits.Clear();
        PaintCaption(g);
        PaintAppName(g, Text, false);

        var a = new RectangleF(32 * s, 76 * s, ClientSize.Width - 64 * s, ClientSize.Height - 76 * s - 28 * s);
        float y = a.Y;
        string installed = Installer.FormatVersion(Installer.InstalledVersion), current = Installer.CurrentVersion;

        string heading, sub, body;
        if (mode == Mode.Install && !finished)
        {
            heading = "Install physmouse"; sub = "Version " + current;
            body = "physmouse moves the cursor between screens by where they sit on your desk. It installs for your user account only and doesn't need administrator rights.";
        }
        else if (mode == Mode.Install)
        {
            heading = "physmouse is installed"; sub = "Version " + current;
            body = "You'll find it in the Start menu and next to the clock. Open it to arrange your screens the way they sit on your desk.";
        }
        else if (mode == Mode.Update && !finished)
        {
            heading = "Update physmouse"; sub = "Version " + installed + " to " + current;
            body = "physmouse " + installed + " is installed in " + dir + ". Updating replaces the program and keeps your settings and screen layout.";
        }
        else if (mode == Mode.Update)
        {
            heading = "physmouse is up to date"; sub = "Version " + current;
            body = "The update is installed. Your settings and screen layout are unchanged.";
        }
        else if (!finished)
        {
            heading = "Uninstall physmouse"; sub = "Version " + installed;
            body = "This removes physmouse from this PC, together with its Start menu and desktop shortcuts and its startup entry.";
        }
        else
        {
            heading = "physmouse has been removed"; sub = "";
            body = keepSettings ? "Your settings and screen layout are still in %APPDATA%\\physmouse, ready for when you install it again." : "Nothing of physmouse is left on this PC.";
        }

        using (var icon = Theme.AppIcon((int)(48 * s), false)) g.DrawImage(icon, a.X, y + 4 * s);
        Draw(g, heading, fTitle, t.Text, new RectangleF(a.X + 64 * s, y - 2 * s, a.Width - 64 * s, 36 * s), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (sub != "") Draw(g, sub, fCaption, t.TextDim, new RectangleF(a.X + 64 * s, y + 34 * s, a.Width - 64 * s, 18 * s), TextFormatFlags.Left);
        y += 76 * s;
        y += DrawWrapped(g, body, fBody, t.TextDim, a.X, y, a.Width) + 20 * s;

        if (!finished && mode == Mode.Install)
        {
            var c = Card(g, ref y, a, "\uE8B7", "Location", dir, Measure(g, "Change", 0));
            Button(g, c.Right - 16 * s - Measure(g, "Change", 0), c.Y + (c.Height - 32 * s) / 2, "Change", false, true, ChooseFolder);
            c = Card(g, ref y, a, "\uE7E8", "Start with Windows", "Runs in the background when you sign in.", ToggleWidth(g));
            Toggle(g, c, autostart, v => autostart = v);
            c = Card(g, ref y, a, "\uE8A7", "Desktop shortcut", "physmouse is always added to the Start menu.", ToggleWidth(g));
            Toggle(g, c, desktop, v => desktop = v);
        }
        else if (!finished && mode == Mode.Uninstall)
        {
            var c = Card(g, ref y, a, "\uE713", "Keep settings and screen layout", "Useful if you plan to install physmouse again.", ToggleWidth(g));
            Toggle(g, c, keepSettings, v => keepSettings = v);
        }

        float by = a.Bottom - 32 * s;
        if (error != null)
        {
            var size = TextRenderer.MeasureText(g, error, fCaption, new Size((int)a.Width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, error, fCaption, new Rectangle((int)a.X, (int)(by - 16 * s - size.Height), (int)a.Width, size.Height), t.Danger,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        // buttons: the main action on the right, like Windows' own dialogs
        string primary = PrimaryText();
        float px = a.Right - Measure(g, primary, 96);
        Button(g, px, by, primary, true, true, Primary);
        if (!finished)
            Button(g, px - 8 * s - Measure(g, "Cancel", 96), by, "Cancel", false, true, Close);
        if (!finished && mode == Mode.Install)
            Button(g, a.X, by, "Run portable", false, true, () => { Result = Outcome.Portable; Close(); });
    }

    string PrimaryText()
    {
        if (finished) return mode == Mode.Uninstall ? "Close" : "Open physmouse";
        return mode == Mode.Install ? "Install" : mode == Mode.Update ? "Update" : "Uninstall";
    }

    void Primary()
    {
        if (finished)
        {
            if (mode != Mode.Uninstall && Installer.InstalledExe != null) Process.Start(Installer.InstalledExe);
            Close();
            return;
        }
        Cursor = Cursors.WaitCursor;
        error = mode == Mode.Uninstall ? Installer.Uninstall(keepSettings) : Installer.Install(dir, autostart, desktop, mode == Mode.Update);
        Cursor = Cursors.Default;
        if (error == null) { finished = true; Result = Outcome.Done; }
        Invalidate();
    }

    void ChooseFolder()
    {
        using (var d = new FolderBrowserDialog { Description = "Choose where to install physmouse.", ShowNewFolderButton = true, SelectedPath = Path.GetDirectoryName(dir) })
        {
            if (d.ShowDialog(this) != DialogResult.OK) return;
            dir = string.Equals(Path.GetFileName(d.SelectedPath), "physmouse", StringComparison.OrdinalIgnoreCase) ? d.SelectedPath : Path.Combine(d.SelectedPath, "physmouse");
            Invalidate();
        }
    }
}
