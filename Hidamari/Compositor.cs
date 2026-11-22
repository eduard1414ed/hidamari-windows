using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Hidamari;

/// Shows a wall's picture through DirectComposition, the way the web engine itself shows pages.
///
/// Needed on the raised desktop (Windows 11 24H2 and later): Progman there has no picture of its own
/// (WS_EX_NOREDIRECTIONBITMAP), and a child window painted the ordinary way, with WM_PAINT or UpdateLayeredWindow,
/// is not shown at all; checked on windows-2025, runs 44713224419 and 44714326787. A composition picture is.
/// The probe was visible there because WebView2 shows its pages this way.
sealed class Compositor : IDisposable
{
    // one graphics device and one composition device for all walls
    static ID3D11Device? d3d;
    static ID3D11DeviceContext? context;
    static IDCompositionDevice? device;

    readonly IntPtr hwnd;
    IDCompositionTarget? target;
    IDCompositionVisual? visual;
    IDCompositionSurface? surface;
    int surfaceW, surfaceH;

    public Compositor(IntPtr hwnd) { this.hwnd = hwnd; }

    /// The shared composition device, made when first needed (the card's window uses it too).
    public static IDCompositionDevice Device { get { Start(); return device!; } }

    static void Start()
    {
        if (device != null) return;
        var levels = new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0, FeatureLevel.Level_9_3 };
        var flags = DeviceCreationFlags.BgraSupport;
        var r = D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, levels, out var dev, out var level, out var ctx);
        string kind = "graphics card";
        if (r.Failure || dev == null)
        {
            Log.Write($"compositor: no graphics card device ({r}), using the software one (WARP)");
            D3D11.D3D11CreateDevice(null, DriverType.Warp, flags, levels, out dev, out level, out ctx).CheckError();
            kind = "software (WARP)";
        }
        d3d = dev!;
        context = ctx!;
        using var dxgi = d3d.QueryInterface<IDXGIDevice>();
        device = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgi);
        Log.Write($"compositor: ready, {kind}, level {level}");
    }

    /// Forgets the shared devices (after an error, for example a new graphics driver); the next frame makes new ones.
    static void Reset()
    {
        device?.Dispose(); device = null;
        context?.Dispose(); context = null;
        d3d?.Dispose(); d3d = null;
    }

    /// Shows `bits` (top row first, blue, green, red, unused; `w` x `h`) stretched over the whole window.
    public void Show(IntPtr bits, int w, int h, int windowW, int windowH)
    {
        try { Put(bits, w, h, windowW, windowH); }
        catch (Exception e)
        {
            Log.Write($"compositor: {e.GetType().Name}: {e.Message}; starting the devices again");
            Drop();
            Reset();
            Put(bits, w, h, windowW, windowH);
        }
    }

    void Put(IntPtr bits, int w, int h, int windowW, int windowH)
    {
        Start();
        if (target == null)
        {
            device!.CreateTargetForHwnd(hwnd, true, out target).CheckError();
            device.CreateVisual(out visual).CheckError();
            target!.SetRoot(visual);
        }
        if (surface == null || surfaceW != w || surfaceH != h)
        {
            surface?.Dispose();
            device!.CreateSurface((uint)w, (uint)h, Format.B8G8R8A8_UNorm, AlphaMode.Ignore, out surface).CheckError();
            surfaceW = w; surfaceH = h;
            visual!.SetContent(surface);
        }
        var texture = surface!.BeginDraw<ID3D11Texture2D>(null, out Int2 offset);
        try
        {
            var box = new Box(offset.X, offset.Y, 0, offset.X + w, offset.Y + h, 1);
            context!.UpdateSubresource(new MappedSubresource(bits, (uint)(w * 4), (uint)(w * h * 4)), texture, 0, box);
        }
        finally
        {
            texture.Dispose();
            surface.EndDraw().CheckError();
        }
        // the page draws at most 1.5 pixels per point (wall.html), so on a 200 % screen the frame is smaller than
        // the screen and is stretched, as the web engine itself would
        var scale = System.Numerics.Matrix3x2.CreateScale((float)windowW / w, (float)windowH / h);
        visual!.SetTransform(ref scale);
        visual.SetBitmapInterpolationMode(BitmapInterpolationMode.Linear);
        device!.Commit().CheckError();
    }

    void Drop()
    {
        surface?.Dispose(); surface = null;
        visual?.Dispose(); visual = null;
        target?.Dispose(); target = null;
    }

    public void Dispose()
    {
        Drop();
        try { device?.Commit(); } catch { }
    }
}
