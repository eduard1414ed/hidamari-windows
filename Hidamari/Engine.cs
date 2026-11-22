using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using static Hidamari.Native;

namespace Hidamari;

/// What happens to the web engine between frames. The wall changes once in five minutes, so the engine need not
/// stay ready all the time. Chosen after measuring all four on the GitHub machines.
enum EnginePolicy
{
    /// stays as it is, ready for the next frame
    Keep,
    /// stays, but is asked to give back as much memory as it can (CoreWebView2MemoryUsageTargetLevel.Low)
    Low,
    /// is asked to give back memory and then frozen (TrySuspendAsync) until the next frame
    Suspend,
    /// is closed after every frame, all its processes leave; the next frame starts it again
    Close,
}

/// The size and the moments of one frame, for the journal.
sealed record FrameResult(int Width, int Height, double Sun, string Mode, long SizeMs, long DrawMs, long CopyMs, string? Png);

/// One web engine (WebView2) for all screens. It is never on the screen: it draws a frame of wall.html at the size of
/// each screen in turn, and the frame's pixels are copied into that screen's wall (WallWindow).
///
/// The page itself is untouched. A small script added before it calls komorebi.render() and, in the same go, reads the
/// canvas' pixels (WebGL readPixels) into memory shared with the app, so no picture file is made per frame.
///
/// For live motion (stage 4) the same engine can be given to the walls instead: a WebView2 inside each WallWindow.
sealed class Engine : IDisposable
{
    public const string Host = "wall.hidamari";
    // the page's own colour behind the canvas before the first frame (wall.html, :root --wall)
    static readonly Color Beige = Color.FromArgb(0xd9, 0xd0, 0xc4);

    const string Bridge = """
        window.hidamari = {
          buf: null,
          async frame(id, v, png) {
            try {
              const r = await komorebi.render(v);
              // the canvas keeps its picture only within the task that drew it, so it is drawn again and read at once
              draw();
              const c = document.getElementById('c');
              const n = c.width * c.height * 4;
              let mode = 'png';
              if (this.buf && this.buf.byteLength >= n) {
                gl.readPixels(0, 0, c.width, c.height, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array(this.buf, 0, n));
                mode = 'shared';
              }
              const data = (png || mode === 'png') ? c.toDataURL('image/png') : null;
              chrome.webview.postMessage({ id, ok: true, sun: r.sun, width: c.width, height: c.height, mode, png: data,
                                           dpr: window.devicePixelRatio, inner: [innerWidth, innerHeight] });
            } catch (e) {
              chrome.webview.postMessage({ id, ok: false, error: String(e && e.message || e) });
            }
          },
          async thumbs(id, v) {
            try {
              const out = await komorebi.thumbs(v);
              chrome.webview.postMessage({ id, ok: true, thumbs: out });
            } catch (e) {
              chrome.webview.postMessage({ id, ok: false, error: String(e && e.message || e) });
            }
          },
          gl() {
            const g = document.createElement('canvas').getContext('webgl2');
            if (!g) return { ok: false };
            const x = g.getExtension('WEBGL_debug_renderer_info');
            return { ok: true, vendor: x ? g.getParameter(x.UNMASKED_VENDOR_WEBGL) : g.getParameter(g.VENDOR),
                     renderer: x ? g.getParameter(x.UNMASKED_RENDERER_WEBGL) : g.getParameter(g.RENDERER) };
          },
        };
        chrome.webview.addEventListener('sharedbufferreceived', e => {
          if (hidamari.buf) chrome.webview.releaseBuffer(hidamari.buf);
          hidamari.buf = e.getBuffer();
        });
        """;

    readonly EnginePolicy policy;
    readonly string webFolder;
    readonly HostWindow host = new();
    readonly Dictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    CoreWebView2Environment? env;
    CoreWebView2Controller? controller;
    CoreWebView2? core;
    CoreWebView2SharedBuffer? buffer;
    TaskCompletionSource? exited;
    bool software, suspended, broken, sharedFailed;
    int nextId;

    public string WebGl { get; private set; } = "";
    public string Version { get; private set; } = "";
    public bool Running => controller != null;
    public EnginePolicy Policy => policy;
    /// Someone else (the card) uses the same browser process: closing the engine does not wait for it to leave.
    public Func<bool>? SharedWith { get; set; }

    public Engine(EnginePolicy policy, string webFolder)
    {
        this.policy = policy;
        this.webFolder = webFolder;
    }

    /// The engine's processes (for the measurements); empty while it is closed.
    public uint BrowserPid { get; private set; }

    /// Makes the engine ready to draw. Returns how long starting took, 0 if it was ready.
    public async Task<long> Begin()
    {
        if (broken) await Shut("it failed before");
        var sw = Stopwatch.StartNew();
        if (controller == null)
        {
            await Start();
            return sw.ElapsedMilliseconds;
        }
        if (suspended)
        {
            core!.Resume();
            suspended = false;
        }
        if (policy != EnginePolicy.Keep) core!.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
        controller.IsVisible = true;
        return 0;
    }

    async Task Start()
    {
        Directory.CreateDirectory(webFolder);
        using (var page = typeof(Engine).Assembly.GetManifestResourceStream("wall.html")!)
        using (var file = File.Create(Path.Combine(webFolder, "wall.html")))
            page.CopyTo(file);

        await Open();
        var gl = await Script("hidamari.gl()");
        if (!gl.GetProperty("ok").GetBoolean() && !software)
        {
            // a machine without a usable graphics card (the GitHub machines): the web engine draws in software
            Log.Write($"web: no WebGL 2 ({gl}), starting the web engine again with software drawing");
            await Shut("software drawing");
            software = true;
            await Open();
            gl = await Script("hidamari.gl()");
        }
        var text = gl.GetProperty("ok").GetBoolean() ? $"{gl.GetProperty("vendor")} / {gl.GetProperty("renderer")}" : "none";
        if (text != WebGl) Log.Write($"web: WebView2 {Version}, WebGL 2 {text}{(software ? " (software)" : "")}");
        WebGl = text;
    }

    /// The engine's environment, for the card: the same browser process, so the card costs no second one.
    public async Task<CoreWebView2Environment> SharedEnvironment()
    {
        if (env == null) await MakeEnvironment();
        return env!;
    }

    async Task MakeEnvironment()
    {
        var options = new CoreWebView2EnvironmentOptions
        {
            // The engine is never on the screen. Without these Chromium would treat it as a hidden page and could put
            // off its work; frames are asked for only once in five minutes anyway.
            AdditionalBrowserArguments = "--disable-background-timer-throttling --disable-renderer-backgrounding " +
                "--disable-backgrounding-occluded-windows --disable-features=CalculateNativeWinOcclusion" +
                (software ? " --use-angle=swiftshader --enable-unsafe-swiftshader --ignore-gpu-blocklist" : ""),
        };
        var data = Path.Combine(Log.Folder, software ? "WebView2-software" : "WebView2");
        env = await CoreWebView2Environment.CreateAsync(null, data, options);
        Version = env.BrowserVersionString;
        exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var myExit = exited;
        env.BrowserProcessExited += (_, e) => myExit.TrySetResult();
    }

    async Task Open()
    {
        if (env == null) await MakeEnvironment();
        controller = await env!.CreateCoreWebView2ControllerAsync(host.Handle);
        controller.ShouldDetectMonitorScaleChanges = false;
        controller.DefaultBackgroundColor = Beige;
        controller.IsVisible = true;
        core = controller.CoreWebView2;
        BrowserPid = core.BrowserProcessId;
        var s = core.Settings;
        s.AreDefaultContextMenusEnabled = false;
        s.AreDevToolsEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsPinchZoomEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        core.SetVirtualHostNameToFolderMapping(Host, webFolder, CoreWebView2HostResourceAccessKind.Deny);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(Bridge);
        core.WebMessageReceived += OnMessage;
        core.ProcessFailed += (_, e) =>
        {
            Log.Write($"web: process failed: {e.ProcessFailedKind}, {e.Reason}, exit {e.ExitCode}; the engine starts anew for the next frame");
            broken = true;
            foreach (var t in pending.Values) t.TrySetException(new InvalidOperationException("the web engine failed"));
            pending.Clear();
        };
        var nav = new TaskCompletionSource<(bool ok, CoreWebView2WebErrorStatus status)>();
        void done(object? _, CoreWebView2NavigationCompletedEventArgs e) => nav.TrySetResult((e.IsSuccess, e.WebErrorStatus));
        core.NavigationCompleted += done;
        core.Navigate($"https://{Host}/wall.html");
        var (ok, status) = await nav.Task;
        core.NavigationCompleted -= done;
        if (!ok) throw new InvalidOperationException($"wall.html did not load: {status}");
    }

    /// Draws one frame for `screen` and copies it into `wall`. With `png`, also returns the page's own picture of
    /// the canvas (for the checks).
    public async Task<FrameResult> Draw(ScreenInfo screen, Dictionary<string, object?> state, WallWindow wall, bool png)
    {
        var sw = Stopwatch.StartNew();
        await Size(screen);
        long sizeMs = sw.ElapsedMilliseconds;

        // memory shared with the page for the pixels, as large as the screen
        long bytes = (long)screen.Bounds.Width * screen.Bounds.Height * 4;
        if (!sharedFailed && (buffer == null || (long)buffer.Size < bytes))
        {
            try
            {
                buffer?.Dispose();
                buffer = env!.CreateSharedBuffer((ulong)bytes);
                core!.PostSharedBufferToScript(buffer, CoreWebView2SharedBufferAccess.ReadWrite, null);
                for (int i = 0; i < 100; i++)
                {
                    var have = await Script("hidamari.buf ? hidamari.buf.byteLength : 0");
                    if (have.GetInt64() >= bytes) break;
                    await Task.Delay(20);
                }
            }
            catch (Exception e)
            {
                Log.Write($"web: no shared memory ({e.Message}), frames come as PNG pictures");
                sharedFailed = true;
                buffer?.Dispose();
                buffer = null;
            }
        }

        sw.Restart();
        var r = await Frame(state, png);
        long drawMs = sw.ElapsedMilliseconds;
        if (!r.GetProperty("ok").GetBoolean()) throw new InvalidOperationException($"the page could not draw: {r.GetProperty("error")}");
        int w = r.GetProperty("width").GetInt32(), h = r.GetProperty("height").GetInt32();
        var mode = r.GetProperty("mode").GetString()!;
        string? data = r.TryGetProperty("png", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        sw.Restart();
        var dest = wall.Pixels(w, h);
        if (mode == "shared") CopyFlipped(buffer!.Buffer, dest, w, h);
        else CopyPng(data!, dest, w, h);
        wall.Present();
        long copyMs = sw.ElapsedMilliseconds;
        return new FrameResult(w, h, r.GetProperty("sun").GetDouble(), mode, sizeMs, drawMs, copyMs, png ? data : null);
    }

    /// Gives the page the screen's size and scale, and waits until the page sees them.
    async Task Size(ScreenInfo screen)
    {
        var c = controller!;
        var bounds = new Rectangle(0, 0, screen.Bounds.Width, screen.Bounds.Height);
        if (Math.Abs(c.RasterizationScale - screen.Scale) > 0.001) c.RasterizationScale = screen.Scale;
        if (c.Bounds != bounds) c.Bounds = bounds;
        int wantW = (int)Math.Round(screen.Bounds.Width / screen.Scale), wantH = (int)Math.Round(screen.Bounds.Height / screen.Scale);
        JsonElement seen = default;
        for (int i = 0; i < 150; i++)
        {
            seen = await Script("[innerWidth, innerHeight, devicePixelRatio]");
            int iw = seen[0].GetInt32(), ih = seen[1].GetInt32();
            double dpr = seen[2].GetDouble();
            if (Math.Abs(iw - wantW) <= 1 && Math.Abs(ih - wantH) <= 1 && Math.Abs(dpr - screen.Scale) < 0.01) return;
            await Task.Delay(20);
        }
        Log.Write($"web: the page did not take the size of {screen} within 3 s (it sees {seen}), drawing anyway");
    }

    /// How long a frame may take before the page counts as stuck and is made anew. The checks on GitHub raise it:
    /// their machines have no graphics card, and a full-screen window-scene frame there takes 10–40 seconds.
    public static TimeSpan PageTimeout = TimeSpan.FromSeconds(30);

    async Task<JsonElement> Frame(Dictionary<string, object?> state, bool png)
    {
        int id = ++nextId;
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = tcs;
        await core!.ExecuteScriptAsync($"hidamari.frame({id}, {JsonSerializer.Serialize(state)}, {(png ? "true" : "false")})");
        if (await Task.WhenAny(tcs.Task, Task.Delay(PageTimeout)) != tcs.Task)
        {
            pending.Remove(id);
            broken = true;
            throw new TimeoutException($"the page did not answer within {PageTimeout.TotalSeconds:F0} seconds");
        }
        return await tcs.Task;
    }

    /// The small pictures of every scene for an own wall (wall.html, komorebi.thumbs): JPEG data URLs by name.
    /// The engine must be ready (Begin).
    public async Task<Dictionary<string, string>> Thumbs(Dictionary<string, object?> wall)
    {
        int id = ++nextId;
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = tcs;
        await core!.ExecuteScriptAsync($"hidamari.thumbs({id}, {JsonSerializer.Serialize(wall)})");
        var limit = PageTimeout * 2;   // seven full frames at 1920×1243 points; 60 s, on GitHub's machines 240 s
        if (await Task.WhenAny(tcs.Task, Task.Delay(limit)) != tcs.Task)
        {
            pending.Remove(id);
            broken = true;
            throw new TimeoutException($"the page did not draw the small pictures within {limit.TotalSeconds:F0} seconds");
        }
        var r = await tcs.Task;
        if (!r.GetProperty("ok").GetBoolean()) throw new InvalidOperationException($"the page could not draw the small pictures: {r.GetProperty("error")}");
        var out_ = new Dictionary<string, string>();
        foreach (var p in r.GetProperty("thumbs").EnumerateObject()) out_[p.Name] = p.Value.GetString() ?? "";
        return out_;
    }

    async Task<JsonElement> Script(string js)
    {
        var json = await core!.ExecuteScriptAsync(js);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var msg = JsonDocument.Parse(e.WebMessageAsJson).RootElement.Clone();
        if (msg.TryGetProperty("id", out var idEl) && pending.Remove(idEl.GetInt32(), out var tcs)) tcs.TrySetResult(msg);
    }

    /// WebGL's rows start at the bottom and its pixels are red, green, blue, alpha; the wall's picture starts at the
    /// top and wants blue, green, red.
    static unsafe void CopyFlipped(IntPtr src, IntPtr dest, int w, int h)
    {
        uint* s = (uint*)src, d = (uint*)dest;
        for (int y = 0; y < h; y++)
        {
            uint* from = s + (long)(h - 1 - y) * w, to = d + (long)y * w;
            for (int x = 0; x < w; x++)
            {
                uint p = from[x];
                to[x] = (p & 0x0000FF00u) | ((p & 0x000000FFu) << 16) | ((p >> 16) & 0x000000FFu) | 0xFF000000u;
            }
        }
    }

    static void CopyPng(string dataUrl, IntPtr dest, int w, int h)
    {
        var bytes = Convert.FromBase64String(dataUrl[(dataUrl.IndexOf(',') + 1)..]);
        using var ms = new MemoryStream(bytes);
        using var bmp = new Bitmap(ms);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        unsafe
        {
            for (int y = 0; y < h; y++)
                Buffer.MemoryCopy((byte*)data.Scan0 + (long)y * data.Stride, (byte*)dest + (long)y * w * 4, (long)w * 4, (long)w * 4);
        }
        bmp.UnlockBits(data);
    }

    /// After the frames: what the policy says.
    public async Task Rest()
    {
        if (controller == null) return;
        if (broken) { await Shut("it failed"); return; }
        controller.IsVisible = false;
        switch (policy)
        {
            case EnginePolicy.Low:
                core!.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                break;
            case EnginePolicy.Suspend:
                core!.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                try { suspended = await core.TrySuspendAsync(); }
                catch (Exception e) { Log.Write($"web: could not freeze the engine: {e.Message}"); }
                break;
            case EnginePolicy.Close:
                await Shut(null);
                break;
        }
    }

    /// Closes the engine and waits until its processes have left.
    async Task Shut(string? why)
    {
        if (why != null) Log.Write($"web: closing the engine ({why})");
        foreach (var t in pending.Values) t.TrySetCanceled();
        pending.Clear();
        buffer?.Dispose();
        buffer = null;
        try { controller?.Close(); } catch { }
        controller = null;
        core = null;
        env = null;
        suspended = false;
        broken = false;
        BrowserPid = 0;
        if (exited != null && SharedWith?.Invoke() != true) await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        exited = null;
    }

    public void Dispose()
    {
        buffer?.Dispose();
        try { controller?.Close(); } catch { }
        controller = null;
        host.DestroyHandle();
    }

    /// A window that is never shown, only there to hold the web engine.
    sealed class HostWindow : NativeWindow
    {
        public HostWindow() => CreateHandle(new CreateParams
        {
            Caption = "Hidamari engine",
            Style = unchecked((int)WS_POPUP),
            ExStyle = (int)(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE),
            X = -32000, Y = -32000, Width = 1, Height = 1,
        });
    }
}
