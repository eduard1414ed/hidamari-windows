using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace HidamariProbe;

/// One screen's wall: a borderless window with a WebView2 showing wall.html, put under the desktop icons.
/// The page draws one frame each time komorebi.render() is called and nothing in between.
sealed class WallWindow : Form
{
    public const string Host = "wall.hidamari";
    // the page's own colour behind the canvas before the first frame (wall.html, :root --wall)
    static readonly Color Beige = Color.FromArgb(0xd9, 0xd0, 0xc4);

    /// Added to the page before its own script runs. It only calls the page's komorebi.render() and reads the
    /// canvas for the checks; the drawing code itself is untouched.
    const string Bridge = """
        window.hidamariProbe = {
          async frame(id, v, capture) {
            try {
              const r = await komorebi.render(v);
              let png = null;
              // the canvas keeps its picture only within the task that drew it, so it is drawn again and read at once
              if (capture) { draw(); png = document.getElementById('c').toDataURL('image/png'); }
              chrome.webview.postMessage({ id, ok: true, sun: r.sun, width: r.width, height: r.height, png, dpr: window.devicePixelRatio });
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
        """;

    public Screen Screen { get; }
    readonly WebView2 web;
    readonly Dictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    int nextId;

    public WallWindow(Screen screen)
    {
        Screen = screen;
        Text = "Hidamari probe";
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Beige;
        // shown far off screen first, then moved under the icons, so it never flashes over other windows
        Bounds = new Rectangle(-32000, -32000, screen.Bounds.Width, screen.Bounds.Height);
        web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Beige };
        Controls.Add(web);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= (int)(Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            return cp;
        }
    }

    public async Task Open(Desktop desktop, CoreWebView2Environment env, string webFolder)
    {
        Show();
        if (!desktop.Attach(Handle, Screen.Bounds)) throw new InvalidOperationException("could not put the window under the icons");
        await web.EnsureCoreWebView2Async(env);
        var core = web.CoreWebView2;
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
        core.ProcessFailed += (_, e) => Log.Write($"web: process failed on {Screen.DeviceName}: {e.ProcessFailedKind}, {e.Reason}, exit {e.ExitCode}");
        var nav = new TaskCompletionSource<(bool ok, CoreWebView2WebErrorStatus status)>();
        void done(object? _, CoreWebView2NavigationCompletedEventArgs e) => nav.TrySetResult((e.IsSuccess, e.WebErrorStatus));
        core.NavigationCompleted += done;
        core.Navigate($"https://{Host}/wall.html");
        var (ok, status) = await nav.Task;
        core.NavigationCompleted -= done;
        if (!ok) throw new InvalidOperationException($"wall.html did not load: {status}");
    }

    /// WebGL of this page's web engine: { ok, vendor, renderer }.
    public async Task<JsonElement> Graphics()
    {
        var json = await web.CoreWebView2.ExecuteScriptAsync("hidamariProbe.gl()");
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    /// Draws one frame. With `capture`, the result carries the canvas as a PNG data URL.
    public async Task<JsonElement> Frame(Dictionary<string, object?> state, bool capture = false)
    {
        int id = ++nextId;
        var tcs = new TaskCompletionSource<JsonElement>();
        pending[id] = tcs;
        await web.CoreWebView2.ExecuteScriptAsync($"hidamariProbe.frame({id}, {JsonSerializer.Serialize(state)}, {(capture ? "true" : "false")})");
        if (await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(30))) != tcs.Task)
        {
            pending.Remove(id);
            throw new TimeoutException("the page did not answer within 30 seconds");
        }
        return tcs.Task.Result;
    }

    void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var msg = JsonDocument.Parse(e.WebMessageAsJson).RootElement.Clone();
        if (msg.TryGetProperty("id", out var idEl) && pending.Remove(idEl.GetInt32(), out var tcs)) tcs.TrySetResult(msg);
    }

    /// Process ids of this window's web engine (the browser process and its helpers are found by the checks).
    public uint BrowserProcessId => web.CoreWebView2?.BrowserProcessId ?? 0;
}
