using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using Microsoft.Win32;
using System.Windows.Forms;

// Colours follow the Windows 11 Settings app: light or dark from the system, accent from the user's accent colour.
class Theme
{
    public bool Dark, HighContrast;
    public Color Bg, Card, CardHover, Stroke, Text, TextDim, TextOff, Accent, AccentHover, OnAccent,
        Control, ControlHover, ControlDown, ControlStroke, NavSelected, NavHover, Canvas, Grid, Tile, TileStroke, Danger;

    public static Theme Current()
    {
        if (SystemInformation.HighContrast)
            return new Theme
            {
                HighContrast = true,
                Dark = SystemColors.Window.GetBrightness() < 0.5f,
                Bg = SystemColors.Window, Card = SystemColors.Window, CardHover = SystemColors.ControlLight,
                Stroke = SystemColors.WindowFrame, Text = SystemColors.WindowText, TextDim = SystemColors.WindowText,
                TextOff = SystemColors.GrayText, Accent = SystemColors.Highlight, AccentHover = SystemColors.Highlight,
                OnAccent = SystemColors.HighlightText, Control = SystemColors.Window, ControlHover = SystemColors.ControlLight,
                ControlDown = SystemColors.Control, ControlStroke = SystemColors.WindowFrame,
                NavSelected = SystemColors.Control, NavHover = SystemColors.ControlLight,
                Canvas = SystemColors.Window, Grid = SystemColors.ControlLight,
                Tile = SystemColors.Window, TileStroke = SystemColors.WindowFrame, Danger = SystemColors.HotTrack,
            };

        bool dark = false;
        using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            if (k != null) dark = Convert.ToInt32(k.GetValue("AppsUseLightTheme", 1)) == 0;

        // AccentPalette holds 8 RGBA shades from lightest to darkest; Settings uses Light2 on dark, Dark1 on light.
        Color accent = dark ? Color.FromArgb(96, 205, 255) : Color.FromArgb(0, 95, 184);
        using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
        {
            var pal = k == null ? null : k.GetValue("AccentPalette") as byte[];
            if (pal != null && pal.Length >= 32) { int i = dark ? 1 : 4; accent = Color.FromArgb(pal[i * 4], pal[i * 4 + 1], pal[i * 4 + 2]); }
        }

        return dark ? new Theme
        {
            Dark = true, Bg = Rgb(0x202020), Card = Rgb(0x2B2B2B), CardHover = Rgb(0x323232), Stroke = Rgb(0x1D1D1D),
            Text = Rgb(0xFFFFFF), TextDim = Rgb(0xC5C5C5), TextOff = Rgb(0x7A7A7A), Accent = accent, AccentHover = Mix(accent, Color.Black, 0.1), OnAccent = Rgb(0x000000),
            Control = Rgb(0x373737), ControlHover = Rgb(0x3D3D3D), ControlDown = Rgb(0x323232), ControlStroke = Rgb(0x454545),
            NavSelected = Rgb(0x2D2D2D), NavHover = Rgb(0x292929), Canvas = Rgb(0x1C1C1C), Grid = Rgb(0x262626),
            Tile = Rgb(0x2E2E2E), TileStroke = Rgb(0x444444), Danger = Rgb(0xFF99A4),
        } : new Theme
        {
            Dark = false, Bg = Rgb(0xF3F3F3), Card = Rgb(0xFBFBFB), CardHover = Rgb(0xF6F6F6), Stroke = Rgb(0xE5E5E5),
            Text = Rgb(0x1B1B1B), TextDim = Rgb(0x5F5F5F), TextOff = Rgb(0xA0A0A0), Accent = accent, AccentHover = Mix(accent, Color.White, 0.1), OnAccent = Rgb(0xFFFFFF),
            Control = Rgb(0xFDFDFD), ControlHover = Rgb(0xF6F6F6), ControlDown = Rgb(0xF0F0F0), ControlStroke = Rgb(0xDCDCDC),
            NavSelected = Rgb(0xEAEAEA), NavHover = Rgb(0xEDEDED), Canvas = Rgb(0xF9F9F9), Grid = Rgb(0xEDEDED),
            Tile = Rgb(0xFFFFFF), TileStroke = Rgb(0xD0D0D0), Danger = Rgb(0xC42B1C),
        };
    }

    static Color Rgb(int v) { return Color.FromArgb(255, Color.FromArgb(v)); }
    public static Color Mix(Color a, Color b, double t)
    {
        return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    public static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // The app icon: three screens of different sizes on a tile. Drawn in code so every size is sharp.
    public static Bitmap AppIcon(int n, bool paused)
    {
        var bmp = new Bitmap(n, n);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float u = n / 64f;
            using (var tile = Round(new RectangleF(2 * u, 2 * u, n - 4 * u, n - 4 * u), n * 0.22f))
            using (var grad = new LinearGradientBrush(new PointF(0, 0), new PointF(n, n),
                paused ? Color.FromArgb(150, 150, 150) : Color.FromArgb(64, 156, 255),
                paused ? Color.FromArgb(105, 105, 105) : Color.FromArgb(88, 86, 214)))
                g.FillPath(grad, tile);
            foreach (var r in new[] { new RectangleF(10, 20, 13, 28), new RectangleF(26, 12, 29, 14), new RectangleF(28, 30, 24, 16) })
                using (var path = Round(new RectangleF(r.X * u, r.Y * u, r.Width * u, r.Height * u), 3 * u))
                    g.FillPath(Brushes.White, path);
        }
        return bmp;
    }

    // An Icon that owns its handle, so disposing it frees the GDI icon.
    public static Icon MakeIcon(int n, bool paused)
    {
        using (var bmp = AppIcon(n, paused))
        {
            IntPtr h = bmp.GetHicon();
            try { using (var borrowed = Icon.FromHandle(h)) return (Icon)borrowed.Clone(); }
            finally { DestroyIcon(h); }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32")] static extern bool DestroyIcon(IntPtr h);

    public static readonly string TextFont = Pick("Segoe UI Variable Text", "Segoe UI");
    public static readonly string StrongFont = Pick("Segoe UI Variable Text Semibold", "Segoe UI Semibold");
    public static readonly string DisplayFont = Pick("Segoe UI Variable Display Semib", "Segoe UI Semibold");
    public static readonly string IconFont = Pick("Segoe Fluent Icons", "Segoe MDL2 Assets");

    static string Pick(string want, string fallback)
    {
        return FontFamily.Families.Any(f => f.Name == want) ? want : fallback;
    }
}
