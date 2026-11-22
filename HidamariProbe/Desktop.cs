using static HidamariProbe.Native;

namespace HidamariProbe;

/// Puts windows under the desktop icons. Windows has no official way to do this; this is the way the living
/// open projects do it (Lively Wallpaper, src/Lively/Lively/Core/WinDesktopCore.cs), in its two forms:
///
/// Classic desktop (Windows 10, Windows 11 before the change). Message 0x052C makes Progman split the icons
/// into their own top-level WorkerW and create a second WorkerW right behind it, which paints the wallpaper:
///     WorkerW            ← holds SHELLDLL_DefView (the icons)
///     WorkerW            ← the wallpaper: our windows become its children
///     Progman
///
/// Raised desktop (Windows 11 24H2 and later, also Windows Server 2025). Progman is drawn with
/// WS_EX_NOREDIRECTIONBITMAP and everything lives inside it. As Microsoft described it to Lively:
/// "create its own WS_EX_LAYERED child HWND that is z-ordered under the DefView window but above the
/// WorkerW window", with SetLayeredWindowAttributes(alpha 255).
///     Progman
///       SHELLDLL_DefView ← the icons, transparent around them
///       our windows      ← layered children of Progman
///       WorkerW          ← the wallpaper, last child
///
/// Which form a machine has is read from Progman's style every time, never from the version number.
sealed class Desktop
{
    public IntPtr Progman { get; private set; }
    public IntPtr DefView { get; private set; }
    public IntPtr WorkerW { get; private set; }
    public bool Raised { get; private set; }
    public IntPtr Parent => Raised ? Progman : WorkerW;

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
                  $"Progman {Describe(Progman)}, icons {Describe(DefView)}, wallpaper layer {Describe(WorkerW)}");
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
            // should not happen, but if the icons are elsewhere the classic search below finds them
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

    /// Makes `hwnd` a child of the layer under the icons, covering `screen` (pixels, screen coordinates).
    public bool Attach(IntPtr hwnd, Rectangle screen)
    {
        var style = Style(hwnd) & ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU) | WS_CHILD;
        SetStyle(hwnd, style);
        var ex = ExStyle(hwnd) & ~WS_EX_APPWINDOW | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        if (Raised)
        {
            // layered before SetParent, as Lively notes some windows refuse it afterwards
            SetExStyle(hwnd, ex | WS_EX_LAYERED);
            SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
        }
        else
        {
            SetExStyle(hwnd, ex);
        }
        if (SetParent(hwnd, Parent) == IntPtr.Zero && GetParent(hwnd) != Parent)
        {
            Log.Write($"desktop: SetParent failed, error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            return false;
        }
        var origin = new POINT { X = screen.Left, Y = screen.Top };
        ScreenToClient(Parent, ref origin);
        bool ok = Raised
            // straight under the icons, so above the wallpaper WorkerW
            ? SetWindowPos(hwnd, DefView, origin.X, origin.Y, screen.Width, screen.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW)
            : SetWindowPos(hwnd, IntPtr.Zero, origin.X, origin.Y, screen.Width, screen.Height, SWP_NOACTIVATE | SWP_NOZORDER | SWP_SHOWWINDOW);
        if (Raised) KeepWallpaperLast();
        GetWindowRect(hwnd, out var placed);
        Log.Write($"desktop: window 0x{(long)hwnd:X} under the icons on {screen.Left},{screen.Top} {screen.Width}x{screen.Height}, " +
                  $"parent {Describe(GetParent(hwnd))}, now at {placed}, SetWindowPos {(ok ? "ok" : "failed")}");
        return true;
    }

    /// On the raised desktop the wallpaper WorkerW must stay the last child of Progman, under our windows.
    void KeepWallpaperLast()
    {
        var last = GetWindow(GetWindow(Progman, GW_CHILD), GW_HWNDLAST);
        if (last == WorkerW) return;
        Log.Write($"desktop: wallpaper layer was not last (last was {Describe(last)}), moving it to the bottom");
        SetWindowPos(WorkerW, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// The order of Progman's children, top first (for the journal).
    public string ChildOrder()
    {
        var names = new List<string>();
        for (var c = GetWindow(Progman, GW_CHILD); c != IntPtr.Zero && names.Count < 20; c = GetWindow(c, GW_HWNDNEXT))
            names.Add(Describe(c));
        return string.Join(" > ", names);
    }

    /// After our windows are gone. On the classic desktop the wallpaper WorkerW does not always repaint by itself,
    /// so it is asked to, and then the wallpaper is set again from the same setting (as Lively's RefreshDesktop does).
    /// On the raised desktop nothing is needed: the WorkerW below was drawing the wallpaper all along, and
    /// setting the wallpaper there would destroy it.
    public void Restore()
    {
        if (Raised)
        {
            Log.Write("desktop: raised desktop, the wallpaper layer was never covered, nothing to restore");
            return;
        }
        if (IsWindow(WorkerW))
            RedrawWindow(WorkerW, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW | RDW_FRAME);
        // the same picture again, without writing anything to the settings (Lively passes null, which the
        // documentation describes as "revert to the default wallpaper", so the current path is passed instead)
        var path = new System.Text.StringBuilder(1024);
        SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)path.Capacity, path, 0);
        bool ok = SystemParametersInfoSet(SPI_SETDESKWALLPAPER, 0, path.ToString(), 0);
        Log.Write($"desktop: wallpaper layer repainted, wallpaper \"{path}\" shown again ({(ok ? "ok" : "failed")})");
    }
}
