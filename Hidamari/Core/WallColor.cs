namespace Hidamari;

/// The person's own wall, as the prototype keeps it: lightness, the place on the picker's field, an own shade colour
/// or none, the strength of light (0–1) and the texture (0–2). The texture is used for every wall.
public sealed record CustomWall(double L, double X, double Y, double K, double Rough, (double X, double Y)? Shade)
{
    /// The prototype's starting own wall (CustomWall.standard on the Mac).
    public static readonly CustomWall Standard = new(0.88, 0.5, 0.8, 0.1, 1.0, null);

    public Dictionary<string, object?> ForPage() => new()
    {
        ["L"] = L, ["x"] = X, ["y"] = Y, ["k"] = K, ["rough"] = Rough,
        ["shade"] = Shade is { } s ? new Dictionary<string, object?> { ["x"] = s.X, ["y"] = s.Y } : null,
    };

    /// Kept within the prototype's ranges (wall.html, setWall).
    public CustomWall Clamped() => new(Unit(L), Unit(X), Unit(Y), Unit(K), Math.Clamp(double.IsFinite(Rough) ? Rough : 1, 0, 2),
        Shade is { } s ? (Unit(s.X), Unit(s.Y)) : null);

    static double Unit(double v) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0.5;
}

/// The colour maths of the prototype's walls (prototype/index.html, "Wall colour"), as the Mac app's WallColor.swift
/// ports it: the six approved walls, OKLab, the chroma cap and the own wall. Only what the Direct3D drawing needs:
/// the card does its own colour maths in card.js. The same numbers, the same order.
public static class WallColor
{
    /// The six approved walls: colour in full sun and what the room gives back in the shade (prototype's WALLS).
    public static readonly (string c, double[] amb)[] Walls =
    {
        ("#d9d0c4", new[] { 0.56, 0.61, 0.72 }),   // beige
        ("#cfd6dd", new[] { 0.56, 0.61, 0.72 }),   // grey-blue
        ("#c5d7d0", new[] { 0.56, 0.61, 0.72 }),   // mint
        ("#f3ead6", new[] { 0.25, 0.20, 0.16 }),   // linen on taupe
        ("#f1dfb0", new[] { 0.24, 0.17, 0.11 }),   // honey on umber
        ("#f2cdb0", new[] { 0.42, 0.23, 0.17 }),   // peach on clay
    };

    const double Rad = Math.PI / 180;

    public static double Clamp(double x, double a, double b) => Math.Max(a, Math.Min(b, x));

    public static double Smooth(double a, double b, double x)
    {
        var t = Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    public static double[] Mix3(double[] a, double[] b, double t) => new[] { a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t };

    public static double[] HexLin(string hex) =>
        new[] { 1, 3, 5 }.Select(i => Math.Pow(Convert.ToInt32(hex.Substring(i, 2), 16) / 255.0, 2.2)).ToArray();

    // ---------- OKLab ----------

    public static double[] Lab2Lin(double[] c)
    {
        double L = c[0], a = c[1], b = c[2];
        double l = Math.Pow(L + 0.3963377774 * a + 0.2158037573 * b, 3);
        double m = Math.Pow(L - 0.1055613458 * a - 0.0638541728 * b, 3);
        double s = Math.Pow(L - 0.0894841775 * a - 1.2914855480 * b, 3);
        return new[]
        {
            4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
            -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
            -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s,
        };
    }

    public static double[] Lch2Lin(double L, double C, double h) => Lab2Lin(new[] { L, C * Math.Cos(h * Rad), C * Math.Sin(h * Rad) });

    static bool InGamut(double[] c) => c.All(v => v >= -1e-4 && v <= 1.0001);

    /// How colourful a wall may get at this lightness and hue: never past what the screen can show, never acid.
    public static double ChromaCap(double L, double h)
    {
        double lo = 0, hi = 0.37;
        for (int i = 0; i < 16; i++)
        {
            var m = (lo + hi) / 2;
            if (InGamut(Lch2Lin(L, m, h))) lo = m; else hi = m;
        }
        return Math.Min(0.105, lo * 0.85);
    }

    static double[] MeanOne(double[] c)
    {
        var m = (c[0] + c[1] + c[2]) / 3;
        return c.Select(v => v / m).ToArray();
    }

    /// Across the field: hue, warm colours in the middle.
    public static double PadHue(double x) => 70 + (x - 0.5) * 360;

    static readonly double[] Sky = MeanOne(new[] { 0.56, 0.61, 0.72 });

    /// A wall to draw: its colour in full sun (linear) and what the room gives back in the shade.
    public readonly record struct Wall(double[] Lin, double[] Amb);

    /// The prototype's customWall().
    public static Wall Own(CustomWall c)
    {
        double h = PadHue(c.X), C = (1 - c.Y) * ChromaCap(c.L, h);
        var lin = Lch2Lin(c.L, C, h).Select(v => Clamp(v, 0, 1)).ToArray();
        double[] tint;
        if (c.Shade is { } s)
        {
            // shade colour picked by hand
            double sh = PadHue(s.X), sC = (1 - s.Y) * ChromaCap(0.72, sh);
            tint = MeanOne(Lch2Lin(0.72, sC, sh).Select(v => Math.Max(v, 0.01)).ToArray());
        }
        else
        {
            // warm, coloured walls get a shade of their own colour, pale or cool ones keep bluish skylight
            var warm = Clamp(0.5 + 0.5 * Math.Cos((h - 65) * Rad), 0, 1);
            var w = Smooth(0.012, 0.035, C) * Smooth(0.35, 0.85, warm);
            var own = MeanOne(new[] { lin[0] * 1.12, lin[1] * 1, lin[2] * 0.92 });
            tint = MeanOne(Mix3(Sky, own, w));
        }
        var lum = 0.68 - 0.52 * c.K;   // strength of light: how much darker the shade is
        return new Wall(lin, tint.Select(v => v * lum).ToArray());
    }

    /// An approved wall, as the prototype's currentWall() gives it.
    public static Wall Approved(int i) => new(HexLin(Walls[i].c), Walls[i].amb);
}
