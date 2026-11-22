using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

// Stage 2: the card by the tray icon, checked as a person uses it. Every click is a real click of the mouse on the
// place the card's page reports for that control (card-layout.json, written by the app in check mode only).
static partial class Check
{
    static readonly string SettingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hidamari", "settings.json");
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    static int commandId;
    static bool win11 => Environment.OSVersion.Version.Build >= 22000;

    // ---------- talking to the app ----------

    /// Asks the running app for something (check mode): open, close, clock, glass, choose … Returns its reply.
    static string Command(Run run, object cmd)
    {
        int id = ++commandId;
        var json = JsonSerializer.Serialize(cmd);
        json = "{\"id\":" + id + "," + json[1..];
        var reply = Path.Combine(run.Folder, "command-reply.json");
        File.WriteAllText(Path.Combine(run.Folder, "command.json"), json);
        Run.Signal("Command");
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(90))
        {
            var text = Run.Read(reply);
            if (text.Contains($"\"id\":{id},")) return text;
            Thread.Sleep(100);
        }
        return "no reply";
    }

    /// The app's clock at `local` Berlin time on 18 November 2026 (the day of the design's pictures).
    static void Clock(Run run, int h, int m) =>
        Command(run, new { @do = "clock", ms = (double)new DateTimeOffset(2026, 11, 18, h, m, 0, TimeSpan.FromHours(1)).ToUnixTimeMilliseconds() });

    sealed record Layout(Rectangle Window, Rectangle Card, double Scale, bool Open, bool Settings, bool Picker, JsonElement Rects, JsonElement Checked, DateTime At)
    {
        public bool Has(string name) => Rects.TryGetProperty(name, out var r) && r.ValueKind == JsonValueKind.Array && r[2].GetDouble() > 0 && r[3].GetDouble() > 0;

        /// The middle of control `name` on the screen, in pixels (or at fx, fy inside it).
        public Point At_(string name, double fx = 0.5, double fy = 0.5)
        {
            var r = Rects.GetProperty(name);
            return new Point((int)Math.Round(Window.X + (r[0].GetDouble() + fx * r[2].GetDouble()) * Scale),
                             (int)Math.Round(Window.Y + (r[1].GetDouble() + fy * r[3].GetDouble()) * Scale));
        }

        public Rectangle Rect(string name)
        {
            var r = Rects.GetProperty(name);
            return new Rectangle((int)Math.Round(Window.X + r[0].GetDouble() * Scale), (int)Math.Round(Window.Y + r[1].GetDouble() * Scale),
                                 (int)Math.Round(r[2].GetDouble() * Scale), (int)Math.Round(r[3].GetDouble() * Scale));
        }

        public bool Is(string key) => Checked.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
        public string Str(string key) => Checked.TryGetProperty(key, out var v) ? v.ToString() : "";
    }

    static Layout? ReadLayout(Run run)
    {
        var path = Path.Combine(run.Folder, "card-layout.json");
        var text = Run.Read(path);
        if (text.Length == 0) return null;
        try
        {
            var d = JsonDocument.Parse(text).RootElement.Clone();
            Rectangle R(JsonElement e) => new(e.GetProperty("X").GetInt32(), e.GetProperty("Y").GetInt32(), e.GetProperty("Width").GetInt32(), e.GetProperty("Height").GetInt32());
            var rects = d.GetProperty("rects");
            return new Layout(R(d.GetProperty("window")), R(d.GetProperty("card")), d.GetProperty("scale").GetDouble(), d.GetProperty("open").GetBoolean(),
                rects.GetProperty("settings").GetBoolean(), rects.GetProperty("picker").GetBoolean(), rects, rects.GetProperty("checked"), File.GetLastWriteTimeUtc(path));
        }
        catch { return null; }
    }

    /// The card's layout once it settles after `since` (the page writes it 0.4 s after it last changed).
    static Layout? LayoutAfter(Run run, DateTime since, Func<Layout, bool>? until = null, double seconds = 20)
    {
        var sw = Stopwatch.StartNew();
        Layout? l = null;
        while (sw.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            l = ReadLayout(run);
            if (l != null && l.At > since && (until == null || until(l))) { Thread.Sleep(150); return ReadLayout(run) ?? l; }
            Thread.Sleep(100);
        }
        return null;
    }

    /// The card's layout once it has stopped changing for 1.5 s (the page writes it 0.4 s after each change of size).
    static Layout? SettledLayout(Run run, double seconds = 20)
    {
        var sw = Stopwatch.StartNew();
        var last = ReadLayout(run);
        var still = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            Thread.Sleep(200);
            var now = ReadLayout(run);
            if (now != null && (last == null || now.At != last.At)) { last = now; still.Restart(); }
            else if (still.Elapsed > TimeSpan.FromSeconds(1.5)) return last;
        }
        return last;
    }

    static IntPtr CardHwnd() => Native.FindWindow(null, "Hidamari");
    static bool CardSeen() => Native.Seen(CardHwnd());

    static bool WaitSeen(bool seen, double seconds = 5)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(seconds)) { if (CardSeen() == seen) return true; Thread.Sleep(20); }
        return false;
    }

    /// Opens the card with a click on the tray icon; the layout once it is open, and how long it took to be seen.
    static (Layout? layout, double ms) OpenByIcon(Run run)
    {
        var icon = FindTray();
        if (icon == null) return (null, 0);
        var since = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();
        Native.Click(Desk.Center(icon.Value) with { Y = icon.Value.Y + icon.Value.Height / 2 });
        bool seen = WaitSeen(true, 8);
        double ms = sw.Elapsed.TotalMilliseconds - 210;   // Click() itself waits 210 ms before letting go
        var l = LayoutAfter(run, since, x => x.Open);
        return (seen ? l : null, ms);
    }

    static void Esc() => Native.Keys(Native.VK_ESCAPE);

    /// Clicks control `name` of the open card; the layout afterwards.
    static Layout? ClickCard(Run run, Layout l, string name, double fx = 0.5, double fy = 0.5, Func<Layout, bool>? until = null)
    {
        if (!l.Has(name)) { Say($"card: no {name} on the card"); return null; }
        var since = DateTime.UtcNow;
        Native.Click(l.At_(name, fx, fy));
        return LayoutAfter(run, since, until) ?? ReadLayout(run);
    }

    /// The wall's state as last drawn on the main screen (the app's check copy of komorebi.render's argument).
    static JsonElement? WallState(Run run)
    {
        var text = Run.Read(Path.Combine(run.Folder, "canvas", "state-DISPLAY1.json"));
        if (text.Length == 0) return null;
        try { return JsonDocument.Parse(text).RootElement.Clone(); } catch { return null; }
    }

    /// Saves the frame just drawn for the comparison with the Mac (tools/windows-compare.swift <dir>).
    static void KeepForMac(Run run, string name)
    {
        var dir = Path.Combine(outDir, "mac-compare-card", name, "canvas");
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(Path.Combine(run.Folder, "canvas")))
            File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true);
    }

    static void WriteSettings(Dictionary<string, object?> s)
    {
        var all = new Dictionary<string, object?>
        {
            ["version"] = 1, ["scene"] = 0, ["wall"] = 0,
            ["custom"] = new Dictionary<string, object?> { ["L"] = 0.88, ["x"] = 0.5, ["y"] = 0.8, ["k"] = 0.1, ["rough"] = 1.0, ["shade"] = null },
            ["plant"] = 2, ["followsSun"] = true, ["pinnedMinutes"] = 720.0, ["manualWeather"] = "clear",
            ["screens"] = "all", ["firstLaunchAnswered"] = true,
            // the checks of stage 2 as they were: Berlin chosen, the weather by hand (the place and the live weather
            // have checks of their own, PlaceWeather.cs)
            ["liveWeather"] = false, ["placeMode"] = "chosen",
            ["chosenPlace"] = new Dictionary<string, object?> { ["name"] = "Berlin", ["country"] = "Germany", ["lat"] = 52.52, ["lng"] = 13.405, ["tz"] = "Europe/Berlin" },
        };
        foreach (var (k, v) in s) all[k] = v;
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(all));
        // no weather kept from an earlier run: each run asks anew at launch
        try { File.Delete(Path.Combine(Path.GetDirectoryName(SettingsFile)!, "weather.json")); } catch { }
    }

    static string? RunEntry() { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("Hidamari") as string; }

    static Run StartApp(string name, Dictionary<string, string>? env = null)
    {
        var run = Run.App(app, Path.Combine(outDir, name), env: env);
        if (run.Next("frame 1 (", TimeSpan.FromSeconds(180)) == null) Say($"{name}: no first frame");
        Thread.Sleep(2500);
        return run;
    }

    static void Quit(Run run)
    {
        Run.Signal("Quit");
        run.Proc.WaitForExit(20000);
        run.WaitGone();
        run.SaveLog("hidamari.log");
    }

    // ================= the first launch and the second start (inside the main run) =================

    /// The main run is the app's very first launch on this machine: the card opens by itself with the question.
    static void FirstLaunch(Run run)
    {
        const string g = "card: first launch";
        var opened = run.Next("card: opened (first launch)", TimeSpan.FromSeconds(30));
        var openedAt = DateTime.UtcNow.AddSeconds(-5);
        var deferred = run.Lines().FirstOrDefault(x => x.Contains("place: Windows will be asked once the first launch's card has closed"));
        Thread.Sleep(1500);
        var l = opened != null ? LayoutAfter(run, openedAt, x => x.Open) : null;
        Result(g, "on a clean machine the card opens by itself with the question", opened != null && l != null && l.Is("first") && CardSeen(),
            $"journal: {opened ?? "no open"}; question shown: {l?.Is("first")}");
        if (l == null) { Command(run, new { @do = "close" }); AnswerWindows(); return; }
        Pictures.Crop(Pictures.Shot(null), l.Window, "card/first-launch-real.png");
        var after = ClickCard(run, l, "firstNotNow", until: x => !x.Is("first"));
        var said = run.Next("settings: first launch answered: Not now", TimeSpan.FromSeconds(5));
        Result(g, "Not now: the question folds away and is never asked again", after != null && !after.Is("first") && said != null && RunEntry() == null,
            $"journal: {said ?? "nothing"}; Run entry: {RunEntry() ?? "none"}");
        Esc();
        var closed = run.Next("card: closed (Escape)", TimeSpan.FromSeconds(5));
        Result(g, "Escape closes the card", closed != null && WaitSeen(false), closed ?? "not closed");
        AnswerWindows();

        void AnswerWindows()
        {
            // only now Windows is asked for the place; Windows 11 asks the person itself, over everything, the screen
            // dimmed, until answered: a picture of it for the sheet, then "No", as a careful person would (the rest of the
            // run goes the "location unavailable" way, as on these machines anyway)
            var asked = run.Next("place: asking Windows now (the first launch's card closed)", TimeSpan.FromSeconds(5));
            Result(g, "the place is asked of Windows only after the first launch's card has closed (Windows' own question would cover it)",
                deferred != null && asked != null, $"journal: {deferred ?? "asked at once"}; then: {asked ?? "nothing"}");
            Thread.Sleep(2000);
            Pictures.Shot("card/windows-asks-location.png");
            facts["windowsLocationQuestion"] = Desk.AnswerLocationQuestion("No", TimeSpan.FromSeconds(8));
            Say($"Windows' question about the place: {facts["windowsLocationQuestion"]}");
            for (var sw = Stopwatch.StartNew(); sw.Elapsed < TimeSpan.FromSeconds(60)
                 && !run.Lines().Any(x => x.Contains("place: located (launch") || x.Contains("place: not located (launch")); ) Thread.Sleep(500);
            facts["locationAtFirstLaunch"] = run.Lines().FirstOrDefault(x => x.Contains("(launch)") && x.Contains("place:"));
            Thread.Sleep(1500);
        }
    }

    static void SecondStart(Run run)
    {
        const string g = "card";
        run.Mark();
        var second = Process.Start(new ProcessStartInfo(app) { UseShellExecute = false })!;
        bool secondLeft = second.WaitForExit(15000);
        var noted = run.Next("started again while running, opening the card", TimeSpan.FromSeconds(5));
        var opened = run.Next("card: opened (Hidamari started again)", TimeSpan.FromSeconds(10));
        bool seen = WaitSeen(true);
        bool front = Native.GetForegroundWindow() == CardHwnd();
        Result(g, "a second start opens the card of the running one, no second copy",
            secondLeft && second.ExitCode == 0 && noted != null && opened != null && seen && !run.Proc.HasExited,
            $"second start {(secondLeft ? $"left with {second.ExitCode}" : "still running")}, journal: {opened ?? noted ?? "nothing"}; " +
            $"in front with the keyboard: {front}");
        Esc();
        run.Next("card: closed", TimeSpan.FromSeconds(5));
        WaitSeen(false);
        Thread.Sleep(1000);
    }

    // ================= the card =================

    static void CardRun()
    {
        WriteSettings(new());
        using var run = StartApp("card");
        try { CardSteps(run); }
        finally { if (!run.Proc.HasExited) run.SaveLog("hidamari.log"); }   // Restart() saved it before quitting
    }

    static void CardSteps(Run run)
    {
        const string g = "card";
        var work = Work;
        // ---- open by the icon, where it stands, what kind of window it is
        run.Mark();
        var (l, ms) = OpenByIcon(run);
        var opened = run.Next("card: opened (a click on the icon)", TimeSpan.FromSeconds(5));
        Result(g, "a left click on the tray icon opens the card", l != null && opened != null, $"seen after {ms:F0} ms; journal: {opened ?? "nothing"}");
        if (l == null) return;
        Pictures.Shot("card/open-by-icon.png");
        Placement(g, l, "taskbar at the bottom, 100 %");
        var h = CardHwnd();
        long ex = Native.ExStyle(h);
        bool tool = (ex & 0x80) != 0, appwin = (ex & 0x40000) != 0, topmost = (ex & 0x8) != 0;
        Result(g, "no taskbar button, not in Alt+Tab, on top of other windows", tool && !appwin && topmost,
            $"tool window {tool}, app window {appwin}, topmost {topmost}");
        bool keys = Native.GetForegroundWindow() == h;
        Result(g, "the card takes the keyboard (for Escape)", keys, keys ? "it is the foreground window" : $"the foreground window is {Native.Describe(Native.GetForegroundWindow())}");

        // ---- the three ways to close it
        run.Mark();
        Esc();
        var esc = run.Next("card: closed (Escape)", TimeSpan.FromSeconds(5));
        Result(g, "Escape closes it", esc != null && WaitSeen(false), esc ?? "not closed");
        Thread.Sleep(800);

        (l, _) = OpenByIcon(run);
        run.Mark();
        var cardNow = l?.Card ?? Rectangle.Empty;
        var empty = new Point(work.Left + work.Width / 3, work.Top + work.Height / 2);
        if (Rectangle.Inflate(cardNow, 20, 20).Contains(empty) || Desk.Icons().Any(i => Rectangle.Inflate(i.rect, 10, 10).Contains(empty)))
            empty = new Point(work.Left + work.Width / 2, work.Top + 30);
        Native.Click(empty);
        var outside = run.Next("card: closed (click outside)", TimeSpan.FromSeconds(5));
        Result(g, "a click outside closes it", outside != null && WaitSeen(false), $"clicked {empty}; {outside ?? "not closed"}");
        Thread.Sleep(800);

        (l, _) = OpenByIcon(run);
        run.Mark();
        var icon = FindTray();
        if (icon != null)
        {
            Native.Click(Desk.Center(icon.Value) with { Y = icon.Value.Y + icon.Value.Height / 2 });
            var again = run.Next("card: closed (the icon again)", TimeSpan.FromSeconds(5));
            Thread.Sleep(2000);
            var reopened = run.Since_("card: opened");
            Result(g, "a second click on the icon closes it and does not open it again", again != null && reopened.Count == 0 && !CardSeen(),
                $"{again ?? "not closed"}{(reopened.Count > 0 ? $"; but: {reopened[0]}" : "")}");
        }
        Thread.Sleep(800);

        // ---- the right click keeps its menu with Quit
        if (icon != null)
        {
            Native.Click(Desk.Center(icon.Value) with { Y = icon.Value.Y + icon.Value.Height / 2 }, right: true);
            Thread.Sleep(1500);
            var item = Desk.MenuItem("Quit");
            Result(g, "a right click still opens the menu with Quit, not the card", item != null && !CardSeen(), item?.ToString() ?? "no Quit");
            Native.Keys(Native.VK_ESCAPE);
            Thread.Sleep(800);
        }

        Elements(run);
        Settings_(run);
        Restart(run);
    }

    /// The card stands by the icon, 12 points from the taskbar and the screen's edge, inside the work area.
    static void Placement(string g, Layout l, string where)
    {
        var work = Work;
        int gap = (int)Math.Round(12 * l.Scale);
        var c = l.Card;
        var bar = Desk.Taskbar();
        var icon = Desk.TrayIcon("Hidamari");
        string edge = bar.Width >= bar.Height ? (bar.Top <= work.Top ? "top" : "bottom") : (bar.Left <= work.Left ? "left" : "right");
        bool inside = c.Left >= work.Left + gap - 1 && c.Right <= work.Right - gap + 1 && c.Top >= work.Top + gap - 1 && c.Bottom <= work.Bottom - gap + 1;
        bool byBar = edge switch
        {
            "bottom" => Math.Abs(c.Bottom - (work.Bottom - gap)) <= 1,
            "top" => Math.Abs(c.Top - (work.Top + gap)) <= 1,
            "left" => Math.Abs(c.Left - (work.Left + gap)) <= 1,
            _ => Math.Abs(c.Right - (work.Right - gap)) <= 1,
        };
        bool width = Math.Abs(c.Width - (int)Math.Round(300 * l.Scale)) <= 1;
        string near = "";
        bool byIcon = true;
        if (icon is { } ir && edge is "bottom" or "top")
        {
            int want = Math.Clamp((ir.Left + ir.Right) / 2 - c.Width / 2, work.Left + gap, work.Right - gap - c.Width);
            byIcon = Math.Abs(c.Left - want) <= 2;
            near = $", icon at {ir}";
        }
        Result(g, $"{where}: by the icon, 12 pt from the taskbar and the edge, {300 * l.Scale:F0} px wide", inside && byBar && width && byIcon,
            $"card {c} at {l.Scale * 100:F0} %, work area {work}, taskbar {edge} {bar}{near}");
    }

    // ---------- every control changes the wall ----------

    static void Elements(Run run)
    {
        const string g = "card: the wall follows";
        var (l, _) = OpenByIcon(run);
        if (l == null) { Result(g, "the card opens", false, "not open"); return; }

        // pressing a control, then the frame it asks for and what the wall was given
        bool Press(string control, string why, Func<JsonElement, bool> ok, string name, string label, double fx = 0.5, double fy = 0.5)
        {
            run.Mark();
            var sw = Stopwatch.StartNew();
            var after = ClickCard(run, l!, control, fx, fy);
            // the first frame that shows the choice: with the own wall, "being changed" and "was changed" can come as
            // one frame (two asks while a frame is drawn), named after the first
            bool Ours(string x) => x.Contains("frame ") && (x.Contains($"({why})") || why.Contains("own wall") && x.Contains("(the own wall"));
            string? frame = null;
            JsonElement? st = null;
            long ms = 0;
            bool pass = false;
            while (sw.Elapsed < TimeSpan.FromSeconds(150))
            {
                var f = run.Next(Ours, TimeSpan.FromSeconds(150) - sw.Elapsed);
                if (f == null) break;
                frame = f;
                ms = sw.ElapsedMilliseconds;
                Thread.Sleep(400);
                st = WallState(run);
                if (st != null && ok(st.Value.GetProperty("state"))) { pass = true; break; }
            }
            if (frame == null) ms = sw.ElapsedMilliseconds;
            if (pass) KeepForMac(run, name);
            Result(g, label, pass, $"frame after {ms} ms: {frame ?? "none"}; wall given {(st != null ? JsonSerializer.Serialize(st.Value.GetProperty("state")) : "nothing")}");
            if (after != null) l = after;
            return pass;
        }
        static int I(JsonElement s, string k) => s.GetProperty(k).GetInt32();
        static double D(JsonElement s, string k) => s.GetProperty(k).GetDouble();

        for (int w = 1; w <= 5; w++) { int ww = w; Press($"wall{w}", "a wall was chosen", s => I(s, "wall") == ww, $"wall-{w}", $"wall dot {w}: that wall"); }
        Press("wall0", "a wall was chosen", s => I(s, "wall") == 0, "wall-0", "wall dot 0: beige again");
        foreach (var sc in new[] { 2, 3, 1 }) { int x = sc; Press($"scene{sc}", "a scene was chosen", s => I(s, "scene") == x, $"scene-{sc}", $"scene {sc}"); }
        l = LayoutAfter(run, DateTime.UtcNow.AddSeconds(-3)) ?? l;
        foreach (var p in new[] { 0, 5, 2 }) { int x = p; Press($"plant{p}", "a plant was chosen", s => I(s, "plant") == x, $"plant-{p}", $"plant {p} (window scene)"); }
        // the weather by hand, as the Mac draws it (ManualWeather, Weather.drawn(in:))
        Press("weather-cloudy", "the weather was chosen", s => D(s, "cloud") == 0.8 && I(s, "weather") == 0, "weather-cloudy", "cloudy: cloud 0.8");
        Press("weather-rain", "the weather was chosen", s => D(s, "cloud") == 0.2 && I(s, "weather") == 1, "weather-rain", "rain in the window: rain on the glass, cloud thinned to 0.2");
        Press("weather-frost", "the weather was chosen", s => D(s, "cloud") == 0.3 && I(s, "weather") == 2, "weather-frost", "frost in the window: frost on the glass, cloud 0.3");
        Press("scene0", "a scene was chosen", s => I(s, "scene") == 0 && D(s, "cloud") == 0.8 && I(s, "weather") == 0, "frost-elsewhere", "frost chosen, then dapples: shown as cloudy");
        l = LayoutAfter(run, DateTime.UtcNow.AddSeconds(-3)) ?? l;
        Press("weather-clear", "the weather was chosen", s => D(s, "cloud") == 0 && I(s, "weather") == 0, "weather-clear", "clear again");

        // the sun by hand: a press on the arc pins the time and turns Live sun off
        double FxOf(int minutes) => (4 + 228 * minutes / 1440.0) / 236.0;
        Press("arc", "the time was chosen", s =>
        {
            double local = ((D(s, "time") / 60000 + I(s, "utcOffset")) % 1440 + 1440) % 1440;
            return Math.Abs(local - 940) <= 4;
        }, "arc-1540", "a press on the arc at 15:40: the wall shows 15:40", FxOf(940), 0.5);
        Result(g, "the press turned Live sun off", !l!.Is("follow"), $"Live sun {(l.Is("follow") ? "on" : "off")}");

        // a drag: frames follow the hand, the last one lands at once
        run.Mark();
        int dragFrom = run.Lines().Length;
        var a = l.At_("arc", FxOf(540), 0.5);
        var b = l.At_("arc", FxOf(840), 0.5);
        Native.Down(a);
        for (int i = 1; i <= 30; i++) { Native.MoveTo(new Point(a.X + (b.X - a.X) * i / 30, a.Y)); Thread.Sleep(50); }
        var dragShot = Pictures.Shot("card/dragging.png");
        var letGo = Stopwatch.StartNew();
        Native.Up();
        var started = run.Next("card: the sun is being dragged", TimeSpan.FromSeconds(3));
        // the frame of the last place: "the time was chosen", or a "dragged" one that already took it (on a slow
        // machine the last ask can come while a frame is drawn, and the next frame is named after the first ask)
        var last = run.Next(x => x.Contains("frame ") && x.Contains(" 14:00:00 ") && (x.Contains("(the time was chosen)") || x.Contains("(the sun is dragged)")),
            TimeSpan.FromSeconds(150));
        var lastMs = letGo.ElapsedMilliseconds;
        var dragLines = run.Lines().Skip(dragFrom).Where(x => x.Contains("frame ") && x.Contains("(the sun is dragged)")).ToList();
        var dragFrames = dragLines.Count;
        // "at once": the frame already being drawn when the hand let go ends, then the last place is drawn; on
        // GitHub's machines (no graphics card) one frame takes a second or more, so the limit is counted in frames
        double drawMs = Run.Number(last ?? "", "draw ") ?? 1000;
        double limit = Math.Max(3000, 2.5 * drawMs + 1000);
        Thread.Sleep(400);
        var ds = WallState(run);
        double localEnd = ds == null ? -1 : ((D(ds.Value.GetProperty("state"), "time") / 60000 + I(ds.Value.GetProperty("state"), "utcOffset")) % 1440 + 1440) % 1440;
        bool dragOk = started != null && last != null && Math.Abs(localEnd - 840) <= 6 && lastMs <= limit;
        if (dragOk) KeepForMac(run, "arc-drag-1400");
        Result(g, "dragging the sun: the wall follows, the last place lands at once", dragOk,
            $"{dragFrames} frames while dragging; last frame {lastMs} ms after letting go (one frame draws in {drawMs:F0} ms here, limit {limit:F0} ms) at {Settings_Clock(localEnd)} (wanted 14:00): {last ?? "none"}");
        facts["dragFramesWhileDragging"] = dragFrames;
        facts["dragLastFrameMs"] = lastMs;

        // Live sun on again: the real time
        l = LayoutAfter(run, DateTime.UtcNow.AddSeconds(-3)) ?? l;
        Press("follow", "Live sun on", s => Math.Abs(D(s, "time") - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) < 120_000, "live-sun-on", "Live sun on: back to the real time");

        // a double click on the arc also follows the sun again
        ClickCard(run, l!, "arc", FxOf(600), 0.5);
        run.Next("(the time was chosen)", TimeSpan.FromSeconds(10));
        run.Mark();
        var mid = l!.At_("arc", 0.5, 0.5);
        Native.DoubleClick(mid);
        var dbl = run.Next("settings: Live sun on", TimeSpan.FromSeconds(10));
        // and it stays on: the first press letting go late must not pin the time again
        Thread.Sleep(3000);
        var after = run.Since_("settings:");
        bool stays = after.Count == 0 && ReadLayout(run)?.Is("follow") == true;
        Result(g, "a double click on the arc: Live sun on again", dbl != null && stays,
            $"{dbl ?? "nothing"}; after it: {(after.Count == 0 ? "no other change" : string.Join(" / ", after))}; " +
            $"journal: {string.Join(" / ", run.Lines().Where(x => x.Contains("double click")).TakeLast(2))}");

        // Live weather on: the icons go, the source's name comes; off: the icons again (CardParts.swift)
        l = LayoutAfter(run, DateTime.UtcNow.AddSeconds(-3)) ?? l;
        if (l!.Has("live"))
        {
            run.Mark();
            var on = ClickCard(run, l, "live", until: x => x.Is("live")) ?? l;
            var said = run.Next("settings: Live weather on", TimeSpan.FromSeconds(10));
            var asked = run.Next("weather: test source for ", TimeSpan.FromSeconds(20));
            Result(g, "Live weather on: asked at once, the icons hidden, nothing new at the bottom",
                on.Is("live") && !on.Is("weatherIcons") && said != null && asked != null,
                $"journal: {said ?? "no change"} / {asked ?? "nothing asked"}; icons {(on.Is("weatherIcons") ? "shown" : "hidden")}");
            Pictures.Crop(Pictures.Shot(null), on.Window, "card/live-weather-on.png");
            run.Mark();
            var off = ClickCard(run, on, "live", until: x => !x.Is("live")) ?? on;
            var offSaid = run.Next("settings: Live weather off", TimeSpan.FromSeconds(10));
            Result(g, "Live weather off: the icons again", !off.Is("live") && off.Is("weatherIcons") && offSaid != null,
                $"journal: {offSaid ?? "no change"}; icons {(off.Is("weatherIcons") ? "shown" : "hidden")}");
            l = off;
        }

        // ---- the own-wall picker
        const string o = "card: own wall";
        l = ClickCard(run, l, "own", until: x => x.Picker);
        Result(o, "the rainbow dot opens the picker", l != null && l.Picker, l?.Picker == true ? "open" : "not open");
        if (l == null || !l.Picker) return;
        Pictures.Crop(Pictures.Shot(null), l.Window, "card/picker-real.png");
        Press("pad", "the own wall was changed", s => I(s, "wall") == -1 && Math.Abs(D(s.GetProperty("custom"), "x") - 0.25) < 0.08 && Math.Abs(D(s.GetProperty("custom"), "y") - 0.3) < 0.08,
            "own-pad", "a press on the field: an own wall of that colour", 0.25, 0.3);
        var thumbs = run.Next("thumbs: 9 pictures of the own wall drawn", TimeSpan.FromSeconds(300));
        Result(o, "its small pictures are drawn half a second after the hand lets go", thumbs != null, thumbs ?? "none within 300 s");
        facts["ownThumbsMs"] = thumbs != null ? Run.Number(thumbs, "drawn in ") : null;
        // the small pictures take the wall's engine for a while (36 s on GitHub's machines without a graphics card,
        // under a second with one); the next press waits for them so each frame is timed on its own
        void Pictures_() => run.Next("thumbs: ", TimeSpan.FromSeconds(300));
        double Was(string k) => WallState(run) is { } w ? D(w.GetProperty("state").GetProperty("custom"), k) : double.NaN;
        double rough0 = Was("rough");
        Press("wave", "the own wall was changed", s => Math.Abs(D(s.GetProperty("custom"), "rough") - rough0) > 0.05, "own-texture", "the wave: the texture", 0.85, 0.5);
        Pictures_();
        double k0 = Was("k");
        Press("dial", "the own wall was changed", s => Math.Abs(D(s.GetProperty("custom"), "k") - k0) > 0.05, "own-light", "the dial: the strength of light", 0.9, 0.35);
        Pictures_();
        Press("shadeOn", "the own wall was changed", s => s.GetProperty("custom").GetProperty("shade").ValueKind == JsonValueKind.Object, "own-shade", "+: an own shade colour");
        Pictures_();
        Press("shadeOff", "the own wall was changed", s => s.GetProperty("custom").GetProperty("shade").ValueKind == JsonValueKind.Null, "own-shade-off", "−: the shade works itself out again");
        Pictures_();
        run.Mark();
        Esc();
        var pickerClosed = LayoutAfter(run, DateTime.UtcNow.AddSeconds(-0.2), x => !x.Picker);
        Result(o, "Escape closes the picker first, the card stays", pickerClosed != null && CardSeen(), pickerClosed != null ? "picker closed" : "picker still open");
        // the card is shorter now: the controls' places once it has settled (on a slow machine it takes a while)
        l = SettledLayout(run) ?? pickerClosed ?? l;
        Press("wall2", "a wall was chosen", s => I(s, "wall") == 2, "wall-2-after-own", "an approved wall after the own one");
        Esc();
        run.Next("card: closed", TimeSpan.FromSeconds(5));
        Thread.Sleep(800);
    }

    static string Settings_Clock(double minutes) { int m = ((int)Math.Round(minutes) % 1440 + 1440) % 1440; return $"{m / 60:D2}:{m % 60:D2}"; }

    // ---------- the settings ----------

    static void Settings_(Run run)
    {
        const string g = "card: settings";
        var (l, _) = OpenByIcon(run);
        if (l == null) return;
        l = ClickCard(run, l, "gear", until: x => x.Settings);
        Result(g, "the gear opens the settings in the whole card", l != null && l.Settings, l?.Settings == true ? "open" : "not open");
        if (l == null || !l.Settings) return;
        Pictures.Crop(Pictures.Shot(null), l.Window, "card/settings-real.png");

        // Launch at login
        run.Mark();
        l = ClickCard(run, l, "login", until: x => x.Is("login")) ?? l;
        var entry = RunEntry();
        Result(g, "Launch at login on: the app's entry in the Run key", entry != null && entry.Trim('"').Equals(app, StringComparison.OrdinalIgnoreCase) && l.Is("login"),
            $"Run entry: {entry ?? "none"}; journal: {run.Since_("login:").FirstOrDefault() ?? "nothing"}");
        // Task Manager turns it off: the switch shows it honestly
        using (var k = Registry.CurrentUser.CreateSubKey(ApprovedKey)) k.SetValue("Hidamari", new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
        Command(run, new { @do = "push" });
        var off = LayoutAfter(run, DateTime.UtcNow.AddSeconds(-0.2), x => !x.Is("login"));
        Result(g, "turned off in Task Manager: the switch shows off", off != null, off != null ? "off" : "still on");
        l = off ?? l;
        l = ClickCard(run, l, "login", until: x => x.Is("login")) ?? l;
        using (var k = Registry.CurrentUser.OpenSubKey(ApprovedKey))
            Result(g, "on again: the Task Manager's off mark is cleared", k?.GetValue("Hidamari") == null && RunEntry() != null && l.Is("login"), $"mark {(k?.GetValue("Hidamari") == null ? "gone" : "still there")}");
        l = ClickCard(run, l, "login", until: x => !x.Is("login")) ?? l;
        Result(g, "Launch at login off: the entry is gone", RunEntry() == null && !l.Is("login"), $"Run entry: {RunEntry() ?? "none"}");

        // Screens: Main
        run.Mark();
        l = ClickCard(run, l, "screensMain", until: x => x.Is("screensMain")) ?? l;
        var built = run.Next("build (Screens: Main)", TimeSpan.FromSeconds(10));
        var frame = run.Next("frame ", TimeSpan.FromSeconds(30));
        Result(g, "Screens: Main: the wall only on the main screen (one screen here)", built != null && frame != null, $"{built ?? "no new walls"} / {frame ?? "no frame"}");
        l = ClickCard(run, l, "screensAll", until: x => !x.Is("screensMain")) ?? l;
        run.Next("build (Screens: All)", TimeSpan.FromSeconds(10));
        run.Next("frame ", TimeSpan.FromSeconds(30));

        l = LightFrom(run, l);

        // Desktops is not offered (Windows does not tell the desktop of the app's windows)
        Result(g, "no Desktops row and no Screensaver row in the settings", !l.Has("desktopsAll") && !l.Has("desktopsThis"),
            l.Has("desktopsAll") ? "the Desktops row is shown" : "only Launch at login and Screens");
        // back, then Escape
        l = ReadLayout(run);
        if (l != null && l.Settings && l.Has("back"))
        {
            l = ClickCard(run, l, "back", until: x => !x.Settings);
            Result(g, "the back arrow closes the settings", l != null && !l.Settings, l?.Settings == false ? "closed" : "still open");
        }
        Esc();
        run.Next("card: closed", TimeSpan.FromSeconds(5));
        Thread.Sleep(800);
    }

    // ---------- Light from (the Mac's update 1.3) ----------

    /// Right mirrors the window and the blinds only, the small pictures turn over with them; left as Right for the
    /// restart that follows (it must survive it).
    static Layout LightFrom(Run run, Layout l)
    {
        const string g = "card: Light from";
        Result(g, "the Light from row is there, Left chosen at first", l.Has("lightLeft") && l.Has("lightRight") && !l.Is("lightRight"),
            $"left {l.Has("lightLeft")}, right {l.Has("lightRight")}, right chosen {l.Is("lightRight")}");
        string sceneBefore = l.Str("scene");
        run.Mark();
        l = ClickCard(run, l, "lightRight", until: x => x.Is("lightRight")) ?? l;
        var said = run.Next("settings: Light from: Right", TimeSpan.FromSeconds(10));
        Thread.Sleep(1000);
        var saved = Run.Read(SettingsFile);
        Result(g, "a click on Right chooses it and saves it", l.Is("lightRight") && said != null && saved.Contains("\"windowOnRight\": true"),
            $"journal: {said ?? "nothing"}; settings.json: {(saved.Contains("\"windowOnRight\": true") ? "windowOnRight true" : "no windowOnRight true")}");
        // each scene on the wall: only the window and the blinds are mirrored
        var wrong = new List<string>();
        foreach (var sc in new[] { 1, 2, 0, 3 })
        {
            run.Mark();
            var stateFile = Path.Combine(run.Folder, "canvas", "state-DISPLAY1.json");
            var before = File.Exists(stateFile) ? File.GetLastWriteTimeUtc(stateFile) : DateTime.MinValue;
            Command(run, new { @do = "choose", type = "set", change = new Dictionary<string, object> { ["scene"] = sc } });
            var frame = run.Next("frame ", TimeSpan.FromSeconds(120));
            // on a slow machine the journal says "frame" a moment before the check copy of the state is written
            for (var sw = Stopwatch.StartNew(); sw.Elapsed < TimeSpan.FromSeconds(10) && File.Exists(stateFile)
                 && File.GetLastWriteTimeUtc(stateFile) <= before; ) Thread.Sleep(200);
            Thread.Sleep(300);
            var st = WallState(run)?.GetProperty("state");
            bool mirror = st != null && st.Value.TryGetProperty("mirror", out var mv) && mv.ValueKind == JsonValueKind.True;
            bool want = sc is 1 or 2;
            if (st == null || st.Value.GetProperty("scene").GetInt32() != sc || mirror != want) wrong.Add($"scene {sc}: mirror {mirror} ({frame ?? "no frame"})");
            KeepForMac(run, $"light-right-scene{sc}");
        }
        Result(g, "Right mirrors the window and the blinds, not the dapples and the water", wrong.Count == 0,
            wrong.Count == 0 ? "window and blinds mirrored, dapples and water as they were (frames kept for the Mac)" : string.Join("; ", wrong));
        // the small pictures, seen on the card
        Command(run, new { @do = "choose", type = "set", change = new Dictionary<string, object> { ["scene"] = 1 } });
        run.Next("frame ", TimeSpan.FromSeconds(120));
        // the window scene opens the plants' row: the card is taller now, the back arrow elsewhere
        l = SettledLayout(run) ?? l;
        l = ClickCard(run, l, "back", until: x => !x.Settings) ?? l;
        Thread.Sleep(1200);
        l = ReadLayout(run) ?? l;
        Pictures.Crop(Pictures.Shot(null), l.Window, "card/light-right.png");
        Result(g, "the small pictures of the window, the blinds and the plants turn over", l.Str("mirroredScenes") == "1,2" && l.Is("mirroredPlants"),
            $"turned over: scenes {l.Str("mirroredScenes")}, plants {l.Is("mirroredPlants")} (picture card/light-right.png)");
        // back to the scene it was, the settings open again
        if (int.TryParse(sceneBefore, out var before))
        {
            Command(run, new { @do = "choose", type = "set", change = new Dictionary<string, object> { ["scene"] = before } });
            run.Next("frame ", TimeSpan.FromSeconds(120));
        }
        l = ClickCard(run, l, "gear", until: x => x.Settings) ?? l;
        return l;
    }

    // ---------- settings survive a restart; broken files ----------

    static void Restart(Run run)
    {
        const string g = "card: settings kept";
        // a few choices, then quit and start again
        var (l, _) = OpenByIcon(run);
        if (l == null) return;
        l = ClickCard(run, l, "scene3") ?? l; run.Next("(a scene was chosen)", TimeSpan.FromSeconds(20));
        l = ClickCard(run, l, "wall4") ?? l; run.Next("(a wall was chosen)", TimeSpan.FromSeconds(20));
        l = ClickCard(run, l, "weather-cloudy") ?? l; run.Next("(the weather was chosen)", TimeSpan.FromSeconds(20));
        ClickCard(run, l, "arc", (4 + 228 * 1000 / 1440.0) / 236.0, 0.5); run.Next("(the time was chosen)", TimeSpan.FromSeconds(20));
        Esc();
        Thread.Sleep(1500);
        var saved = Run.Read(SettingsFile);
        Quit(run);

        using var again = StartApp("card-restart");
        try
        {
            var st = WallState(again);
            var s = st?.GetProperty("state");
            double local = s == null ? -1 : ((s.Value.GetProperty("time").GetDouble() / 60000 + s.Value.GetProperty("utcOffset").GetInt32()) % 1440 + 1440) % 1440;
            Result(g, "after a restart the wall is as it was left", s != null && s.Value.GetProperty("scene").GetInt32() == 3 && s.Value.GetProperty("wall").GetInt32() == 4
                && s.Value.GetProperty("cloud").GetDouble() == 0.8 && Math.Abs(local - 1000) <= 4,
                $"saved: {saved.Replace("\r", "").Replace("\n", " ")}; first frame: {s?.GetRawText() ?? "none"}");
            var (l2, _) = OpenByIcon(again);
            Result(g, "after a restart the card shows the same", l2 != null && l2.Str("scene") == "3" && l2.Str("wall") == "4" && !l2.Is("follow") && l2.Str("weather") == "cloudy",
                l2 == null ? "card not open" : $"scene {l2.Str("scene")}, wall {l2.Str("wall")}, Live sun {(l2.Is("follow") ? "on" : "off")}, weather {l2.Str("weather")}");
            Result(g, "Light from: Right survives a restart", l2 != null && l2.Is("lightRight") && l2.Str("mirroredScenes") == "1,2",
                l2 == null ? "card not open" : $"right {l2.Is("lightRight")}, pictures turned over: {l2.Str("mirroredScenes")}");
            Esc();
        }
        finally { Quit(again); }

        // a broken file: the defaults, a line in the journal, no crash
        File.WriteAllText(SettingsFile, "{\"version\": 1, \"scene\": 2, \"wall\": ");
        using (var broken = StartApp("card-broken"))
        {
            try
            {
                var said = broken.Lines().FirstOrDefault(x => x.Contains("settings: the file is broken"));
                var st = WallState(broken)?.GetProperty("state");
                bool kept = File.Exists(Path.Combine(Path.GetDirectoryName(SettingsFile)!, "settings.broken.json"));
                Result(g, "a broken settings file: the defaults, a line in the journal, no crash",
                    said != null && st != null && st.Value.GetProperty("scene").GetInt32() == 0 && st.Value.GetProperty("wall").GetInt32() == 0 && !broken.Proc.HasExited && kept,
                    $"journal: {said ?? "nothing"}; first frame scene {st?.GetProperty("scene")}, wall {st?.GetProperty("wall")}; broken copy kept: {kept}");
            }
            finally { Quit(broken); }
        }
        // an incomplete one: what is there is kept, the rest takes its default
        File.WriteAllText(SettingsFile, "{\"version\": 1, \"scene\": 3, \"plant\": 7}");
        using (var part = StartApp("card-incomplete"))
        {
            try
            {
                var said = part.Lines().FirstOrDefault(x => x.Contains("settings: the file is broken or incomplete"));
                var st = WallState(part)?.GetProperty("state");
                Result(g, "an incomplete settings file: what is there is kept, the rest takes its default",
                    said != null && st != null && st.Value.GetProperty("scene").GetInt32() == 3 && st.Value.GetProperty("plant").GetInt32() == 2 && !part.Proc.HasExited,
                    $"journal: {said ?? "nothing"}; first frame scene {st?.GetProperty("scene")}, plant {st?.GetProperty("plant")}");
            }
            finally { Quit(part); }
        }

        // Launch at login after the app was moved: the entry follows it
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) k.SetValue("Hidamari", "\"C:\\Old place\\Hidamari.exe\"");
        WriteSettings(new());
        using (var moved = StartApp("card-moved"))
        {
            try
            {
                var said = moved.Lines().FirstOrDefault(x => x.Contains("login: the app was moved"));
                var entry = RunEntry();
                Result("card: settings", "Launch at login survives the app being moved", said != null && entry != null && entry.Trim('"').Equals(app, StringComparison.OrdinalIgnoreCase),
                    $"journal: {said ?? "nothing"}; Run entry now {entry ?? "none"}");
            }
            finally { Quit(moved); }
        }
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) k.DeleteValue("Hidamari", false);
    }

    // ================= where the card stands: the taskbar's edges, the scale, the icon under "^" =================

    static void CardPlaces()
    {
        const string g = "card: placement";
        WriteSettings(new());
        using var run = StartApp("card-places", new() { ["HIDAMARI_CHECK_PNG"] = "first" });
        try
        {
            // scale 125 % on the 1024×768 screen
            var s125 = Native.Scale();
            if (s125 != null && s125.Value.max >= 125 && s125.Value.now != 125)
            {
                run.Mark();
                Native.SetScale(125);
                run.Next("build (", TimeSpan.FromSeconds(30));
                run.Next("frame ", TimeSpan.FromSeconds(150));
                Thread.Sleep(2000);
                var (l125, _) = OpenByIcon(run);
                if (l125 == null) Result(g, "taskbar at the bottom, 125 %", false, "the card did not open");
                else
                {
                    Placement(g, l125, "taskbar at the bottom, 125 %");
                    Pictures.Crop(Pictures.Shot(null), l125.Window, "card/scale-125.png");
                    Esc();
                }
                run.Mark();
                Native.SetScale(s125.Value.now);
                run.Next("build (", TimeSpan.FromSeconds(30));
                run.Next("frame ", TimeSpan.FromSeconds(150));
                Thread.Sleep(2000);
            }

            // scale 150 % (the 1024×768 screen of GitHub's machines allows at most 125 %)
            run.Mark();
            bool big = Desk.Resolutions().Any(s => s.w == 1920 && s.h == 1080) && Desk.SetResolution(1920, 1080) == 0;
            if (big) { run.Next("build (", TimeSpan.FromSeconds(30)); run.Next("frame ", TimeSpan.FromSeconds(150)); Thread.Sleep(2000); }
            var scale = Native.Scale();
            if (scale == null || scale.Value.max < 150) Result(g, "scale 150 %", "SKIP", scale == null ? "the scale cannot be read" : $"at most {scale.Value.max} %");
            else
            {
                run.Mark();
                Native.SetScale(150);
                run.Next("build (", TimeSpan.FromSeconds(30));
                run.Next("frame ", TimeSpan.FromSeconds(30));
                Thread.Sleep(3000);
                var (l, _) = OpenByIcon(run);
                if (l == null) Result(g, "taskbar at the bottom, 150 %", false, "the card did not open");
                else
                {
                    Placement(g, l, "taskbar at the bottom, 150 %");
                    Pictures.Crop(Pictures.Shot(null), l.Window, "card/scale-150.png");
                    Esc();
                }
                run.Mark();
                Native.SetScale(scale.Value.now);
                run.Next("build (", TimeSpan.FromSeconds(30));
                run.Next("frame ", TimeSpan.FromSeconds(150));
                Thread.Sleep(2000);
            }
            if (big)
            {
                run.Mark();
                Desk.ResetResolution();
                run.Next("build (", TimeSpan.FromSeconds(30));
                run.Next("frame ", TimeSpan.FromSeconds(150));
                Thread.Sleep(2000);
            }

            // the icon under the "^": the card at the right end of the taskbar
            bool hidden = win11 ? Desk.Demote(app) : Desk.SetAutoTray(true);
            if (!win11) { RestartExplorer(); Desk.MinimizeAll(); }
            Thread.Sleep(3000);
            run.Mark();
            var since = DateTime.UtcNow;
            var second = Process.Start(new ProcessStartInfo(app) { UseShellExecute = false })!;
            second.WaitForExit(15000);
            var lo = LayoutAfter(run, since, x => x.Open, 10);
            if (lo != null)
            {
                int gap = (int)Math.Round(12 * lo.Scale);
                bool right = Math.Abs(lo.Card.Right - (Work.Right - gap)) <= 1 && Math.Abs(lo.Card.Bottom - (Work.Bottom - gap)) <= 1;
                Pictures.Shot("card/icon-hidden.png");
                Result(g, "the icon under \"^\": the card at the right end of the taskbar", hidden && right,
                    $"icon hidden: {hidden}, on the taskbar: {Desk.TrayIcon("Hidamari")?.ToString() ?? "no"}; card {lo.Card}, work area {Work}; " +
                    $"{run.Lines().LastOrDefault(x => x.Contains("card: opened")) ?? ""}");
                Esc();
            }
            else Result(g, "the icon under \"^\": the card at the right end of the taskbar", false, "the card did not open");
            if (win11) Desk.Promote(app); else { Desk.SetAutoTray(false); RestartExplorer(); Desk.MinimizeAll(); }
            Thread.Sleep(3000);

            // the taskbar's other edges (Windows 10 / Server 2022 lets it move; Windows 11 keeps it at the bottom)
            if (win11) Result(g, "taskbar at the top, left and right", "SKIP", "Windows 11 keeps the taskbar at the bottom");
            else
            {
                foreach (var (edge, code) in new[] { ("left", 0), ("top", 1), ("right", 2) })
                {
                    Desk.SetTaskbarEdge(code);
                    RestartExplorer();
                    Desk.MinimizeAll();
                    Thread.Sleep(4000);
                    var (l, _) = OpenByIcon(run);
                    if (l == null) { Result(g, $"taskbar on the {edge}", false, $"the card did not open; tray: {Desk.TrayNames()}"); continue; }
                    Placement(g, l, $"taskbar on the {edge}");
                    Pictures.Shot($"card/taskbar-{edge}.png");
                    Esc();
                    Thread.Sleep(800);
                }
                Desk.SetTaskbarEdge(3);
                RestartExplorer();
                Desk.MinimizeAll();
                Thread.Sleep(3000);
            }
        }
        finally { Quit(run); }
    }

    // ================= pictures for the sheet next to the design =================

    // a journal frame line drawn for Berlin h:m (the check's clock runs on, so up to 15 minutes later)
    static bool FrameAt(string line, int h, int m)
    {
        int i = line.IndexOf("Berlin 2026-11-18 ");
        if (i < 0 || !TimeSpan.TryParse(line.Substring(i + 18, 8), out var t)) return false;
        var d = t - new TimeSpan(h, m, 0);
        return d >= TimeSpan.Zero && d <= TimeSpan.FromMinutes(15);
    }

    static void CardShots()
    {
        const string g = "card: pictures";
        // the design's pictures are 2× points; at 150 % on a 1920×1080 screen the tallest card (the picker) fits
        var (w0, h0) = Desk.Resolution();
        bool big = Desk.Resolutions().Any(s => s.w == 1920 && s.h == 1080) && Desk.SetResolution(1920, 1080) == 0;
        var scale = Native.Scale();
        bool scaled = big && scale != null && scale.Value.max >= 150 && Native.SetScale(150);
        Thread.Sleep(3000);
        WriteSettings(new() { ["firstLaunchAnswered"] = false });
        using var run = StartApp("card-shots", new() { ["HIDAMARI_CHECK_PNG"] = "none" });
        int n = 0;
        try
        {
            Layout? Shot(string name, int h, int m, Action<Layout>? before_ = null, Func<Layout, bool>? until = null)
            {
                // the wall first, then the card: on GitHub's machines (no graphics card) a full-screen frame takes
                // up to a minute, and the card's page, in the same browser, waits for it
                run.Mark();
                Clock(run, h, m);
                // a frame of this moment: the clock's own frame, or a chosen thing's frame that already took it
                var drawn = run.Next(x => x.Contains(" frame ") && FrameAt(x, h, m), TimeSpan.FromSeconds(150));
                if (drawn == null) Say($"{name}: no frame after the clock was set");
                Thread.Sleep(500);
                var since = DateTime.UtcNow;
                Command(run, new { @do = "open" });
                var l = LayoutAfter(run, since, x => x.Open);
                if (l == null)
                {
                    // once more, with more time: on GitHub's machines the page sometimes answers late after a heavy frame
                    Say($"{name}: no layout within 20 s, opening again");
                    Command(run, new { @do = "close" });
                    Thread.Sleep(2000);
                    since = DateTime.UtcNow;
                    Command(run, new { @do = "open" });
                    l = LayoutAfter(run, since, x => x.Open, 60);
                }
                if (l == null) { Result(g, name, false, "the card did not open"); return null; }
                if (before_ != null) { before_(l); l = LayoutAfter(run, DateTime.UtcNow.AddSeconds(-0.3), until) ?? ReadLayout(run) ?? l; }
                Thread.Sleep(800);
                // the window and 10 points around the card, as the design's pictures show it
                int pad = (int)Math.Round(10 * l.Scale);
                var area = Rectangle.Union(l.Window, Rectangle.Inflate(l.Card, pad, pad));
                Pictures.Crop(Pictures.Shot(null), area, $"card/sheet/{name}.png");
                File.WriteAllText(Path.Combine(outDir, "card", "sheet", $"{name}.json"), JsonSerializer.Serialize(new { l.Window, l.Card, l.Scale, Shot = area, glass = l.Str("glass") }));
                n++;
                return l;
            }
            Directory.CreateDirectory(Path.Combine(outDir, "card", "sheet"));
            var fl = Shot("first-launch", 12, 10);
            if (fl != null) { ClickCard(run, fl, "firstNotNow", until: x => !x.Is("first")); }
            Command(run, new { @do = "close" });
            Shot("day", 12, 10); Command(run, new { @do = "close" });
            Shot("settings-button", 12, 10); Command(run, new { @do = "close" });
            // each picture with the choices its design picture shows
            Command(run, new { @do = "choose", type = "set", change = new { wall = 3, scene = 2 } });
            Command(run, new { @do = "choose", type = "pin", minutes = 940.0, dragging = false });
            Shot("golden", 15, 40); Command(run, new { @do = "close" });
            Command(run, new { @do = "choose", type = "set", change = new { follow = true, wall = 2, scene = 1 } });
            Shot("night", 22, 40); Command(run, new { @do = "close" });
            Command(run, new { @do = "choose", type = "set", change = new { wall = 0, plant = 2 } });
            Shot("window-plants", 12, 10); Command(run, new { @do = "close" });
            Command(run, new { @do = "choose", type = "set", change = new { weather = "rain" } });
            Shot("weather-by-hand", 12, 10); Command(run, new { @do = "close" });
            Command(run, new { @do = "choose", type = "set", change = new { weather = "clear", scene = 2 } });
            Command(run, new { @do = "choose", type = "custom", custom = new { L = 0.88, x = 0.5, y = 0.4, k = 0.45, rough = 1.0, shade = new { x = 0.06, y = 0.5 } }, startedFrom = (int?)null, finished = true });
            // the own wall's small pictures first: on GitHub's machines they take minutes, and the card waits for them
            if (run.Next("thumbs:", TimeSpan.FromSeconds(300)) == null) Say("own-wall-picker: no small pictures within 300 s");
            Thread.Sleep(2000);
            Shot("own-wall-picker", 11, 20, l => Native.Click(l.At_("own")), x => x.Picker); Command(run, new { @do = "close" });
            Command(run, new { @do = "choose", type = "set", change = new { scene = 0, wall = 0 } });

            // the glasses side by side: over a light wall (day) and a dark one (night)
            foreach (var glass in new[] { "acrylic", "plain", "site" })
            {
                if (glass == "acrylic" && Environment.OSVersion.Version.Build < 22621) continue;
                Command(run, new { @do = "glass", glass });
                foreach (var (when, h, m) in new[] { ("light", 12, 10), ("dark", 22, 40) })
                { Shot($"glass-{glass}-{when}", h, m); Command(run, new { @do = "close" }); }
            }
            Command(run, new { @do = "glass", glass = "plain" });
            // the settings' last line, where the weather and the cities come from, at night too
            Shot("settings-night", 22, 40, l => Native.Click(l.At_("gear")), x => x.Settings); Command(run, new { @do = "close" });
            // the settings last: turning Launch at login on makes Windows show a notice in the corner a while later,
            // over the pictures after it
            Command(run, new { @do = "choose", type = "login", on = true });
            Shot("settings", 12, 10, l => Native.Click(l.At_("gear")), x => x.Settings); Command(run, new { @do = "close" });
            Command(run, new { @do = "choose", type = "login", on = false });
            Result(g, "pictures of the card for the sheet", n >= 9, $"{n} pictures at {Native.Scale()?.now} % on {ScreenRect.Width}x{ScreenRect.Height}");
        }
        finally
        {
            Command(run, new { @do = "realClock" });
            Quit(run);
            if (scaled && scale != null) Native.SetScale(scale.Value.now);
            if (big) Desk.ResetResolution();
            Thread.Sleep(2000);
            WriteSettings(new());
        }
    }

    // ================= memory and speed: the card's engine asleep, or closed =================

    static readonly List<Dictionary<string, object?>> cardVariants = new();

    static void CardMemory()
    {
        foreach (var v in new[] { "sleep", "close" })
        {
            WriteSettings(new());
            using var run = StartApp($"card-memory-{v}", new() { ["HIDAMARI_CARD"] = v, ["HIDAMARI_CHECK_PNG"] = "none" });
            var row = new Dictionary<string, object?> { ["variant"] = v };
            try
            {
                Thread.Sleep(5000);
                row["before"] = run.Usage(TimeSpan.FromSeconds(20));
                var opens = new List<object>();
                for (int i = 0; i < 4; i++)
                {
                    run.Mark();
                    var (l, ms) = OpenByIcon(run);
                    var said = run.Next("card: opened", TimeSpan.FromSeconds(5));
                    opens.Add(new { seenMs = Math.Round(ms), appMs = said != null ? Run.Number(said, " in ") : null });
                    Thread.Sleep(1500);
                    if (i < 3) { Esc(); run.Next("card: closed", TimeSpan.FromSeconds(5)); Thread.Sleep(4000); }
                }
                row["opens"] = opens;
                row["open"] = run.Usage(TimeSpan.FromSeconds(15));
                // the processor while the sun is dragged back and forth for 15 seconds
                var lay = ReadLayout(run);
                if (lay != null && lay.Has("arc"))
                {
                    var a = lay.At_("arc", 0.1, 0.5); var b = lay.At_("arc", 0.9, 0.5);
                    var drag = new Thread(() =>
                    {
                        Native.Down(a);
                        var sw = Stopwatch.StartNew();
                        while (sw.Elapsed < TimeSpan.FromSeconds(15))
                        {
                            double t = (Math.Sin(sw.Elapsed.TotalSeconds * 1.2) + 1) / 2;
                            Native.MoveTo(new Point((int)(a.X + (b.X - a.X) * t), a.Y));
                            Thread.Sleep(16);
                        }
                        Native.Up();
                    });
                    int dragFrom = run.Lines().Length;
                    drag.Start();
                    row["dragging"] = run.Usage(TimeSpan.FromSeconds(14));
                    drag.Join();
                    row["dragFrames"] = run.Lines().Skip(dragFrom).Count(x => x.Contains("(the sun is dragged)"));
                }
                // a city looked for, as a person would: the list is read now, and let go two minutes after
                var lay2 = ReadLayout(run);
                if (lay2 != null && OpenSearch(run, lay2) != null)
                {
                    TypeCity(run, "san", "San");
                    row["searching"] = run.Usage(TimeSpan.FromSeconds(5));
                    Esc();
                    Thread.Sleep(1000);
                }
                Esc();
                run.Next("card: closed", TimeSpan.FromSeconds(5));
                Thread.Sleep(3000);
                row["justClosed"] = run.Usage(TimeSpan.FromSeconds(15));
                if (full)
                {
                    Thread.Sleep(TimeSpan.FromMinutes(10));
                    row["tenMinutes"] = run.Usage(TimeSpan.FromSeconds(20));
                    row["citiesLetGo"] = run.Lines().Any(x => x.Contains("place: the list of places let go"));
                }
            }
            finally
            {
                Quit(run);
                cardVariants.Add(row);
                Say($"card memory {v}: {JsonSerializer.Serialize(row)}");
            }
        }
        facts["cardMemory"] = cardVariants;
    }
}
