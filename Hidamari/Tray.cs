using System.Runtime.InteropServices;
using Microsoft.Win32;
using static Hidamari.Native;

namespace Hidamari;

/// The icon in the tray (the notification area next to the clock). Windows does not recolour tray icons the way
/// macOS does in the menu bar, so there are two: black for a light taskbar, white for a dark one, swapped as soon as
/// the person switches. Each comes in the sizes for 100–200 % (16–32 pixels), the right one is picked for the
/// taskbar's scale. A left click (or Enter on the icon) opens and closes the card; a right click opens a menu with one
/// item, Quit.
sealed class Tray : IDisposable
{
    public const int CallbackMessage = WM_APP + 1;
    const uint Id = 1;
    const int QuitCommand = 1;

    readonly IntPtr hwnd;
    readonly Action quit;
    readonly Action<string> select;
    readonly Dictionary<(bool white, int size), Icon> icons = new();
    bool added, light;
    int size;
    IntPtr shownIcon;

    public Tray(IntPtr hwnd, Action quit, Action<string> select)
    {
        this.hwnd = hwnd;
        this.quit = quit;
        this.select = select;
        // menus in the dark when apps are dark, as the system's own (see Native.SetPreferredAppMode)
        if (Environment.OSVersion.Version.Build >= 18362)
        {
            try { SetPreferredAppMode(1); FlushMenuThemes(); }
            catch (Exception e) { Log.Write($"tray: menus stay light ({e.Message})"); }
        }
    }

    /// Puts the icon in the tray, again after Explorer started anew.
    public void Add(string why)
    {
        Pick();
        var data = Data();
        added = Shell_NotifyIcon(NIM_ADD, ref data);
        if (!added)
        {
            // Explorer may still hold the old one (it came back before telling everyone): replace it
            Shell_NotifyIcon(NIM_DELETE, ref data);
            added = Shell_NotifyIcon(NIM_ADD, ref data);
        }
        data.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
        Log.Write($"tray: icon {(added ? "added" : "NOT added")} ({why}), {(light ? "light" : "dark")} taskbar, {size} px, {Where()}");
    }

    /// The taskbar changed colour or scale: the matching icon.
    public void Refresh(string why)
    {
        if (!added) return;
        bool wasLight = light; int wasSize = size;
        Pick();
        if (light == wasLight && size == wasSize) return;
        var data = Data();
        Shell_NotifyIcon(NIM_MODIFY, ref data);
        Log.Write($"tray: {(light ? "light" : "dark")} taskbar, {size} px icon ({why}), {Where()}");
    }

    /// Where the icon is on the screen (for the journal), or why it cannot be told.
    public string Where()
    {
        var id = new NOTIFYICONIDENTIFIER { cbSize = Marshal.SizeOf<NOTIFYICONIDENTIFIER>(), hWnd = hwnd, uID = Id };
        int hr = Shell_NotifyIconGetRect(ref id, out var r);
        return hr == 0 ? $"at {r}" : $"place unknown (0x{hr:X})";
    }

    /// Where the icon is on the screen, in pixels; null when Windows cannot tell (it is hidden under the "^").
    public RECT? Rect()
    {
        var id = new NOTIFYICONIDENTIFIER { cbSize = Marshal.SizeOf<NOTIFYICONIDENTIFIER>(), hWnd = hwnd, uID = Id };
        return Shell_NotifyIconGetRect(ref id, out var r) == 0 && r.Width > 0 ? r : null;
    }

    void Pick()
    {
        light = TaskbarIsLight();
        var taskbar = FindWindow("Shell_TrayWnd", null);
        uint dpi = taskbar != IntPtr.Zero ? GetDpiForWindow(taskbar) : 0;
        if (dpi == 0) dpi = GetDpiForSystem();
        size = GetSystemMetricsForDpi(SM_CXSMICON, dpi);
        if (size <= 0) size = (int)Math.Round(16 * dpi / 96.0);
        // black on a light taskbar, white on a dark one
        shownIcon = IconFor(white: !light, size).Handle;
    }

    Icon IconFor(bool white, int px)
    {
        if (icons.TryGetValue((white, px), out var icon)) return icon;
        using var s = typeof(Tray).Assembly.GetManifestResourceStream(white ? "tray-white.ico" : "tray-black.ico")!;
        icon = new Icon(s, new Size(px, px));
        icons[(white, px)] = icon;
        return icon;
    }

    /// Windows 10 1903 and later: the taskbar is light when "Choose your default Windows mode" is Light.
    /// Windows 11 keeps the icons of the tray under the "^" arrow unless the person put them on the taskbar
    /// (Settings → Personalisation → Taskbar → Other system tray icons), and tells which in
    /// HKCU\Control Panel\NotifyIconSettings\<n>\IsPromoted. There Windows gives the arrow's place for a hidden icon,
    /// so this says whether the icon is hidden. Windows 10 has no such list: null.
    public static bool? HiddenUnderArrow()
    {
        if (Environment.OSVersion.Version.Build < 22000) return null;
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
            if (root == null) return null;
            var me = Environment.ProcessPath ?? "";
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                if (k?.GetValue("ExecutablePath") is not string path) continue;
                bool mine = string.Equals(path, me, StringComparison.OrdinalIgnoreCase)
                            || (path.StartsWith("{") && me.EndsWith(path[(path.IndexOf('}') + 1)..], StringComparison.OrdinalIgnoreCase));
                if (mine) return !(k.GetValue("IsPromoted") is int v && v == 1);
            }
            return true;   // not listed yet: a new icon, hidden
        }
        catch { return null; }
    }

    public static bool TaskbarIsLight() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int v && v != 0;

    NOTIFYICONDATA Data() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = hwnd,
        uID = Id,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP,
        uCallbackMessage = CallbackMessage,
        hIcon = shownIcon,
        szTip = Strings.Name,
        szInfo = "", szInfoTitle = "",
    };

    /// The tray's news for the icon (version 4: the event in the low word of lParam, the place in wParam).
    public void OnCallback(IntPtr wParam, IntPtr lParam)
    {
        int ev = (int)((long)lParam & 0xFFFF);
        if (ev is NIN_SELECT or NIN_KEYSELECT)
        {
            select(ev == NIN_SELECT ? "a click on the icon" : "the keyboard on the icon");
            return;
        }
        if (ev != WM_CONTEXTMENU) return;
        int x = (short)((long)wParam & 0xFFFF), y = (short)(((long)wParam >> 16) & 0xFFFF);
        ShowMenu(x, y);
    }

    void ShowMenu(int x, int y)
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, MF_STRING, new UIntPtr(QuitCommand), Strings.Quit);
        // without this the menu would not close when the person clicks elsewhere
        SetForegroundWindow(hwnd);
        int cmd = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY, x, y, hwnd, IntPtr.Zero);
        PostMessage(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);
        if (cmd == QuitCommand)
        {
            Log.Write("tray: Quit");
            quit();
        }
    }

    public void Dispose()
    {
        if (added)
        {
            var data = Data();
            Shell_NotifyIcon(NIM_DELETE, ref data);
            added = false;
        }
        foreach (var i in icons.Values) i.Dispose();
        icons.Clear();
    }
}
