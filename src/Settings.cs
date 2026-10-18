using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

// Plain-text settings file, so it can be read, diffed and backed up by hand.
class Settings
{
    public bool Enabled = true;
    public bool HoldAtEdge = true;      // nothing beyond the edge: stop there, or let Windows decide
    public int Resistance;              // pixels of push before the cursor crosses to another screen
    public Keys BypassKey = Keys.None;  // ShiftKey, ControlKey or Menu
    public bool LockFullscreen = true;
    public string[] BlockedApps = new string[0];
    public Keys Hotkey = Keys.None;     // key code plus Control/Alt/Shift
    public bool TrayIcon = true;
    public bool Inches;
    public bool WelcomeShown;
    public readonly Dictionary<string, double[]> Layout = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public bool Existed;

    public static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "physmouse", "settings.ini");

    public static Settings Load()
    {
        string legacy = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini");
        string path = File.Exists(FilePath) ? FilePath : legacy;
        if (!File.Exists(path)) return new Settings();
        var s = Parse(File.ReadAllText(path));
        s.Existed = true;
        if (path != FilePath) s.Save(); // move an old portable settings file into the permanent per-user location
        return s;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, Format(), new UTF8Encoding(false));
        if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath);
        Existed = true;
    }

    // Place each screen from the saved layout. A screen whose connection id changed (new GPU driver,
    // different port) still matches by its EDID product code when that's unambiguous.
    // Returns the screens the layout doesn't know yet; they keep their detected guess.
    public List<Mon> ApplyTo(List<Mon> mons)
    {
        var unknown = new List<Mon>();
        foreach (var m in mons)
        {
            double[] v;
            if (!Layout.TryGetValue(m.Id, out v))
            {
                string product = Product(m.Id);
                var saved = Layout.Keys.Where(k => Product(k) == product).ToList();
                if (saved.Count == 1 && mons.Count(n => Product(n.Id) == product) == 1 && !mons.Any(n => n.Id == saved[0]))
                    v = Layout[saved[0]];
            }
            if (v != null) { m.X = v[0]; m.Y = v[1]; m.W = v[2]; m.H = v[3]; }
            else unknown.Add(m);
        }
        return unknown;
    }

    static string Product(string id) { int i = id.IndexOf('\\'); return i < 0 ? id : id.Substring(0, i); }

    // Screens that aren't connected right now keep their saved place.
    public void Store(IEnumerable<Mon> mons)
    {
        foreach (var m in mons)
        {
            Layout[m.Id] = new[] { Math.Round(m.X, 1), Math.Round(m.Y, 1), Math.Round(m.W, 1), Math.Round(m.H, 1) };
            notes[m.Id] = string.Format(CultureInfo.InvariantCulture, "{0}{1}x{2}", m.Model != "" ? m.Model + ", " : "", m.R.Width, m.R.Height);
        }
    }

    public string Format()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# physmouse settings. Changes made here are read when physmouse starts.");
        sb.AppendLine();
        Line(sb, "enabled", Enabled ? "true" : "false", "");
        Line(sb, "no-screen-beyond-edge", HoldAtEdge ? "stop" : "windows", "stop | windows");
        Line(sb, "edge-resistance", Resistance.ToString(CultureInfo.InvariantCulture), "pixels to push before crossing");
        Line(sb, "bypass-key", BypassName(BypassKey), "none | shift | ctrl | alt");
        Line(sb, "lock-to-fullscreen", LockFullscreen ? "true" : "false", "");
        Line(sb, "toggle-shortcut", Hotkey == Keys.None ? "none" : HotkeyText(Hotkey).ToLowerInvariant().Replace(" ", ""), "e.g. ctrl+alt+f12");
        Line(sb, "tray-icon", TrayIcon ? "true" : "false", "");
        Line(sb, "units", Inches ? "in" : "mm", "mm | in (display only, the layout is always stored in mm)");
        Line(sb, "welcome-shown", WelcomeShown ? "true" : "false", "");
        sb.AppendLine();
        sb.AppendLine("[fullscreen-apps]");
        foreach (var app in BlockedApps.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) sb.AppendLine(app);
        sb.AppendLine();
        sb.AppendLine("[layout]");
        sb.AppendLine("# screen id   x   y   width   height   (mm; x/y is the top-left of the visible image)");
        foreach (var kv in Layout.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            string note;
            notes.TryGetValue(kv.Key, out note);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0}  {1}  {2}  {3}  {4}{5}",
                kv.Key, kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3], string.IsNullOrEmpty(note) ? "" : "   # " + note));
        }
        return sb.ToString();
    }

    static void Line(StringBuilder sb, string key, string value, string hint)
    {
        sb.AppendLine(hint == "" ? key + " = " + value : (key + " = " + value).PadRight(36) + "# " + hint);
    }

    public static Settings Parse(string text)
    {
        var s = new Settings { WelcomeShown = !string.IsNullOrWhiteSpace(text) }; // non-empty older settings have already been through first run
        bool layout = false, fullscreenApps = false;
        var blockedApps = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            string sectionLine = raw.Trim();
            if (sectionLine.StartsWith("["))
            {
                layout = sectionLine.Equals("[layout]", StringComparison.OrdinalIgnoreCase);
                fullscreenApps = sectionLine.Equals("[fullscreen-apps]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (fullscreenApps)
            {
                if (sectionLine != "" && !sectionLine.StartsWith("#")) blockedApps.Add(sectionLine);
                continue;
            }
            int hash = raw.IndexOf('#');
            string line = (hash >= 0 ? raw.Substring(0, hash) : raw).Trim();
            if (line == "") continue;
            if (layout)
            {
                var t = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                double[] v = new double[4];
                if (t.Length >= 5 && Enumerable.Range(0, 4).All(i => double.TryParse(t[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) && v[2] > 0 && v[3] > 0)
                {
                    s.Layout[t[0]] = v;
                    if (hash >= 0) s.notes[t[0]] = raw.Substring(hash + 1).Trim();
                }
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string key = line.Substring(0, eq).Trim().ToLowerInvariant(), val = line.Substring(eq + 1).Trim().ToLowerInvariant();
            int n;
            switch (key)
            {
                case "enabled": s.Enabled = val != "false"; break;
                case "no-screen-beyond-edge": s.HoldAtEdge = val != "windows"; break;
                case "edge-resistance": if (int.TryParse(val, out n)) s.Resistance = Math.Max(0, Math.Min(1000, n)); break;
                case "bypass-key": s.BypassKey = ParseBypass(val); break;
                case "lock-to-fullscreen": s.LockFullscreen = val != "false"; break;
                case "toggle-shortcut": s.Hotkey = ParseHotkey(val); break;
                case "tray-icon": s.TrayIcon = val != "false"; break;
                case "units": s.Inches = val == "in"; break;
                case "welcome-shown": s.WelcomeShown = val == "true"; break;
            }
        }
        s.BlockedApps = blockedApps.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return s;
    }

    public static readonly Keys[] BypassKeys = { Keys.None, Keys.ShiftKey, Keys.ControlKey, Keys.Menu };
    public static readonly string[] BypassNames = { "None", "Shift", "Ctrl", "Alt" };
    static string BypassName(Keys k) { return BypassNames[Math.Max(0, Array.IndexOf(BypassKeys, k))].ToLowerInvariant(); }
    static Keys ParseBypass(string v)
    {
        int i = Array.FindIndex(BypassNames, n => n.Equals(v, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? Keys.None : BypassKeys[i];
    }

    public static string HotkeyText(Keys k)
    {
        if (k == Keys.None) return "None";
        var parts = new List<string>();
        if ((k & Keys.Control) != 0) parts.Add("Ctrl");
        if ((k & Keys.Alt) != 0) parts.Add("Alt");
        if ((k & Keys.Shift) != 0) parts.Add("Shift");
        var code = k & Keys.KeyCode;
        string name = code >= Keys.D0 && code <= Keys.D9 ? ((int)(code - Keys.D0)).ToString(CultureInfo.InvariantCulture) : code.ToString();
        parts.Add(name);
        return string.Join(" + ", parts);
    }

    public static Keys ParseHotkey(string v)
    {
        Keys k = Keys.None;
        foreach (var p in v.Split('+').Select(x => x.Trim()))
        {
            Keys code;
            if (p == "ctrl") k |= Keys.Control;
            else if (p == "alt") k |= Keys.Alt;
            else if (p == "shift") k |= Keys.Shift;
            else if (p.Length == 1 && char.IsDigit(p[0])) k |= Keys.D0 + (p[0] - '0');
            else if (Enum.TryParse(p, true, out code) && (code & Keys.Modifiers) == 0) k |= code;
            else return Keys.None;
        }
        return (k & Keys.KeyCode) == Keys.None ? Keys.None : k;
    }
}
