using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Hidamari;

/// One moment of the wall in both forms: what wall.html's komorebi.render() takes, and what the Direct3D drawing takes
/// (the frame's size is each screen's own).
sealed record WallState(Dictionary<string, object?> Page, WallFrameInput Input);

/// The drawing of the walls: Direct3D (the default) or the web engine of stages 1–3 (WallpaperRenderer on the Mac).
/// The app speaks only to this; which one draws is chosen at start (Settings.Renderer) and falls back to the web engine
/// by itself when Direct3D cannot draw.
interface IWallRenderer : IDisposable
{
    /// "Direct3D" or "web"
    string Name { get; }
    /// What draws, for the journal: the graphics card, or the web engine's WebGL.
    string Describe { get; }
    /// The walls show their picture through DirectComposition on both desktops (the web frames only on the raised one).
    bool ComposesEverywhere { get; }
    /// Can draw the live motion (Animation).
    bool Moves { get; }
    /// Ready to draw; returns how long starting took.
    Task<long> Begin();
    /// One still frame on `wall`. With `png`, also the frame as a PNG data URL (for the checks).
    Task<FrameResult> Draw(WallWindow wall, WallState state, bool png);
    /// After the frames.
    Task Rest();
    /// The own wall's small pictures: JPEG files by name (dapples, blinds, water, window-p, plant-p).
    Task<Dictionary<string, byte[]>> Thumbs(CustomWall custom);
    /// The wall is gone.
    void Forget(WallWindow wall);
}

/// The web engine's drawing, as stages 1–3 had it.
sealed class WebRenderer : IWallRenderer
{
    readonly Engine engine;
    public WebRenderer(Engine engine) { this.engine = engine; }
    public string Name => "web";
    public string Describe => $"web engine, WebGL {(engine.WebGl == "" ? "not started yet" : engine.WebGl)}";
    public bool ComposesEverywhere => false;
    public bool Moves => false;
    public Task<long> Begin() => engine.Begin();
    public Task<FrameResult> Draw(WallWindow wall, WallState state, bool png) => engine.Draw(wall.Screen, state.Page, wall, png);
    public Task Rest() => engine.Rest();
    public async Task<Dictionary<string, byte[]>> Thumbs(CustomWall custom)
    {
        await engine.Begin();
        var pictures = await engine.Thumbs(new Dictionary<string, object?> { ["wall"] = Settings.OwnWall, ["custom"] = custom.ForPage() });
        return pictures.ToDictionary(p => p.Key, p => Convert.FromBase64String(p.Value[(p.Value.IndexOf(',') + 1)..]));
    }
    public void Forget(WallWindow wall) { }
    /// The engine belongs to the app (the card shares its browser process).
    public void Dispose() { }
}

/// The Direct3D drawing (MetalRenderer on the Mac): every wall gets a DirectComposition picture of its frame's size,
/// stretched over the screen as the web engine's canvas was. The frames are drawn on a worker thread, the moving ones
/// on each screen's pacing thread (Animator); all of it goes through D3DWall.Lock.
///
/// When the graphics device is lost (a new driver, the card restarted after a hang, another card), everything is made
/// anew and drawn again; when that keeps failing, `GaveUp` tells the app, which goes on with the web engine.
sealed class D3DRenderer : IWallRenderer
{
    /// Failures in a row (making the device, or a frame) before the app goes on with the web engine.
    public const int FailuresBeforeWeb = 3;

    D3DWall gpu;
    readonly Dictionary<WallWindow, Shown> shown = new();
    readonly object shownLock = new();
    int failuresInARow;
    /// The device was lost and made again this many times (for the journal and the checks).
    public int Recovered { get; private set; }
    /// Called (on any thread) when Direct3D has failed `FailuresBeforeWeb` times in a row.
    public event Action<string>? GaveUp;
    /// For the checks: the next frame acts as if the device were lost (HIDAMARI check command "loseDevice").
    public int SimulateLoss;

    D3DRenderer(D3DWall gpu) { this.gpu = gpu; }

    /// Direct3D, or null with the reason when this machine cannot draw it.
    public static D3DRenderer? TryMake(out string why)
    {
        try
        {
            if (Environment.GetEnvironmentVariable("HIDAMARI_D3D") == "fail") throw new InvalidOperationException("asked to fail by HIDAMARI_D3D=fail (the checks)");
            var sw = Stopwatch.StartNew();
            var gpu = new D3DWall();
            why = "";
            Log.Write($"d3d: {gpu.Kind}, level {gpu.Level}, shader ready in {gpu.ShaderMs} ms, device in {sw.ElapsedMilliseconds} ms");
            return new D3DRenderer(gpu);
        }
        catch (Exception e)
        {
            why = $"{e.GetType().Name}: {e.Message}";
            return null;
        }
    }

    public string Name => "Direct3D";
    public string Describe => $"Direct3D, {gpu.Kind}, level {gpu.Level}";
    public bool ComposesEverywhere => true;
    public bool Moves => true;
    public D3DWall Gpu => gpu;
    public Task<long> Begin() => Task.FromResult(0L);
    public Task Rest() => Task.CompletedTask;

    /// What wall.html's canvas would be for this screen: its size in points times the scale, at most 1.5.
    public static (int w, int h) FrameSize(ScreenInfo s)
    {
        static int R(double x) => (int)Math.Floor(x + 0.5);   // JavaScript's Math.round
        double scale = Math.Min(s.Scale, 1.5);
        int cw = R(s.Bounds.Width / s.Scale), ch = R(s.Bounds.Height / s.Scale);
        return (Math.Max(1, R(cw * scale)), Math.Max(1, R(ch * scale)));
    }

    /// What one screen's wall holds: its composition target and picture (made on the app's thread, filled anywhere).
    sealed class Shown
    {
        public IDCompositionTarget? Target;
        public IDCompositionVisual? Visual;
        public IDCompositionSurface? Surface;
        public int W, H, WindowW, WindowH;
        public D3DWall? Of;
        public void Drop()
        {
            Surface?.Dispose(); Surface = null;
            Visual?.Dispose(); Visual = null;
            Target?.Dispose(); Target = null;
            Of = null;
        }
    }

    /// Makes the wall's composition target, on the app's thread (the thread of its window).
    void Prepare(WallWindow wall)
    {
        lock (shownLock)
        {
            if (!shown.TryGetValue(wall, out var s)) shown[wall] = s = new Shown();
            lock (gpu.Lock)
            {
                if (s.Of != gpu) s.Drop();
                if (s.Target != null) return;
                gpu.Composition.CreateTargetForHwnd(wall.Handle, true, out s.Target).CheckError();
                gpu.Composition.CreateVisual(out s.Visual).CheckError();
                s.Target!.SetRoot(s.Visual);
                s.Of = gpu;
                GetWindowSize(wall.Handle, out s.WindowW, out s.WindowH);
            }
        }
    }

    static void GetWindowSize(IntPtr hwnd, out int w, out int h)
    {
        Native.GetWindowRect(hwnd, out var r);
        w = r.Width; h = r.Height;
    }

    public async Task<FrameResult> Draw(WallWindow wall, WallState state, bool png)
    {
        var sw = Stopwatch.StartNew();
        var (w, h) = FrameSize(wall.Screen);
        var input = state.Input with { Width = w, Height = h };
        var u = WallLighting.Uniforms(input);
        long sizeMs = 0, drawMs = 0;
        string? data = null;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (!wall.Composed) throw new InvalidOperationException("the wall is not shown through DirectComposition");
                Prepare(wall);
                sizeMs = sw.ElapsedMilliseconds;
                sw.Restart();
                byte[]? pixels = await Task.Run(() => { var px = Present(wall, u, null, 1, png); gpu.Finish(); return px; });
                drawMs = sw.ElapsedMilliseconds;
                if (pixels != null) data = "data:image/png;base64," + Convert.ToBase64String(Png(pixels, w, h));
                failuresInARow = 0;
                break;
            }
            catch (TargetGone) when (attempt < FailuresBeforeWeb + 2) { }   // made again by Prepare
            catch (Exception e) when (attempt < FailuresBeforeWeb + 2)
            {
                if (!Lost(e, "a still frame")) throw;
            }
        }
        return new FrameResult(w, h, u.SunDegrees, "direct3d", sizeMs, drawMs, 0, data);
    }

    /// Draws `u` (with `from` mixed in, a crossfade) straight into the wall's picture and shows it. With `readBack`,
    /// also returns the frame's pixels (rows from the top, blue, green, red, 255). Any thread.
    public byte[]? Present(WallWindow wall, WallUniformValues u, WallUniformValues? from, double mix, bool readBack)
    {
        Shown? s;
        lock (shownLock) { if (!shown.TryGetValue(wall, out s)) return null; }
        var g = gpu;
        lock (g.Lock)
        {
            try { return PresentOn(g, s, wall, u, from, mix, readBack); }
            // another thread found the device lost and made it anew while this frame waited: not a failure of its own
            catch (Exception e) when (e is not TargetGone && g != gpu) { throw new TargetGone(); }
        }
    }

    byte[]? PresentOn(D3DWall g, Shown s, WallWindow wall, WallUniformValues u, WallUniformValues? from, double mix, bool readBack)
    {
        {
            if (s.Of != g || s.Visual == null) throw new TargetGone();
            if (SimulateLoss > 0) { SimulateLoss--; throw new COMException("simulated loss of the device (the checks)", unchecked((int)0x887A0005)); }
            int w = (int)u.Res[0], h = (int)u.Res[1];
            var names = D3DWall.PictureNames(u);
            if (from != null && mix < 1) names.UnionWith(D3DWall.PictureNames(from));
            g.SetNeeded(names, wall);
            if (s.Surface == null || s.W != w || s.H != h)
            {
                s.Surface?.Dispose();
                g.Composition.CreateSurface((uint)w, (uint)h, Format.B8G8R8A8_UNorm, AlphaMode.Ignore, out s.Surface).CheckError();
                s.W = w; s.H = h;
                s.Visual!.SetContent(s.Surface);
                // the frame is at most 1.5 pixels per point (wall.html), so on a 200 % screen it is smaller than the
                // screen and is stretched, as the web engine itself would
                var scale = System.Numerics.Matrix3x2.CreateScale((float)s.WindowW / w, (float)s.WindowH / h);
                s.Visual.SetTransform(ref scale);
                s.Visual.SetBitmapInterpolationMode(BitmapInterpolationMode.Linear);
            }
            var texture = s.Surface!.BeginDraw<ID3D11Texture2D>(null, out Int2 offset);
            byte[]? pixels;
            try { pixels = g.Frame(u, from, mix, texture, offset.X, offset.Y, readBack); }
            finally
            {
                texture.Dispose();
                s.Surface.EndDraw().CheckError();
            }
            g.Composition.Commit().CheckError();
            failuresInARow = 0;
            return pixels;
        }
    }

    /// A frame failed. True when it was the device going away and the drawing was made anew (try again); false when
    /// it was something else. After `FailuresBeforeWeb` in a row the app is told to go on with the web engine.
    public bool Lost(Exception e, string doing)
    {
        int hr = e.HResult;
        int reason = gpu.RemovedReason;
        bool lost = D3DWall.IsLost(hr) || D3DWall.IsLost(reason);
        if (!lost) return false;
        lock (shownLock)
        {
            failuresInARow++;
            Log.Write($"d3d: the graphics device was lost during {doing} (0x{hr:X8}, reason 0x{reason:X8}), {failuresInARow} in a row; making it anew");
            if (failuresInARow >= FailuresBeforeWeb)
            {
                Log.Write($"d3d: {failuresInARow} failures in a row, going on with the web engine");
                GaveUp?.Invoke($"the graphics device was lost {failuresInARow} times in a row");
                throw new InvalidOperationException("Direct3D gave up", e);
            }
            var old = gpu;
            try
            {
                var fresh = new D3DWall();
                lock (old.Lock) foreach (var s in shown.Values) s.Drop();
                gpu = fresh;
                Recovered++;
                Log.Write($"d3d: made anew ({fresh.Kind}, level {fresh.Level}), {Recovered} time(s) since start");
            }
            catch (Exception again)
            {
                Log.Write($"d3d: could not make the device anew ({again.Message})");
                failuresInARow++;
                if (failuresInARow >= FailuresBeforeWeb)
                {
                    GaveUp?.Invoke($"the graphics device could not be made anew: {again.Message}");
                    throw new InvalidOperationException("Direct3D gave up", again);
                }
                return true;
            }
            try { old.Dispose(); } catch { }
            DeviceRemade?.Invoke();
            return true;
        }
    }

    /// The wall's picture belonged to a device that was made anew: its target is made again on the app's thread.
    public sealed class TargetGone : Exception { public TargetGone() : base("the wall's picture belongs to a lost device") { } }

    /// The device was made anew: the walls' targets must be made again on the app's thread (Animator listens).
    public event Action? DeviceRemade;

    /// Makes the wall's target again after a new device (app's thread).
    public void Again(WallWindow wall) => Prepare(wall);

    public void Forget(WallWindow wall)
    {
        lock (shownLock)
        {
            if (shown.Remove(wall, out var s)) lock (gpu.Lock) s.Drop();
        }
        gpu.Forget(wall);
    }

    // ---------- the own wall's small pictures (MetalWall.thumbnails on the Mac) ----------

    // Berlin, 20 April 2026, 11:00, clear; a 1920×1243 frame drawn at 1.5, then made small (THUMB in wall.html)
    const double ThumbMoment = 1_776_675_600_000.0, ThumbOffset = 120;
    const int ThumbFrameW = 1920, ThumbFrameH = 1243, ThumbWidth = 240;
    const double ThumbScale = 1.5;
    static readonly Dictionary<int, double[]> ThumbCrops = new() { [0] = new double[] { 40, 560, 600, 480 }, [2] = new double[] { 140, 330, 560, 448 }, [5] = new double[] { 0, 780, 580, 463 } };
    const int PlantW = 120, PlantH = 96;

    public Task<Dictionary<string, byte[]>> Thumbs(CustomWall custom) => Task.Run(() =>
    {
        int w = (int)Math.Round(ThumbFrameW * ThumbScale), h = (int)Math.Round(ThumbFrameH * ThumbScale);
        double minutes = WallFrameInput.MinutesOf(ThumbMoment, ThumbOffset);
        Bitmap Shot(int scene, int plant)
        {
            var f = new WallFrameInput { Ms = ThumbMoment, Minutes = minutes, Lat = 52.52, Lng = 13.405, Scene = scene, Wall = Settings.OwnWall,
                Custom = custom, WinPlant = plant, Cloud = 0, Weather = 0, Width = w, Height = h };
            return Bitmap(gpu.Image(WallLighting.Uniforms(f)), w, h);
        }
        byte[] Small(Bitmap b)
        {
            int sh = (int)Math.Round(ThumbWidth * (double)b.Height / b.Width);
            using var s = Scaled(b, ThumbWidth, sh, new RectangleF(0, 0, b.Width, b.Height));
            return Jpeg(s);
        }
        var o = new Dictionary<string, byte[]>();
        foreach (var p in new[] { 0, 2, 5 })
        {
            using var frame = Shot(1, p);
            o[$"window-{p}"] = Small(frame);
            // the frame made 1920 wide first, then the plant's part of it made small, as wall.html does
            int fullH = (int)Math.Round(ThumbFrameW * (double)h / w);
            using var full = Scaled(frame, ThumbFrameW, fullH, new RectangleF(0, 0, frame.Width, frame.Height));
            var c = ThumbCrops[p];
            using var plant = Scaled(full, PlantW, PlantH, new RectangleF((float)c[0], (float)c[1], (float)c[2], (float)c[3]));
            o[$"plant-{p}"] = Jpeg(plant);
        }
        using (var b = Shot(2, 2)) o["blinds"] = Small(b);
        using (var b = Shot(0, 2)) o["dapples"] = Small(b);
        using (var b = Shot(3, 2)) o["water"] = Small(b);
        // Mac update 1.4; the Tree still
        using (var b = Shot(4, 2)) o["prism"] = Small(b);
        using (var b = Shot(6, 2)) o["moon-gate"] = Small(b);
        using (var b = Shot(7, 2)) o["tree"] = Small(b);
        using (var b = Shot(8, 2)) o["glass-blocks"] = Small(b);
        return o;
    });

    // ---------- pictures in memory ----------

    public static Bitmap Bitmap(byte[] bgra, int w, int h)
    {
        var b = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        var d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        for (int y = 0; y < h; y++) Marshal.Copy(bgra, y * w * 4, d.Scan0 + y * d.Stride, w * 4);
        b.UnlockBits(d);
        return b;
    }

    static Bitmap Scaled(Bitmap src, int w, int h, RectangleF from)
    {
        var o = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        using var g = Graphics.FromImage(o);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
        using var wrap = new ImageAttributes();
        wrap.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);
        g.DrawImage(src, new Rectangle(0, 0, w, h), from.X, from.Y, from.Width, from.Height, GraphicsUnit.Pixel, wrap);
        return o;
    }

    static byte[] Jpeg(Bitmap b)
    {
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var p = new EncoderParameters(1);
        p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);
        using var m = new MemoryStream();
        b.Save(m, codec, p);
        return m.ToArray();
    }

    public static byte[] Png(byte[] bgra, int w, int h)
    {
        using var b = Bitmap(bgra, w, h);
        using var m = new MemoryStream();
        b.Save(m, ImageFormat.Png);
        return m.ToArray();
    }

    public void Dispose()
    {
        lock (shownLock)
        {
            lock (gpu.Lock) foreach (var s in shown.Values) s.Drop();
            shown.Clear();
        }
        try { gpu.Composition.Commit(); } catch { }
        gpu.Dispose();
    }
}
