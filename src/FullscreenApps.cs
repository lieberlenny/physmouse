using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

sealed class OpenApplication
{
    public string ProcessName;
    public string Title;
    public bool IsOpen = true;
    public override string ToString() { return IsOpen ? Title + "  (" + ProcessName + ".exe)" : ProcessName + ".exe (not currently open)"; }
}

static class FullscreenApps
{
    const uint GW_OWNER = 4;
    const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80;
    static uint cachedProcessId;
    static string cachedProcessName;

    public static List<OpenApplication> FindOpen()
    {
        var found = new Dictionary<string, OpenApplication>(StringComparer.OrdinalIgnoreCase);
        Native.EnumWindows((hwnd, lParam) =>
        {
            if (!Native.IsWindowVisible(hwnd) || Native.GetWindow(hwnd, GW_OWNER) != IntPtr.Zero
                || (Native.GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
            var title = new StringBuilder(512);
            if (Native.GetWindowText(hwnd, title, title.Capacity) == 0) return true;
            uint id;
            Native.GetWindowThreadProcessId(hwnd, out id);
            if (id == 0 || id == (uint)Process.GetCurrentProcess().Id) return true;
            try
            {
                using (var process = Process.GetProcessById((int)id))
                    if (!found.ContainsKey(process.ProcessName))
                        found.Add(process.ProcessName, new OpenApplication { ProcessName = process.ProcessName, Title = title.ToString() });
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            return true;
        }, IntPtr.Zero);
        return found.Values.OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static bool IsSelectedWindow(IntPtr hwnd, string[] selected)
    {
        if (selected == null || selected.Length == 0) return false;
        uint id;
        Native.GetWindowThreadProcessId(hwnd, out id);
        if (id == 0) return false;
        if (id != cachedProcessId)
        {
            cachedProcessId = id;
            cachedProcessName = null;
            try
            {
                using (var process = Process.GetProcessById((int)id)) cachedProcessName = process.ProcessName;
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        return cachedProcessName != null && selected.Contains(cachedProcessName, StringComparer.OrdinalIgnoreCase);
    }
}
