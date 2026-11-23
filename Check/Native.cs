using System.Runtime.InteropServices;
using System.Text;

/// The Windows calls the check needs: the screen, the mouse and keys, windows, processes, display settings.
static class Native
{
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")] public static extern IntPtr SendMessageTimeoutText(IntPtr h, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    // the union's largest member is MOUSEINPUT on 64-bit Windows, so the struct has the right size with it alone
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public MOUSEINPUT mi; }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);
    // a key of the keyboard in the same 40-byte INPUT (type, then the union at 8)
    [StructLayout(LayoutKind.Explicit, Size = 40)] struct KEYINPUT
    {
        [FieldOffset(0)] public uint type; [FieldOffset(8)] public ushort vk; [FieldOffset(10)] public ushort scan;
        [FieldOffset(12)] public uint flags; [FieldOffset(16)] public uint time; [FieldOffset(24)] public IntPtr extra;
    }
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SendInput")] static extern uint SendKeys(uint count, KEYINPUT[] inputs, int size);

    /// Types `text` letter by letter as a keyboard would (any language: the letters themselves, not keys), 80 ms apart.
    public static void Type(string text)
    {
        foreach (var ch in text)
        {
            var e = new[] { new KEYINPUT { type = 1, scan = ch, flags = 0x4 }, new KEYINPUT { type = 1, scan = ch, flags = 0x4 | 0x2 } };
            SendKeys(2, e, 40);
            Thread.Sleep(80);
        }
    }
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool SystemParametersInfo(uint action, uint param, StringBuilder value, uint winIni);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SystemParametersInfoW")] public static extern bool SystemParametersInfoSet(uint action, uint param, string value, uint winIni);
    [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("shell32.dll")] public static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplaySettings(string? device, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int ChangeDisplaySettingsEx(string? device, ref DEVMODE dm, IntPtr hwnd, uint flags, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")] public static extern int ChangeDisplaySettingsExReset(string? device, IntPtr dm, IntPtr hwnd, uint flags, IntPtr param);
    [DllImport("user32.dll")] public static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] public static extern int QueryDisplayConfig(uint flags, ref uint paths, IntPtr pathArray, ref uint modes, IntPtr modeArray, IntPtr topology);
    [DllImport("user32.dll")] public static extern int DisplayConfigGetDeviceInfo(IntPtr packet);
    [DllImport("user32.dll")] public static extern int DisplayConfigSetDeviceInfo(IntPtr packet);

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; public Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom); }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID; public IntPtr th32DefaultHeapID; public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage, uEdge; public RECT rc; public IntPtr lParam; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    public static IntPtr RealWindowFromPoint(Point p) => WindowFromPoint(new POINT { X = p.X, Y = p.Y });

    /// The processor a Windows program is built for (from its PE header): x64, arm64, x86.
    public static string MachineOf(string exe)
    {
        try
        {
            using var f = File.OpenRead(exe);
            using var r = new BinaryReader(f);
            f.Seek(0x3C, SeekOrigin.Begin);
            f.Seek(r.ReadInt32() + 4, SeekOrigin.Begin);
            return r.ReadUInt16() switch { 0x8664 => "x64", 0xAA64 => "arm64", 0x14C => "x86", var m => $"0x{m:X4}" };
        }
        catch (Exception e) { return $"unreadable ({e.Message})"; }
    }

    public static string ClassOf(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
    public static string TitleOf(IntPtr h) { var sb = new StringBuilder(256); GetWindowText(h, sb, 256); return sb.ToString(); }
    public static string Describe(IntPtr h)
    {
        var t = TitleOf(h);
        return $"{ClassOf(h)}{(t.Length > 0 ? $" \"{t}\"" : "")}{(IsWindowVisible(h) ? "" : " hidden")}";
    }

    public static void Click(Point p, bool right = false)
    {
        SetCursorPos(p.X, p.Y);
        Thread.Sleep(150);
        mouse_event(right ? 0x8u : 0x2u, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(60);
        mouse_event(right ? 0x10u : 0x4u, 0, 0, 0, IntPtr.Zero);
    }

    /// Two presses as a person double-clicks: 60 ms down, 100 ms apart.
    public static void DoubleClick(Point p)
    {
        SetCursorPos(p.X, p.Y);
        Thread.Sleep(150);
        // the four events in one go, as a mouse sends them: on GitHub's machines (no graphics card) the first press
        // starts a frame that takes the processor, and pauses between separate calls stretch past Windows' double-click
        // time; one SendInput puts all four in at once
        var e = new[] { 0x2u, 0x4u, 0x2u, 0x4u }.Select(f => new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = f } }).ToArray();
        SendInput((uint)e.Length, e, Marshal.SizeOf<INPUT>());
    }

    public const byte VK_LWIN = 0x5B, VK_CONTROL = 0x11, VK_F4 = 0x73, VK_ESCAPE = 0x1B, VK_LEFT = 0x25, VK_RIGHT = 0x27,
        VK_UP = 0x26, VK_DOWN = 0x28, VK_RETURN = 0x0D;

    /// The left button pressed at `p` and held (a drag follows with MoveTo, then Up).
    public static void Down(Point p) { SetCursorPos(p.X, p.Y); Thread.Sleep(100); mouse_event(0x2u, 0, 0, 0, IntPtr.Zero); }
    public static void MoveTo(Point p) => SetCursorPos(p.X, p.Y);
    public static void Up() => mouse_event(0x4u, 0, 0, 0, IntPtr.Zero);

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    /// Shown by Windows: visible and not hidden by the compositor (a cloaked window is "visible" but not seen).
    public static bool Seen(IntPtr h) => h != IntPtr.Zero && IsWindowVisible(h) && !(DwmGetWindowAttribute(h, 14, out int c, 4) == 0 && c != 0);
    public static long ExStyle(IntPtr h) => (long)GetWindowLongPtr(h, -20);

    /// Presses the keys together, as a person would: down in order, up in reverse.
    public static void Keys(params byte[] keys)
    {
        foreach (var k in keys) { keybd_event(k, 0, 0, IntPtr.Zero); Thread.Sleep(40); }
        foreach (var k in keys.Reverse()) { keybd_event(k, 0, 2, IntPtr.Zero); Thread.Sleep(40); }
    }

    public static List<(int child, int parent, string exe)> Processes()
    {
        var list = new List<(int, int, string)>();
        var snap = CreateToolhelp32Snapshot(0x2, 0);
        var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
        if (Process32FirstW(snap, ref e))
            do list.Add(((int)e.th32ProcessID, (int)e.th32ParentProcessID, e.szExeFile)); while (Process32NextW(snap, ref e));
        CloseHandle(snap);
        return list;
    }

    // ---------- the scale of the main screen, as the Settings app sets it (no sign-out needed) ----------
    // DisplayConfigGetDeviceInfo / SetDeviceInfo with the types -3 and -4 (not in the documentation; used by the
    // Settings app and by tools such as SetDPI). Scales go in steps relative to the screen's recommended one.
    static readonly int[] Steps = { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 };

    static (uint low, int high, uint id)? MainSource()
    {
        if (GetDisplayConfigBufferSizes(2, out uint np, out uint nm) != 0) return null;   // QDC_ONLY_ACTIVE_PATHS
        var paths = Marshal.AllocHGlobal((int)np * 72);
        var modes = Marshal.AllocHGlobal((int)nm * 64);
        try
        {
            if (QueryDisplayConfig(2, ref np, paths, ref nm, modes, IntPtr.Zero) != 0 || np == 0) return null;
            // DISPLAYCONFIG_PATH_INFO starts with the source: adapter LUID (low, high), then the source id
            return ((uint)Marshal.ReadInt32(paths, 0), Marshal.ReadInt32(paths, 4), (uint)Marshal.ReadInt32(paths, 8));
        }
        finally { Marshal.FreeHGlobal(paths); Marshal.FreeHGlobal(modes); }
    }

    /// The main screen's scale in percent, with the lowest and highest it allows; null when it cannot be read.
    public static (int now, int min, int max)? Scale()
    {
        var src = MainSource();
        if (src == null) return null;
        var p = Marshal.AllocHGlobal(32);
        try
        {
            Marshal.WriteInt32(p, 0, -3); Marshal.WriteInt32(p, 4, 32);
            Marshal.WriteInt32(p, 8, (int)src.Value.low); Marshal.WriteInt32(p, 12, src.Value.high); Marshal.WriteInt32(p, 16, (int)src.Value.id);
            if (DisplayConfigGetDeviceInfo(p) != 0) return null;
            int min = Marshal.ReadInt32(p, 20), cur = Marshal.ReadInt32(p, 24), max = Marshal.ReadInt32(p, 28);
            int rec = Math.Abs(min);
            int At(int rel) => Steps[Math.Clamp(rec + rel, 0, Steps.Length - 1)];
            return (At(cur), At(min), At(max));
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// Sets the main screen's scale in percent. False if Windows refused it.
    public static bool SetScale(int percent)
    {
        var src = MainSource();
        if (src == null) return false;
        var p = Marshal.AllocHGlobal(32);
        try
        {
            Marshal.WriteInt32(p, 0, -3); Marshal.WriteInt32(p, 4, 32);
            Marshal.WriteInt32(p, 8, (int)src.Value.low); Marshal.WriteInt32(p, 12, src.Value.high); Marshal.WriteInt32(p, 16, (int)src.Value.id);
            if (DisplayConfigGetDeviceInfo(p) != 0) return false;
            int rec = Math.Abs(Marshal.ReadInt32(p, 20));
            int idx = Array.IndexOf(Steps, percent);
            if (idx < 0) return false;
            Marshal.WriteInt32(p, 0, -4); Marshal.WriteInt32(p, 4, 24);
            Marshal.WriteInt32(p, 20, idx - rec);
            return DisplayConfigSetDeviceInfo(p) == 0;
        }
        finally { Marshal.FreeHGlobal(p); }
    }
}
