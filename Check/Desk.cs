using System.Diagnostics;
using System.Text;
using Microsoft.Win32;
using UIA = System.Windows.Automation;

/// The desktop as a person uses it: icons, the tray, the taskbar, the wallpaper, the theme, the screen's size.
static class Desk
{
    // ---------- desktop icons ----------

    public static IntPtr DefView()
    {
        var progman = Native.FindWindow("Progman", null);
        var dv = Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        IntPtr w = IntPtr.Zero;
        while (dv == IntPtr.Zero && (w = Native.FindWindowEx(IntPtr.Zero, w, "WorkerW", null)) != IntPtr.Zero)
            dv = Native.FindWindowEx(w, IntPtr.Zero, "SHELLDLL_DefView", null);
        return dv;
    }

    public static IntPtr ListView() => Native.FindWindowEx(DefView(), IntPtr.Zero, "SysListView32", null);
    public static bool IconsShown() => Native.IsWindowVisible(ListView());
    /// The GitHub machine sometimes shows a system dialog of its own a while after it starts (for example
    /// "System Properties": a temporary paging file). It covers the wall and the empty desktop. Closed as a person
    /// would, every two seconds, for as long as the check runs; only dialogs of other programs, each one noted.
    public static void CloseStrayDialogs()
    {
        var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Check", "Hidamari", "HidamariProbe" };
        var t = new Thread(() =>
        {
            while (true)
            {
                var found = new List<IntPtr>();
                Native.EnumWindows((h, _) =>
                {
                    if (Native.IsWindowVisible(h) && Native.ClassOf(h) == "#32770") found.Add(h);
                    return true;
                }, IntPtr.Zero);
                foreach (var h in found)
                {
                    Native.GetWindowThreadProcessId(h, out uint pid);
                    string name;
                    try { name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { continue; }
                    if (own.Contains(name)) continue;
                    Check.Say($"closing a system dialog: \"{Native.TitleOf(h)}\" ({name})");
                    Native.PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero);   // WM_CLOSE
                }
                Thread.Sleep(2000);
            }
        }) { IsBackground = true, Name = "stray dialogs" };
        t.Start();
    }

    /// Closes File Explorer folder windows (a restarted shell sometimes opens one).
    public static void CloseFolderWindows()
    {
        var found = new List<IntPtr>();
        Native.EnumWindows((h, _) => { if (Native.IsWindowVisible(h) && Native.ClassOf(h) == "CabinetWClass") found.Add(h); return true; }, IntPtr.Zero);
        foreach (var h in found) { Check.Say($"closing a folder window: \"{Native.TitleOf(h)}\""); Native.PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero); }
        if (found.Count > 0) Thread.Sleep(1000);
    }

    public static void ToggleIcons() => Native.SendMessage(DefView(), 0x0111, new IntPtr(0x7402), IntPtr.Zero);   // WM_COMMAND "show desktop icons"

    public static List<(string name, Rectangle rect)> Icons()
    {
        var lv = ListView();
        if (lv == IntPtr.Zero) return new();
        try
        {
            var list = UIA.AutomationElement.FromHandle(lv);
            var items = list.FindAll(UIA.TreeScope.Children, new UIA.PropertyCondition(UIA.AutomationElement.ControlTypeProperty, UIA.ControlType.ListItem));
            var found = new List<(string, Rectangle)>();
            foreach (UIA.AutomationElement it in items)
            {
                var b = it.Current.BoundingRectangle;
                if (b.IsEmpty || b.Width < 4) continue;
                found.Add((it.Current.Name, new Rectangle((int)b.X, (int)b.Y, (int)b.Width, (int)b.Height)));
            }
            return found;
        }
        catch { return new(); }
    }

    public static List<(string name, Rectangle rect)> WaitForIcons(int atLeast, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        List<(string, Rectangle)> icons;
        do { icons = Icons(); if (icons.Count >= atLeast) break; Thread.Sleep(500); } while (sw.Elapsed < timeout);
        return icons;
    }

    public static bool IsSelected(string name)
    {
        try
        {
            var list = UIA.AutomationElement.FromHandle(ListView());
            var it = list.FindFirst(UIA.TreeScope.Children, new UIA.PropertyCondition(UIA.AutomationElement.NameProperty, name));
            return it != null && it.TryGetCurrentPattern(UIA.SelectionItemPattern.Pattern, out var p) && ((UIA.SelectionItemPattern)p).Current.IsSelected;
        }
        catch { return false; }
    }

    public static Point Center(Rectangle r) => new(r.X + r.Width / 2, r.Y + r.Height / 3);   // the picture, above the label

    public static Point EmptyPoint(List<(string name, Rectangle rect)> icons, Rectangle work)
    {
        var p = new Point(work.Left + work.Width * 2 / 3, work.Top + work.Height / 2);
        return icons.Any(i => Rectangle.Inflate(i.rect, 20, 20).Contains(p)) ? new Point(work.Right - 40, work.Bottom - 40) : p;
    }

    public static List<Rectangle> Around(IEnumerable<(string name, Rectangle rect)> icons, int by = 6) =>
        icons.Select(i => Rectangle.Inflate(i.rect, by, by)).ToList();

    public static void MinimizeAll()
    {
        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        shell.GetType().InvokeMember("MinimizeAll", System.Reflection.BindingFlags.InvokeMethod, null, shell, null);
    }

    /// Progman's children (and the top-level WorkerW windows), top first.
    public static string Tree()
    {
        var sb = new StringBuilder();
        var progman = Native.FindWindow("Progman", null);
        sb.Append($"Progman exstyle 0x{(long)Native.GetWindowLongPtr(progman, -20):X}: ");
        for (var c = Native.GetWindow(progman, 5); c != IntPtr.Zero; c = Native.GetWindow(c, 2))
            sb.Append($"[{Native.Describe(c)}] ");
        sb.Append("| top-level WorkerW: ");
        IntPtr w = IntPtr.Zero;
        while ((w = Native.FindWindowEx(IntPtr.Zero, w, "WorkerW", null)) != IntPtr.Zero)
        {
            Native.GetWindowRect(w, out var r);
            if (r.Right - r.Left < 100) continue;    // Explorer's small helper windows
            sb.Append($"[{Native.Describe(w)} {r.Right - r.Left}x{r.Bottom - r.Top}:");
            for (var c = Native.GetWindow(w, 5); c != IntPtr.Zero; c = Native.GetWindow(c, 2)) sb.Append($" {Native.Describe(c)}");
            sb.Append("] ");
        }
        return sb.ToString();
    }

    // ---------- the tray ----------

    /// The tray button whose name starts with `name`, on the taskbar (not under the "^" arrow), or null.
    public static Rectangle? TrayIcon(string name)
    {
        try
        {
            var bar = Native.FindWindow("Shell_TrayWnd", null);
            if (bar == IntPtr.Zero) return null;
            var root = UIA.AutomationElement.FromHandle(bar);
            var buttons = root.FindAll(UIA.TreeScope.Descendants, new UIA.PropertyCondition(UIA.AutomationElement.ControlTypeProperty, UIA.ControlType.Button));
            foreach (UIA.AutomationElement b in buttons)
            {
                var n = b.Current.Name ?? "";
                var r = b.Current.BoundingRectangle;
                if (n.StartsWith(name) && !r.IsEmpty && r.Width > 4)
                    return new Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
            }
        }
        catch (Exception e) { Check.Say($"tray search failed: {e.Message}"); }
        return null;
    }

    /// Every button name on the taskbar (for the report when the icon is not found).
    public static string TrayNames()
    {
        try
        {
            var root = UIA.AutomationElement.FromHandle(Native.FindWindow("Shell_TrayWnd", null));
            var buttons = root.FindAll(UIA.TreeScope.Descendants, new UIA.PropertyCondition(UIA.AutomationElement.ControlTypeProperty, UIA.ControlType.Button));
            return string.Join(" | ", buttons.Cast<UIA.AutomationElement>().Select(b => b.Current.Name).Where(n => !string.IsNullOrEmpty(n)));
        }
        catch (Exception e) { return e.Message; }
    }

    /// Windows 11: the icon of a new app goes under the "^" arrow. The Settings app's "Other system tray icons"
    /// switch is HKCU\Control Panel\NotifyIconSettings\<n>\IsPromoted; turned on here for the app's icon.
    public static bool Promote(string exe)
    {
        bool any = false;
        using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", writable: false);
        if (root == null) return false;
        foreach (var sub in root.GetSubKeyNames())
        {
            using var k = Registry.CurrentUser.OpenSubKey($@"Control Panel\NotifyIconSettings\{sub}", writable: true);
            var path = k?.GetValue("ExecutablePath") as string;
            if (path == null || !string.Equals(Path.GetFullPath(path), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase)) continue;
            k!.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
            any = true;
        }
        return any;
    }

    /// Windows 11: the app's icon back under the "^" arrow (as a new app's icon is).
    public static bool Demote(string exe)
    {
        bool any = false;
        using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", writable: false);
        if (root == null) return false;
        foreach (var sub in root.GetSubKeyNames())
        {
            using var k = Registry.CurrentUser.OpenSubKey($@"Control Panel\NotifyIconSettings\{sub}", writable: true);
            var path = k?.GetValue("ExecutablePath") as string;
            if (path == null || !string.Equals(Path.GetFullPath(path), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase)) continue;
            k!.SetValue("IsPromoted", 0, RegistryValueKind.DWord);
            any = true;
        }
        return any;
    }

    /// Windows 10: new icons hidden under the "^" arrow (on) or all shown (off). Explorer must start again.
    public static bool SetAutoTray(bool on)
    {
        Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer", "EnableAutoTray", on ? 1 : 0, RegistryValueKind.DWord);
        return true;
    }

    const string StuckRects = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3";

    /// Windows 10: the taskbar's edge (0 left, 1 top, 2 right, 3 bottom), as dragging it there does. Explorer must start again.
    public static bool SetTaskbarEdge(int edge)
    {
        if (Registry.GetValue(StuckRects, "Settings", null) is not byte[] b || b.Length < 13) return false;
        b[12] = (byte)edge;
        Registry.SetValue(StuckRects, "Settings", b, RegistryValueKind.Binary);
        return true;
    }

    public static int TaskbarEdge() => Registry.GetValue(StuckRects, "Settings", null) is byte[] b && b.Length >= 13 ? b[12] : 3;

    /// The popup menu's item `name` (a right-click menu), or null.
    public static Rectangle? MenuItem(string name)
    {
        try
        {
            var menus = UIA.AutomationElement.RootElement.FindAll(UIA.TreeScope.Children,
                new UIA.PropertyCondition(UIA.AutomationElement.ControlTypeProperty, UIA.ControlType.Menu));
            foreach (UIA.AutomationElement m in menus)
            {
                var it = m.FindFirst(UIA.TreeScope.Descendants, new UIA.PropertyCondition(UIA.AutomationElement.NameProperty, name));
                if (it == null) continue;
                var r = it.Current.BoundingRectangle;
                return new Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
            }
        }
        catch (Exception e) { Check.Say($"menu search failed: {e.Message}"); }
        return null;
    }

    public static Rectangle Taskbar()
    {
        Native.GetWindowRect(Native.FindWindow("Shell_TrayWnd", null), out var r);
        return r.ToRectangle();
    }

    // ---------- theme, wallpaper ----------

    const string Personalize = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool LightTaskbar() => Registry.GetValue(Personalize, "SystemUsesLightTheme", 0) is int v && v != 0;

    /// Light or dark taskbar (and apps), as the Settings app's "Choose your mode" does, and tells everyone.
    public static void SetLight(bool light)
    {
        Registry.SetValue(Personalize, "SystemUsesLightTheme", light ? 1 : 0, RegistryValueKind.DWord);
        Registry.SetValue(Personalize, "AppsUseLightTheme", light ? 1 : 0, RegistryValueKind.DWord);
        Native.SendMessageTimeoutText(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "ImmersiveColorSet", 0x2, 3000, out _);
    }

    public static string Wallpaper()
    {
        var sb = new StringBuilder(1024);
        Native.SystemParametersInfo(0x0073, (uint)sb.Capacity, sb, 0);
        return $"{Reg(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallPaper")} | shown: {sb}";
    }

    public static string WallpaperPath() => Reg(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallPaper");

    /// Sets the wallpaper as the Settings app does (saved and told to everyone).
    public static bool SetWallpaper(string path) => Native.SystemParametersInfoSet(0x0014, 0, path, 0x3);

    public static string Reg(string key, string name) => Registry.GetValue(key, name, "")?.ToString() ?? "";

    // ---------- the taskbar hiding itself ----------

    public static int TaskbarState()
    {
        var d = new Native.APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.APPBARDATA>(), hWnd = Native.FindWindow("Shell_TrayWnd", null) };
        return (int)Native.SHAppBarMessage(4, ref d);   // ABM_GETSTATE
    }

    public static void SetTaskbarState(int state)
    {
        var d = new Native.APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.APPBARDATA>(), hWnd = Native.FindWindow("Shell_TrayWnd", null), lParam = new IntPtr(state) };
        Native.SHAppBarMessage(10, ref d);   // ABM_SETSTATE
    }

    // ---------- the screen's size ----------

    public static (int w, int h) Resolution()
    {
        var dm = new Native.DEVMODE { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<Native.DEVMODE>() };
        Native.EnumDisplaySettings(null, -1, ref dm);
        return (dm.dmPelsWidth, dm.dmPelsHeight);
    }

    /// The sizes the main screen offers.
    public static List<(int w, int h)> Resolutions()
    {
        var list = new List<(int, int)>();
        for (int i = 0; i < 500; i++)
        {
            var dm = new Native.DEVMODE { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<Native.DEVMODE>() };
            if (!Native.EnumDisplaySettings(null, i, ref dm)) break;
            if (dm.dmBitsPerPel == 32 && !list.Contains((dm.dmPelsWidth, dm.dmPelsHeight))) list.Add((dm.dmPelsWidth, dm.dmPelsHeight));
        }
        return list;
    }

    /// Changes the main screen's size for now (not saved). 0 when Windows took it.
    public static int SetResolution(int w, int h)
    {
        var dm = new Native.DEVMODE { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<Native.DEVMODE>() };
        Native.EnumDisplaySettings(null, -1, ref dm);
        dm.dmPelsWidth = w; dm.dmPelsHeight = h;
        dm.dmFields = 0x80000 | 0x100000;   // DM_PELSWIDTH | DM_PELSHEIGHT
        return Native.ChangeDisplaySettingsEx(null, ref dm, IntPtr.Zero, 0, IntPtr.Zero);
    }

    /// Back to the saved size.
    public static int ResetResolution() => Native.ChangeDisplaySettingsExReset(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);

    // ---------- a window over the whole screen, as a game or a video would be ----------

    public sealed class Cover : IDisposable
    {
        System.Windows.Forms.Form? form;
        readonly Thread thread;
        readonly ManualResetEventSlim shown = new();

        public Cover(Rectangle bounds)
        {
            thread = new Thread(() =>
            {
                form = new System.Windows.Forms.Form
                {
                    FormBorderStyle = System.Windows.Forms.FormBorderStyle.None, StartPosition = System.Windows.Forms.FormStartPosition.Manual, Bounds = bounds,
                    TopMost = true, BackColor = System.Drawing.Color.FromArgb(20, 20, 30), ShowInTaskbar = false, Text = "Hidamari check: full screen",
                };
                form.Shown += (_, _) => shown.Set();
                System.Windows.Forms.Application.Run(form);
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            shown.Wait(10000);
        }

        public void Dispose()
        {
            try { form?.Invoke(() => form.Close()); } catch { }
            thread.Join(5000);
        }
    }
}
