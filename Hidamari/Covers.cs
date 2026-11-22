using System.Diagnostics;
using static Hidamari.Native;

namespace Hidamari;

/// Tells whether a screen's wall can be seen: a game or a video on the whole screen hides it.
/// While a wall waits to be seen again, other windows' moves are listened to, so the wall gets a fresh frame the
/// moment it is uncovered; otherwise nothing is listened to.
sealed class Covers : IDisposable
{
    readonly int myPid = Environment.ProcessId;
    readonly Action changed;
    readonly System.Windows.Forms.Timer settle = new() { Interval = 400 };
    readonly WinEventProc proc;
    readonly List<IntPtr> hooks = new();
    readonly HashSet<string> listeners = new();

    public Covers(Action changed)
    {
        this.changed = changed;
        proc = OnEvent;
        settle.Tick += (_, _) => { settle.Stop(); this.changed(); };
    }

    /// The window that covers all of `screen`, or null when the wall there can be seen.
    public string? CoveredBy(ScreenInfo screen)
    {
        string? found = null;
        var area = screen.Bounds;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h) || Cloaked(h)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == myPid) return true;
            var cls = ClassOf(h);
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            long ex = ExStyle(h);
            // see-through overlays over the whole screen (a graphics card's overlay, screen recorders) hide nothing
            if ((ex & WS_EX_TRANSPARENT) != 0) return true;
            if ((ex & WS_EX_LAYERED) != 0 && (!GetLayeredWindowAttributes(h, out uint key, out byte alpha, out uint flags)
                                              || (flags & LWA_COLORKEY) != 0 || ((flags & LWA_ALPHA) != 0 && alpha < 255))) return true;
            var r = Bounds(h);
            if (r.Left <= area.Left && r.Top <= area.Top && r.Right >= area.Right && r.Bottom >= area.Bottom)
            {
                found = $"{cls} \"{TitleOf(h)}\" ({Exe(pid)})";
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static string Exe(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; } catch { return $"pid {pid}"; }
    }

    /// What hides the whole of `screen`'s wall from the motion's point of view, or null when some of it can be seen:
    /// every opaque window over it together, the taskbar too (the motion only runs where someone can see it). A
    /// maximised window leaves only the taskbar's strip, and the taskbar is not see-through: hidden.
    public string? Hidden(ScreenInfo screen)
    {
        var area = screen.Bounds;
        var left = CreateRectRgn(area.Left, area.Top, area.Right, area.Bottom);
        var names = new List<string>();
        bool all = false;
        try
        {
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h) || IsIconic(h) || Cloaked(h)) return true;
                GetWindowThreadProcessId(h, out uint pid);
                if (pid == myPid) return true;
                var cls = ClassOf(h);
                if (cls is "Progman" or "WorkerW") return true;
                long ex = ExStyle(h);
                if ((ex & WS_EX_TRANSPARENT) != 0) return true;
                if ((ex & WS_EX_LAYERED) != 0 && (!GetLayeredWindowAttributes(h, out uint key, out byte alpha, out uint flags)
                                                  || (flags & LWA_COLORKEY) != 0 || ((flags & LWA_ALPHA) != 0 && alpha < 255))) return true;
                var r = Bounds(h);
                if (r.Right <= area.Left || r.Left >= area.Right || r.Bottom <= area.Top || r.Top >= area.Bottom) return true;
                var cut = CreateRectRgn(r.Left, r.Top, r.Right, r.Bottom);
                int kind = CombineRgn(left, left, cut, RGN_DIFF);
                DeleteObject(cut);
                if (names.Count < 3) names.Add(cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" ? "the taskbar" : $"{cls} \"{TitleOf(h)}\" ({Exe(pid)})");
                if (kind == NULLREGION) { all = true; return false; }
                return true;
            }, IntPtr.Zero);
        }
        finally { DeleteObject(left); }
        return all ? string.Join(" + ", names) : null;
    }

    /// Listens to other windows while someone wants it: a wall waits to be seen again ("stale"), or the motion
    /// would run if its wall could be seen ("motion").
    public void Listen(string who, bool on)
    {
        if (on) listeners.Add(who); else listeners.Remove(who);
        Listen(listeners.Count > 0);
    }

    /// Listens to other windows while `on`.
    void Listen(bool on)
    {
        if (on == hooks.Count > 0) return;
        if (on)
        {
            uint flags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;
            hooks.Add(SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, proc, 0, 0, flags));
            hooks.Add(SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, proc, 0, 0, flags));
            hooks.Add(SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE, IntPtr.Zero, proc, 0, 0, flags));
            hooks.Add(SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, proc, 0, 0, flags));
            hooks.Add(SetWinEventHook(0x8017, EVENT_OBJECT_UNCLOAKED, IntPtr.Zero, proc, 0, 0, flags));
        }
        else
        {
            foreach (var h in hooks) if (h != IntPtr.Zero) UnhookWinEvent(h);
            hooks.Clear();
            settle.Stop();
        }
    }

    void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return;   // windows only, not their parts
        if (ev != EVENT_SYSTEM_FOREGROUND && GetAncestor(hwnd, GA_ROOT) != hwnd) return;
        settle.Stop();
        settle.Start();
    }

    public void Dispose() { listeners.Clear(); Listen(false); }
}
