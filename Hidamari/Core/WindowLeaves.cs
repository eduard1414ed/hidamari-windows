using System.Numerics;
using System.Text.Json;

namespace Hidamari;

// Mac update 1.4 (prompts/update-5.md on the Mac): the window's plant in the wind, as the approved sketch
// design/window-motion/window-motion.html moves it, ported one to one from the Mac app's WindowLeaves.swift. Only the
// numbers live here; the Direct3D drawing (Wall/D3DWall.cs) draws them.
//
// The two photo plants (2, the hanging branch; 5, the palm) are cut into pieces (textures/rig<i>.json and the map of
// pieces rigid<i>.htex, taken out of wall.html on the Mac by tools/make-metal-textures.swift). Every frame each piece
// gets a turn and a shift (rigPose / rigPoseChain in the sketch), and the pieces are drawn into the plant's picture the
// way the still photo is laid out: three depth layers in red, green and blue.
//
// The drawn ficus (plant 0) is drawn again every frame with the wind in its angles (makeFicus with a pose in the
// sketch): the same random numbers as the still picture, so here it is a list of strokes and leaves for the card.
//
// Every formula is the sketch's, in the same order; the sketch keeps angles and places in 32-bit arrays
// (Float32Array), so they are rounded to float at exactly the same points here, as on the Mac.
// Tests/Program.cs compares the posed pieces with the Mac's Swift (Tests/leaves.json.gz).

public static class WindowLeaves
{
    // ---------- the sketch's helpers ----------

    static double Clamp(double x, double a, double b) => Math.Max(a, Math.Min(b, x));
    public static double Smoothstep(double a, double b, double x)
    {
        var t = Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }
    /// JavaScript's Math.round.
    public static double Round(double x) => Math.Floor(x + 0.5);

    static double Frac(double v) => v - Math.Floor(v);

    /// Smooth noise in time, 0..1.
    public static double N1(double x, double s)
    {
        double i = Math.Floor(x), f = x - i, u = f * f * (3 - 2 * f);
        double H(double k) => Frac(Math.Sin(k * 12.9898 + s * 78.233) * 43758.5453);
        var h0 = H(i);
        return h0 + (H(i + 1) - h0) * u;
    }
    /// A value that mostly rests and then moves quickly to the next one: how a leaf answers a puff of air.
    public static double Jerk(double x, double s)
    {
        double i = Math.Floor(x), f = x - i;
        double H(double k, double q) => Frac(Math.Sin(k * 12.9898 + (s + q) * 78.233) * 43758.5453);
        double c = 0.10 + 0.45 * H(i, 3.1), w = 0.22 + 0.16 * H(i, 5.7), h0 = H(i, 0);
        var u = Clamp((f - c) / w, 0, 1); u = u * u * (3 - 2 * u);
        return h0 + (H(i + 1, 0) - h0) * u;
    }

    /// What N1() and Jerk() last worked out for one caller: their random steps change only when the whole part of x
    /// does (about once a second), so a leaf asked 30 times a second can keep them. The answer is the very same number.
    /// A fresh memo holds NaN (`Fresh`), never the zeros of a default struct, so it never matches.
    public struct N1Memo
    {
        public double I, S, H0, H1;
        public static N1Memo Fresh => new() { I = double.NaN, S = double.NaN };
    }
    public struct JerkMemo
    {
        public double I, S, C, W, H0, H1;
        public static JerkMemo Fresh => new() { I = double.NaN, S = double.NaN };
    }
    public static double N1(double x, double s, ref N1Memo m)
    {
        double i = Math.Floor(x), f = x - i, u = f * f * (3 - 2 * f);
        if (i != m.I || s != m.S)
        {
            double H(double k) => Frac(Math.Sin(k * 12.9898 + s * 78.233) * 43758.5453);
            m = new N1Memo { I = i, S = s, H0 = H(i), H1 = H(i + 1) };
        }
        return m.H0 + (m.H1 - m.H0) * u;
    }
    public static double Jerk(double x, double s, ref JerkMemo m)
    {
        double i = Math.Floor(x), f = x - i;
        if (i != m.I || s != m.S)
        {
            double H(double k, double q) => Frac(Math.Sin(k * 12.9898 + (s + q) * 78.233) * 43758.5453);
            m = new JerkMemo { I = i, S = s, C = 0.10 + 0.45 * H(i, 3.1), W = 0.22 + 0.16 * H(i, 5.7), H0 = H(i, 0), H1 = H(i + 1, 0) };
        }
        var u = Clamp((f - m.C) / m.W, 0, 1); u = u * u * (3 - 2 * u);
        return m.H0 + (m.H1 - m.H0) * u;
    }

    /// The memos of one plant: three for each leaf or frond (and its layer), grown as needed. Not safe for two threads
    /// at once: keep one per drawing thread (the Mac keeps one for the ficus, under the drawing's lock).
    public sealed class Memos
    {
        public JerkMemo[] A = Array.Empty<JerkMemo>(), C = Array.Empty<JerkMemo>();
        public N1Memo[] B = Array.Empty<N1Memo>();
        /// More N1() memos, for what a branch's joints and a palm's fronds ask.
        public N1Memo[] N = Array.Empty<N1Memo>(), N2 = Array.Empty<N1Memo>();

        static T[] Grown<T>(T[] a, int count, T fresh)
        {
            if (a.Length >= count) return a;
            var b = new T[count];
            Array.Copy(a, b, a.Length);
            for (int i = a.Length; i < count; i++) b[i] = fresh;
            return b;
        }
        public void MakeN(int k)
        {
            if (N.Length < k) { N = Grown(N, k, N1Memo.Fresh); N2 = Grown(N2, k, N1Memo.Fresh); }
        }
        public void Make(int n)
        {
            if (A.Length >= n) return;
            A = Grown(A, n, JerkMemo.Fresh); B = Grown(B, n, N1Memo.Fresh); C = Grown(C, n, JerkMemo.Fresh);
        }
    }

    /// The per-piece character hash of rigLoad() and chainPrep().
    public static double Hash(double k, double s) => Frac(Math.Sin(k * 127.1 + s * 311.7) * 43758.5453);

    /// The wind's scale for the plants (Ed, 25 November: 15 m/s moves them as 3 m/s did in the first trial).
    public static (double A, double Amp) Scale(double ms)
    {
        var e = Clamp(ms / 15, 0, 1);
        return (0.25 * e, 0.06 + 0.341 * Math.Pow(e, 0.9));
    }
    public static double Gust(double tt, double a, double seed) =>
        0.65 * Smoothstep(0.30, 0.85, N1(tt * (0.22 + 0.25 * a), seed + 1)) + 0.35 * N1(tt * 0.9, seed + 2);
    public static double Gust(double tt, double a, double seed, ref N1Memo m1, ref N1Memo m2) =>
        0.65 * Smoothstep(0.30, 0.85, N1(tt * (0.22 + 0.25 * a), seed + 1, ref m1)) + 0.35 * N1(tt * 0.9, seed + 2, ref m2);

    // ---------- the photo plants in pieces ----------

    /// The three layers of the plant's picture (photoLayers() in the sketch): zoom, shift, the moment's offset.
    public static readonly (double Zoom, double Dx, double Off)[] Layers = { (1, 0, 0.0), (0.86, 0.06, 4.7), (1.12, 0.03, 9.1) };
    /// PLANT_FILL: the share of the square's height the nearest layer takes.
    public const double PlantFill = 0.8;
    /// PLANT2_LAYERS: the hanging branch is drawn with its nearest layer only (WallLighting.Plant2Layers).
    public static int Plant2Layers => WallLighting.Plant2Layers;
    /// PLANTS in the sketch: the plant hangs from above (Top) or is turned over (Flip). Index plant − 1.
    public static readonly (bool Flip, bool Top)[] Plants = { (true, false), (false, true), (false, false), (false, true), (false, false) };
    /// The photo plants that are cut into pieces (textures/rig<i>.json): the hanging branch and the palm
    /// (MetalWall.rigPlants on the Mac).
    public static readonly int[] RigPlants = { 2, 5 };

    /// One plant cut into pieces: the sketch's RIGS[i] with what rigLoad() / chainPrep() work out once. Its memos make it
    /// not safe for two threads at once (the Mac poses under the drawing's lock).
    public sealed class Rig
    {
        struct Node
        {
            public double X, Y; public int Parent; public double S; public int Run; public double F, L;
            public int Kids, Br; public bool Main;
        }
        readonly record struct Stem(double Id, int K, float B0, float B1, float B2, float B3);
        // At: the node the leaf grows from
        readonly record struct Leaf(double Id, int At, double Px, double Py, double Ax, float B0, float B1, float B2, float B3,
                                    bool Single, double Seed, double Turn);
        sealed class RunInfo
        {
            public int Id; public double L, Tx, Ty, Ax, Ay, F0 = double.PositiveInfinity, Amp, Seed;
        }

        /// The size of the cut photo in pixels (whole numbers; double as the lead's drawing wants them).
        public readonly double W, H;
        readonly bool chain;
        readonly Node[] nodes;
        readonly Stem[] stems;
        readonly Leaf[] leaves = Array.Empty<Leaf>();
        readonly double Ls;
        readonly RunInfo[] runs = Array.Empty<RunInfo>();
        /// For every node its run's place in `runs` (−1: not on a run), so a frame need not look it up.
        readonly int[] nodeRun = Array.Empty<int>();
        /// Pieces drawn per layer.
        public int Count => stems.Length + leaves.Length;
        /// What the wind's steps were on the last frame, by layer seed (see N1Memo).
        readonly Dictionary<double, Memos> memos = new();
        Memos MemosFor(double seed)
        {
            if (memos.TryGetValue(seed, out var m)) return m;
            m = new Memos(); m.Make(Math.Max(leaves.Length, runs.Length)); m.MakeN(11 * nodes.Length + runs.Length); memos[seed] = m;
            return m;
        }

        /// The rig embedded in the app as textures/rig<n>.json (n 2 or 5).
        public static Rig FromResource(int n)
        {
            using var s = typeof(WindowLeaves).Assembly.GetManifestResourceStream($"textures/rig{n}.json")
                ?? throw new FileNotFoundException($"rig{n}.json is not in the app");
            using var r = new StreamReader(s);
            return FromJson(r.ReadToEnd());
        }

        public static Rig FromJson(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return new Rig(doc.RootElement);
        }

        // NSNumber.intValue on the Mac: a whole number, cut towards zero
        static int Int(JsonElement e) => (int)e.GetDouble();
        static double[] Row(JsonElement e) => e.EnumerateArray().Select(x => x.GetDouble()).ToArray();

        Rig(JsonElement o)
        {
            W = Int(o.GetProperty("W")); H = Int(o.GetProperty("H"));
            chain = o.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "chain";
            var nodeRows = o.GetProperty("nodes").EnumerateArray().Select(Row).ToArray();
            stems = o.GetProperty("stems").EnumerateArray().Select(Row)
                .Select(a => new Stem(a[0], (int)a[1], (float)a[2], (float)a[3], (float)a[4], (float)a[5])).ToArray();
            if (chain)
            {
                // chainPrep()
                nodes = nodeRows.Select(a => new Node { X = a[0], Y = a[1], Parent = (int)a[2], S = a[3], Run = (int)a[4], F = a[5], L = a[6] }).ToArray();
                var runList = new List<RunInfo>();
                var runIndex = new Dictionary<int, int>();
                foreach (var n in nodes)
                {
                    if (n.Run == 0) continue;
                    if (!runIndex.TryGetValue(n.Run, out var i)) { i = runList.Count; runIndex[n.Run] = i; runList.Add(new RunInfo { Id = n.Run, Ax = n.X, Ay = n.Y }); }
                    var r = runList[i];
                    if (n.F > r.L) { r.L = n.F; r.Tx = n.X; r.Ty = n.Y; }
                    if (n.F < r.F0) { r.F0 = n.F; r.Ax = n.X; r.Ay = n.Y; }
                }
                runs = runList.ToArray();
                nodeRun = nodes.Select(n => n.Run != 0 ? runIndex[n.Run] : -1).ToArray();
                foreach (var r in runs)
                {
                    double id = r.Id;
                    // ends that leave the picture on the left or at the bottom are not free, and short spurs stay put
                    var pinned = r.Tx < 70 || r.Ty > H - 40 || r.Ax < 40;
                    r.Amp = pinned ? 0 : Smoothstep(35, 140, r.L) * (0.7 + 0.6 * Hash(id, 1));
                    r.Seed = Hash(id, 2);
                }
            }
            else
            {
                // rigLoad(): the main branch, the side twigs' numbers, every leaf's character
                var ns = nodeRows.Select(a => new Node { X = a[0], Y = a[1], Parent = (int)a[2], S = a[3] }).ToArray();
                for (int k = 0; k < ns.Length; k++) if (ns[k].Parent >= 0) ns[ns[k].Parent].Kids += 1;
                Ls = ns.Length > 0 ? ns.Max(n => n.S) : 1;
                int tip = 0;
                for (int k = 0; k < ns.Length; k++) if (ns[k].S > ns[tip].S) tip = k;
                for (int k = tip; k >= 0; k = ns[k].Parent) ns[k].Main = true;
                int nb = 0;
                for (int k = 0; k < ns.Length; k++)
                {
                    if (ns[k].Parent < 0) continue;
                    var p = ns[ns[k].Parent];
                    if (ns[k].Main) ns[k].Br = 0;
                    else if ((p.Kids > 1 && !p.Main) || p.Main) { nb += 1; ns[k].Br = nb; }
                    else ns[k].Br = p.Br;
                }
                nodes = ns;
                leaves = o.GetProperty("leaves").EnumerateArray().Select(Row).Select(a =>
                {
                    double id = a[0], el = a[6], area = a[7];
                    bool onpath = a[8] != 0;
                    return new Leaf(id, (int)a[1], a[2], a[3], a[4], (float)a[9], (float)a[10], (float)a[11], (float)a[12],
                        el > 2.6 && area < 2600, Hash(id, 1), (0.55 + 0.9 * Hash(id, 2)) * (onpath ? 0.2 : area > 3000 ? 0.55 : 1));
                }).ToArray();
            }
        }

        /// One layer of the plant at time t: for every piece its box (4 floats), the two rows of its transform (6) and its
        /// number (1): 11 floats a piece, the sketch's instance data. `seed` is the layer's, 31 · layer + 3.
        public float[] Pose(double t, double ms, double seed) => chain ? PoseChain(t, ms, seed) : PoseBranch(t, ms, seed);

        float[] PoseBranch(double t, double ms, double seed)
        {
            var (a, amp) = Scale(ms);
            var M = MemosFor(seed);
            double Sway(double tt, int m)
            {
                var gg = Gust(tt, a, seed, ref M.N[m], ref M.N2[m]);
                var lean = 0.55 * gg + 0.25 * (N1(tt * 0.13, seed + 3, ref M.N[m + 2]) - 0.5);
                var swing = (0.3 + 0.7 * gg) * (0.55 * Math.Sin(6.2832 * 0.5 * tt + 3 * N1(tt * 0.15, seed + 4, ref M.N[m + 3]))
                                               + 0.9 * (N1(tt * 1.1, seed + 5, ref M.N[m + 4]) - 0.5));
                return amp * (lean + swing);
            }
            const double K = 0.14;                                   // radians of bend at the free end per unit of sway
            var phi = new float[nodes.Length]; var X = new float[nodes.Length]; var Y = new float[nodes.Length];
            for (int k = 0; k < nodes.Length; k++)
            {
                var n = nodes[k];
                var f = n.S / Ls;
                var tt = t - 0.6 * f;                                // the bend travels down the branch, a little late
                var ang = K * (0.45 * Sway(tt, 11 * k) + 0.75 * Sway(tt - 0.2, 11 * k + 5) * Math.Pow(f, 1.4));
                if (n.Br != 0) ang += amp * 0.10 * (N1(t * (0.7 + 0.8 * a) + 7.3 * n.Br, seed + 6, ref M.N[11 * k + 10]) - 0.5) * 2;
                if (n.Parent < 0) { phi[k] = 0; X[k] = (float)n.X; Y[k] = (float)n.Y; continue; }
                var p = nodes[n.Parent];
                double pa = phi[n.Parent], c = Math.Cos(pa), s = Math.Sin(pa), dx = n.X - p.X, dy = n.Y - p.Y;
                X[k] = (float)((double)X[n.Parent] + c * dx - s * dy); Y[k] = (float)((double)Y[n.Parent] + s * dx + c * dy);
                phi[k] = (float)ang;
            }
            var output = new float[Count * 11];
            int o = 0;
            void Put(float b0, float b1, float b2, float b3, double m0, double m1, double m2, double m3, double m4, double m5, double id)
            {
                output[o++] = b0; output[o++] = b1; output[o++] = b2; output[o++] = b3;
                output[o++] = (float)m0; output[o++] = (float)m1; output[o++] = (float)m2; output[o++] = (float)m3; output[o++] = (float)m4; output[o++] = (float)m5;
                output[o++] = (float)id;
            }
            // a stem piece goes with its node: turned by the node's angle about the node's resting place, then carried
            foreach (var st in stems)
            {
                var n = nodes[st.K];
                double c = Math.Cos((double)phi[st.K]), s = Math.Sin((double)phi[st.K]);
                double x = X[st.K], y = Y[st.K];
                Put(st.B0, st.B1, st.B2, st.B3, c, -s, x - c * n.X + s * n.Y, s, c, y - s * n.X - c * n.Y, st.Id);
            }
            var g = Gust(t - 0.3, a, seed);                          // the same gust for every leaf of the layer
            for (int li = 0; li < leaves.Length; li++)
            {
                var L = leaves[li];
                var n = nodes[L.At];
                double pk = phi[L.At], c0 = Math.Cos(pk), s0 = Math.Sin(pk);
                double qx = (double)X[L.At] + c0 * (L.Px - n.X) - s0 * (L.Py - n.Y), qy = (double)Y[L.At] + s0 * (L.Px - n.X) + c0 * (L.Py - n.Y);
                // the leaf's own turn on its stalk: jerky, harder in gusts, each leaf with its own timing
                var tt = t + 13 * L.Seed;
                var rate = 0.45 + 1.0 * a + 0.4 * L.Seed;
                var th = amp * L.Turn * (0.3 + 0.7 * g) * (0.60 * (Jerk(tt * rate, L.Id + seed, ref M.A[li]) - 0.5) * 2
                                                           + 0.22 * (N1(tt * 2.3, L.Id + seed + 9, ref M.B[li]) - 0.5) * 2 + 0.20 * (g - 0.4));
                // now and then a single leaf turns edge-on to the light and its shadow narrows
                var sq = 1.0;
                if (L.Single)
                {
                    var e = Jerk(tt * 0.45 * (0.6 + a), L.Id + seed + 17, ref M.C[li]);
                    sq = 1 - amp * 0.7 * Smoothstep(0.55, 0.95, e) * (0.5 + 0.5 * g);
                }
                double ang = pk + th, c = Math.Cos(ang), s = Math.Sin(ang);
                double ux = -Math.Sin(L.Ax), uy = Math.Cos(L.Ax), k1 = sq - 1;   // squeeze across the leaf's own length
                double s00 = 1 + k1 * ux * ux, s01 = k1 * ux * uy, s11 = 1 + k1 * uy * uy;
                double m00 = c * s00 - s * s01, m01 = c * s01 - s * s11, m10 = s * s00 + c * s01, m11 = s * s01 + c * s11;
                Put(L.B0, L.B1, L.B2, L.B3, m00, m01, qx - m00 * L.Px - m01 * L.Py, m10, m11, qy - m10 * L.Px - m11 * L.Py, L.Id);
            }
            return output;
        }

        /// The palm: only the free ends of its fronds move, each leaflet turning on its base (rigPoseChain()).
        float[] PoseChain(double t, double ms, double seed)
        {
            var (a, amp) = Scale(ms);
            var gust = Gust(t, a, seed);
            var M = MemosFor(seed);
            var th = new double[runs.Length];
            for (int i = 0; i < runs.Length; i++)
            {
                var r = runs[i];
                if (r.Amp == 0 || double.IsNaN(r.Amp)) { th[i] = 0; continue; }
                double id = r.Id, tt = t + 11 * r.Seed;
                var wave = N1(t * (0.5 + 0.6 * a) - 0.004 * r.Ax + 0.002 * r.Ay, seed + 4, ref M.N[i]) - 0.5;   // a puff along the frond
                var own = 0.65 * (N1(tt * (0.9 + 1.6 * a + 0.5 * r.Seed), id + seed, ref M.B[i]) - 0.5)
                    + 0.35 * (Jerk(tt * (0.6 + 0.8 * a), id + seed + 7, ref M.A[i]) - 0.5);
                th[i] = amp * r.Amp * 0.30 * (0.35 + 0.65 * gust) * 2 * (0.45 * wave + 0.75 * own + 0.15 * (gust - 0.4));
            }
            var phi = new float[nodes.Length]; var X = new float[nodes.Length]; var Y = new float[nodes.Length];
            for (int k = 0; k < nodes.Length; k++)
            {
                var n = nodes[k];
                if (n.Parent < 0) { X[k] = (float)n.X; Y[k] = (float)n.Y; phi[k] = 0; continue; }
                // most joints do not turn: cos 0 and sin 0 are exactly 1 and 0, so they are not worked out
                var p = nodes[n.Parent];
                double pa = phi[n.Parent], c = pa == 0 ? 1 : Math.Cos(pa), s = pa == 0 ? 0 : Math.Sin(pa), dx = n.X - p.X, dy = n.Y - p.Y;
                X[k] = (float)((double)X[n.Parent] + c * dx - s * dy); Y[k] = (float)((double)Y[n.Parent] + s * dx + c * dy);
                // a leaflet turns at its base and bends a little more towards its tip
                phi[k] = n.Run != 0 ? (float)(th[nodeRun[k]] * (0.55 + 0.45 * n.F / Math.Max(n.L, 1))) : 0;
            }
            // the whole plant leans a touch about where it comes into the picture
            double g = amp * 0.012 * (0.6 * gust + 0.4 * (N1(t * 0.35, seed + 9) - 0.5) * 2 - 0.3), gc = Math.Cos(g), gs = Math.Sin(g);
            double px = 0.2 * W, py = H;
            var output = new float[stems.Length * 11];
            int o = 0;
            foreach (var st in stems)
            {
                double pk = phi[st.K];
                var n = nodes[st.K];
                double c = pk == 0 ? 1 : Math.Cos(pk), s = pk == 0 ? 0 : Math.Sin(pk);
                double tx = (double)X[st.K] - c * n.X + s * n.Y, ty = (double)Y[st.K] - s * n.X - c * n.Y;
                double m00 = gc * c - gs * s, m01 = -gc * s - gs * c, m10 = gs * c + gc * s, m11 = -gs * s + gc * c;
                double qx = gc * (tx - px) - gs * (ty - py) + px, qy = gs * (tx - px) + gc * (ty - py) + py;
                output[o++] = st.B0; output[o++] = st.B1; output[o++] = st.B2; output[o++] = st.B3;
                output[o++] = (float)m00; output[o++] = (float)m01; output[o++] = (float)qx; output[o++] = (float)m10; output[o++] = (float)m11; output[o++] = (float)qy;
                output[o++] = (float)st.Id;
            }
            return output;
        }
    }

    /// Where layer `layer` of plant `plant` goes in the 1024 square (rigRender()'s uPlace): scale, shift, top, flip width.
    public static float[] Place(int plant, Rig rig, int layer)
    {
        var L = Layers[layer]; var P = Plants[plant - 1];
        double W = 1024.0, h = PlantFill * W * L.Zoom, k = h / rig.H, w = rig.W * k;
        return new[] { (float)k, (float)(L.Dx * W), (float)(P.Top ? 0 : W - h), (float)(P.Flip ? w : 0) };
    }

    /// The same as Place(), as a vector for a constant buffer.
    public static Vector4 PlaceVector(int plant, Rig rig, int layer)
    {
        var p = Place(plant, rig, layer);
        return new Vector4(p[0], p[1], p[2], p[3]);
    }

    // ---------- the drawn ficus ----------

    /// The sketch's rng(): a small random number generator, the same numbers in the same order.
    sealed class Rng
    {
        uint a;
        public Rng(uint seed) { a = seed; }
        public double Next()
        {
            unchecked
            {
                a += 0x6D2B79F5;
                var t = a;
                t = (t ^ (t >> 15)) * (t | 1);
                t ^= t + ((t ^ (t >> 7)) * (t | 61));
                return (t ^ (t >> 14)) / 4294967296.0;
            }
        }
    }

    /// The ficus in the wind (ficusWind()): each step of a branch leans a little, twigs nod on their own, every leaf
    /// turns on its stalk in jerks and now and then turns edge-on.
    sealed class FicusWind
    {
        readonly double t, a, amp, seed, g0;
        readonly double[] S = new double[24];
        public FicusWind(double t, double ms, double seed)
        {
            this.t = t; this.seed = seed;
            var sc = Scale(ms);
            a = sc.A; amp = sc.Amp;
            var a0 = a;
            double Sway(double tt)
            {
                var gg = Gust(tt, a0, seed);
                return 0.55 * gg + 0.25 * (N1(tt * 0.13, seed + 3) - 0.5)
                    + (0.3 + 0.7 * gg) * (0.45 * Math.Sin(6.2832 * 0.45 * tt + 3 * N1(tt * 0.15, seed + 4))
                                          + 0.9 * (N1(tt * 0.8, seed + 5) - 0.5));
            }
            for (int i = 0; i < 24; i++) S[i] = Sway(t - 0.05 * i);    // the bend climbs the branch a little late
            g0 = Gust(t - 0.3, a, seed);
        }
        /// The twig's own nod: the same for every step of branch b, so worked out once per branch.
        public double Nod(int b) => N1(t * (0.45 + 0.5 * a) + 7.3 * b, seed + 6);
        public double Bend(double nod, double k, int depth) =>
            amp * 0.0065 * (depth != 0 ? 0.5 + 0.5 * k : Math.Pow(k, 1.8))
                * (S[Math.Min((int)Round(k * 16), 23)] - 0.3
                   + (depth != 0 ? 1.1 : 0.4) * (nod - 0.5) * 2);
        public (double Th, double Sq) Leaf(int id, Memos memos, int slot)
        {
            double idd = id;
            // each leaf's own character never changes: looked up, not worked out again on every frame
            var (sd, h2) = id < LeafHashes.Length ? LeafHashes[id] : (Hash(idd, 1), Hash(idd, 2));
            double tt = t + 13 * sd, rate = 0.45 + 1.0 * a + 0.4 * sd, turn = 0.55 + 0.9 * h2;
            var th = 0.75 * amp * turn * (0.3 + 0.7 * g0) * (0.60 * (Jerk(tt * rate, idd + seed, ref memos.A[slot]) - 0.5) * 2
                                                           + 0.22 * (N1(tt * 2.3, idd + seed + 9, ref memos.B[slot]) - 0.5) * 2 + 0.20 * (g0 - 0.4));
            var ee = Jerk(tt * 0.45 * (0.6 + a), idd + seed + 17, ref memos.C[slot]);
            return (th, 1 - amp * 0.7 * Smoothstep(0.55, 0.95, ee) * (0.5 + 0.5 * g0));
        }
    }

    /// Hash(id, 1) and Hash(id, 2) of the ficus's leaves, by leaf number (a bush has fewer than 2048 leaves).
    static readonly (double, double)[] LeafHashes = Enumerable.Range(0, 2048).Select(i => (Hash(i, 1), Hash(i, 2))).ToArray();

    /// Floats per shape of the ficus: kind (0 a stroke with round ends, 1 a leaf), layer (0 red, 1 green, 2 blue),
    /// then for a stroke its two ends and half its width, for a leaf where it starts, its turn, length and half width.
    public const int FicusStride = 8;

    /// makeFicus() with a pose: every stroke and leaf of the three bushes in canvas pixels (1024 square, y down),
    /// FicusStride floats a shape. Keep one `memos` for the ficus and pass it every frame (far less work, the same numbers).
    public static float[] Ficus(double t, double ms, Memos? memos = null)
    {
        var mem = memos ?? new Memos();
        double W = 1024.0, px = W / 1.05;                            // PLANT.size: the plant drawn on a 1.05 m square
        var output = new List<float>(60_000);
        Rng R = new(8);
        FicusWind W8 = new(t, ms, 3);
        int brNo = 0, leafNo = 0, layer = 0;
        void Branch(double x0, double y0, double ang0, double len, double thick, int depth)
        {
            double x = x0, y = y0, ang = ang0;
            double step = 0.034 * px;
            int n = (int)Round(len * px / step);
            var nod = W8.Nod(brNo);
            brNo += 1;
            for (int i = 0; i < Math.Max(n, 0); i++)
            {
                double k = (double)i / n, nx = x + Math.Cos(ang) * step, ny = y + Math.Sin(ang) * step;
                var lineWidth = Math.Max(1.2, thick * px * (1 - 0.7 * k));
                output.Add(0); output.Add(layer); output.Add((float)x); output.Add((float)y);
                output.Add((float)nx); output.Add((float)ny); output.Add((float)(lineWidth / 2)); output.Add(0);
                x = nx; y = ny;
                ang += Math.Cos(ang) * 0.085 * (0.4 + k) + (R.Next() - 0.5) * 0.16;      // bend towards the ground
                ang += W8.Bend(nod, k, depth);
                if (i > (depth != 0 ? 0 : 3))
                {
                    for (int s = 0; s < 2; s++)
                    {
                        var side = s == 0 ? 1.0 : -1.0;
                        if (R.Next() < 0.2) continue;
                        var l = (0.050 + 0.040 * R.Next()) * px;
                        var rot = ang + side * (0.55 + 0.6 * R.Next()) + 0.25;
                        var wid = l * (0.27 + 0.08 * R.Next());
                        var slot = layer * 4096 + leafNo;
                        if (mem.A.Length <= slot) mem.Make(slot + 512);
                        var q = W8.Leaf(leafNo, mem, slot); leafNo += 1;
                        output.Add(1); output.Add(layer); output.Add((float)x); output.Add((float)y);
                        output.Add((float)(rot + q.Th)); output.Add((float)l); output.Add((float)(wid * q.Sq)); output.Add(0);
                    }
                }
                if (depth < 2 && i > 2 && R.Next() < (depth != 0 ? 0.16 : 0.34))
                {
                    double r1 = R.Next(), r2 = R.Next(), r3 = R.Next();
                    Branch(x, y, ang + (r1 < 0.5 ? 1 : -1) * (0.5 + 0.5 * r2), len * (0.42 + 0.25 * r3), thick * 0.6, depth + 1);
                }
            }
        }
        // three plants at three depths, each kept in its own colour channel: near (red), middle (green), far (blue)
        var offsets = new[] { 0, 4.7, 9.1 };
        var stems = new (double dx, double a, double len)[] { (-0.10, -1.95, 0.44), (-0.03, -1.70, 0.56), (0.02, -1.50, 0.60), (0.08, -1.28, 0.52), (0.12, -1.02, 0.40), (-0.14, -2.25, 0.34) };
        for (int l = 0; l < 3; l++)
        {
            layer = l;
            R = new Rng((uint)(8 + l * 5));
            W8 = new FicusWind(t + offsets[l], ms, 31 * l + 3); brNo = 0; leafNo = 0;
            foreach (var (dx, a, len) in stems)
            {
                double r1 = R.Next(), r2 = R.Next();
                Branch(W / 2 + (dx + (l - 1) * 0.16) * px, W + 0.02 * px, a + (r1 - 0.5) * 0.2, len * (0.85 + 0.3 * r2), 0.010, 0);
            }
        }
        return output.ToArray();
    }

    /// The leaf's outline (leafPath()): two curves from its base to its tip, mirrored. The half width along the leaf,
    /// in parts of `wid`, at 256 steps from base to tip (257 numbers).
    public static readonly float[] LeafProfile = MakeLeafProfile();

    static float[] MakeLeafProfile()
    {
        // the upper curve: (0, 0) → (0.22, 1) → (0.68, 0.72) → (1, 0); its x only grows, so y is a function of x
        static (double, double) At(double t)
        {
            var u = 1 - t;
            return (3 * u * u * t * 0.22 + 3 * u * t * t * 0.68 + t * t * t, 3 * u * u * t * 1 + 3 * u * t * t * 0.72);
        }
        var o = new float[257];
        for (int i = 0; i <= 256; i++)
        {
            var s = i / 256.0;
            double lo = 0.0, hi = 1.0;
            for (int j = 0; j < 50; j++) { var m = (lo + hi) / 2; if (At(m).Item1 < s) lo = m; else hi = m; }
            o[i] = (float)At((lo + hi) / 2).Item2;
        }
        return o;
    }
}
