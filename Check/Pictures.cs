using System.Drawing.Imaging;
using System.Runtime.InteropServices;

/// Pictures of the screen and how much two pictures differ.
static class Pictures
{
    public static string OutDir = "";

    /// The whole screen (all screens, from the virtual screen's corner), saved as `name` when given.
    public static Bitmap Shot(string? name)
    {
        var v = SystemInformation.VirtualScreen;
        var bmp = new Bitmap(v.Width, v.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            // SRCCOPY | CAPTUREBLT: layered windows included (Graphics.CopyFromScreen refuses the combination)
            var dest = g.GetHdc();
            var src = Native.GetDC(IntPtr.Zero);
            Native.BitBlt(dest, 0, 0, v.Width, v.Height, src, v.X, v.Y, 0x00CC0020 | 0x40000000);
            Native.ReleaseDC(IntPtr.Zero, src);
            g.ReleaseHdc(dest);
        }
        if (name != null)
        {
            bmp.Save(Path.Combine(OutDir, name), ImageFormat.Png);
            Check.Say($"screen saved: {name}");
        }
        return bmp;
    }

    /// A part of a picture (screen coordinates), saved as `name`.
    public static void Crop(Bitmap shot, Rectangle area, string name)
    {
        var v = SystemInformation.VirtualScreen;
        area.Intersect(v);
        var r = new Rectangle(area.X - v.X, area.Y - v.Y, area.Width, area.Height);
        r.Intersect(new Rectangle(0, 0, shot.Width, shot.Height));
        if (r.Width <= 0 || r.Height <= 0) return;
        using var part = shot.Clone(r, PixelFormat.Format32bppArgb);
        part.Save(Path.Combine(OutDir, name), ImageFormat.Png);
    }

    /// A picture file read without keeping it open (the app writes it again with every frame).
    public static Bitmap? Load(string path)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var bytes = File.ReadAllBytes(path);
                using var ms = new MemoryStream(bytes);
                using var b = new Bitmap(ms);
                return new Bitmap(b);
            }
            catch { Thread.Sleep(200); }
        }
        return null;
    }

    public record struct Stats(double Mean, double[] Hist, long Pixels)
    {
        /// Share of pixels whose largest channel difference is at most `d`.
        public double Within(int d) => Pixels == 0 ? 0 : Hist.Take(d + 1).Sum() / Pixels;
        public bool Same => Pixels > 0 && Mean <= 1.5 && Within(8) >= 0.99;
        public override string ToString() => $"mean {Mean:F2}/255, within 3: {100 * Within(3):F2} %, within 8: {100 * Within(8):F2} %, pixels {Pixels}";
    }

    /// Difference of two pictures inside `area` (screen pixels; `a` covers the virtual screen),
    /// counting only `only` rectangles or leaving out `skip` rectangles. `b` is placed at `offset`.
    public static Stats Diff(Bitmap a, Bitmap b, Rectangle area, List<Rectangle>? only = null, List<Rectangle>? skip = null, Point offset = default, bool bVirtual = true)
    {
        var v = SystemInformation.VirtualScreen;
        var pa = Pixels(a); var pb = Pixels(b);
        double sum = 0; long n = 0; var hist = new double[256];
        int step = area.Width * area.Height > 3_000_000 ? 2 : 1;   // big screens: every other pixel is plenty
        for (int y = area.Top; y < area.Bottom; y += step)
            for (int x = area.Left; x < area.Right; x += step)
            {
                var p = new Point(x, y);
                if (only != null && !only.Any(r => r.Contains(p))) continue;
                if (skip != null && skip.Any(r => r.Contains(p))) continue;
                int ax = x - v.X, ay = y - v.Y;
                int bx = bVirtual ? x - v.X : x - offset.X, by = bVirtual ? y - v.Y : y - offset.Y;
                if (ax < 0 || ay < 0 || ax >= a.Width || ay >= a.Height || bx < 0 || by < 0 || bx >= b.Width || by >= b.Height) continue;
                int ia = (ay * a.Width + ax) * 4, ib = (by * b.Width + bx) * 4;
                int m = 0;
                for (int c = 0; c < 3; c++)
                {
                    int d = Math.Abs(pa[ia + c] - pb[ib + c]);
                    sum += d; if (d > m) m = d;
                }
                hist[m]++; n++;
            }
        return new Stats(n == 0 ? 0 : sum / (3.0 * n), hist, n);
    }

    /// A screen picture against a frame of the page that covers `screen` (the frame starts at the screen's corner).
    public static Stats VsFrame(Bitmap shot, Bitmap frame, Rectangle screen, Rectangle area, List<Rectangle>? skip = null) =>
        Diff(shot, frame, area, skip: skip, offset: new Point(screen.X, screen.Y), bVirtual: false);

    static readonly ConditionalWeakTable<Bitmap, byte[]> cache = new();
    static byte[] Pixels(Bitmap b)
    {
        if (cache.TryGetValue(b, out var px)) return px;
        var r = new Rectangle(0, 0, b.Width, b.Height);
        var data = b.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        px = new byte[b.Width * b.Height * 4];
        for (int y = 0; y < b.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, px, y * b.Width * 4, b.Width * 4);
        b.UnlockBits(data);
        cache.AddOrUpdate(b, px);
        return px;
    }

    /// Mean brightness (0–255) of a part of the screen.
    public static double Brightness(Bitmap shot, Rectangle area)
    {
        var v = SystemInformation.VirtualScreen;
        var px = Pixels(shot);
        double sum = 0; long n = 0;
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
            {
                int ax = x - v.X, ay = y - v.Y;
                if (ax < 0 || ay < 0 || ax >= shot.Width || ay >= shot.Height) continue;
                int i = (ay * shot.Width + ax) * 4;
                sum += (px[i] + px[i + 1] + px[i + 2]) / 3.0; n++;
            }
        return n == 0 ? 0 : sum / n;
    }

    /// The darkest and the lightest pixel brightness inside `area`.
    public static (double dark, double light) Range(Bitmap shot, Rectangle area)
    {
        var v = SystemInformation.VirtualScreen;
        var px = Pixels(shot);
        double lo = 255, hi = 0;
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
            {
                int ax = x - v.X, ay = y - v.Y;
                if (ax < 0 || ay < 0 || ax >= shot.Width || ay >= shot.Height) continue;
                int i = (ay * shot.Width + ax) * 4;
                double l = (px[i] + px[i + 1] + px[i + 2]) / 3.0;
                lo = Math.Min(lo, l); hi = Math.Max(hi, l);
            }
        return (lo, hi);
    }
}
