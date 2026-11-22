namespace Hidamari;

// Everything the prototype's JavaScript works out before a frame and hands to its shader (prototype/index.html: sky(),
// aim(), lighting() and the uniforms set in draw()), as the Mac app's WallLighting.swift ports it for Metal. The same
// numbers, the same order, double precision (the graphics card gets them as 32-bit floats, as WebGL does).
// Tests/Program.cs checks this against the Mac's Swift on hundreds of states (Tests/lighting.json).

/// One frame to draw, in the terms of wall.html's komorebi.render() (WallFrameInput on the Mac).
public sealed record WallFrameInput
{
    /// The moment, milliseconds since 1970 (UTC).
    public double Ms;
    /// Local time of day at the place, minutes 0 ..< 1440.
    public double Minutes;
    public double Lat, Lng;
    /// 0 dapples, 1 window, 2 blinds, 3 water.
    public int Scene;
    /// 0–5 an approved wall, -1 the own wall.
    public int Wall;
    public CustomWall Custom = CustomWall.Standard;
    /// The window scene's plant: 0 drawn bush, 2 hanging branch, 5 palm.
    public int WinPlant = 2;
    public double Cloud;
    /// 0 clear, 1 rain, 2 frost.
    public int Weather;
    /// Size of the frame in pixels.
    public int Width, Height;
    /// Live motion (Mac update 1.1). Still: the approved picture.
    public Motion Motion = Motion.Still;
    /// The frame is drawn as its mirror image (Light from: Right, Mac update 1.3).
    public bool Mirror;

    /// Local time of day at the place, minutes, as wall.html's renderNow() works it out from the moment and its offset.
    public static double MinutesOf(double ms, double utcOffsetMinutes) =>
        ((ms / 60000 + utcOffsetMinutes) % 1440 + 1440) % 1440;
}

/// Which picture the shader's uPlant is for this frame (texture units 2, 3, 4 in the prototype).
public enum PlantPicture
{
    /// the monstera drawn by makePlant(): the shader never reads it, only scenes without a window are left with it
    Monstera,
    /// the three branches drawn by makeFicus(): the window scene's "drawn bush"
    Ficus,
    /// the hanging branch, cut into three depth layers by photoLayers()
    Photo2,
    /// the palm
    Photo5,
}

/// The shader's uniforms, as draw() sets them.
public sealed record WallUniformValues
{
    public double[] Res = { 0, 0 };     // uRes
    public double WallH, Tex, Src, Corner, Phase, Calm;
    public double[] Depth = { 0, 0, 0 };
    public double WT;
    public double[] PlantRect = { 0, 0, 1, 1 };
    public double[] L1 = { 0, 0, 0 };
    public double Rough;
    public double[] C1 = { 0, 0, 0 };
    public double FrostBottom;
    public double[] Win1 = { 0, 0, 0, 0 };
    public double[] WallLin = { 0, 0, 0 };
    public int Scene;
    public double[] Amb = { 0, 0, 0 };
    public int Layers;
    public int Weather;
    public double T, Wind, WindLeaves;
    public bool Mirror;
    public PlantPicture Plant;
    /// Height of the sun in degrees (what komorebi.render() resolves with).
    public double SunDegrees;

    /// The pictures this frame reads, so the others need not be in memory.
    public bool ReadsCrowns => Scene == 0;
    public bool ReadsPlant => Scene == 1 || Scene == 2;
    public bool ReadsFrost => Weather == 2;

    /// All numbers in the shader's order, for the comparison with the Mac.
    public double[] All => Res.Concat(new[] { WallH, Tex, Src, Corner, Phase, Calm }).Concat(Depth).Append(WT).Concat(PlantRect)
        .Concat(L1).Append(Rough).Concat(C1).Append(FrostBottom).Concat(Win1).Concat(WallLin).Append(Scene).Concat(Amb)
        .Append(Layers).Append(Weather).ToArray();
}

public static class WallLighting
{
    const double Rad = Math.PI / 180, DayMs = 86_400_000.0;
    static readonly double Obl = Rad * 23.4397;
    public const double WallH = 0.95;                                  // WALL_H
    const double WindowWidth = 1.30, WindowHeight = 0.76;              // WINDOW
    const double PlantSize = 1.05, PlantBehind = 0.5;                  // PLANT
    const double PlantFill = 0.8;                                      // PLANT_FILL
    /// PLANTS: how each photo is cut (1-based by plant number).
    static readonly (bool top, double x)[] Plants = { (false, 0), (true, 0.42), (false, 0), (true, 0.10), (false, 0) };
    public const double FrostBottom = -1.0;                            // FROST_BOTTOM
    public const double TextureSize = 1024.0;                          // SIZE in initGL()

    // ---------- sun and moon (sky()) ----------

    static double ToDays(double ms) => ms / DayMs - 0.5 + 2440588 - 2451545;
    static double RightAsc(double l, double b) => Math.Atan2(Math.Sin(l) * Math.Cos(Obl) - Math.Tan(b) * Math.Sin(Obl), Math.Cos(l));
    static double Declin(double l, double b) => Math.Asin(Math.Sin(b) * Math.Cos(Obl) + Math.Cos(b) * Math.Sin(Obl) * Math.Sin(l));
    public static double WrapPi(double a) => a - 2 * Math.PI * Math.Floor((a + Math.PI) / (2 * Math.PI));

    public readonly record struct SkyNow(double Alt, double H, double H0, double MoonLit);

    public static SkyNow Sky(double ms, double lat, double lng)
    {
        var d = ToDays(ms);
        var M = Rad * (357.5291 + 0.98560028 * d);
        var C = Rad * (1.9148 * Math.Sin(M) + 0.02 * Math.Sin(2 * M) + 0.0003 * Math.Sin(3 * M));
        var L = M + C + Rad * 102.9372 + Math.PI;
        double sDec = Declin(L, 0), sRa = RightAsc(L, 0);
        double mL = Rad * (218.316 + 13.176396 * d), mM = Rad * (134.963 + 13.064993 * d), mF = Rad * (93.272 + 13.229350 * d);
        double ml = mL + Rad * 6.289 * Math.Sin(mM), mb = Rad * 5.128 * Math.Sin(mF);
        double mRa = RightAsc(ml, mb), mDec = Declin(ml, mb), mDist = 385001 - 20905 * Math.Cos(mM);
        var phi = Rad * lat;
        var H = WrapPi(Rad * (280.16 + 360.9856235 * d) + Rad * lng - sRa);
        var alt = Math.Asin(Math.Sin(phi) * Math.Sin(sDec) + Math.Cos(phi) * Math.Cos(sDec) * Math.Cos(H));
        var H0 = Math.Acos(Math.Max(-1, Math.Min(1, -Math.Tan(phi) * Math.Tan(sDec))));
        var ph = Math.Acos(Math.Sin(sDec) * Math.Sin(mDec) + Math.Cos(sDec) * Math.Cos(mDec) * Math.Cos(sRa - mRa));
        var inc = Math.Atan2(149598000 * Math.Sin(ph), mDist - 149598000 * Math.Cos(ph));
        return new SkyNow(alt, H, H0, (1 + Math.Cos(inc)) / 2);
    }

    // ---------- helpers as in the prototype ----------

    static double Clamp(double x, double a, double b) => Math.Max(a, Math.Min(b, x));
    static double Smooth(double a, double b, double x) => WallColor.Smooth(a, b, x);
    static double[] Mix3(double[] a, double[] b, double t) => WallColor.Mix3(a, b, t);

    /// currentWall(): the approved wall or the own wall.
    public static WallColor.Wall CurrentWall(WallFrameInput f) =>
        f.Wall >= 0 ? WallColor.Approved(Math.Min(f.Wall, WallColor.Walls.Length - 1)) : WallColor.Own(f.Custom);

    /// state.plant: blinds always have the palm, the other scenes the window scene's plant.
    public static int Plant(WallFrameInput f) => f.Scene == 2 ? 5 : f.WinPlant;

    // ---------- aim() ----------

    readonly record struct Aim(double[] Dir, double[] Win, double[]? PlantR);

    static Aim AimAt(double alt, double h, double wallW, WallFrameInput f)
    {
        var plant = Plant(f);
        if (f.Scene == 0 || f.Scene == 3)
        {
            double el0 = Clamp(alt, 2 * Rad, 70 * Rad), az = -h * 52 * Rad;
            return new Aim(new[] { Math.Cos(el0) * Math.Sin(az), Math.Sin(el0), Math.Cos(el0) * Math.Cos(az) }, new double[] { 0, 0, 0, 0 }, null);
        }
        double S = wallW / 1.52, a = Clamp(alt / Rad, 0, 70);
        var win1 = f.Scene == 1;
        double phi = (win1 ? 36 + 8 * (h + 1) / 2 : 34 + 12 * (h + 1) / 2) * Rad, el = (win1 ? 6 + 0.15 * a : 5 + 0.22 * a) * Rad;
        var dir = new[] { -Math.Cos(el) * Math.Sin(phi), Math.Sin(el), Math.Cos(el) * Math.Cos(phi) };
        double[] Photo(double[] win, double bottom, double top, double tall, double left = 0)
        {
            var P = Plants[plant - 1];
            var side = tall * Math.Min(1, 0.55 + 0.45 * S) / PlantFill;
            double x0 = win[0] + P.x * (win[2] - win[0]) - 0.004 - left * side, y0 = P.top ? top + 0.03 - side : bottom - 0.03;
            return new[] { x0, y0, x0 + side, y0 + side };
        }
        if (win1)
        {
            double[] w = { 0.10 * S, -1.0, 0.81 * S, 0.42 };
            var left = 0.15 * S / Math.Tan(phi);
            return new Aim(dir, w, plant != 0 ? Photo(new[] { left - 0.02, 0, w[2], 0 }, -0.40, 0.42, 0.62)
                : new[] { 0.75 * S - PlantSize / 2, -0.50, 0.75 * S + PlantSize / 2, -0.50 + PlantSize });
        }
        double u1 = 0.35 * S, u2 = u1 + WindowWidth * S, mid = 0.02 + 0.26 * S;
        double[] win = { u1, mid - WindowHeight / 2, u2, mid + WindowHeight / 2 };
        if (plant != 0) return new Aim(dir, win, Photo(win, win[1], win[3], 0.80, 0.09));
        double pz = (u1 + u2) / 2 + 0.62, py = mid - WindowHeight / 2 + 0.08;
        return new Aim(dir, win, new[] { pz - PlantSize / 2, py, pz + PlantSize / 2, py + PlantSize });
    }

    // ---------- lighting() ----------

    public readonly record struct Light(double[] Amb, double Deg, double[] L1, double[] Win1, double[] PlantR, double[] C1, double Src);

    public static Light Lighting(WallFrameInput f, double wallW)
    {
        var s = Sky(f.Ms, f.Lat, f.Lng);
        double c = f.Cloud, deg = s.Alt / Rad;
        double hemi = f.Lat >= 0 ? 1 : -1, H0 = Clamp(s.H0, 0.2, Math.PI - 0.2);
        var day = Math.Pow(Smooth(-7, 6, deg), 1.5);
        var room = CurrentWall(f).Amb;
        var lum = (room[0] + room[1] + room[2]) / 3;
        var dayAmb = room.Select(v => v + c * 0.22 * (lum + 0.25 - v * 0.6)).ToArray();
        var night = new[] { 0.034, 0.045, 0.082 }.Select(v => v * Clamp(lum / 0.6, 0.55, 1)).ToArray();
        var amb = Mix3(night, dayAmb, day);
        var dusk = Math.Exp(-Math.Pow(deg / 5, 2)) * (1 - c);
        double[] duskK = { 0.10, -0.06, 0.04 };
        amb = Enumerable.Range(0, 3).Select(i => amb[i] * (1 + dusk * duskK[i])).ToArray();
        var thin = 1 - 0.5 * c;
        var high = Math.Pow(Smooth(0, 32, deg), 0.6);
        double[] lowK = { 0.33, -0.27, -0.62 };
        var sunCol = Enumerable.Range(0, 3).Select(i => (1.03 - room[i]) * (1 + (1 - high) * lowK[i]) * (0.5 + 0.5 * Smooth(-1, 6, deg))).ToArray();
        var nightCol = new[] { 0.55, 0.68, 1.0 }.Select(v => v * (0.115 + 0.035 * s.MoonLit)).ToArray();
        var isDay = s.Alt >= 0;
        var h = isDay ? s.H / H0 : -WrapPi(s.H + Math.PI) / (Math.PI - H0);
        var A = AimAt(Math.Abs(s.Alt), hemi * Clamp(h, -1, 1), wallW, f);
        var wx = f.Scene == 1 ? f.Weather : 0;
        var dim = new[] { 1, 0.72, 0.85 }[wx];
        var tint = new[] { new[] { 1.0, 1, 1 }, new[] { 0.92, 0.97, 1.05 }, new[] { 0.95, 1.0, 1.06 } }[wx];
        if (wx == 1) { double[] k = { 0.88, 0.93, 0.99 }; amb = Enumerable.Range(0, 3).Select(i => amb[i] * k[i]).ToArray(); }
        if (wx == 2) { double[] k = { 0.85, 1.0, 1.2 }; amb = Enumerable.Range(0, 3).Select(i => amb[i] + day * 0.09 * (1 - amb[i]) * k[i]).ToArray(); }
        var mixed = Mix3(nightCol, sunCol, Smooth(-5, 1.5, deg));
        var col = Enumerable.Range(0, 3).Select(i => mixed[i] * thin * dim * tint[i] * (0.72 + 0.28 * Math.Max(0.42, A.Dir[2]))).ToArray();
        var src = f.Scene == 0 ? 1 + 1.1 * c : 1.6 * (1 + 2.5 * c) * new[] { 1, 1.25, 1.15 }[wx];
        return new Light(amb, deg, A.Dir, A.Win, A.PlantR ?? new double[] { 0, 0, 1, 1 }, col, src);
    }

    // ---------- draw() ----------

    public static WallUniformValues Uniforms(WallFrameInput f)
    {
        double w = f.Width, h = f.Height;
        var wallW = WallH * w / h;
        var L = Lighting(f, wallW);
        var plant = Plant(f);
        var wx = f.Scene == 1 ? f.Weather : 0;
        return new WallUniformValues
        {
            Res = new[] { w, h }, WallH = WallH, Tex = TextureSize, Src = L.Src,
            Corner = -wallW / 2 - 0.15 * wallW / 1.52,
            Phase = Phase(f.Minutes), Calm = 0.12 * f.Cloud,
            Depth = plant != 0 ? new[] { 0, 0.12, 0.30 } : f.Scene == 1 ? new[] { 0.15, 0.45, 0.90 } : new[] { PlantBehind, 1.0, 0 },
            WT = f.Minutes * (wx == 2 ? 0.11 : 1.0 / 25),
            PlantRect = L.PlantR, L1 = L.L1, Rough = f.Custom.Rough, C1 = L.C1, FrostBottom = FrostBottom,
            Win1 = L.Win1, WallLin = CurrentWall(f).Lin, Scene = f.Scene, Amb = L.Amb,
            Layers = f.Scene == 1 || plant != 0 ? 3 : 1, Weather = wx,
            T = f.Motion.T, Wind = f.Motion.Wind, WindLeaves = f.Motion.WindLeaves, Mirror = f.Mirror,
            Plant = plant == 2 ? PlantPicture.Photo2 : plant == 5 ? PlantPicture.Photo5 : f.Scene == 1 ? PlantPicture.Ficus : PlantPicture.Monstera,
            SunDegrees = L.Deg,
        };
    }

    /// uPhase: the slow turn of the crown and the water over the day.
    public static double Phase(double minutes) => 6.2832 * minutes / 1440;

    /// Only the dapples and the water move (Mac update 1.1); the window and the blinds never do.
    public static bool Moves(int scene) => scene == 0 || scene == 3;
}
