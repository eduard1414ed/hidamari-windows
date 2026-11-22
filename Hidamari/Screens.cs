using System.Runtime.InteropServices;
using static Hidamari.Native;

namespace Hidamari;

/// One screen as Windows has it now: where it is in pixels and its scale (1.0 = 100 %, 1.5 = 150 %).
sealed record ScreenInfo(string Name, Rectangle Bounds, Rectangle Work, double Scale, bool Primary, int Hz)
{
    /// A short name for files and the journal: DISPLAY1.
    public string Short => new(Name.Where(char.IsLetterOrDigit).ToArray());
    public override string ToString() =>
        $"{Short} {Bounds.X},{Bounds.Y} {Bounds.Width}x{Bounds.Height} at {Scale * 100:F0} %{(Hz > 0 ? $", {Hz} Hz" : "")}{(Primary ? ", main" : "")}";
}

/// The screens, read straight from Windows every time (Windows Forms' own list is refreshed only through a helper
/// thread the app does not otherwise need).
static class Screens
{
    public static List<ScreenInfo> All()
    {
        var list = new List<ScreenInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr _, ref RECT __, IntPtr ___) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(m, ref info)) return true;
            double scale = GetDpiForMonitor(m, 0, out uint dpi, out uint dpiY) == 0 ? dpi / 96.0 : 1.0;
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            int hz = EnumDisplaySettings(info.szDevice, ENUM_CURRENT_SETTINGS, ref dm) ? dm.dmDisplayFrequency : 0;
            list.Add(new ScreenInfo(info.szDevice,
                Rectangle.FromLTRB(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom),
                Rectangle.FromLTRB(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom),
                scale, (info.dwFlags & 1) != 0, hz));
            return true;
        }, IntPtr.Zero);
        return list.OrderByDescending(s => s.Primary).ThenBy(s => s.Name).ToList();
    }

    /// What decides the walls: names, places, sizes and scales. Two equal signatures need no new walls.
    public static string Signature(IEnumerable<ScreenInfo> screens) =>
        string.Join("; ", screens.Select(s => $"{s.Name} {s.Bounds} {s.Scale}"));

    /// The graphics cards Windows knows of (for the journal).
    public static string Cards()
    {
        var names = new List<string>();
        for (uint i = 0; i < 16; i++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
            if (!string.IsNullOrWhiteSpace(dd.DeviceString) && !names.Contains(dd.DeviceString)) names.Add(dd.DeviceString);
        }
        return names.Count == 0 ? "unknown" : string.Join(", ", names);
    }
}
