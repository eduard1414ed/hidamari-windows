using static Hidamari.Native;

namespace Hidamari;

/// One screen's wall: a plain window under the desktop icons that shows a finished picture. No web engine lives in
/// it; the frames are drawn elsewhere (Engine) and copied in. The picture is handed to Windows whole: through
/// DirectComposition on the raised desktop (Compositor), with UpdateLayeredWindow on the classic one.
sealed class WallWindow : NativeWindow, IDisposable
{
    public ScreenInfo Screen { get; }
    /// The wall shows an older moment than it should (a frame was skipped while it could not be seen).
    public bool Stale { get; set; } = true;
    public bool Shown { get; set; }
    /// Called when Windows destroyed the window without being asked (Explorer closed its parent).
    public event Action<WallWindow>? Lost;

    IntPtr dib, bits;
    int width, height;
    bool disposing;
    Compositor? compositor;

    /// The picture goes through DirectComposition (the raised desktop, Desktop.Composed) instead of GDI.
    public bool Composed { get; set; }

    public WallWindow(ScreenInfo screen)
    {
        Screen = screen;
        CreateHandle(new CreateParams
        {
            Caption = "Hidamari wall",
            Style = unchecked((int)(WS_POPUP | WS_CLIPSIBLINGS)),
            ExStyle = (int)(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE),
            X = -32000, Y = -32000, Width = screen.Bounds.Width, Height = screen.Bounds.Height,
        });
    }

    public int FrameWidth => width;
    public int FrameHeight => height;

    /// The picture's memory, top row first, 4 bytes a pixel (blue, green, red, unused), made the right size.
    public IntPtr Pixels(int w, int h)
    {
        if (w == width && h == height && bits != IntPtr.Zero) return bits;
        if (dib != IntPtr.Zero) DeleteObject(dib);
        var bmi = new BITMAPINFOHEADER
        {
            biSize = System.Runtime.InteropServices.Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32,
        };
        dib = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero) throw new OutOfMemoryException($"no picture of {w}x{h} for {Screen.Short}");
        width = w; height = h;
        return bits;
    }

    /// The picture changed: show it. A layered wall (Desktop.Attach) gets the whole picture handed to Windows, which
    /// keeps it and draws it by itself; a plain one (HIDAMARI_CLASSIC_LAYERED=0) paints it when asked.
    public void Present()
    {
        GdiFlush();
        if (Composed)
        {
            GetWindowRect(Handle, out var wr);
            compositor ??= new Compositor(Handle);
            compositor.Show(bits, width, height, wr.Width, wr.Height);
            return;
        }
        if ((ExStyle(Handle) & WS_EX_LAYERED) == 0)
        {
            InvalidateRect(Handle, IntPtr.Zero, false);
            UpdateWindow(Handle);
            return;
        }
        GetWindowRect(Handle, out var r);
        var mem = CreateCompatibleDC(IntPtr.Zero);
        IntPtr old, scaled = IntPtr.Zero;
        if (r.Width == width && r.Height == height) old = SelectObject(mem, dib);
        else
        {
            // the page draws at most 1.5 pixels per point (wall.html), so on a 200 % screen the frame is smaller than
            // the screen and is stretched, as the web engine itself would
            var bmi = new BITMAPINFOHEADER { biSize = System.Runtime.InteropServices.Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = r.Width, biHeight = -r.Height, biPlanes = 1, biBitCount = 32 };
            scaled = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out var sbits, IntPtr.Zero, 0);
            old = SelectObject(mem, scaled);
            var src = CreateCompatibleDC(IntPtr.Zero);
            var sold = SelectObject(src, dib);
            SetStretchBltMode(mem, HALFTONE);
            SetBrushOrgEx(mem, 0, 0, IntPtr.Zero);
            StretchBlt(mem, 0, 0, r.Width, r.Height, src, 0, 0, width, height, SRCCOPY);
            SelectObject(src, sold);
            DeleteDC(src);
            // HALFTONE leaves the unused byte at 0; the layered window reads it as transparency
            unsafe { uint* p = (uint*)sbits; for (long i = 0, n = (long)r.Width * r.Height; i < n; i++) p[i] |= 0xFF000000u; }
        }
        var size = new SIZE { cx = r.Width, cy = r.Height };
        var origin = new POINT();
        var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
        if (!UpdateLayeredWindow(Handle, IntPtr.Zero, IntPtr.Zero, ref size, mem, ref origin, 0, ref blend, ULW_ALPHA))
            Log.Write($"wall: UpdateLayeredWindow failed on {Screen.Short}, error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        SelectObject(mem, old);
        DeleteDC(mem);
        if (scaled != IntPtr.Zero) DeleteObject(scaled);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_ERASEBKGND:
                m.Result = new IntPtr(1);
                return;
            case WM_PAINT:
                Paint();
                m.Result = IntPtr.Zero;
                return;
            case WM_MOUSEACTIVATE:
                m.Result = new IntPtr(MA_NOACTIVATE);
                return;
            case WM_NCDESTROY:
                base.WndProc(ref m);
                if (!disposing) Lost?.Invoke(this);
                return;
        }
        base.WndProc(ref m);
    }

    void Paint()
    {
        var hdc = BeginPaint(Handle, out var ps);
        try
        {
            if (dib == IntPtr.Zero || Composed) return;
            GetWindowRect(Handle, out var r);
            var mem = CreateCompatibleDC(hdc);
            var old = SelectObject(mem, dib);
            if (r.Width == width && r.Height == height)
                BitBlt(hdc, 0, 0, width, height, mem, 0, 0, SRCCOPY);
            else
            {
                // the page draws at most 1.5 pixels per point (wall.html), so on a 200 % screen the frame is
                // smaller than the screen and is stretched, as the web engine itself would
                SetStretchBltMode(hdc, HALFTONE);
                SetBrushOrgEx(hdc, 0, 0, IntPtr.Zero);
                StretchBlt(hdc, 0, 0, r.Width, r.Height, mem, 0, 0, width, height, SRCCOPY);
            }
            SelectObject(mem, old);
            DeleteDC(mem);
        }
        finally { EndPaint(Handle, ref ps); }
    }

    public void Dispose()
    {
        disposing = true;
        compositor?.Dispose(); compositor = null;
        if (Handle != IntPtr.Zero) DestroyHandle();
        if (dib != IntPtr.Zero) { DeleteObject(dib); dib = IntPtr.Zero; bits = IntPtr.Zero; }
    }
}
