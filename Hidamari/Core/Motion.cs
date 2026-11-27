namespace Hidamari;

// The live motion of prototype/live-motion.html, as the Mac app's Motion.swift keeps it (update 1.1): the same rules and
// numbers. Tests/Program.cs runs this clock for a day and a week, as tools/check-motion.swift does on the Mac.
// Mac update 1.4: the Tree's rustle and the motion of the window and the blinds (design/window-motion), kept the same way.

/// What the shader's motion uniforms are for one frame: uT (seconds), uWind (the water's strength), uWindLeaves.
/// `Still` is the approved still picture.
/// Mac update 1.4 (the window and the blinds, design/window-motion):
/// - `Moving`: uMoving. The window scenes draw a lighter frame while they move, and the rain and the snow are drawn differently.
/// - `WindMs`: the wind in m/s the window's leaves are posed with (their own scale, e = ms / 15).
/// - `LeafT`: the leaves' clock (seconds): posed on the processor in 64-bit numbers, so it runs on and is never started again.
/// - `Wt`: the rain's slow clock uWT while the window moves. Still, uWT is the frame's minutes / 25 and changes when the
///   light is worked out again (every five minutes): in motion that would make the large drops and old trails jump, so
///   here it runs on smoothly with real time. Negative: the still uWT.
public readonly record struct Motion(double T, double Wind, double WindLeaves, bool Moving = false, double WindMs = 0, double LeafT = 0, double Wt = -1)
{
    public static readonly Motion Still = new(0, 0, 0);
    public override string ToString() =>
        $"t {T:F3} s, wind {Wind:F3}, leaves {WindLeaves:F3}" + (Moving ? $", moving: {WindMs:F1} m/s, leaves' clock {LeafT:F1} s, rain's clock {Wt:F3}" : "");
}

/// The motion of one frame. While one motion turns into another (`From` → `Motion`), the frame is both of them, mixed:
/// `Mix` is the share of `Motion`, 0…1.
public readonly record struct MotionFrame(Motion Motion, Motion? From = null, double Mix = 1)
{
    public static readonly MotionFrame Still = new(Motion.Still);
    public bool IsStill => Motion == Motion.Still && (From == null || From == Motion.Still || Mix >= 1);
}

/// Wind in metres per second turned into the shader's strengths, as live-motion.html's windStrength() and leafWind().
public static class Wind
{
    /// The wind while Live weather is off or has no answer (Ed, 18 November 2026).
    public const double Steady = 4.0;

    /// windStrength(): calm air barely moves; 7.5 m/s is a gentle sway.
    public static double Strength(double ms) => Math.Min(1, 0.02 + 0.10 * Math.Pow(ms / 7.5, 1.3));
    /// leafWind(): the leaves take a narrower, livelier part of the scale.
    public static double LeafWind(double ms) => 7 + 0.6 * Math.Min(ms, 15);
    /// What uWindLeaves is for this wind.
    public static double Leaves(double ms) => Strength(LeafWind(ms));

    /// Mac update 1.4: how strongly the Tree rustles in this wind, on the sketch's wind slider (0…1; Ed looked at 0.4 and
    /// approved it). As with the dapples' leaves, the crown never stands quite still: calm air gives 0.15. The steady
    /// 4 m/s (Live weather off) gives 0.40; 10 m/s 0.78; from about 13.6 m/s on, the slider's full 1.
    public static double Tree(double ms) => Math.Min(1, 0.15 + 0.0625 * Math.Max(ms, 0));
}

/// Keeps the motion's clock: which uT, uWind and uWindLeaves each frame gets (MotionClock on the Mac).
///
/// The graphics card counts in 32-bit numbers, so a uT that just runs on grows too coarse within hours, and a new wind
/// (once an hour from the weather) would make the picture jump. So:
/// - **Water** has one phase, ph = uPhase + uT · (0.05 + 2.25 · uWind), and its picture repeats when ph goes round 2π.
///   The clock adds the phase up itself, in 64-bit numbers, and gives the shader a uT that lands on the same phase:
///   never more than one turn. A new wind changes the speed smoothly over `WaterRamp` seconds. While it moves, the
///   water no longer depends on the time of day (uPhase), so the light's recomputation every five minutes does not nudge it.
/// - **Dapples** have no such repeat (nor has the **Tree**'s rustle, Mac update 1.4, kept the same way). When the wind
///   changes, or every `LeavesSpan` seconds, a new clock starts at 0 with the new wind, and for `Crossfade` seconds the
///   frame is the old motion and the new one mixed.
/// - **The window and the blinds** (Mac update 1.4) are kept like the dapples, plus the leaves' own clock that never
///   starts again, and the rain's slow clock.
public sealed class MotionClock
{
    /// Dapples: the longest a clock runs before a new one takes over (seconds).
    public const double LeavesSpan = 20 * 60;
    /// Dapples: how long the old and the new motion are mixed.
    public const double Crossfade = 4;
    /// Water: how long a new wind takes to set the new speed.
    public const double WaterRamp = 8;

    /// Leaves: the dapples; Water; Tree: the Tree's rustle (Mac update 1.4), kept like the dapples, with its own strength;
    /// Window: the window and the blinds (Mac update 1.4, design/window-motion), kept like the dapples, plus the leaves' clock.
    public enum Kind { Leaves, Water, Tree, Window }
    public readonly Kind Of;

    /// The clock a scene (its number) moves with (Kind(scene:) on the Mac).
    public static Kind KindFor(int scene) => scene == 3 ? Kind.Water : scene == 7 ? Kind.Tree : scene == 1 || scene == 2 ? Kind.Window : Kind.Leaves;

    // dapples
    double epochStart, epochLeaves;
    (double start, double leaves, double fadeStart)? old;
    // window: the wind of this stretch in m/s, and the old one's while they are mixed; the leaves' clock starts once;
    // the rain's slow clock where this stretch began, and the old stretch's
    double epochMs, oldMs;
    readonly double leafStart;
    double epochWT, oldWT;
    /// The slow clock is set back to the frame's minutes / 25 at a new stretch once it has run past this (a few days
    /// without a pause), so its 32-bit steps stay fine; the crossfade hides the change.
    public const double WtLimit = 500.0;

    // water
    double phase, last, speedFrom, speedTo, rampStart;

    /// Starts at `now` (seconds, any steady clock) from the still picture: the first frame of water is exactly the still
    /// one (`basePhase` is the frame's uPhase); the first frame of dapples is uT = 0 with this wind.
    public MotionClock(Kind kind, double now, double windMs, double basePhase)
    {
        Of = kind;
        epochStart = now;
        epochLeaves = kind == Kind.Tree ? Wind.Tree(windMs) : Wind.Leaves(windMs);
        epochMs = windMs;
        leafStart = now;
        epochWT = StillWT(basePhase);
        phase = Wrap(basePhase);
        last = now;
        var w = Wind.Strength(windMs);
        speedFrom = w; speedTo = w; rampStart = now - WaterRamp;
    }

    public static double Wrap(double x) { var r = x % (2 * Math.PI); return r < 0 ? r + 2 * Math.PI : r; }
    public static double Rate(double wind) => 0.05 + 2.25 * wind;
    static double Smooth(double x) { var t = Math.Min(Math.Max(x, 0), 1); return t * t * (3 - 2 * t); }

    /// Water: the strength now, on its way from the old wind to the new one.
    double WaterWind(double now) => speedFrom + (speedTo - speedFrom) * Smooth((now - rampStart) / WaterRamp);

    /// The motion of the frame at `now`, for this wind. `basePhase` is the frame's uPhase.
    public MotionFrame Frame(double now, double windMs, double basePhase)
    {
        if (Of == Kind.Water)
        {
            var target = Wind.Strength(windMs);
            if (target != speedTo)
            {
                speedFrom = WaterWind(now); speedTo = target; rampStart = now;
            }
            // the phase runs on at the speed of the moment (the middle of the step, so a ramp adds up right)
            var dt = Math.Max(0, now - last);
            var w = WaterWind(now - dt / 2);
            phase = Wrap(phase + Rate(w) * dt);
            last = now;
            var wNow = WaterWind(now);
            var t = Wrap(phase - basePhase) / Rate(wNow);
            return new MotionFrame(new Motion(t, wNow, Wind.Leaves(windMs)));
        }
        if (Of == Kind.Window)
        {
            // as the dapples: a new stretch every LeavesSpan seconds and with every new wind, the old one mixed in for
            // `Crossfade` seconds. The leaves keep their own clock; only their wind changes with the stretch.
            if (old == null && (windMs != epochMs || now - epochStart >= LeavesSpan))
            {
                old = (epochStart, 0.0, now);
                oldMs = epochMs;
                oldWT = epochWT;
                // the slow clock runs on through the new stretch, unless it has run far
                var on = epochWT + (now - epochStart) / 1500;
                epochWT = on < WtLimit ? on : StillWT(basePhase);
                epochStart = now;
                epochMs = windMs;
            }
            var wm = WindowMotion(now - epochStart, epochMs, now - leafStart, epochWT + (now - epochStart) / 1500);
            if (old is not { } wo) return new MotionFrame(wm);
            var wk = Smooth((now - wo.fadeStart) / Crossfade);
            if (wk >= 1) { old = null; return new MotionFrame(wm); }
            return new MotionFrame(wm, WindowMotion(now - wo.start, oldMs, now - leafStart, oldWT + (now - wo.start) / 1500), wk);
        }
        // Leaves and Tree. The Tree: the same clock, its strength in uWind (what its shadow pass reads), uWindLeaves unused
        var tree = Of == Kind.Tree;
        var leaves = tree ? Wind.Tree(windMs) : Wind.Leaves(windMs);
        if (old == null && (leaves != epochLeaves || now - epochStart >= LeavesSpan))
        {
            old = (epochStart, epochLeaves, now);
            epochStart = now;
            epochLeaves = leaves;
        }
        var motion = tree ? new Motion(now - epochStart, epochLeaves, 0)
            : new Motion(now - epochStart, Wind.Strength(windMs), epochLeaves);
        if (old is not { } o) return new MotionFrame(motion);
        var k = Smooth((now - o.fadeStart) / Crossfade);
        if (k >= 1) { old = null; return new MotionFrame(motion); }
        var before = tree ? new Motion(now - o.start, o.leaves, 0)
            : new Motion(now - o.start, motion.Wind, o.leaves);
        return new MotionFrame(motion, before, k);
    }

    /// The window's motion at clock `t` and wind `ms`, as window-motion.html's draw() sets it; `wt` the rain's slow clock.
    public static Motion WindowMotion(double t, double ms, double leafT, double wt) =>
        new(t, Wind.Strength(ms), Wind.Leaves(ms), Moving: true, WindMs: ms, LeafT: leafT, Wt: wt);

    /// The still uWT of rain for the frame whose uPhase is `basePhase`: its minutes / 25 (WallLighting.Uniforms).
    public static double StillWT(double basePhase) => basePhase / 6.2832 * 1440 / 25;

    /// True while dapples mix an old motion into the new one.
    public bool IsCrossfading => old != null;
}

/// Which refreshes get a frame: exactly `fps` a second on average, whatever the screen's rate (60, 75, 120, 144, a
/// rate that changes). A refresh gets a frame when it is at most half a refresh early for the next frame's moment;
/// the moments themselves are exactly 1/fps apart. Pure arithmetic, so Tests/Program.cs checks it on Linux.
public sealed class FrameSlots
{
    double next = double.NaN, refresh = 1 / 60.0;
    public long Skipped { get; private set; }

    /// Forget the old moments (a pause, a new rate).
    public void Reset() => next = double.NaN;

    /// A refresh at `now` (seconds). True when it gets a frame.
    public bool Due(double now, int fps, double refreshSeconds)
    {
        if (refreshSeconds > 0.0005 && refreshSeconds < 0.2) refresh = refresh * 0.9 + refreshSeconds * 0.1;
        double period = 1.0 / fps;
        if (double.IsNaN(next)) { next = now + period; return true; }
        if (now < next - refresh / 2) return false;
        // frames whose moment went by without one (the drawing took too long, the computer was busy)
        double late = now - next;
        if (late > 2 * period) { Skipped += (long)Math.Floor(late / period); next = now + period; return true; }
        if (late > period - refresh / 2) { long k = (long)Math.Floor((late + refresh / 2) / period); Skipped += k; next += k * period; }
        next += period;
        return true;
    }
}
