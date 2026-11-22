using static Hidamari.Native;

namespace Hidamari;

/// Puts windows under the desktop icons. Windows has no official way to do this; this is the way the living
/// open projects do it (Lively Wallpaper, src/Lively/Lively/Core/WinDesktopCore.cs), in its two forms.
/// Found and checked by the probe (HidamariProbe/Desktop.cs).
///
/// Classic desktop (Windows 10, Windows 11 before 24H2). Message 0x052C makes Progman split the icons
/// into their own top-level WorkerW and create a second WorkerW right behind it, which paints the wallpaper:
///     WorkerW            ← holds SHELLDLL_DefView (the icons)
///     WorkerW            ← the wallpaper: the walls become its children
///     Progman
///
/// Raised desktop (Windows 11 24H2 and later, also Windows Server 2025). Progman is drawn with
/// WS_EX_NOREDIRECTIONBITMAP and everything lives inside it. As Microsoft described it to Lively:
/// "create its own WS_EX_LAYERED child HWND that is z-ordered under the DefView window but above the
/// WorkerW window", with SetLayeredWindowAttributes(alpha 255).
///     Progman
///       SHELLDLL_DefView ← the icons, transparent around them
///       the walls        ← layered children of Progman
///       WorkerW          ← the wallpaper, last child
///
/// Which form a machine has is read from Progman's style every time, never from the version number.
///
/// Unlike the probe, the walls are layered windows on the classic desktop too. A layered child keeps its picture in
/// its own surface, so the wallpaper layer under it is never painted over: when the app is ended from Task Manager
/// and Windows takes the wall away, the wallpaper is simply there again (the probe left its last frame behind).
sealed class Desktop
{
    public IntPtr Progman { get; private set; }
    public IntPtr DefView { get; private set; }
    public IntPtr WorkerW { get; private set; }
    public bool Raised { get; private set; }
    public IntPtr Parent => Raised ? Progman : WorkerW;
    /// HIDAMARI_CLASSIC_LAYERED=0 puts plain (not layered) children on the classic desktop, as the probe did.
    public static bool ClassicLayered { get; } = Environment.GetEnvironmentVariable("HIDAMARI_CLASSIC_LAYERED") != "0";
    bool Layered => Raised || ClassicLayered;
    /// The walls show their picture through DirectComposition (Compositor.cs). Only on the raised desktop: there a
    /// child painted with GDI is not shown at all. On the classic desktop GDI works and costs no graphics device.
    public bool Composed => Raised;

    /// Finds (and if needed makes) the layer under the icons. False if there is none to be found.
    public bool Find()
    {
        Progman = FindWindow("Progman", null);
        if (Progman == IntPtr.Zero) { Log.Write("desktop: no Progman window (Explorer not running?)"); return false; }
        bool raisedBefore = (ExStyle(Progman) & WS_EX_NOREDIRECTIONBITMAP) != 0;

        // 0xD, 1 are the values Lively sends; reports say 0, 0 no longer spawns the layer on 24H2.
        SendMessageTimeout(Progman, 0x052C, new IntPtr(0xD), new IntPtr(0x1), SMTO_NORMAL, 1000, out _);
        Locate();
        if (WorkerW == IntPtr.Zero)
        {
            Log.Write("desktop: nothing after 0x052C (0xD, 1), trying (0, 0)");
            SendMessageTimeout(Progman, 0x052C, IntPtr.Zero, IntPtr.Zero, SMTO_NORMAL, 1000, out _);
            Thread.Sleep(200);
            Locate();
        }
        Log.Write($"desktop: {(Raised ? "raised" : "classic")} (Progman layered style before 0x052C: {raisedBefore}), " +
                  $"Progman {Describe(Progman)}, icons {Describe(DefView)}, wallpaper layer {Describe(WorkerW)}, " +
                  $"walls {(Composed ? "layered, shown through DirectComposition" : Layered ? "layered" : "plain")}");
        if (WorkerW == IntPtr.Zero || DefView == IntPtr.Zero)
        {
            Log.Write("desktop: FAILED to find the layer under the icons");
            return false;
        }
        return true;
    }

    void Locate()
    {
        Raised = (ExStyle(Progman) & WS_EX_NOREDIRECTIONBITMAP) != 0;
        DefView = IntPtr.Zero;
        WorkerW = IntPtr.Zero;
        if (Raised)
        {
            DefView = FindWindowEx(Progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            WorkerW = FindWindowEx(Progman, IntPtr.Zero, "WorkerW", null);
            if (DefView != IntPtr.Zero) return;
            Log.Write("desktop: raised style, but the icons are not inside Progman; looking for them as on the classic desktop");
            Raised = false;
        }
        IntPtr defView = IntPtr.Zero, worker = IntPtr.Zero;
        EnumWindows((top, _) =>
        {
            var p = FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (p != IntPtr.Zero)
            {
                defView = p;
                worker = FindWindowEx(IntPtr.Zero, top, "WorkerW", null);   // the WorkerW right after it
            }
            return true;
        }, IntPtr.Zero);
        DefView = defView;
        WorkerW = worker;
    }

    /// Makes `hwnd` a child of the layer under the icons, covering `screen` (pixels, screen coordinates), still hidden.
    public bool Attach(IntPtr hwnd, Rectangle screen)
    {
        var style = Style(hwnd) & ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_VISIBLE) | WS_CHILD;
        SetStyle(hwnd, style);
        var ex = ExStyle(hwnd) & ~WS_EX_APPWINDOW | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        if (Composed)
        {
            // as Microsoft described it to Lively and as the probe did: layered, fully opaque, before SetParent
            // (Lively notes some windows refuse it afterwards). The picture itself is a composition visual on top.
            SetExStyle(hwnd, ex | WS_EX_LAYERED);
            SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
        }
        else if (Layered)
        {
            // layered before SetParent. The picture is handed over whole with UpdateLayeredWindow
            // (WallWindow.Present), fully opaque.
            SetExStyle(hwnd, ex | WS_EX_LAYERED);
        }
        else SetExStyle(hwnd, ex & ~WS_EX_LAYERED);
        if (SetParent(hwnd, Parent) == IntPtr.Zero && GetParent(hwnd) != Parent)
        {
            Log.Write($"desktop: SetParent failed, error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            return false;
        }
        var origin = new POINT { X = screen.Left, Y = screen.Top };
        ScreenToClient(Parent, ref origin);
        SetWindowPos(hwnd, IntPtr.Zero, origin.X, origin.Y, screen.Width, screen.Height, SWP_NOACTIVATE | SWP_NOZORDER);
        return true;
    }

    /// Shows a wall that has its first frame: straight under the icons.
    public void Show(IntPtr hwnd, Rectangle screen)
    {
        bool ok = Raised
            ? SetWindowPos(hwnd, DefView, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW)
            : SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER | SWP_SHOWWINDOW);
        if (Raised) KeepWallpaperLast();
        GetWindowRect(hwnd, out var placed);
        Log.Write($"desktop: wall 0x{(long)hwnd:X} shown under the icons for {screen.Left},{screen.Top} {screen.Width}x{screen.Height}, " +
                  $"parent {Describe(GetParent(hwnd))}, now at {placed}, {(ok ? "ok" : "SetWindowPos failed")}");
    }

    /// On the raised desktop the wallpaper WorkerW must stay the last child of Progman, under the walls.
    void KeepWallpaperLast()
    {
        var last = GetWindow(GetWindow(Progman, GW_CHILD), GW_HWNDLAST);
        if (last == WorkerW) return;
        Log.Write($"desktop: wallpaper layer was not last (last was {Describe(last)}), moving it to the bottom");
        SetWindowPos(WorkerW, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// Whether the walls are still where they were put: Explorer can rebuild its windows (a new wallpaper, a theme,
    /// a restart). Null when all is well, otherwise what is wrong.
    public string? Broken(IEnumerable<IntPtr> walls)
    {
        if (!IsWindow(Progman) || !IsWindow(DefView) || !IsWindow(WorkerW)) return "the desktop's windows are gone";
        if (FindWindow("Progman", null) != Progman) return "a new desktop window";
        foreach (var w in walls)
        {
            if (!IsWindow(w)) return "a wall is gone";
            if (GetParent(w) != Parent) return "a wall lost its place";
        }
        if (Raised)
        {
            // the walls must lie between the icons and the wallpaper
            var order = new List<IntPtr>();
            for (var c = GetWindow(Progman, GW_CHILD); c != IntPtr.Zero && order.Count < 50; c = GetWindow(c, GW_HWNDNEXT)) order.Add(c);
            int icons = order.IndexOf(DefView), paper = order.IndexOf(WorkerW);
            foreach (var w in walls)
            {
                int i = order.IndexOf(w);
                if (i < icons || (paper >= 0 && i > paper)) return $"order changed: {ChildOrder()}";
            }
        }
        else if (IsWindowVisible(WorkerW) == false) return "the wallpaper layer is hidden";
        return null;
    }

    /// On the raised desktop: puts the walls back between the icons and the wallpaper without new windows.
    public void Reorder(IEnumerable<IntPtr> walls)
    {
        if (!Raised) return;
        foreach (var w in walls) SetWindowPos(w, DefView, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        KeepWallpaperLast();
    }

    /// The order of Progman's children, top first (for the journal).
    public string ChildOrder()
    {
        var names = new List<string>();
        for (var c = GetWindow(Progman, GW_CHILD); c != IntPtr.Zero && names.Count < 20; c = GetWindow(c, GW_HWNDNEXT))
            names.Add(Describe(c));
        return string.Join(" > ", names);
    }

    /// After the walls are gone. Layered walls never painted over the wallpaper, so only a repaint is asked for.
    /// Plain children on the classic desktop (as in the probe) did paint over it: the wallpaper is then set again
    /// from the same setting (as Lively's RefreshDesktop does). On the raised desktop nothing is needed.
    public void Restore()
    {
        if (Raised)
        {
            Log.Write("desktop: raised desktop, the wallpaper layer was never covered, nothing to restore");
            return;
        }
        if (IsWindow(WorkerW))
            RedrawWindow(WorkerW, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW | RDW_FRAME);
        if (Layered)
        {
            Log.Write("desktop: layered walls on the classic desktop, the wallpaper layer repainted");
            return;
        }
        var path = new System.Text.StringBuilder(1024);
        SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)path.Capacity, path, 0);
        bool ok = SystemParametersInfoSet(SPI_SETDESKWALLPAPER, 0, path.ToString(), 0);
        Log.Write($"desktop: wallpaper layer repainted, wallpaper \"{path}\" shown again ({(ok ? "ok" : "failed")})");
    }
}
