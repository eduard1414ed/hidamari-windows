using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

// Stage 4: the wall drawn by Direct3D, Animation. What is checked here (the rest of the check runs with Direct3D too,
// since it is the default now):
// - Direct3D draws by default; the same states as the Mac's comparison drawn by Direct3D and by the web engine on this
//   machine, compared (and kept for the comparison with Metal on the Mac: tools/windows-stage4.sh judge);
// - the web engine takes over by itself when Direct3D cannot draw, and when asked for (remembered);
// - a lost graphics device is made anew; lost again and again, the web engine takes over;
// - the Animation row: only at the dapples and the water, its switch, kept, missing in an old file = off;
// - the motion: its rate (30 on mains, 15 on battery), and not one frame while it must not run, then on without a jump;
// - with "full": memory and processor (still with Direct3D and with the web engine, moving at 30 and 15), the size of
//   Hidamari.exe, and the frames of the videos for Ed (windows-2025 only).
static partial class Check
{
    static string Stage4Dir => Path.Combine(outDir, "stage4");
    static readonly Dictionary<string, object?> numbers = new();

    static string Here(string file) => Path.Combine(AppContext.BaseDirectory, "stage4", file);

    static JsonElement Reply(Run run, object cmd)
    {
        var text = Command(run, cmd);
        try { return JsonDocument.Parse(text).RootElement.Clone(); } catch { return default; }
    }

    static JsonElement Motion(Run run) => Reply(run, new { @do = "motion" });
    static JsonElement Screen0(JsonElement reply) =>
        reply.ValueKind == JsonValueKind.Object && reply.TryGetProperty("motion", out var m) && m.ValueKind == JsonValueKind.Object
            ? m.GetProperty("screens")[0] : default;
    static long Frames(JsonElement reply) => Screen0(reply) is { ValueKind: JsonValueKind.Object } s ? s.GetProperty("frames").GetInt64() : -1;
    static long Skipped(JsonElement reply) => Screen0(reply) is { ValueKind: JsonValueKind.Object } s ? s.GetProperty("skipped").GetInt64() : -1;
    static bool Running(JsonElement reply) => Screen0(reply) is { ValueKind: JsonValueKind.Object } s && s.GetProperty("running").GetBoolean();
    static double MotionTime(JsonElement reply) => Screen0(reply) is { ValueKind: JsonValueKind.Object } s ? s.GetProperty("motionTime").GetDouble() : double.NaN;
    static string Reason(JsonElement reply) => Screen0(reply) is { ValueKind: JsonValueKind.Object } s ? s.GetProperty("reason").GetString() ?? "" : "no motion";

    /// Frames drawn and skipped over `seconds`, a second.
    /// A record, not a tuple: the numbers file writes properties (a tuple came out as {}).
    record RateResult(double drawn, double wanted, long skipped);

    static RateResult Rate(Run run, double seconds)
    {
        var a = Motion(run); var sw = Stopwatch.StartNew();
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        var b = Motion(run); double s = sw.Elapsed.TotalSeconds;
        long f = Frames(b) - Frames(a), k = Skipped(b) - Skipped(a);
        return new RateResult(Math.Round(f / s, 2), Math.Round((f + k) / s, 2), k);
    }

    static Dictionary<string, object?> MotionSettings(int scene, bool? animation) =>
        animation == null ? new() { ["scene"] = scene } : new() { ["scene"] = scene, ["animation"] = animation };

    static void Stage4Run()
    {
        Directory.CreateDirectory(Stage4Dir);
        numbers["exeMB"] = Math.Round(new FileInfo(app).Length / 1048576.0, 1);
        Result("stage 4", "Hidamari.exe", true, $"{numbers["exeMB"]} MB");
        Direct3DRun();
        Fallbacks();
        AnimationRun();
        if (full) Measurements();
        File.WriteAllText(Path.Combine(Stage4Dir, "numbers.json"), JsonSerializer.Serialize(numbers, new JsonSerializerOptions { WriteIndented = true }));
        facts["stage4"] = numbers;
    }

    // ---------- Direct3D by default, its frames against the web engine's ----------

    /// Quits the app and waits for it to be gone, so the next start is not a second copy.
    static void Stop(Run r)
    {
        Run.Signal("Quit");
        if (!r.Proc.WaitForExit(20000)) { Say($"the app (pid {r.Proc.Id}) did not quit in 20 s; ended"); try { r.Proc.Kill(true); } catch { } r.Proc.WaitForExit(5000); }
    }

    /// The mean of a picture's red, green and blue, 0–255, from a grid of points.
    static double Level(Bitmap b)
    {
        double sum = 0; int n = 0;
        for (int y = 5; y < b.Height; y += Math.Max(1, b.Height / 40))
            for (int x = 5; x < b.Width; x += Math.Max(1, b.Width / 40)) { var c = b.GetPixel(x, y); sum += (c.R + c.G + c.B) / 3.0; n++; }
        return n == 0 ? 0 : sum / n;
    }

    static void Direct3DRun()
    {
        const string g = "Direct3D";
        WriteSettings(MotionSettings(1, null));
        using var run = Run.App(app, Path.Combine(Stage4Dir, "d3d"));
        try
        {
            var first = run.Next("frame 1 (", TimeSpan.FromSeconds(120));
            numbers["d3dFirstFrameSeconds"] = first != null ? Math.Round(run.Since.Elapsed.TotalSeconds, 1) : null;
            var drawing = run.Lines().FirstOrDefault(l => l.Contains("app: drawing "));
            Result(g, "Direct3D draws by default", first != null && first.Contains("(direct3d)") && drawing?.Contains("Direct3D") == true,
                $"{drawing}; {first}");
            if (first == null) return;
            Thread.Sleep(3000);
            Desk.ToggleIcons(); Thread.Sleep(2500);
            var shot = Pictures.Shot(Path.Combine("stage4", "d3d-screen.png"));
            Desk.ToggleIcons(); Thread.Sleep(1500);
            using (var frame = run.Frame())
            {
                var s = frame == null ? new Pictures.Stats(255, new double[256], 0) : Pictures.VsFrame(shot, frame, ScreenRect, Work);
                // a black frame on a black screen is "the same" too: the frame must have a wall in it (the first run drew
                // nothing at all and passed this)
                var level = frame == null ? 0 : Level(frame);
                Result(g, "the screen shows Direct3D's frame (through DirectComposition)", s.Same && level > 8,
                    $"{(run.Lines().FirstOrDefault(l => l.Contains("desktop: ") && l.Contains("raised") || l.Contains("classic")) ?? "")}; {s}; frame's mean level {level:F1}/255");
            }

            // the same states drawn by both, here; Metal's on the Mac (tools/windows-stage4.sh judge)
            // full size for Metal (Direct3D only); half size for the web engine, which draws in software here
            // Windows 1.1 (the Mac's 1.4): the new scenes and motions as well (tools/windows-1-1.sh on the Mac)
            foreach (var (file, folder, web, scale) in new[] { ("states.json", "still", false, 1.0), ("motion-states.json", "motion", false, 1.0),
                                                                ("1.4-states.json", "still-1.4", false, 1.0), ("1.4-motion-states.json", "motion-1.4", false, 1.0),
                                                                ("states.json", "still-half", true, 0.5), ("motion-states.json", "motion-half", true, 0.5),
                                                                ("1.4-states.json", "still-1.4-half", true, 0.5), ("1.4-motion-states.json", "motion-1.4-half", true, 0.5) })
            {
                var dir = Path.Combine(Stage4Dir, "compare", folder);
                var sw = Stopwatch.StartNew();
                const int seconds = 2400;
                Reply(run, new { @do = "compare", states = Here(file), @out = dir, web, scale });
                var done = Path.Combine(dir, "done.json");
                while (!File.Exists(done) && sw.Elapsed < TimeSpan.FromSeconds(seconds) && !run.Proc.HasExited) Thread.Sleep(1000);
                var info = Run.Read(done);
                Say($"compare {folder}: {info} after {sw.Elapsed.TotalSeconds:F0} s");
                numbers[$"compare-{folder}-seconds"] = Math.Round(sw.Elapsed.TotalSeconds);
                if (!web) { Result(g, $"Direct3D drew the {folder} states for the comparison with Metal", info.Contains("\"ok\":true"), info); continue; }
                CompareFolder(g, folder, dir, JsonDocument.Parse(File.ReadAllText(Here(file))).RootElement.EnumerateArray().Select(e => e.GetProperty("name").GetString()!).ToList());
            }
        }
        finally { run.SaveLog("hidamari.log"); Stop(run); }
    }

    /// Direct3D's frames against the web engine's, state by state (the limits of the Mac's comparison).
    static void CompareFolder(string g, string what, string dir, List<string> names)
    {
        var lines = new List<string>();
        int ok = 0, missing = 0;
        var worst = new List<(string name, double mean, double within)>();
        foreach (var n in names)
        {
            using var a = Pictures.Load(Path.Combine(dir, "d3d", n + ".png"));
            using var b = Pictures.Load(Path.Combine(dir, "web", n + ".png"));
            if (a == null || b == null || a.Width != b.Width || a.Height != b.Height) { missing++; lines.Add($"{n}: missing or another size"); continue; }
            var (mean, within, max) = Channels(a, b);
            bool pass = mean.All(m => m <= 0.5) && within >= 0.999;
            if (pass) ok++;
            worst.Add((n, mean.Max(), within));
            lines.Add($"{n,-62} mean R {mean[0]:F3} G {mean[1]:F3} B {mean[2]:F3}  within 3: {100 * within:F4} %  max {max}  {(pass ? "ok" : "OUTSIDE")}");
        }
        File.WriteAllLines(Path.Combine(dir, "d3d-vs-web.txt"), lines);
        var w = worst.OrderByDescending(x => x.mean).ThenBy(x => x.within).Take(3).Select(x => $"{x.name} (mean {x.mean:F3}, within 3 {100 * x.within:F3} %)");
        numbers[$"d3dVsWeb-{what}"] = $"{ok} of {names.Count} within the limits, {missing} missing";
        Result(g, $"Direct3D and the web engine draw the same {what} states here", ok == names.Count,
            $"{ok} of {names.Count} within the limits (mean ≤ 0.5/255 a channel, 99.9 % within 3), {missing} missing; worst: {string.Join("; ", w)}");
    }

    static (double[] mean, double within, int max) Channels(Bitmap a, Bitmap b)
    {
        var pa = Bytes(a); var pb = Bytes(b);
        double r = 0, gg = 0, bb = 0; long ok = 0; int max = 0; long n = pa.Length / 4;
        for (long i = 0; i < n; i++)
        {
            int dr = Math.Abs(pa[i * 4 + 2] - pb[i * 4 + 2]), dg = Math.Abs(pa[i * 4 + 1] - pb[i * 4 + 1]), db = Math.Abs(pa[i * 4] - pb[i * 4]);
            r += dr; gg += dg; bb += db;
            int m = Math.Max(dr, Math.Max(dg, db));
            if (m <= 3) ok++;
            if (m > max) max = m;
        }
        return (new[] { r / n, gg / n, bb / n }, ok / (double)n, max);
    }

    static byte[] Bytes(Bitmap b)
    {
        var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var o = new byte[b.Width * b.Height * 4];
        for (int y = 0; y < b.Height; y++) Marshal.Copy(d.Scan0 + y * d.Stride, o, y * b.Width * 4, b.Width * 4);
        b.UnlockBits(d);
        return o;
    }

    // ---------- the web engine when Direct3D cannot draw, or when asked for ----------

    static void Fallbacks()
    {
        const string g = "fallback";
        // Direct3D cannot start: the web engine draws, by itself
        WriteSettings(MotionSettings(0, null));
        using (var run = Run.App(app, Path.Combine(Stage4Dir, "fallback-auto"), env: new() { ["HIDAMARI_D3D"] = "fail" }))
        {
            try
            {
                var first = run.Next("frame 1 (", TimeSpan.FromSeconds(120));
                var why = run.Lines().FirstOrDefault(l => l.Contains("d3d: cannot draw here"));
                Result(g, "Direct3D cannot start: the web engine draws by itself", first != null && why != null && !first.Contains("(direct3d)"), $"{why}; {first}");
            }
            finally { run.SaveLog("hidamari.log"); Stop(run); }
        }
        // asked for at the start, remembered, and back
        string? Start(string name, Dictionary<string, string>? env, out string? drawing)
        {
            using var run = Run.App(app, Path.Combine(Stage4Dir, name), env: env);
            try
            {
                var f = run.Next("frame 1 (", TimeSpan.FromSeconds(120));
                drawing = run.Lines().FirstOrDefault(l => l.Contains("app: drawing "));
                return f;
            }
            finally { run.SaveLog("hidamari.log"); Stop(run); }
        }
        var web = Start("fallback-asked", new() { ["HIDAMARI_RENDERER"] = "web" }, out var d1);
        var kept = Run.Read(SettingsFile).Contains("\"renderer\":\"web\"") || Run.Read(SettingsFile).Contains("\"renderer\": \"web\"");
        var again = Start("fallback-kept", new(), out var d2);
        var back = Start("fallback-back", new() { ["HIDAMARI_RENDERER"] = "direct3d" }, out var d3);
        Result(g, "the web engine asked for (HIDAMARI_RENDERER=web) draws, is remembered, and Direct3D comes back the same way",
            web != null && !web.Contains("(direct3d)") && kept && again != null && !again.Contains("(direct3d)") && back != null && back.Contains("(direct3d)"),
            $"asked: {d1}; settings keep it: {kept}; next start: {d2}; HIDAMARI_RENDERER=direct3d: {d3}");

        // the device lost: made anew; lost three times in a row: the web engine
        const string l = "device lost";
        WriteSettings(MotionSettings(0, null));
        using var r = Run.App(app, Path.Combine(Stage4Dir, "device-lost"));
        try
        {
            if (r.Next("frame 1 (", TimeSpan.FromSeconds(120)) == null) { Result(l, "device lost", false, "no first frame"); return; }
            Thread.Sleep(2000);
            r.Mark();
            Reply(r, new { @do = "loseDevice", times = 1 });
            var anew = r.Next("d3d: made anew", TimeSpan.FromSeconds(60));
            var frame = r.Next(x => x.Contains(" frame ") && x.Contains("(check: the device is lost)"), TimeSpan.FromSeconds(120));
            Thread.Sleep(2000);
            var st = WallStill(r, Path.Combine("stage4", "device-lost-after.png"));
            Result(l, "the graphics device lost once: made anew, the wall drawn again", anew != null && frame != null && frame.Contains("(direct3d)") && st.Same,
                $"{anew}; {frame}; screen vs frame: {st}");

            // a real restart of the graphics adapter, where Windows lets it be done
            r.Mark();
            var restart = RestartAdapter();
            var lost = r.Next(x => x.Contains("d3d: the graphics device was lost"), TimeSpan.FromSeconds(20));
            Run.Signal("Draw");
            var after = r.Next(x => x.Contains(" frame ") && x.Contains("(check)"), TimeSpan.FromSeconds(120));
            Result(l, "the graphics adapter restarted (pnputil): the wall goes on", lost != null ? (after?.Contains("(direct3d)") == true ? "PASS" : "FAIL") : (after != null ? "SKIP" : "FAIL"),
                $"{restart}; {(lost ?? "the drawing's device was not lost by it (on GitHub's machines Direct3D may draw in software)")}; next frame: {after}");

            r.Mark();
            Reply(r, new { @do = "loseDevice", times = 9 });
            var gave = r.Next("d3d: gave up", TimeSpan.FromSeconds(60));
            var webFrame = r.Next(x => x.Contains(" frame 1 (") || (x.Contains(" frame ") && !x.Contains("(direct3d)") && x.Contains(" on DISPLAY")), TimeSpan.FromSeconds(180));
            Thread.Sleep(3000);
            var st2 = WallStill(r, Path.Combine("stage4", "device-lost-web.png"));
            Result(l, "lost again and again: the web engine takes over, written in the journal", gave != null && webFrame != null && st2.Same, $"{gave}; {webFrame}; screen vs frame: {st2}");
        }
        finally { r.SaveLog("hidamari.log"); Stop(r); }
    }

    static string RestartAdapter()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("pnputil", "/enum-devices /class Display") { UseShellExecute = false, RedirectStandardOutput = true })!;
            var text = p.StandardOutput.ReadToEnd(); p.WaitForExit(20000);
            var id = text.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.StartsWith("Instance ID:"))?.Split(':', 2)[1].Trim();
            var name = text.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.StartsWith("Device Description:"))?.Split(':', 2)[1].Trim();
            if (id == null) return "no display adapter listed";
            var q = Process.Start(new ProcessStartInfo("pnputil", $"/restart-device \"{id}\"") { UseShellExecute = false, RedirectStandardOutput = true })!;
            var o = q.StandardOutput.ReadToEnd().Trim().Replace("\r", " ").Replace("\n", " "); q.WaitForExit(60000);
            Thread.Sleep(5000);
            return $"{name}: {o}";
        }
        catch (Exception e) { return $"pnputil: {e.Message}"; }
    }

    // ---------- Animation ----------

    static void AnimationRun()
    {
        const string g = "Animation";
        // a settings file of stage 3: no "animation" in it
        WriteSettings(MotionSettings(0, null));
        using (var run = Run.App(app, Path.Combine(Stage4Dir, "animation")))
        {
            try
            {
                if (run.Next("frame 1 (", TimeSpan.FromSeconds(120)) == null) { Result(g, "the motion", false, "no first frame"); return; }
                Thread.Sleep(3000);
                var m0 = Motion(run);
                Result(g, "an old settings file without Animation: off, the wall still", !m0.GetProperty("animates").GetBoolean() && !Running(m0) && run.Since_("animation: running").Count == 0,
                    $"animates {m0.GetProperty("animates")}, {Reason(m0)}");

                // the card: the row at the scenes that move (since 1.1: the dapples, the window, the blinds, the water, the Tree); its switch
                var (l, _) = OpenByIcon(run);
                if (l == null) { Result(g, "the card", false, "the card did not open"); return; }
                double Row(Layout x) => x.Checked.TryGetProperty("animationRow", out var v) ? v.GetDouble() : -1;
                var rows = new List<string>();
                bool rowsOk = true;
                foreach (var (sc, want) in new[] { (0, true), (1, true), (2, true), (3, true), (4, false), (6, false), (7, true), (8, false), (0, true) })
                {
                    var before = DateTime.UtcNow;
                    Command(run, new { @do = "choose", type = "set", change = new Dictionary<string, object> { ["scene"] = sc } });
                    Thread.Sleep(900);
                    var now = SettledLayout(run) ?? l;
                    rows.Add($"{sc}: {Row(now):F0}");
                    rowsOk &= want ? Row(now) > 20 : Row(now) == 0;
                    l = now;
                }
                Pictures.Shot(Path.Combine("stage4", "card-animation-row.png"));
                Result(g, "the Animation row only at the scenes that move (the dapples, the window, the blinds, the water, the Tree), the card's height follows", rowsOk, $"row height by scene (points): {string.Join(", ", rows)}");
                var on = ClickCard(run, l, "animation", until: x => x.Is("animation"));
                var started = run.Next("animation: running", TimeSpan.FromSeconds(20));
                Thread.Sleep(1500);
                Pictures.Shot(Path.Combine("stage4", "card-animation-on.png"));
                Esc(); Thread.Sleep(1500);
                bool saved = false;
                for (int i = 0; i < 20 && !saved; i++) { saved = Run.Read(SettingsFile).Replace(" ", "").Contains("\"animation\":true"); Thread.Sleep(250); }
                Result(g, "the switch turns the motion on, and is saved", on?.Is("animation") == true && started != null && saved, $"{started}; saved: {saved}");
                if (started == null) return;

                // the rate: 30 on mains, 15 on battery, switched on the fly. GitHub's machines have no graphics card and
                // draw a full frame in software in about 0.4 s: what that gives is written down, and the pacing itself is
                // checked with frames an eighth of the size (a real graphics card draws a full one in a few ms)
                Thread.Sleep(3000);
                var full = Rate(run, 10);
                numbers["motionFullSizeHere"] = new { drawn = full.drawn, wanted = full.wanted, full.skipped };
                Result(g, "full-size moving frames on this machine (no graphics card: drawn in software), written down", "INFO",
                    $"drawn {full.drawn:F2} a second of {full.wanted:F2} due, skipped {full.skipped} (the drawing too slow; counted, not piled up)");
                Command(run, new { @do = "motionScale", scale = 0.125 });
                Thread.Sleep(5000); // the last full-size frames done, the drawing settled at the small size
                var r30 = Rate(run, 20);
                Command(run, new { @do = "power", pretend = "battery" });
                Thread.Sleep(1500);
                var r15 = Rate(run, 20);
                Command(run, new { @do = "power", pretend = "saver" });
                Thread.Sleep(1500);
                var rSaver = Rate(run, 10);
                Command(run, new { @do = "power", pretend = "mains" });
                Thread.Sleep(1500);
                var rBack = Rate(run, 10);
                bool Near(double x, int f) => Math.Abs(x - f) <= f * 0.03;
                // GitHub's machines stall now and then for a second (windows-2022 once gave 28.36 for 30 here, all
                // three other parts exact): a part that is off is measured once more, and both numbers are written down
                RateResult? backFirst = null, mainsFirst = null;
                if (!Near(rBack.wanted, 30)) { backFirst = rBack; Thread.Sleep(1500); rBack = Rate(run, 10); }
                // the first part follows the full-size frames: windows-11-arm once counted 155 of them as skipped in it
                // (drawn exactly 30.06): it is measured once more on mains power, now that nothing is left behind
                if (!Near(r30.wanted, 30)) { mainsFirst = r30; Thread.Sleep(1500); r30 = Rate(run, 20); }
                numbers["motionRate"] = new { mains = r30, battery = r15, saver = rSaver, back = rBack, backFirst, mainsFirst };
                // the pacing must be exact; drawn may fall a little short when the machine's processor, which draws
                // here instead of a graphics card, is busy for a moment (those frames are skipped, not piled up)
                bool Drawn(RateResult x) => x.drawn >= x.wanted * 0.9;
                Result(g, "30 frames a second on mains power, 15 on battery and with energy saver, changed on the fly (frames an eighth of the size)",
                    Near(r30.wanted, 30) && Near(r15.wanted, 15) && Near(rSaver.wanted, 15) && Near(rBack.wanted, 30)
                    && Drawn(r30) && Drawn(r15) && Drawn(rSaver) && Drawn(rBack),
                    $"frames due a second: {r30.wanted:F2} / {r15.wanted:F2} / {rSaver.wanted:F2} / {rBack.wanted:F2}; drawn {r30.drawn:F2} / {r15.drawn:F2} / {rSaver.drawn:F2} / {rBack.drawn:F2}, " +
                    $"skipped (the drawing too slow) {r30.skipped} / {r15.skipped} / {rSaver.skipped} / {rBack.skipped}; " +
                    (mainsFirst is { } mf ? $"mains measured twice: first {mf.wanted:F2} due, {mf.drawn:F2} drawn, {mf.skipped} skipped; " : "") +
                    (backFirst is { } bf ? $"back on mains measured twice: first {bf.wanted:F2} due, {bf.drawn:F2} drawn; " : "") + $"{Screen0(Motion(run)).GetProperty("pacing")}");

                // not one frame while the motion must not run; then on from where it was
                var pauses = new List<string>();
                bool allZero = true, noJump = true;
                void Pause(string name, Action stop, Action go)
                {
                    var a = Motion(run); double tA = MotionTime(a);
                    stop();
                    Thread.Sleep(2500);   // a fade or the last frame
                    var b = Motion(run);
                    Thread.Sleep(6000);
                    var c = Motion(run);
                    long during = Frames(c) - Frames(b);
                    go();
                    Thread.Sleep(2500);
                    var d = Motion(run);
                    double tB = MotionTime(b), tD = MotionTime(d);
                    bool resumed = Running(d) && Frames(d) > Frames(c);
                    // the clock went on from where it stopped: after 2.5 s of motion it is about 2.5 s further, not 8.5 s
                    bool smooth = name == "switch off" || (tD - tB > 1 && tD - tB < 4.5);
                    allZero &= during == 0 && !Running(c);
                    noJump &= resumed && smooth;
                    pauses.Add($"{name}: {during} frames in 6 s ({Reason(c)}), then {(resumed ? "on" : "NOT on")}, clock {tB:F1} → {tD:F1} s");
                }
                Pause("switch off", () => Command(run, new { @do = "choose", type = "set", change = new { animation = false } }),
                                    () => Command(run, new { @do = "choose", type = "set", change = new { animation = true } }));
                Pause("window over the whole screen", () => { cover = new Desk.Cover(ScreenRect); Command(run, new { @do = "lookAgain" }); },
                                                      () => { cover?.Dispose(); cover = null; Command(run, new { @do = "lookAgain" }); });
                Pause("maximised window (the taskbar left)", () => { cover = new Desk.Cover(Work); Command(run, new { @do = "lookAgain" }); },
                                                             () => { cover?.Dispose(); cover = null; Command(run, new { @do = "lookAgain" }); });
                Pause("screen locked", () => Command(run, new { @do = "pretend", what = "lock" }), () => Command(run, new { @do = "pretend", what = "unlock" }));
                Pause("screens off", () => Command(run, new { @do = "pretend", what = "displayOff" }), () => Command(run, new { @do = "pretend", what = "displayOn" }));
                Pause("sleep", () => Command(run, new { @do = "pretend", what = "sleep" }), () => Command(run, new { @do = "pretend", what = "wake" }));
                Pause("another user", () => Command(run, new { @do = "pretend", what = "away" }), () => Command(run, new { @do = "pretend", what = "back" }));
                Pause("Remote Desktop", () => Command(run, new { @do = "remote", pretend = true }), () => Command(run, new { @do = "remote", pretend = false }));
                Pause("the prism scene (still)", () => Command(run, new { @do = "choose", type = "set", change = new { scene = 4 } }),
                                          () => Command(run, new { @do = "choose", type = "set", change = new { scene = 0 } }));
                Result(g, "not one frame while the motion must not run", allZero, string.Join("; ", pauses));
                Result(g, "the motion goes on afterwards without a jump", noJump, "the motion's clock goes on from where it stopped (the switch starts it anew, fading in)");

                // the real Remote Desktop state of this machine, for the report
                var real = run.Lines().FirstOrDefault(x => x.Contains("Remote Desktop session: "));
                numbers["remoteDesktopHere"] = real;
            }
            finally { cover?.Dispose(); cover = null; run.SaveLog("hidamari.log"); Stop(run); }
        }
        // kept: the next start moves at once
        using (var run = Run.App(app, Path.Combine(Stage4Dir, "animation-kept")))
        {
            try
            {
                var f = run.Next("frame 1 (", TimeSpan.FromSeconds(120));
                var started = run.Next("animation: running", TimeSpan.FromSeconds(30));
                Result(g, "Animation stays on after a new start", f != null && started != null, started ?? "not running");
            }
            finally { run.SaveLog("hidamari.log"); Stop(run); }
        }
    }

    static Desk.Cover? cover;

    // ---------- the numbers (full) ----------

    static void Measurements()
    {
        const string g = "stage 4 numbers";
        var rows = new List<Dictionary<string, object?>>();
        foreach (var renderer in new[] { "direct3d", "web" })
        {
            WriteSettings(MotionSettings(0, false));
            using var run = Run.App(app, Path.Combine(Stage4Dir, "measure-" + renderer), png: "first", env: new() { ["HIDAMARI_RENDERER"] = renderer });
            var row = new Dictionary<string, object?> { ["drawing"] = renderer };
            try
            {
                var first = run.Next("frame 1 (", TimeSpan.FromSeconds(120));
                row["firstFrameSeconds"] = first != null ? Math.Round(run.Since.Elapsed.TotalSeconds, 1) : null;
                if (first == null) { Result(g, $"{renderer}: first frame", false, "none"); continue; }
                Thread.Sleep(TimeSpan.FromMinutes(10));
                row["idleAfter10Min"] = run.Usage(TimeSpan.FromSeconds(60));
                var costs = new List<object?>();
                for (int i = 0; i < 3; i++)
                {
                    run.Mark(); Run.Signal("Draw");
                    var b = run.Next("batch ", TimeSpan.FromSeconds(120));
                    if (b != null) costs.Add(new { readyMs = Run.Number(b, "frames ready after "), processorMs = Run.Number(b, "processor "), line = b });
                    Thread.Sleep(8000);
                }
                row["frames"] = costs;
                row["afterFrames"] = run.Usage(TimeSpan.FromSeconds(30));
                if (renderer == "direct3d")
                {
                    Command(run, new { @do = "choose", type = "set", change = new { animation = true } });
                    Thread.Sleep(8000);
                    row["moving30"] = run.Usage(TimeSpan.FromSeconds(60));
                    row["rate30"] = Rate(run, 10);
                    Command(run, new { @do = "power", pretend = "battery" });
                    Thread.Sleep(5000);
                    row["moving15"] = run.Usage(TimeSpan.FromSeconds(60));
                    row["rate15"] = Rate(run, 10);
                    row["motion"] = Motion(run).GetProperty("motion").ToString();
                    Command(run, new { @do = "power", pretend = (string?)null });
                    Command(run, new { @do = "measure", why = "check: moving" });
                    Thread.Sleep(1000);
                    row["journalMeasure"] = run.Lines().LastOrDefault(x => x.Contains("measure (check: moving)"));
                }
            }
            finally { run.SaveLog("hidamari.log"); Stop(run); rows.Add(row); }
            Say($"stage 4 numbers {renderer}: {JsonSerializer.Serialize(row)}");
        }
        numbers["measurements"] = rows;
        string Mb(object? u) => u is Dictionary<string, object> d ? $"{d["workingSetMB"]} MB ({d["privateMB"]} own), {d["cpuPercentOfOneCore"]} % of a core" : "?";
        foreach (var r in rows)
            Result(g, $"{r["drawing"]}: still, 10 minutes after the start", r.ContainsKey("idleAfter10Min") ? "PASS" : "FAIL",
                $"first frame after {r.GetValueOrDefault("firstFrameSeconds")} s; idle {Mb(r.GetValueOrDefault("idleAfter10Min"))}; after 3 frames {Mb(r.GetValueOrDefault("afterFrames"))}" +
                (r.ContainsKey("moving30") ? $"; moving at 30: {Mb(r["moving30"])}; at 15: {Mb(r["moving15"])}" : ""));
        if (full && Environment.OSVersion.Version.Build >= 26100) Videos();
    }

    // ---------- the videos for Ed: the frames (made into mp4 on the Mac) ----------

    static void Videos()
    {
        WriteSettings(MotionSettings(0, false));
        using var run = Run.App(app, Path.Combine(Stage4Dir, "videos-run"), png: "none");
        try
        {
            if (run.Next("frame 1 (", TimeSpan.FromSeconds(120)) == null) return;
            var states = JsonDocument.Parse(File.ReadAllText(Here("motion-states.json"))).RootElement.EnumerateArray().ToList();
            JsonElement PageOf(string name) => states.First(s => s.GetProperty("name").GetString() == name).GetProperty("page");
            var videos = new List<(string name, string page, double wind, double start, double seconds, object? winds)>
            {
                ("dapples-calm", "dapples-noon-wind00-t00.0", 0, 0, 10, null),
                ("dapples-4ms", "dapples-noon-wind04-t00.0", 4, 0, 10, null),
                ("dapples-12ms", "dapples-noon-wind09-t00.0", 12, 0, 10, null),
                ("water-calm", "water-noon-wind00-t00.0", 0, 0, 10, null),
                ("water-4ms", "water-noon-wind04-t00.0", 4, 0, 10, null),
                ("water-12ms", "water-noon-wind09-t00.0", 12, 0, 10, null),
                // the dapples' clock starts anew after 20 minutes: 4 s of crossfade in the middle of the video
                ("dapples-clock-change", "dapples-noon-wind04-t00.0", 4, 1200 - 3, 10, null),
                // the end of a day and of a week of motion, the wind changing every hour
                ("dapples-after-a-day", "dapples-noon-wind04-t00.0", 4, 86400 - 2, 2, Hourly(1)),
                ("dapples-after-a-week", "dapples-noon-wind04-t00.0", 4, 7 * 86400 - 2, 2, Hourly(7)),
                ("water-after-a-week", "water-noon-wind04-t00.0", 4, 7 * 86400 - 2, 2, Hourly(7)),
            };
            foreach (var v in videos)
            {
                var dir = Path.Combine(Stage4Dir, "videos", v.name);
                var page = JsonSerializer.Deserialize<Dictionary<string, object?>>(PageOf(v.page).GetRawText())!;
                page.Remove("motion");
                var sw = Stopwatch.StartNew();
                Reply(run, v.winds == null
                    ? new { @do = "video", folder = dir, page, width = 1280, height = 800, fps = 30, start = v.start, seconds = v.seconds, wind = v.wind }
                    : (object)new { @do = "video", folder = dir, page, width = 1280, height = 800, fps = 30, start = v.start, seconds = v.seconds, winds = v.winds });
                while (!File.Exists(Path.Combine(dir, "done.json")) && sw.Elapsed < TimeSpan.FromMinutes(15)) Thread.Sleep(1000);
                Say($"video {v.name}: {Run.Read(Path.Combine(dir, "done.json"))} in {sw.Elapsed.TotalSeconds:F0} s");
            }
            // the long run: the frames at the end of a day and a week move as evenly as at the start
            var even = new List<string>(); bool smooth = true;
            foreach (var n in new[] { "dapples-4ms", "dapples-after-a-day", "dapples-after-a-week", "water-4ms", "water-after-a-week" })
            {
                var files = Directory.GetFiles(Path.Combine(Stage4Dir, "videos", n), "frame-*.jpg").OrderBy(x => x).Take(61).ToList();
                if (files.Count < 10) { smooth = false; even.Add($"{n}: no frames"); continue; }
                var steps = new List<double>();
                Bitmap? last = null;
                foreach (var f in files) { var b = Pictures.Load(f)!; if (last != null) steps.Add(Channels(last, b).mean.Average()); last?.Dispose(); last = b; }
                last?.Dispose();
                double m = steps.Average(), u = 0;
                for (int i = 1; i < steps.Count - 1; i++) u += Math.Abs(steps[i] - (steps[i - 1] + steps[i + 1]) / 2);
                double uneven = 100 * u / (steps.Count - 2) / m;
                int stalls = steps.Count(s => s < 0.25 * m);
                even.Add($"{n}: step {m:F3}, unevenness {uneven:F1} %, stalled {stalls}");
                smooth &= stalls == 0 && m > 0;
            }
            Result("Animation", "a day and a week of motion, the wind changing every hour: the frames still move evenly", smooth, string.Join("; ", even));
        }
        finally { run.SaveLog("hidamari.log"); Stop(run); }
    }

    static List<double[]> Hourly(int days)
    {
        // the same winds as tools/check-motion.swift: 4 m/s, then every hour 0–15 m/s
        ulong seed = 7;
        ulong Next() { seed += 0x9E3779B97F4A7C15; var z = seed; z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9; z = (z ^ (z >> 27)) * 0x94D049BB133111EB; return z ^ (z >> 31); }
        var o = new List<double[]> { new[] { 0.0, 4 } };
        for (int h = 1; h <= days * 24; h++) o.Add(new[] { h * 3600.0, Next() % 151 / 10.0 });
        return o;
    }
}
