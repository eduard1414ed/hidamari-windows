using System.Runtime.InteropServices;
using static Hidamari.Native;

namespace Hidamari;

/// Presses anywhere on the screen, seen on a thread of their own while the card is open (Card.cs: a click outside
/// closes it; two presses in Windows' double-click time are a double click). A low-level mouse hook holds every mouse
/// event of the whole system until it answers, so it must never wait for the card window's thread: that thread hands
/// presses to the card's page and, on a slow machine, can be busy for seconds. Here the hook only looks and returns;
/// what it saw goes on by the callback, which must be quick and post the rest to the card's thread.
sealed class MouseWatch : IDisposable
{
    const int WM_WATCH = WM_APP + 41, WM_QUIT_ = 0x0012, PM_NOREMOVE = 0;
    readonly LowLevelMouseProc proc;
    readonly Action<int, POINT, uint> pressed;
    readonly ManualResetEventSlim ready = new();
    uint threadId;
    IntPtr hook;

    /// pressed(message, point on the screen in pixels, the press's time in Windows' milliseconds)
    public MouseWatch(Action<int, POINT, uint> pressed)
    {
        this.pressed = pressed;
        proc = Proc;
        new Thread(Loop) { IsBackground = true, Name = "Hidamari mouse watch" }.Start();
        ready.Wait();
    }

    public void Watch(bool on) => PostThreadMessage(threadId, WM_WATCH, (IntPtr)(on ? 1 : 0), IntPtr.Zero);

    void Loop()
    {
        threadId = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);   // makes the thread's message queue
        ready.Set();
        while (GetMessage(out var m, IntPtr.Zero, 0, 0) > 0)
        {
            if (m.hwnd == IntPtr.Zero && m.message == WM_WATCH)
            {
                bool on = m.wParam != IntPtr.Zero;
                if (on && hook == IntPtr.Zero)
                {
                    hook = SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(null), 0);
                    if (hook == IntPtr.Zero) Log.Write($"card: the mouse watch could not start (error {Marshal.GetLastWin32Error()})");
                }
                else if (!on && hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
                continue;
            }
            TranslateMessage(ref m);
            DispatchMessage(ref m);
        }
        if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
    }

    IntPtr Proc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = (int)(long)wParam;
            if (msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                try { pressed(msg, info.pt, info.time); } catch { }
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    public void Dispose() => PostThreadMessage(threadId, WM_QUIT_, IntPtr.Zero, IntPtr.Zero);

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; public uint lPrivate; }
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll", SetLastError = true)] static extern bool PostThreadMessage(uint thread, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
}
