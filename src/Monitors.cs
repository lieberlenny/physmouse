using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

// One connected screen. R is its pixel rectangle on the Windows desktop;
// X, Y, W, H place its visible image on the desk, in millimetres.
class Mon
{
    public string Id, Model;
    public int Number;
    public Rectangle R;
    public bool Primary;
    public double X, Y, W, H;

    public double Diagonal { get { return Math.Sqrt(W * W + H * H); } }
    public Mon Clone() { return (Mon)MemberwiseClone(); }
}

static class Monitors
{
    // Every connected screen, sized from its EDID and placed by scaling the Windows pixel layout.
    public static List<Mon> Detect()
    {
        var list = new List<Mon>();
        foreach (var s in Screen.AllScreens.OrderBy(s => Number(s.DeviceName)))
        {
            string id, model;
            Describe(s.DeviceName, out id, out model);
            int dup = list.Count(m => m.Id == id || m.Id.StartsWith(id + "~"));
            if (dup > 0) id += "~" + (dup + 1); // same EDID twice, e.g. one panel split into two displays
            // DISPLAYn digits drift (e.g. 11, 12, 13 after a driver reinstall); show 1..N in the same order.
            var mon = new Mon { Id = id, Model = model, Number = list.Count + 1, R = s.Bounds, Primary = s.Primary };
            Guess(mon, s.DeviceName);
            list.Add(mon);
        }
        return list;
    }

    static int Number(string device)
    {
        int n;
        return int.TryParse(new string(device.Where(char.IsDigit).ToArray()), out n) ? n : 0;
    }

    // Stable id: EDID product code plus the connection instance, e.g. "LEN68F6\5&15fef2ea&1&UID4353".
    // DISPLAYn numbers can change after driver updates; this only changes when the cable moves.
    static void Describe(string device, out string id, out string model)
    {
        id = device.TrimStart('\\', '.');
        model = "";
        var dd = new Native.DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(Native.DISPLAY_DEVICE)) };
        if (!Native.EnumDisplayDevices(device, 0, ref dd, 1)) return; // 1 = EDD_GET_DEVICE_INTERFACE_NAME
        model = dd.DeviceString;
        var parts = dd.DeviceID.Split('#'); // \\?\DISPLAY#LEN68F6#5&15fef2ea&1&UID4353#{guid}
        if (parts.Length < 3) return;
        id = parts[1] + "\\" + parts[2];
        using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\DISPLAY\" + parts[1] + "\\" + parts[2] + @"\Device Parameters"))
        {
            var edid = k == null ? null : k.GetValue("EDID") as byte[];
            for (int o = 54; edid != null && o + 18 <= edid.Length && o <= 108; o += 18)
                if (edid[o] == 0 && edid[o + 1] == 0 && edid[o + 3] == 0xFC) // display name descriptor
                {
                    model = System.Text.Encoding.ASCII.GetString(edid, o + 5, 13).Split('\n')[0].Trim();
                    break;
                }
        }
    }

    static void Guess(Mon m, string device)
    {
        IntPtr dc = Native.CreateDC(device, null, null, IntPtr.Zero);
        double w = Native.GetDeviceCaps(dc, 4), h = Native.GetDeviceCaps(dc, 6); // HORZSIZE / VERTSIZE, from EDID
        Native.DeleteDC(dc);
        if (w <= 0 || h <= 0) { w = m.R.Width * 0.25; h = m.R.Height * 0.25; } // no EDID size: assume ~100 dpi
        double k = MmPerPixel(w, h, m.R.Width, m.R.Height);
        m.X = m.R.X * k; m.Y = m.R.Y * k; m.W = m.R.Width * k; m.H = m.R.Height * k;
    }

    // EDID sizes ignore rotation, and Windows may split one panel into several displays that all
    // report the full panel size. Pick the orientation where x and y density agree, then the denser axis.
    public static double MmPerPixel(double wMm, double hMm, int wPx, int hPx)
    {
        double a = wMm / wPx, b = hMm / hPx, c = hMm / wPx, d = wMm / hPx;
        return Math.Max(a, b) / Math.Min(a, b) <= Math.Max(c, d) / Math.Min(c, d) ? Math.Min(a, b) : Math.Min(c, d);
    }
}
