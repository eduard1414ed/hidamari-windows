using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace HidamariProbe;

/// Windows, stage 0. Starting the probe puts the wall under the desktop icons on every screen.
/// Starting it again (or `HidamariProbe.exe --stop`) closes the running one and gives the desktop back.
/// Closing it from Task Manager also works, but then the classic desktop may keep the last frame until the
/// wallpaper is next drawn.
static class Program
{
    const string MutexName = @"Local\HidamariProbe";
    const string StopName = @"Local\HidamariProbe.Stop";

    [STAThread]
    static int Main(string[] args)
    {
        using var mutex = new Mutex(true, MutexName, out bool first);
        if (!first || args.Contains("--stop"))
        {
            if (first) return 0;   // --stop with nothing running
            for (int i = 0; i < 20; i++)
            {
                if (EventWaitHandle.TryOpenExisting(StopName, out var ev)) { ev.Set(); ev.Dispose(); return 0; }
                Thread.Sleep(250);   // the running probe is still starting
            }
            return 1;
        }
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopName);
        Log.Start();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write($"error: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"fatal: {e.ExceptionObject}");
        ApplicationConfiguration.Initialize();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        var app = new ProbeApp(stop);
        Application.Run(app);
        Log.Write("stopped");
        return app.ExitCode;
    }
}

sealed class ProbeApp : ApplicationContext
{
    readonly SynchronizationContext ui = SynchronizationContext.Current!;
    readonly Desktop desktop = new();
    readonly List<WallWindow> walls = new();
    readonly MessageWindow messages;
    readonly System.Windows.Forms.Timer tick = new();
    readonly System.Windows.Forms.Timer rebuildSoon = new() { Interval = 1500 };
    readonly string webFolder = Path.Combine(Log.Folder, "web");
    // for the checks on GitHub only: the first frame of each screen is saved here as a PNG with its state
    readonly string? checkFolder = Environment.GetEnvironmentVariable("HIDAMARI_PROBE_CHECK");
    CoreWebView2Environment? env;
    bool software, building, stopping;
    int frames;
    public int ExitCode { get; private set; }

    public ProbeApp(EventWaitHandle stop)
    {
        messages = new MessageWindow(Rebuild);
        ThreadPool.RegisterWaitForSingleObject(stop, (_, _) => ui.Post(_ => Stop("asked to stop"), null), null, -1, true);
        tick.Tick += (_, _) => { tick.Stop(); _ = DrawAll("timer"); };
        rebuildSoon.Tick += (_, _) => { rebuildSoon.Stop(); _ = Build("screens changed"); };
        SystemEvents.DisplaySettingsChanged += (_, _) => Rebuild("screens changed");
        SystemEvents.PowerModeChanged += (_, e) => { if (e.Mode == PowerModes.Resume) ui.Post(_ => _ = DrawAll("woke up"), null); };
        ui.Post(_ => _ = Build("start"), null);
    }

    void Rebuild(string why)
    {
        Log.Write($"desktop: {why}, rebuilding in 1.5 s");
        rebuildSoon.Stop();
        rebuildSoon.Start();
    }

    /// Finds the layer under the icons and puts one wall on every screen.
    async Task Build(string why)
    {
        if (building || stopping) return;
        building = true;
        try
        {
            Close("rebuild");
            Log.Write($"build ({why}): screens {string.Join("; ", Screen.AllScreens.Select(s => $"{s.DeviceName} {s.Bounds.X},{s.Bounds.Y} {s.Bounds.Width}x{s.Bounds.Height}{(s.Primary ? " primary" : "")}"))}");
            bool found = false;
            for (int i = 0; i < 5 && !found; i++)
            {
                found = desktop.Find();
                if (!found) await Task.Delay(1000);
            }
            if (!found) { ExitCode = 2; Stop("no layer under the icons"); return; }
            Directory.CreateDirectory(webFolder);
            using (var page = typeof(ProbeApp).Assembly.GetManifestResourceStream("wall.html")!)
            using (var file = File.Create(Path.Combine(webFolder, "wall.html")))
                page.CopyTo(file);

            env ??= await MakeEnvironment(software);
            await OpenWalls();
            var gl = await walls[0].Graphics();
            Log.Write($"web: WebView2 {env.BrowserVersionString}, WebGL 2 {gl}");
            if (!gl.GetProperty("ok").GetBoolean() && !software)
            {
                // a machine without a usable graphics card (the GitHub machines): the web engine draws in software
                Log.Write("web: no WebGL 2, starting the web engine again with software drawing");
                Close("software drawing");
                software = true;
                env = await MakeEnvironment(true);
                await OpenWalls();
                Log.Write($"web: WebGL 2 {await walls[0].Graphics()}");
            }
            Log.Write($"desktop: Progman children, top first: {desktop.ChildOrder()}");
            await DrawAll("first frame", capture: checkFolder != null);
        }
        catch (Exception e)
        {
            Log.Write($"error: build failed: {e}");
            ExitCode = 3;
            Stop("build failed");
        }
        finally { building = false; }
    }

    async Task OpenWalls()
    {
        foreach (var screen in Screen.AllScreens)
        {
            var wall = new WallWindow(screen);
            walls.Add(wall);
            await wall.Open(desktop, env!, webFolder);
        }
    }

    async Task<CoreWebView2Environment> MakeEnvironment(bool soft)
    {
        var options = new CoreWebView2EnvironmentOptions();
        // Edge refuses software WebGL unless asked (SwiftShader); only used when there is no WebGL 2 otherwise
        if (soft) options.AdditionalBrowserArguments = "--use-angle=swiftshader --enable-unsafe-swiftshader --ignore-gpu-blocklist";
        var data = Path.Combine(Log.Folder, soft ? "WebView2-software" : "WebView2");
        return await CoreWebView2Environment.CreateAsync(null, data, options);
    }

    /// One frame on every screen, then the next one at the next five-minute mark.
    async Task DrawAll(string why, bool capture = false)
    {
        if (stopping || walls.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        var state = Wall.State(now);
        var local = TimeZoneInfo.ConvertTime(now, Wall.Berlin);
        foreach (var wall in walls.ToList())
        {
            try
            {
                var r = await wall.Frame(state, capture);
                if (!r.GetProperty("ok").GetBoolean()) { Log.Write($"frame: FAILED on {wall.Screen.DeviceName}: {r.GetProperty("error")}"); continue; }
                frames++;
                Log.Write($"frame {frames} ({why}) on {wall.Screen.DeviceName}: Berlin {local:yyyy-MM-dd HH:mm:ss}, time {state["time"]}, " +
                          $"utcOffset {state["utcOffset"]}, sun {r.GetProperty("sun").GetDouble():F2}°, canvas {r.GetProperty("width")}x{r.GetProperty("height")}, " +
                          $"devicePixelRatio {r.GetProperty("dpr")}");
                if (capture && checkFolder != null) SaveCheck(wall, state, r);
            }
            catch (Exception e) { Log.Write($"frame: FAILED on {wall.Screen.DeviceName}: {e.Message}"); }
        }
        var wait = Wall.UntilNextTick(DateTimeOffset.UtcNow);
        tick.Interval = Math.Max(1000, (int)wait.TotalMilliseconds);
        tick.Start();
    }

    void SaveCheck(WallWindow wall, Dictionary<string, object?> state, JsonElement r)
    {
        Directory.CreateDirectory(checkFolder!);
        var name = new string(wall.Screen.DeviceName.Where(char.IsLetterOrDigit).ToArray());
        var png = r.GetProperty("png").GetString()!;
        File.WriteAllBytes(Path.Combine(checkFolder!, $"canvas-{name}.png"), Convert.FromBase64String(png[(png.IndexOf(',') + 1)..]));
        File.WriteAllText(Path.Combine(checkFolder!, $"state-{name}.json"), JsonSerializer.Serialize(new
        {
            state,
            screen = new { wall.Screen.DeviceName, wall.Screen.Bounds.X, wall.Screen.Bounds.Y, wall.Screen.Bounds.Width, wall.Screen.Bounds.Height },
            canvas = new { width = r.GetProperty("width").GetInt32(), height = r.GetProperty("height").GetInt32() },
            devicePixelRatio = r.GetProperty("dpr").GetDouble(),
            sun = r.GetProperty("sun").GetDouble(),
            desktop = desktop.Raised ? "raised" : "classic",
            software,
            pid = System.Environment.ProcessId,
            browserPid = wall.BrowserProcessId,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    void Close(string why)
    {
        tick.Stop();
        if (walls.Count == 0) return;
        foreach (var w in walls) w.Dispose();
        Log.Write($"closed {walls.Count} wall(s) ({why})");
        walls.Clear();
    }

    void Stop(string why)
    {
        if (stopping) return;
        stopping = true;
        Log.Write($"stopping: {why}");
        Close("stop");
        if (desktop.Progman != IntPtr.Zero) desktop.Restore();
        messages.DestroyHandle();
        ExitThread();
    }
}

/// A hidden top-level window: Explorer tells top-level windows when it starts again ("TaskbarCreated"),
/// and the walls, being children of the desktop, would never hear it.
sealed class MessageWindow : NativeWindow
{
    readonly uint taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
    readonly Action<string> onExplorer;

    public MessageWindow(Action<string> onExplorer)
    {
        this.onExplorer = onExplorer;
        CreateHandle(new CreateParams { Caption = "Hidamari probe messages" });
    }

    protected override void WndProc(ref Message m)
    {
        if ((uint)m.Msg == taskbarCreated) onExplorer("Explorer started again");
        base.WndProc(ref m);
    }
}
