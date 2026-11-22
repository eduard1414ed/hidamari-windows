using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using static Hidamari.Native;

namespace Hidamari;

/// The running app: one wall under the icons on every screen (or only the main one), a new frame every five minutes
/// and right after anything that changes what the wall should show (a choice on the card, waking up, the clock, the
/// screens), nothing drawn while the wall cannot be seen, an icon in the tray that opens the card.
sealed class App : ApplicationContext
{
    readonly Desktop desktop = new();
    readonly Messages messages;
    readonly Tray tray;
    readonly Engine engine;
    readonly Covers covers;
    readonly Measure measure = new();
    readonly Settings settings;
    readonly bool firstLaunch;
    readonly Card card;
    readonly Surroundings around;
    readonly System.Windows.Forms.Timer saveSoon = new() { Interval = 400 };
    readonly System.Windows.Forms.Timer thumbsSoon = new() { Interval = 500 };
    /// The first launch: the place is asked of Windows only once the card's own question has gone (or after 5 minutes).
    readonly System.Windows.Forms.Timer placeLater = new() { Interval = 5 * 60 * 1000 };
    bool aroundStarted;
    readonly System.Windows.Forms.Timer tick = new();
    readonly System.Windows.Forms.Timer screensSoon = new() { Interval = 1500 };
    readonly System.Windows.Forms.Timer lookSoon = new() { Interval = 1000 };
    readonly System.Windows.Forms.Timer retry = new() { Interval = 60_000 };
    readonly System.Windows.Forms.Timer measureTimer = new() { Interval = 10 * 60_000 };
    readonly System.Windows.Forms.Timer firstMeasure = new() { Interval = 60_000 };
    readonly SynchronizationContext ui = SynchronizationContext.Current!;
    readonly List<RegisteredWaitHandle> waits = new();
    readonly Dictionary<string, (string why, DateTime since)> paused = new();
    // for the checks on GitHub only: frames are saved there as PNG pictures with their state
    readonly string? checkFolder = Environment.GetEnvironmentVariable("HIDAMARI_CHECK");
    readonly string checkPng = Environment.GetEnvironmentVariable("HIDAMARI_CHECK_PNG") ?? "all";
    List<WallWindow> walls = new();
    string signature = "", timeZone = TimeZoneInfo.Local.Id, screensWhy = "";
    string? drawWanted, rebuildWanted;
    bool drawStaleOnly, running, stopping, locked, displayOff, asleep, thumbsWanted, restWanted, wakeWanted, dragging;
    int frames, batches, ownThumbs;
    // the moment of the last frame on the main screen: the card shows that moment (AppModel.frameShown on the Mac)
    DateTimeOffset? shownMoment;
    DateTime lastSelect;
    // check mode only: the app's clock runs this far from the real one, for the card's pictures at given times
    TimeSpan clockShift;
    public int ExitCode { get; private set; }

    /// Chosen from the measurements on GitHub's machines: the engine is put to
    /// sleep between frames. After ten minutes Windows has trimmed it to about 50 MB of working memory for the whole
    /// app (the probe held about 525 MB), and a frame costs half the processor time of closing the engine and
    /// starting it again (Close keeps the least memory reserved, about 80 MB, but each frame starts the engine anew).
    public static EnginePolicy DefaultPolicy => EnginePolicy.Suspend;

    public App(EventWaitHandle again)
    {
        var policy = Enum.TryParse<EnginePolicy>(Environment.GetEnvironmentVariable("HIDAMARI_ENGINE"), true, out var p) ? p : DefaultPolicy;
        engine = new Engine(policy, Path.Combine(Log.Folder, "web"));
        if (checkFolder != null) Engine.PageTimeout = TimeSpan.FromSeconds(120);
        settings = Settings.Load(out firstLaunch);
        around = new Surroundings(settings, Now, why => WantDraw(why), Changed, () => { if (card.IsOpen) card.Push(); }, checkFolder != null);
        LoginItem.FollowMove();
        messages = new Messages(this);
        tray = new Tray(messages.Handle, () => Quit("Quit in the tray menu"), OnSelect);
        card = new Card(engine.SharedEnvironment, CardState, tray.Rect, checkFolder != null);
        engine.SharedWith = () => card.EngineRunning;
        card.Chosen += (type, m) => { try { OnChosen(type, m); } catch (Exception e) { Log.Write($"error: card {type}: {e}"); } };
        card.Opened += () => { wakeWanted = true; Kick(); around.OnCardOpened(); };
        card.Closed += _ => { dragging = false; restWanted = true; Kick(); StartAround("the first launch's card closed"); };
        card.Layout += SaveLayout;
        // Settings → Desktops (All / This one) is not offered: Windows does not tell an app which virtual desktop its windows are on
        ownThumbs = File.Exists(Path.Combine(Card.ThumbsFolder, "dapples.jpg")) ? 1 : 0;
        if (settings.Wall == Hidamari.Settings.OwnWall && ownThumbs == 0) thumbsWanted = true;
        covers = new Covers(() => WantDraw("a wall can be seen again", staleOnly: true));
        Log.Write($"app: web engine between frames: {policy}{(checkFolder != null ? $", check mode ({checkPng} frames saved)" : "")}");
        var screens = Screens.All();
        Measure.Machine(screens);

        tick.Tick += (_, _) => { tick.Stop(); OnTick(); };
        screensSoon.Tick += (_, _) => { screensSoon.Stop(); LookAtScreens(); };
        lookSoon.Tick += (_, _) => { lookSoon.Stop(); LookAtWalls(); };
        retry.Tick += (_, _) => { retry.Stop(); WantDraw("trying again after a failed frame"); };
        measureTimer.Tick += (_, _) => measure.Write("every 10 minutes");
        firstMeasure.Tick += (_, _) => { firstMeasure.Stop(); measure.Write("a minute after the first frame"); measureTimer.Start(); };
        saveSoon.Tick += (_, _) => { saveSoon.Stop(); settings.Save(); };
        thumbsSoon.Tick += (_, _) => { thumbsSoon.Stop(); thumbsWanted = true; Kick(); };

        Wait(again, () => { Log.Write("app: started again while running, opening the card"); _ = card.Open("Hidamari started again"); });
        if (checkFolder != null)
        {
            Wait(new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Hidamari.Check.Draw"), () => WantDraw("check"));
            Wait(new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Hidamari.Check.Measure"), () => measure.Write("check"));
            Wait(new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Hidamari.Check.Quit"), () => Quit("asked by the check"));
            Wait(new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Hidamari.Check.Command"), CheckCommand);
        }
        tray.Add("start");
        ui.Post(_ => WantRebuild("start"), null);
        // Windows 11 asks the person itself whether the app may know the place, over everything and until answered; a
        // click on its answer is a click outside the card, which closes the card. So at the first launch the question of
        // the card comes first, and Windows is asked once the card has closed.
        if (firstLaunch && !settings.FirstLaunchAnswered)
        {
            Log.Write("place: Windows will be asked once the first launch's card has closed (its own question would cover the card)");
            placeLater.Tick += (_, _) => StartAround("5 minutes after the first launch");
            placeLater.Start();
        }
        else ui.Post(_ => StartAround("start"), null);
    }

    void StartAround(string why)
    {
        if (aroundStarted) return;
        aroundStarted = true;
        placeLater.Stop();
        if (why != "start") Log.Write($"place: asking Windows now ({why})");
        around.Start();
    }

    void Wait(EventWaitHandle ev, Action then) =>
        waits.Add(ThreadPool.RegisterWaitForSingleObject(ev, (_, _) => ui.Post(_ => { if (!stopping) then(); }, null), null, -1, false));

    // ---------- what is wanted, done one at a time ----------

    void WantRebuild(string why)
    {
        rebuildWanted ??= why;
        Kick();
    }

    void WantDraw(string why, bool staleOnly = false)
    {
        if (drawWanted == null) { drawWanted = why; drawStaleOnly = staleOnly; }
        else drawStaleOnly &= staleOnly;
        Kick();
    }

    async void Kick()
    {
        if (running || stopping) return;
        running = true;
        try
        {
            while (!stopping)
            {
                if (rebuildWanted != null)
                {
                    var why = rebuildWanted;
                    rebuildWanted = null;
                    drawWanted = null;
                    await Rebuild(why);
                }
                else if (drawWanted != null)
                {
                    var why = drawWanted;
                    bool staleOnly = drawStaleOnly;
                    drawWanted = null;
                    await Draw(why, staleOnly);
                }
                else if (wakeWanted)
                {
                    // the wall's engine stays ready while the card is open, so every choice shows at once
                    wakeWanted = false;
                    if (card.IsOpen && walls.Count > 0) await Wake();
                }
                else if (thumbsWanted && !dragging)
                {
                    thumbsWanted = false;
                    await OwnThumbs();
                }
                else if (restWanted)
                {
                    restWanted = false;
                    if (!card.IsOpen) await Rest("the card closed");
                }
                else break;
            }
        }
        catch (Exception e) { Log.Write($"error: {e}"); }
        finally { running = false; }
    }

    // ---------- walls ----------

    /// Finds the layer under the icons and makes one wall per screen; the old walls go once the new ones have a frame.
    async Task Rebuild(string why)
    {
        var all = Screens.All();
        signature = Signature(all);
        // Settings → Screens: Main, only the main screen gets the wall; the others keep their own wallpaper
        var screens = settings.Screens == "main" ? all.Where(s => s.Primary).Take(1).ToList() : all;
        Log.Write($"build ({why}): screens {string.Join("; ", all)}{(settings.Screens == "main" ? $"; the wall only on the main one ({screens.FirstOrDefault()?.Short})" : "")}");
        bool found = false;
        for (int i = 0; i < 10 && !found && !stopping; i++)
        {
            found = desktop.Find();
            if (!found) await Task.Delay(1000);
        }
        if (stopping) return;
        if (!found)
        {
            Log.Write("build: no desktop to put the walls under; waiting for Explorer");
            Drop(walls, "no desktop");
            walls = new();
            Schedule();
            return;
        }
        var old = walls;
        var fresh = new List<WallWindow>();
        foreach (var s in screens)
        {
            var w = new WallWindow(s) { Composed = desktop.Composed };
            w.Lost += OnLost;
            if (!desktop.Attach(w.Handle, s.Bounds)) { w.Dispose(); continue; }
            fresh.Add(w);
        }
        walls = fresh;
        paused.Clear();
        await Draw(why, staleOnly: false);
        Drop(old, "replaced");
        Log.Write($"desktop: Progman children, top first: {desktop.ChildOrder()}");
    }

    static void Drop(List<WallWindow> list, string why)
    {
        if (list.Count == 0) return;
        foreach (var w in list) w.Dispose();
        Log.Write($"closed {list.Count} wall(s) ({why})");
    }

    void OnLost(WallWindow w)
    {
        if (stopping || !walls.Contains(w)) return;
        Log.Write($"desktop: the wall of {w.Screen.Short} was destroyed by Windows (Explorer closed its parent)");
        ui.Post(_ => WantRebuild("a wall was destroyed"), null);
    }

    /// The screens may have changed (Windows says so in several ways, often more than once): new walls only if
    /// names, places, sizes or scales really differ.
    public void ScreensMayHaveChanged(string why)
    {
        screensWhy = why;
        screensSoon.Stop();
        screensSoon.Start();
        tray.Refresh(why);
    }

    void LookAtScreens()
    {
        var now = Signature(Screens.All());
        if (now != signature) WantRebuild($"screens changed ({screensWhy})");
        card.OnScreensChanged();
    }

    /// What decides the walls: the screens, which one is the main one, and Settings → Screens.
    string Signature(List<ScreenInfo> screens) => Screens.Signature(screens) + $"; main {screens.FirstOrDefault(s => s.Primary)?.Name}; walls on {settings.Screens}";

    /// Explorer may have rebuilt its windows (a new wallpaper, a theme): are the walls still in their place?
    public void WallsMayHaveMoved() { lookSoon.Stop(); lookSoon.Start(); }

    void LookAtWalls()
    {
        if (stopping || walls.Count == 0 || running) return;
        var broken = desktop.Broken(walls.Select(w => w.Handle));
        if (broken == null) return;
        if (desktop.Raised && broken.StartsWith("order changed"))
        {
            Log.Write($"desktop: {broken}; putting the walls back between the icons and the wallpaper");
            desktop.Reorder(walls.Select(w => w.Handle));
            if (desktop.Broken(walls.Select(w => w.Handle)) == null) return;
        }
        WantRebuild($"the walls are not in place: {broken}");
    }

    // ---------- frames ----------

    void OnTick()
    {
        if (walls.Count == 0) { WantRebuild("no walls yet"); return; }
        if (Signature(Screens.All()) != signature) { WantRebuild("screens changed (seen at the five-minute mark)"); return; }
        // while Live sun is off the wall keeps its time: nothing to draw (AppClock on the Mac)
        if (settings.FollowsSun) WantDraw("timer");
        else Schedule();
    }

    DateTimeOffset Now() => DateTimeOffset.UtcNow + clockShift;

    /// A frame on every wall that can be seen (with `staleOnly`, only on those that show an older moment).
    async Task Draw(string why, bool staleOnly)
    {
        if (walls.Count == 0) { Schedule(); return; }
        var broken = desktop.Broken(walls.Select(w => w.Handle));
        if (broken != null && !(desktop.Raised && broken.StartsWith("order changed")))
        {
            rebuildWanted ??= $"the walls are not in place: {broken}";
            return;
        }
        if (broken != null) desktop.Reorder(walls.Select(w => w.Handle));

        string? all = locked ? "the screen is locked" : displayOff ? "the screens are off" : asleep ? "the computer sleeps" : null;
        var targets = new List<WallWindow>();
        foreach (var w in walls)
        {
            if (staleOnly && !w.Stale) continue;
            var cover = all ?? (covers.CoveredBy(w.Screen) is { } c ? $"covered by {c}" : null);
            if (cover != null)
            {
                Pause(w, cover, why);
                continue;
            }
            targets.Add(w);
        }
        covers.Listen(walls.Any(w => w.Stale && paused.TryGetValue(w.Screen.Name, out var p) && p.why.StartsWith("covered")));
        if (targets.Count == 0) { Schedule(); return; }

        var now = Now();
        var place = settings.Place;
        var weather = around.ForWall(settings.Scene);
        var state = Wall.State(now, settings, weather);
        var moment = Wall.Moment(now, settings);
        var local = TimeZoneInfo.ConvertTime(moment, place.TimeZone);
        var total = Stopwatch.StartNew();
        var cpuBefore = measure.CpuByPid();
        long startMs = 0;
        int drawn = 0;
        try
        {
            startMs = await engine.Begin();
            foreach (var w in targets)
            {
                if (stopping || !walls.Contains(w)) continue;
                bool png = checkFolder != null && (checkPng == "all" || (checkPng == "first" && frames == 0));
                var r = await engine.Draw(w.Screen, state, w, png);
                if (stopping) return;
                if (!w.Shown) { desktop.Show(w.Handle, w.Screen.Bounds); w.Shown = true; }
                w.Stale = false;
                Resume(w);
                frames++;
                drawn++;
                if (w == walls[0])
                {
                    shownMoment = moment;
                    if (card.IsOpen) card.Push();
                }
                Log.Write($"frame {frames} ({why}) on {w.Screen.Short}: {place.Name} {local:yyyy-MM-dd HH:mm:ss}{(settings.FollowsSun ? "" : " (Live sun off)")}, utcOffset {state["utcOffset"]}, " +
                          $"scene {settings.Scene}, wall {settings.Wall}, plant {settings.Plant}, weather {(settings.LiveWeather ? "live" : "by hand " + settings.ManualWeather)} (cloud {state["cloud"]:0.00}, {state["weather"]}){((bool)state["mirror"]! ? ", mirrored" : "")}, " +
                          $"sun {r.Sun:F2}°, frame {r.Width}x{r.Height} for {w.Screen.Bounds.Width}x{w.Screen.Bounds.Height} at {w.Screen.Scale * 100:F0} %, " +
                          $"size {r.SizeMs} ms, draw {r.DrawMs} ms, copy {r.CopyMs} ms ({r.Mode})");
                if (r.Png != null) SaveCheck(w, state, r);
            }
        }
        catch (Exception e)
        {
            Log.Write($"error: frame failed: {e.Message}");
            foreach (var w in targets) w.Stale = true;
            retry.Stop();
            retry.Start();
        }
        var cpuAfter = measure.CpuByPid();
        var drawMs = total.ElapsedMilliseconds;
        var rest = Stopwatch.StartNew();
        // while the card is open the engine stays ready: the next choice shows at once
        bool held = card.IsOpen || dragging;
        if (!held)
        {
            try { await engine.Rest(); }
            catch (Exception e) { Log.Write($"web: could not put the engine to rest: {e.Message}"); }
        }
        batches++;
        // while the sun is dragged, frames follow each other quickly: the journal keeps only every tenth
        if (!dragging || batches % 10 == 0)
            Log.Write($"batch {batches} ({why}): {drawn} of {walls.Count} wall(s), engine start {startMs} ms, frames ready after {drawMs} ms, " +
                      $"processor {Measure.CpuSince(cpuBefore, cpuAfter) * 1000:F0} ms (app and web engine), " +
                      (held ? "engine kept ready (the card is open)" : $"engine to rest ({engine.Policy}) {rest.ElapsedMilliseconds} ms"));
        if (batches == 1)
        {
            firstMeasure.Start();
            if (firstLaunch && !settings.FirstLaunchAnswered) ui.Post(_ => _ = card.Open("first launch"), null);
        }
        Schedule();
    }

    async Task Wake()
    {
        try { await engine.Begin(); }
        catch (Exception e) { Log.Write($"web: could not wake the engine for the card: {e.Message}"); }
    }

    async Task Rest(string why)
    {
        try { await engine.Rest(); Log.Write($"web: engine to rest ({engine.Policy}, {why})"); }
        catch (Exception e) { Log.Write($"web: could not put the engine to rest: {e.Message}"); }
    }

    void Pause(WallWindow w, string reason, string why)
    {
        w.Stale = true;
        if (paused.TryGetValue(w.Screen.Name, out var p) && p.why == reason) return;
        paused[w.Screen.Name] = (reason, DateTime.UtcNow);
        Log.Write($"pause on {w.Screen.Short}: {reason} (no frame for: {why})");
    }

    void Resume(WallWindow w)
    {
        if (!paused.Remove(w.Screen.Name, out var p)) return;
        Log.Write($"pause over on {w.Screen.Short} after {(DateTime.UtcNow - p.since).TotalMinutes:F1} min (was: {p.why})");
    }

    /// The next frame at the next five-minute mark.
    void Schedule()
    {
        if (stopping) return;
        tick.Stop();
        tick.Interval = Math.Max(1000, (int)Wall.UntilNextTick(Now()).TotalMilliseconds);
        tick.Start();
    }

    void SaveCheck(WallWindow w, Dictionary<string, object?> state, FrameResult r)
    {
        try
        {
            var dir = Path.Combine(checkFolder!, "canvas");
            Directory.CreateDirectory(dir);
            var png = r.Png!;
            File.WriteAllBytes(Path.Combine(dir, $"canvas-{w.Screen.Short}.png"), Convert.FromBase64String(png[(png.IndexOf(',') + 1)..]));
            File.WriteAllText(Path.Combine(dir, $"state-{w.Screen.Short}.json"), JsonSerializer.Serialize(new
            {
                state,
                frame = frames,
                settings = new { settings.Scene, settings.Wall, settings.Plant, settings.FollowsSun, settings.PinnedMinutes, settings.ManualWeather, settings.WindowOnRight, settings.LiveWeather, settings.PlaceMode, place = settings.Place.ForPage(), custom = settings.Custom.ForPage() },
                screen = new { w.Screen.Name, w.Screen.Bounds.X, w.Screen.Bounds.Y, w.Screen.Bounds.Width, w.Screen.Bounds.Height, w.Screen.Scale },
                canvas = new { width = r.Width, height = r.Height },
                devicePixelRatio = w.Screen.Scale,
                sun = r.Sun,
                mode = r.Mode,
                desktop = desktop.Raised ? "raised" : "classic",
                pid = Environment.ProcessId,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { Log.Write($"check: could not save the frame: {e.Message}"); }
    }

    // ---------- the card ----------

    /// A click on the tray icon (or Enter on it).
    void OnSelect(string why)
    {
        // Windows sends Enter on the icon twice
        if (why.StartsWith("the keyboard") && (DateTime.UtcNow - lastSelect).TotalMilliseconds < 400) return;
        lastSelect = DateTime.UtcNow;
        card.Toggle(why);
    }

    /// Everything the card shows (card.js, `A`).
    Dictionary<string, object?> CardState()
    {
        var now = Now();
        return new()
        {
            ["scene"] = settings.Scene, ["wall"] = settings.Wall, ["custom"] = settings.Custom.ForPage(), ["plant"] = settings.Plant,
            ["follow"] = settings.FollowsSun, ["pinned"] = settings.PinnedMinutes, ["weather"] = settings.ManualWeather,
            ["screens"] = settings.Screens, ["desktops"] = "all", ["desktopsOffered"] = false, ["windowOnRight"] = settings.WindowOnRight,
            ["login"] = LoginItem.IsOn, ["firstAnswered"] = settings.FirstLaunchAnswered,
            ["shown"] = (double)(shownMoment ?? Wall.Moment(now, settings)).ToUnixTimeMilliseconds(),
            ["now"] = (double)now.ToUnixTimeMilliseconds(),
            ["place"] = settings.Place.ForPage(),
            ["placeMode"] = settings.PlaceMode,
            ["locationUnavailable"] = around.LocationUnavailable,
            ["live"] = settings.LiveWeather,
            ["ownThumbs"] = ownThumbs,
        };
    }

    /// Something was chosen on the card: it goes into the settings, on the wall at once, and into the journal.
    void OnChosen(string type, JsonElement m)
    {
        switch (type)
        {
            case "set":
                foreach (var p in m.GetProperty("change").EnumerateObject()) Set(p.Name, p.Value);
                break;
            case "pin":
            {
                double minutes = Math.Clamp(m.GetProperty("minutes").GetDouble(), 0, 1439);
                bool now = m.GetProperty("dragging").GetBoolean();
                bool wasFollowing = settings.FollowsSun;
                if (now && !dragging) Log.Write("card: the sun is being dragged");
                dragging = now;
                settings.FollowsSun = false;
                settings.PinnedMinutes = minutes;
                if (!now)
                {
                    Changed($"time pinned at {Hidamari.Settings.Clock(minutes)}{(wasFollowing ? ", Live sun off" : "")}");
                    if (thumbsWanted) Kick();
                }
                WantDraw(now ? "the sun is dragged" : "the time was chosen");
                break;
            }
            case "custom":
            {
                var c = m.GetProperty("custom");
                (double, double)? shade = c.TryGetProperty("shade", out var sh) && sh.ValueKind == JsonValueKind.Object
                    ? (sh.GetProperty("x").GetDouble(), sh.GetProperty("y").GetDouble()) : null;
                settings.Custom = new CustomWall(c.GetProperty("L").GetDouble(), c.GetProperty("x").GetDouble(), c.GetProperty("y").GetDouble(),
                    c.GetProperty("k").GetDouble(), c.GetProperty("rough").GetDouble(), shade).Clamped();
                bool wasOwn = settings.Wall == Hidamari.Settings.OwnWall;
                settings.Wall = Hidamari.Settings.OwnWall;
                if (m.TryGetProperty("startedFrom", out var from) && from.ValueKind == JsonValueKind.Number) StandIns(from.GetInt32());
                bool finished = m.GetProperty("finished").GetBoolean();
                dragging = !finished;
                if (finished)
                {
                    Changed($"own wall{(wasOwn ? "" : " chosen")}: L {settings.Custom.L:F3} x {settings.Custom.X:F3} y {settings.Custom.Y:F3} " +
                            $"k {settings.Custom.K:F3} texture {settings.Custom.Rough:F2} shade {(settings.Custom.Shade is { } s ? $"{s.X:F3},{s.Y:F3}" : "none")}");
                    // the own wall's small pictures half a second after the hand lets go (OwnWallThumbs on the Mac)
                    thumbsSoon.Stop();
                    thumbsSoon.Start();
                }
                WantDraw(finished ? "the own wall was changed" : "the own wall is being changed");
                break;
            }
            case "quit":
                Quit("Quit on the card");
                break;
            case "search":
            {
                var text = m.GetProperty("text").GetString() ?? "";
                int sid = m.TryGetProperty("id", out var si) ? si.GetInt32() : 0;
                _ = Found(text, sid);
                break;
            }
            case "searchOpen":
                if (m.GetProperty("open").GetBoolean()) { Log.Write("card: place search opened"); around.SearchOpened(); }
                else { Log.Write("card: place search closed"); around.SearchClosed(); }
                break;
            case "choosePlace":
                around.Choose(m.GetProperty("index").GetInt32());
                break;
            case "currentLocation":
                around.ChooseCurrent();
                break;
            case "openLink":
            {
                var to = m.GetProperty("to").GetString();
                var url = to == "geonames" ? "https://www.geonames.org/" : "https://api.met.no/doc/License";
                Log.Write($"card: {url} opened");
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch (Exception e) { Log.Write($"card: could not open {url} ({e.Message})"); }
                break;
            }
            case "locationSettings":
                Log.Write("card: Windows' location settings opened");
                Locator.OpenSettings();
                break;
            case "login":
                LoginItem.Set(m.GetProperty("on").GetBoolean(), "Settings → Launch at login");
                settings.FirstLaunchAnswered = true;
                Changed($"Launch at login {(LoginItem.IsOn ? "on" : "off")}");
                card.Push();
                break;
            case "firstLaunch":
            {
                bool open = m.GetProperty("openAtLogin").GetBoolean();
                if (open) LoginItem.Set(true, "the first launch's question");
                settings.FirstLaunchAnswered = true;
                Changed($"first launch answered: {(open ? "Open at login" : "Not now")}");
                card.Push();
                break;
            }
            default:
                Log.Write($"card: unknown message {type}");
                break;
        }
    }

    void Set(string key, JsonElement v)
    {
        switch (key)
        {
            case "wall":
            {
                int w = v.GetInt32();
                if (w is < -1 or > 5 || w == settings.Wall) return;
                settings.Wall = w;
                Changed($"wall {(w == Hidamari.Settings.OwnWall ? "own" : Strings.Walls[w])}");
                WantDraw("a wall was chosen");
                break;
            }
            case "scene":
            {
                int sc = v.GetInt32();
                if (sc is < 0 or > 3 || sc == settings.Scene) return;
                settings.Scene = sc;
                Changed($"scene {Strings.Scenes[sc]}");
                WantDraw("a scene was chosen");
                break;
            }
            case "plant":
            {
                int pl = v.GetInt32();
                if (!Hidamari.Settings.PlantChoices.Contains(pl) || pl == settings.Plant) return;
                settings.Plant = pl;
                Changed($"plant {Strings.Plants[pl]}");
                WantDraw("a plant was chosen");
                break;
            }
            case "weather":
            {
                var w = v.GetString() ?? "";
                if (!Hidamari.Settings.Weathers.Contains(w) || w == settings.ManualWeather) return;
                settings.ManualWeather = w;
                Changed($"weather by hand {w}");
                WantDraw("the weather was chosen");
                break;
            }
            case "follow":
            {
                bool on = v.GetBoolean();
                if (on == settings.FollowsSun) return;
                settings.FollowsSun = on;
                Changed(on ? "Live sun on (the real time again)" : $"Live sun off at {Hidamari.Settings.Clock(settings.PinnedMinutes)}");
                WantDraw(on ? "Live sun on" : "Live sun off");
                break;
            }
            case "pinned":
                settings.PinnedMinutes = Math.Clamp(v.GetDouble(), 0, 1439);
                break;
            case "live":
                around.SetLive(v.GetBoolean());
                break;
            case "windowOnRight":
            {
                bool right = v.GetBoolean();
                if (right == settings.WindowOnRight) return;
                settings.WindowOnRight = right;
                Changed($"Light from: {(right ? "Right" : "Left")}");
                // only the window and the blinds change; a frame anyway, it is one choice and cheap enough
                WantDraw("Light from was chosen");
                break;
            }
            case "screens":
            {
                var sc = v.GetString() == "main" ? "main" : "all";
                if (sc == settings.Screens) return;
                settings.Screens = sc;
                Changed($"Screens: {(sc == "main" ? "Main" : "All")}");
                WantRebuild($"Screens: {(sc == "main" ? "Main" : "All")}");
                break;
            }
        }
    }

    /// The search's lines for what was typed; `id` lets the page drop answers to letters typed since.
    async Task Found(string text, int id)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var list = await around.Search(text);
            card.Send(new
            {
                type = "found", id, text,
                items = list.Select((c, i) => new { title = c.Title, subtitle = c.Subtitle, index = i }).ToArray(),
            });
            // what was typed is never written in the journal: only how long it took
            if (checkFolder != null || sw.ElapsedMilliseconds > 50) Log.Write($"card: search {text.Length} letter(s), {list.Count} place(s) in {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception e) { Log.Write($"card: the search failed ({e.Message})"); }
    }

    /// A settings change: saved a moment later (several come together), told to the card, written in the journal.
    void Changed(string what)
    {
        Log.Write($"settings: {what}");
        saveSoon.Stop();
        saveSoon.Start();
    }

    // ---------- the own wall's small pictures ----------

    static readonly string[] ThumbNames = { "dapples", "blinds", "water", "window-0", "window-2", "window-5", "plant-0", "plant-2", "plant-5" };

    /// The own wall was started from approved wall `from`: until its own pictures are drawn, the card shows that
    /// wall's pictures (OwnWallThumbs on the Mac does the same).
    void StandIns(int from)
    {
        if (from is < 0 or > 5) return;
        try
        {
            foreach (var n in ThumbNames)
                File.Copy(Path.Combine(Card.Folder, "img", "thumbs", $"{n}-{from}.jpg"), Path.Combine(Card.ThumbsFolder, $"{n}.jpg"), overwrite: true);
            ownThumbs++;
        }
        catch (Exception e) { Log.Write($"thumbs: could not put the stand-in pictures ({e.Message})"); }
    }

    async Task OwnThumbs()
    {
        var sw = Stopwatch.StartNew();
        var custom = settings.Custom;
        try
        {
            await engine.Begin();
            var pictures = await engine.Thumbs(new Dictionary<string, object?> { ["wall"] = Hidamari.Settings.OwnWall, ["custom"] = custom.ForPage() });
            Directory.CreateDirectory(Card.ThumbsFolder);
            foreach (var (name, url) in pictures)
                File.WriteAllBytes(Path.Combine(Card.ThumbsFolder, $"{name}.jpg"), Convert.FromBase64String(url[(url.IndexOf(',') + 1)..]));
            File.WriteAllText(Path.Combine(Card.ThumbsFolder, "wall.json"), JsonSerializer.Serialize(custom.ForPage()));
            ownThumbs++;
            Log.Write($"thumbs: {pictures.Count} pictures of the own wall drawn in {sw.ElapsedMilliseconds} ms");
            card.Push();
        }
        catch (Exception e) { Log.Write($"thumbs: could not draw the own wall's pictures ({e.Message})"); }
        if (!card.IsOpen) await Rest("after the small pictures");
    }

    // ---------- check mode ----------

    void SaveLayout(JsonElement m)
    {
        if (checkFolder == null) return;
        try
        {
            var w = card.WindowRect;
            File.WriteAllText(Path.Combine(checkFolder, "card-layout.json"), JsonSerializer.Serialize(new
            {
                window = new { w.X, w.Y, w.Width, w.Height },
                card = new { card.CardRect.X, card.CardRect.Y, card.CardRect.Width, card.CardRect.Height },
                scale = card.Scale,
                open = card.IsOpen,
                settings = card.SettingsOpen,
                rects = m.GetProperty("rects"),
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { Log.Write($"check: could not save the card's layout ({e.Message})"); }
    }

    /// The check on GitHub asks for something through command.json: open or close the card, the clock, the glass.
    void CheckCommand()
    {
        string reply;
        int id = 0;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(checkFolder!, "command.json")));
            var c = doc.RootElement;
            id = c.TryGetProperty("id", out var i) ? i.GetInt32() : 0;
            var what = c.GetProperty("do").GetString();
            Log.Write($"check: command {id} {c.GetRawText()}");
            switch (what)
            {
                case "open": _ = card.Open("check"); break;
                case "close": card.Close("check"); break;
                case "clock":
                    clockShift = TimeSpan.FromMilliseconds(c.GetProperty("ms").GetDouble() - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    shownMoment = null;
                    WantDraw("check: the clock");
                    break;
                case "realClock": clockShift = TimeSpan.Zero; shownMoment = null; WantDraw("check: the real clock"); break;
                case "glass":
                    card.Close("check: another glass");
                    card.SetGlass(Enum.Parse<Glass>(c.GetProperty("glass").GetString()!, true));
                    break;
                case "choose": OnChosen(c.GetProperty("type").GetString()!, c); break;
                case "push": card.Push(); break;
                case "weather":
                {
                    var t = around.Test ?? throw new InvalidOperationException("the weather comes from MET, not from the test source");
                    if (c.TryGetProperty("offline", out var off) && off.GetBoolean()) t.GoOffline();
                    else t.Set(new WeatherNow(c.GetProperty("cloud").GetDouble(), Enum.Parse<WeatherKind>(c.GetProperty("kind").GetString()!, true),
                        c.TryGetProperty("wind", out var wi) ? wi.GetDouble() : null));
                    // "ask": false only changes what the source will say next time
                    if (!c.TryGetProperty("ask", out var ask) || ask.GetBoolean()) around.Ask("check: new test weather", scheduled: true);
                    break;
                }
                case "fix":
                    _ = around.Take(new Locator.Fix(Locator.Outcome.Located, c.GetProperty("lat").GetDouble(), c.GetProperty("lng").GetDouble(), 100, "given by the check"), "check");
                    break;
                case "locate": _ = around.Locate("check"); break;
                case "zones": _ = Zones_(); break;
                case "measure": measure.Write(c.TryGetProperty("why", out var why) ? why.GetString() ?? "check" : "check"); break;
                default: throw new InvalidDataException($"unknown command {what}");
            }
            reply = "ok";
        }
        catch (Exception e) { reply = e.Message; Log.Write($"check: command failed ({e.Message})"); }
        try { File.WriteAllText(Path.Combine(checkFolder!, "command-reply.json"), JsonSerializer.Serialize(new { id, reply, open = card.IsOpen, lastOpenMs = card.LastOpenMs, asked = around.Asked, located = around.Located?.ToString(), place = settings.Place.ForPage(), placeMode = settings.PlaceMode })); }
        catch { }
    }

    async Task Zones_()
    {
        try { File.WriteAllText(Path.Combine(checkFolder!, "zones.json"), JsonSerializer.Serialize(await around.ZoneReport(), new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception e) { Log.Write($"check: zones failed ({e.Message})"); }
    }

    // ---------- news from Windows (Messages) ----------

    public void OnExplorer()
    {
        Log.Write("app: Explorer started again");
        tray.Add("Explorer started again");
        WantRebuild("Explorer started again");
    }

    public void OnClock()
    {
        TimeZoneInfo.ClearCachedData();
        timeZone = TimeZoneInfo.Local.Id;
        WantDraw("the clock or the time zone changed");
    }

    public void OnSettings(int what, string? area)
    {
        if (area == "ImmersiveColorSet") tray.Refresh("the taskbar's colour changed");
        if (what == (int)SPI_SETDESKWALLPAPER || area is "Policy" or "WindowsThemeElement") WallsMayHaveMoved();
        ScreensMayHaveChanged("settings changed");
        // a new time zone is not always told by WM_TIMECHANGE
        TimeZoneInfo.ClearCachedData();
        if (TimeZoneInfo.Local.Id != timeZone)
        {
            Log.Write($"app: time zone {timeZone} → {TimeZoneInfo.Local.Id}");
            timeZone = TimeZoneInfo.Local.Id;
            WantDraw("the time zone changed");
        }
    }

    public void OnSleep(bool sleeping)
    {
        asleep = sleeping;
        Log.Write(sleeping ? "power: going to sleep" : "power: woke up");
        if (sleeping) around.SetQuiet(true, "going to sleep");
        else { WantDraw("woke up"); around.OnWake(); }
    }

    public void OnDisplay(int state)
    {
        bool off = state == 0;
        Log.Write($"power: screens {(state switch { 0 => "off", 1 => "on", 2 => "dimmed", _ => state.ToString() })}");
        if (off == displayOff) return;
        displayOff = off;
        if (!off) WantDraw("the screens are on again", staleOnly: true);
    }

    public void OnSession(int change)
    {
        switch (change)
        {
            case WTS_SESSION_LOCK:
                locked = true;
                Log.Write("session: locked");
                around.SetQuiet(true, "locked");
                break;
            case WTS_SESSION_UNLOCK:
                locked = false;
                Log.Write("session: unlocked");
                around.SetQuiet(false, "unlocked");
                WantDraw("unlocked", staleOnly: true);
                break;
            default:
                Log.Write($"session: change {change}");
                break;
        }
    }

    public void OnTrayMessage(IntPtr w, IntPtr l) => tray.OnCallback(w, l);

    public void OnEndSession()
    {
        Log.Write("session: Windows is shutting down or signing out");
        Quit("Windows is shutting down or signing out");
    }

    // ---------- the end ----------

    public void Quit(string why)
    {
        if (stopping) return;
        Log.Write($"stopping: {why}");
        try { measure.Write("at quit"); } catch { }
        stopping = true;
        foreach (var t in new[] { tick, screensSoon, lookSoon, retry, measureTimer, firstMeasure, thumbsSoon }) t.Stop();
        if (saveSoon.Enabled) { saveSoon.Stop(); settings.Save(); }
        foreach (var w in waits) w.Unregister(null);
        try { card.Close("the app quits"); card.Dispose(); } catch (Exception e) { Log.Write($"card: {e.Message}"); }
        covers.Dispose();
        around.Dispose();
        Drop(walls, "quit");
        walls = new();
        if (desktop.Progman != IntPtr.Zero) desktop.Restore();
        tray.Dispose();
        engine.Dispose();
        measure.Dispose();
        messages.DestroyHandle();
        Log.Write("stopped");
        ExitThread();
    }

    /// Something went wrong that the app cannot go on from: give the desktop back as well as possible.
    public void Crash(string what)
    {
        Log.Write($"fatal: {what}");
        try { Quit("a fatal error"); } catch (Exception e) { Log.Write($"fatal: could not clean up: {e.Message}"); }
    }
}

/// A hidden top-level window that hears what Windows tells all programs: Explorer started again, screens and
/// settings changed, the clock, power, the session, the end of the session, and the tray icon's clicks.
sealed class Messages : NativeWindow
{
    readonly App app;
    readonly uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");

    public Messages(App app)
    {
        this.app = app;
        CreateHandle(new CreateParams { Caption = "Hidamari messages", X = 0, Y = 0, Width = 1, Height = 1 });
        var display = GUID_CONSOLE_DISPLAY_STATE;
        RegisterPowerSettingNotification(Handle, ref display, 0);
        WTSRegisterSessionNotification(Handle, 0);
    }

    protected override void WndProc(ref Message m)
    {
        try
        {
            switch (m.Msg)
            {
                case Tray.CallbackMessage:
                    app.OnTrayMessage(m.WParam, m.LParam);
                    return;
                case WM_DISPLAYCHANGE:
                    app.ScreensMayHaveChanged("display change");
                    break;
                case WM_DPICHANGED:
                    app.ScreensMayHaveChanged("scale change");
                    break;
                case WM_SETTINGCHANGE:
                    app.OnSettings((int)(long)m.WParam, m.LParam == IntPtr.Zero ? null : Marshal.PtrToStringUni(m.LParam));
                    break;
                case WM_TIMECHANGE:
                    app.OnClock();
                    break;
                case WM_POWERBROADCAST:
                    switch ((int)(long)m.WParam)
                    {
                        case PBT_APMSUSPEND: app.OnSleep(true); break;
                        case PBT_APMRESUMEAUTOMATIC: app.OnSleep(false); break;
                        case PBT_POWERSETTINGCHANGE:
                            var s = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(m.LParam);
                            if (s.PowerSetting == GUID_CONSOLE_DISPLAY_STATE) app.OnDisplay(s.Data);
                            break;
                    }
                    m.Result = new IntPtr(1);
                    return;
                case WM_WTSSESSION_CHANGE:
                    app.OnSession((int)(long)m.WParam);
                    break;
                case WM_QUERYENDSESSION:
                    // never holds Windows up
                    m.Result = new IntPtr(1);
                    return;
                case WM_ENDSESSION:
                    if (m.WParam != IntPtr.Zero) app.OnEndSession();
                    m.Result = IntPtr.Zero;
                    return;
                default:
                    if ((uint)m.Msg == taskbarCreated) app.OnExplorer();
                    break;
            }
        }
        catch (Exception e) { Log.Write($"error: {e}"); }
        base.WndProc(ref m);
    }

    public override void DestroyHandle()
    {
        if (Handle == IntPtr.Zero) return;
        WTSUnRegisterSessionNotification(Handle);
        base.DestroyHandle();
    }
}
