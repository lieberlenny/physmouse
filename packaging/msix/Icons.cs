using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

// Run by pack.cmd: draws the MSIX tile and taskbar icons with the same code as the app icon.
static class Icons
{
    static void Save(string dir, string name, int n)
    {
        using (var bmp = Theme.AppIcon(n, false)) bmp.Save(Path.Combine(dir, name), ImageFormat.Png);
    }

    static void Main(string[] args)
    {
        string dir = args[0];
        Directory.CreateDirectory(dir);
        foreach (int scale in new[] { 100, 200 })
        {
            Save(dir, "Square150x150Logo.scale-" + scale + ".png", 150 * scale / 100);
            Save(dir, "Square44x44Logo.scale-" + scale + ".png", 44 * scale / 100);
            Save(dir, "StoreLogo.scale-" + scale + ".png", 50 * scale / 100);
        }
        // unplated: the taskbar and Start show these as they are, without a coloured plate behind them
        foreach (int n in new[] { 16, 24, 32, 48, 256 })
        {
            Save(dir, "Square44x44Logo.targetsize-" + n + ".png", n);
            Save(dir, "Square44x44Logo.targetsize-" + n + "_altform-unplated.png", n);
        }
    }
}
