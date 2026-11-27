using System.Diagnostics;

namespace Hidamari;

/// Animation (the Mac's update 1.1, Animation.swift): the dapples and the water move.
///
/// Every screen with a wall has its own pacing thread. It waits for its screen's refresh and draws a frame on those
/// refreshes that are due: exactly 30 a second on mains power, 15 on battery or with a power saver on (Power), whatever
/// the screen's own rate. Each screen keeps its own motion clock (MotionClock), so a screen that was covered goes on
/// from its own last frame when it is seen again: nothing jumps.
///
/// Not a single frame is drawn — the thread sleeps until it is woken — while: the Animation switch is off; the scene
/// does not move (window, blinds); that screen's wall cannot be seen (windows over all of it, a full-screen game or
/// video); the screens are off; the screen is locked or another user is on the computer; the computer sleeps; the
/// session is a Remote Desktop one (the motion would only be sent over the network, frame by frame, and the remote
/// computer shows no wallpaper of this one anyway). Each stop and start is written in the journal with its reason.
///
/// Turned on and off by hand, the motion fades in from the still picture (1.5 s) and back into it (1 s), as on the Mac.
sealed class Animator : IDisposable
{
    public const double FadeIn = 1.5, FadeOut = 1.0;
    /// The journal gets a summary of each screen's motion this often (seconds).
    public const double SummaryEvery = 600;

    readonly D3DRenderer renderer;
    readonly SynchronizationContext ui;
    readonly Dictionary<WallWindow, Pacer> pacers = new();
    readonly object gate = new();
    static readonly Stopwatch clock = Stopwatch.StartNew();
    public static double Now => clock.Elapsed.TotalSeconds;

    // what every screen shares (under gate)
    WallState? baseState;
    double windMs = Wind.Steady;
    bool switchOn;
    int scene = -1;
    string? globalBlock = "not started";
    (int fps, string why) power = Power.Wanted();
    bool ended;

    /// For the checks: seconds added to every screen's motion clock (a day or a week of motion without waiting).
    public double ClockOffset;
    /// For the checks only: moving frames this much smaller (GitHub's machines draw in software, about two full frames
    /// a second; the pacing is checked with small frames). 1 everywhere else.
    public double CheckScale = 1;
    /// Called on the app's thread when a screen's wall must get a still frame (the motion faded out into it).
    public event Action<WallWindow, string>? StillWanted;

    public Animator(D3DRenderer renderer, SynchronizationContext ui)
    {
        this.renderer = renderer;
        this.ui = ui;
        renderer.DeviceRemade += () => ui.Post(_ => { foreach (var w in pacers.Keys.ToList()) try { renderer.Again(w); } catch (Exception e) { Log.Write($"animation: {e.Message}"); } }, null);
    }

    public int Fps { get { lock (gate) return power.fps; } }
    public string FpsWhy { get { lock (gate) return power.why; } }

    // ---------- what the app tells ----------

    /// The walls now (after every build): a pacer for each, the old ones end.
    public void SetWalls(IEnumerable<WallWindow> walls)
    {
        var now = walls.ToHashSet();
        foreach (var (w, p) in pacers.Where(kv => !now.Contains(kv.Key)).ToList()) { p.End(); pacers.Remove(w); }
        foreach (var w in now) if (!pacers.ContainsKey(w)) pacers[w] = new Pacer(this, w);
    }

    /// The still state of the moment (every still frame, and every five minutes) and the wind for it.
    public void SetBase(WallState state, double wind)
    {
        lock (gate) { baseState = state; windMs = wind; }
    }

    /// The scene the walls show now: a new one starts its own motion (its first frame is the still one).
    public void Scene(int sceneNow)
    {
        lock (gate)
        {
            if (scene == sceneNow) return;
            scene = sceneNow;
        }
        foreach (var p in pacers.Values) p.NewScene();
    }

    /// Looks again at everything that starts or stops the motion. `blocks` is what stops it on every screen (or null),
    /// `hidden` says for each screen what hides its wall (or null). The app's thread.
    public void Update(string trigger, bool animates, int sceneNow, string? blocks, Func<WallWindow, string?> hidden)
    {
        var wanted = Power.Wanted();
        Scene(sceneNow);
        bool fpsChanged, switched;
        lock (gate)
        {
            fpsChanged = wanted.fps != power.fps;
            power = wanted;
            switched = switchOn != animates;
            switchOn = animates;
            globalBlock = ended ? "quit" : blocks;
        }
        if (fpsChanged) Log.Write($"animation: {wanted.fps} frames a second ({wanted.why})");
        foreach (var p in pacers.Values)
        {
            if (switched) p.Switched(animates);
            string? block = null;
            if (ended) block = "quit";
            else if (!animates && !p.FadingOut) block = "switch off";
            else if (!WallLighting.Moves(sceneNow)) block = "scene does not move";
            else if (blocks != null) block = blocks;
            else if (!p.Wall.Shown) block = "wall not shown yet";
            else if (hidden(p.Wall) is { } h) block = $"wall hidden ({h})";
            p.Set(block, trigger);
        }
    }

    /// The motion the wall shows now (or would show, while it pauses): the still frames are drawn with it, so a still
    /// frame in between (the sun moved, a choice) never makes the motion jump.
    public MotionFrame Held(WallWindow wall) => pacers.TryGetValue(wall, out var p) ? p.Held : MotionFrame.Still;

    /// Any screen moving now.
    public bool Running => pacers.Values.Any(p => p.Running);

    /// What runs and why, for the journal and the checks.
    public object Status() => new
    {
        fps = Fps, fpsWhy = FpsWhy, clockOffset = ClockOffset,
        screens = pacers.Values.Select(p => p.Status()).ToArray(),
    };

    public string Summary() => pacers.Count == 0 ? "no walls" : string.Join("; ", pacers.Values.Select(p => p.Summary()));

    public void End()
    {
        lock (gate) { ended = true; globalBlock = "quit"; }
        foreach (var p in pacers.Values) p.End();
        pacers.Clear();
    }

    public void Dispose() => End();

    // ---------- one screen ----------

    sealed class Pacer
    {
        readonly Animator a;
        public readonly WallWindow Wall;
        readonly Thread thread;
        readonly AutoResetEvent wake = new(false);
        volatile string? block = "not started";
        volatile bool stop;
        string reason = "not started";

        // the motion (pacing thread, and the app's thread under `me`)
        readonly object me = new();
        MotionClock? motion;
        int motionScene = -1;
        double motionTime, resumedAt, resumedTime;
        bool running;
        (bool @out, double start)? fade;
        MotionFrame held = MotionFrame.Still;
        WallUniformValues? baseU;
        WallState? baseFor;
        int baseW, baseH;

        // what the journal says about it
        readonly FrameSlots slots = new();
        long frames, ticks, failed, skippedAtStart;
        double drawTotal, drawMax, runStart, lastSummary, lastTick;
        long framesAtStart;
        string pacing = "";

        public Pacer(Animator a, WallWindow wall)
        {
            this.a = a;
            Wall = wall;
            thread = new Thread(Loop) { IsBackground = true, Name = $"Hidamari motion {wall.Screen.Short}", Priority = ThreadPriority.AboveNormal };
            thread.Start();
        }

        public bool Running { get { lock (me) return running; } }
        public bool FadingOut { get { lock (me) return fade is { @out: true }; } }
        public MotionFrame Held { get { lock (me) return held; } }

        double MotionNow() => running ? resumedTime + (Now - resumedAt) : motionTime;

        public void NewScene()
        {
            lock (me) { motion = null; fade = null; held = MotionFrame.Still; motionScene = -1; }
        }

        public void Switched(bool on)
        {
            lock (me)
            {
                if (on) { fade = (false, MotionNow() + a.ClockOffset); motion = null; held = MotionFrame.Still; }
                else if (running) fade = (true, MotionNow() + a.ClockOffset);
                else
                {
                    // off while hidden: the wall goes back to the still picture the next time it gets a frame
                    motion = null; fade = null; held = MotionFrame.Still;
                }
            }
        }

        /// Runs (null) or stops (the reason), on the app's thread.
        public void Set(string? why, string trigger)
        {
            bool start = false, pause = false;
            lock (me)
            {
                block = why;
                if (why == null && !running)
                {
                    running = true; start = true;
                    resumedAt = Now; resumedTime = motionTime;
                    slots.Reset();
                    runStart = Now; lastSummary = runStart; framesAtStart = frames; skippedAtStart = slots.Skipped;
                }
                else if (why != null && running)
                {
                    motionTime = MotionNow();
                    running = false; pause = true;
                }
            }
            if (start)
            {
                reason = "";
                Log.Write($"animation: running on {Wall.Screen.Short}, {a.Fps} frames a second ({a.FpsWhy}), scene {a.scene}, wind {a.windMs:F1} m/s, " +
                          $"motion clock {MotionNowLocked():F1} s ({trigger})");
                wake.Set();
            }
            else if (pause)
            {
                reason = why!;
                Log.Write($"animation: paused on {Wall.Screen.Short}, {why} ({trigger}); {Summary()}");
            }
            else if (why != null && why != reason)
            {
                reason = why;
                Log.Write($"animation: stays still on {Wall.Screen.Short}, {why}");
            }
        }

        double MotionNowLocked() { lock (me) return MotionNow(); }

        public void End()
        {
            stop = true;
            block = "quit";
            wake.Set();
            thread.Join(2000);
            wake.Dispose();
        }

        void Loop()
        {
            VBlank? vblank = null;
            try
            {
                while (!stop)
                {
                    if (block != null)
                    {
                        // nothing at all until woken
                        vblank?.Dispose(); vblank = null;
                        wake.WaitOne();
                        continue;
                    }
                    if (vblank == null) { vblank = new VBlank(Wall.Screen); pacing = vblank.How; lastTick = 0; Log.Write($"animation: {Wall.Screen.Short} paced by {pacing}"); }
                    vblank.Next();
                    if (stop || block != null) continue;
                    double real = Now;
                    ticks++;
                    double gap = lastTick > 0 ? real - lastTick : 0;
                    lastTick = real;
                    if (!slots.Due(real, a.Fps, gap)) continue;
                    Tick(real);
                }
            }
            catch (Exception e) { Log.Write($"animation: the pacing of {Wall.Screen.Short} stopped: {e}"); }
            finally { vblank?.Dispose(); }
        }

        void Tick(double real)
        {
            WallState? state; double wind; int scene;
            lock (a.gate) { state = a.baseState; wind = a.windMs; scene = a.scene; }
            if (state == null) return;
            WallUniformValues u0;
            MotionFrame f;
            bool fadedOut = false;
            lock (me)
            {
                if (!running) return;
                var (w, h) = D3DRenderer.FrameSize(Wall.Screen);
                if (a.CheckScale < 1) { w = Math.Max(8, (int)Math.Round(w * a.CheckScale)); h = Math.Max(8, (int)Math.Round(h * a.CheckScale)); }
                if (baseU == null || baseFor != state || baseW != w || baseH != h)
                {
                    baseU = WallLighting.Uniforms(state.Input with { Width = w, Height = h, Motion = Motion.Still });
                    baseFor = state; baseW = w; baseH = h;
                }
                u0 = baseU;
                double now = MotionNow() + a.ClockOffset;
                double phase = WallLighting.Phase(state.Input.Minutes);
                if (motion == null || motionScene != scene)
                {
                    // the clock's kind follows the scene (Mac update 1.4: the Tree's rustle, the window and the blinds)
                    motion = new MotionClock(MotionClock.KindFor(scene), now, wind, phase);
                    motionScene = scene;
                }
                f = motion.Frame(now, wind, phase);
                if (fade is { } fd)
                {
                    double k = Math.Clamp((now - fd.start) / (fd.@out ? FadeOut : FadeIn), 0, 1), s = k * k * (3 - 2 * k);
                    if (fd.@out)
                    {
                        f = new MotionFrame(Motion.Still, f.Motion, s);
                        if (k >= 1) { fade = null; motion = null; f = MotionFrame.Still; fadedOut = true; }
                    }
                    else
                    {
                        f = new MotionFrame(f.Motion, Motion.Still, s);
                        if (k >= 1) fade = null;
                    }
                }
                held = f;
            }
            WallUniformValues U(Motion m) => Full(scene) ? WallLighting.Uniforms(state.Input with { Width = (int)u0.Res[0], Height = (int)u0.Res[1], Motion = m }) : With(u0, m);
            var u = U(f.Motion);
            var from = f.From is { } fm && f.Mix < 1 ? U(fm) : null;
            var sw = Stopwatch.StartNew();
            try
            {
                a.renderer.Present(Wall, u, from, f.Mix, false);
                frames++;
            }
            catch (D3DRenderer.TargetGone) { failed++; }
            catch (Exception e)
            {
                failed++;
                try { if (!a.renderer.Lost(e, $"a moving frame on {Wall.Screen.Short}")) Log.Write($"animation: a frame failed on {Wall.Screen.Short}: {e.Message}"); }
                catch (Exception gaveUp) { Log.Write($"animation: {gaveUp.Message}"); block = "Direct3D gave up"; }
            }
            double ms = sw.Elapsed.TotalMilliseconds;
            drawTotal += ms; drawMax = Math.Max(drawMax, ms);
            if (fadedOut)
            {
                // the motion has faded into the still picture: the wall stays on it, and the thread stops
                block = "switch off";
                a.ui.Post(_ => { lock (me) { if (running) { motionTime = MotionNow(); running = false; } } reason = "switch off"; Log.Write($"animation: faded out on {Wall.Screen.Short}; {Summary()}"); a.StillWanted?.Invoke(Wall, "the motion faded out"); }, null);
            }
            if (real - lastSummary >= SummaryEvery)
            {
                lastSummary = real;
                Log.Write($"animation: {Summary()}");
            }
        }

        /// The still uniforms with this motion: for the dapples and the water only the three motion numbers differ from
        /// frame to frame.
        static WallUniformValues With(WallUniformValues u, Motion m) => u with { T = m.T, Wind = m.Wind, WindLeaves = m.WindLeaves };

        /// Mac update 1.4: the Tree (its shadow's clock and wind), the window and the blinds (the plant's pose, the rain's
        /// clock, the moving shader) take more from the motion, so their uniforms are worked out whole for every frame,
        /// as the Mac's MetalRenderer does for every scene.
        static bool Full(int scene) => scene is not (0 or 3);

        public string Summary()
        {
            double secs, rate;
            long n, skipped;
            bool run;
            lock (me) { run = running; secs = Math.Max(0.001, Now - runStart); n = frames - framesAtStart; skipped = slots.Skipped - skippedAtStart; }
            if (runStart == 0) return $"{Wall.Screen.Short}: has not run yet";
            rate = n / secs;
            return $"{Wall.Screen.Short}: {n} frames in {secs:F0} s ({rate:F1} a second, {a.Fps} wanted, {a.FpsWhy}){(run ? "" : ", stopped")}, " +
                   $"skipped {skipped}, failed {failed}, drawing {(frames > 0 ? drawTotal / frames : 0):F2} ms a frame on average (longest {drawMax:F1} ms), " +
                   $"refreshes {ticks}, paced by {pacing}";
        }

        public object Status()
        {
            lock (me)
            {
                return new
                {
                    screen = Wall.Screen.Short, running, reason = block ?? "", frames, skipped = slots.Skipped, failed, ticks,
                    motionTime = MotionNow(), pacing, held = held.ToString(), drawMs = frames > 0 ? drawTotal / frames : 0,
                };
            }
        }
    }
}
