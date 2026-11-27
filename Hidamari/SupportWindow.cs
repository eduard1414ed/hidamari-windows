using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Vortice.DirectComposition;
using static Hidamari.Native;

namespace Hidamari;

// The support window (the Mac's update 1.4, SupportWindow.swift, design/support/; Ed on 27 November 2026: on Windows
// too, and see whether the Microsoft Store's certification takes it). Once after every update the app shows a small
// card in the middle of the main screen: the app's icon and version, a few words, a button to Ed's donation page, Done.
//
// Built like the card: a borderless window of its own with a web engine shown through DirectComposition, in the same
// browser as the card and the wall; the page is Card/support.html with the card's look. It is made when shown and
// thrown away when closed: a closed window costs nothing.

/// When the window shows (SupportPrompt on the Mac). The last version it was shown for is kept in settings.json
/// (supportShownVersion).
static class SupportPrompt
{
    /// HIDAMARI_SHOW_SUPPORT=1: shown at this start whatever the version, and nothing remembered (for checks by hand).
    public static bool Asked => Environment.GetEnvironmentVariable("HIDAMARI_SHOW_SUPPORT") == "1";

    /// Whether to show the window at this start, and remember this version when it does (or quietly, at the very first
    /// installation). `firstLaunch`: there was no settings.json yet. Versions before 1.1 did not remember their version,
    /// but always left settings behind. In the GitHub checks (`check`) only HIDAMARI_SHOW_SUPPORT or
    /// HIDAMARI_CHECK_SUPPORT=1 brings it, so it never covers the other checks' clicks and pictures.
    public static bool ShowNow(Settings settings, bool firstLaunch, bool check)
    {
        if (Asked)
        {
            Log.Write("support window: asked for (HIDAMARI_SHOW_SUPPORT), not remembered");
            return true;
        }
        if (check && Environment.GetEnvironmentVariable("HIDAMARI_CHECK_SUPPORT") != "1")
        {
            Log.Write("support window: not in check mode (HIDAMARI_CHECK_SUPPORT=1 checks its rule)");
            return false;
        }
        var version = Program.Version;
        var shown = settings.SupportShownVersion;
        if (shown == version) return false;
        settings.SupportShownVersion = version;
        if (shown == null && firstLaunch)
        {
            // kept with the first save, so a first launch quit before anything was chosen stays a first launch
            Log.Write($"support window: first installation of {version}, not shown");
            return false;
        }
        settings.Save();
        Log.Write($"support window: {shown ?? "an earlier version"} → {version}, shown");
        return true;
    }
}

/// Shows and closes the window.
sealed class SupportWindow : IDisposable
{
    // the card's width (SupportView.width) and the room around it for its shadow, as the card by the tray has (points)
    const double Width = 420, ShadowSide = 40, ShadowTop = 32, ShadowBottom = 76;
    const int WM_NCLBUTTONDOWN = 0x00A1, HTCAPTION = 2, VK_LBUTTON = 0x01;

    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);

    readonly Func<Task<CoreWebView2Environment>> environment;
    readonly Func<Dictionary<string, object?>> state;
    readonly string script;
    readonly bool check;
    readonly SynchronizationContext ui = SynchronizationContext.Current!;

    Host? host;
    CoreWebView2CompositionController? controller;
    CoreWebView2? core;
    IDCompositionTarget? target;
    IDCompositionVisual? visual;
    TaskCompletionSource<bool>? painted;
    int paintId, generation;
    bool opening, moved;
    double heightPt = 260, scale = 1;
    Rectangle windowRect;
    // where the card and its buttons are on the page (points, from the window's top left): a press on the card
    // that is not on a button drags the window
    RectangleF cardPt;
    readonly List<RectangleF> buttonsPt = new();

    public bool IsOpen { get; private set; }
    public bool EngineRunning => controller != null;
    /// Check mode: where the card and its buttons are on the screen.
    public event Action<JsonElement>? Layout;
    public Rectangle WindowRect => windowRect;
    public double Scale => scale;

    public SupportWindow(Func<Task<CoreWebView2Environment>> environment, Func<Dictionary<string, object?>> state, bool check)
    {
        this.environment = environment;
        this.state = state;
        this.check = check;
        script = "window.HidamariStrings = " + JsonSerializer.Serialize(Strings.ForCard()) + ";\n" + Card.ColourScript();
    }

    public async Task Show(string why)
    {
        if (IsOpen)
        {
            if (host != null) SetForegroundWindow(host.Handle);
            return;
        }
        if (opening) return;
        if (!Card.RuntimeThere())
        {
            Log.Write("support window: not shown, no WebView2 runtime");
            return;
        }
        opening = true;
        int gen = ++generation;
        var clock = Stopwatch.StartNew();
        try
        {
            moved = false;
            host = new Host(this);
            await Start();
            if (gen != generation) { Close("closed while it opened"); return; }
            windowRect = Where();
            Apply(show: true);
            controller!.IsVisible = true;
            painted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int id = ++paintId;
            Push(reset: true, id);
            await Task.WhenAny(painted.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            if (gen != generation || host == null) { Close("closed while it opened"); return; }
            if (!painted.Task.IsCompleted) Log.Write("support window: the page did not say it was drawn within 3 s, showing it anyway");
            windowRect = Where();
            Apply(show: true);
            Cloak(host.Handle, false);
            IsOpen = true;
            SetForegroundWindow(host.Handle);
            controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
            Send(new { type = "shown" });
            bool keys = GetForegroundWindow() == host.Handle;
            Log.Write($"support window shown ({why}) in {clock.ElapsedMilliseconds} ms, {windowRect.Width}x{windowRect.Height} px at {windowRect.X},{windowRect.Y}, " +
                      $"{scale * 100:F0} %{(keys ? "" : "; Windows did not give it the keyboard")}");
        }
        catch (Exception e)
        {
            Log.Write($"support window: could not show ({e.GetType().Name}: {e.Message})");
            Close("it could not be shown");
        }
        finally { opening = false; }
    }

    async Task Start()
    {
        var env = await environment();
        if (host == null) return;
        controller = await env.CreateCoreWebView2CompositionControllerAsync(host.Handle);
        controller.ShouldDetectMonitorScaleChanges = false;
        controller.DefaultBackgroundColor = Color.FromArgb(0, 255, 255, 255);
        controller.CursorChanged += (_, _) => host?.SetCursorFromWeb();
        core = controller.CoreWebView2;
        var s = core.Settings;
        s.AreDefaultContextMenusEnabled = false;
        s.AreDevToolsEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsPinchZoomEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        core.SetVirtualHostNameToFolderMapping(Card.Host, Card.Folder, CoreWebView2HostResourceAccessKind.Deny);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
        core.WebMessageReceived += OnMessage;
        core.ProcessFailed += (_, e) =>
        {
            Log.Write($"support window: web engine failed: {e.ProcessFailedKind}, {e.Reason}");
            painted?.TrySetResult(false);
            ui.Post(_ => Close("the web engine failed"), null);
        };

        // the page's pictures go straight into the window (DirectComposition), see-through around the rounded card
        var device = Compositor.Device;
        device.CreateTargetForHwnd(host!.Handle, true, out target).CheckError();
        device.CreateVisual(out visual).CheckError();
        target!.SetRoot(visual);
        controller.RootVisualTarget = Marshal.GetObjectForIUnknown(visual!.NativePointer);
        device.Commit().CheckError();

        var nav = new TaskCompletionSource<(bool ok, CoreWebView2WebErrorStatus status)>();
        void done(object? _, CoreWebView2NavigationCompletedEventArgs e) => nav.TrySetResult((e.IsSuccess, e.WebErrorStatus));
        core.NavigationCompleted += done;
        core.Navigate($"https://{Card.Host}/support.html");
        var (ok, status) = await nav.Task;
        core.NavigationCompleted -= done;
        if (!ok) throw new InvalidOperationException($"support.html did not load: {status}");
    }

    /// What the page needs: the moment, the place, the version (App.SupportState), and the room around the card.
    void Push(bool reset, int id)
    {
        if (core == null) return;
        var s = state();
        s["insets"] = new Dictionary<string, double> { ["left"] = ShadowSide, ["top"] = ShadowTop, ["right"] = ShadowSide, ["bottom"] = ShadowBottom };
        s["glass"] = "plain";
        s["check"] = check;
        try { core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "state", state = s, reset, id })); }
        catch (Exception e) { Log.Write($"support window: could not send the state ({e.Message})"); }
    }

    void Send(object message)
    {
        if (core == null) return;
        try { core.PostWebMessageAsJson(JsonSerializer.Serialize(message)); }
        catch (Exception e) { Log.Write($"support window: could not send a message ({e.Message})"); }
    }

    void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonElement m;
        try { m = JsonDocument.Parse(e.WebMessageAsJson).RootElement.Clone(); } catch { return; }
        var type = m.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
        switch (type)
        {
            case "ready": break;
            case "painted":
                if (m.TryGetProperty("id", out var id) && id.GetInt32() == paintId) painted?.TrySetResult(true);
                break;
            case "size":
                OnSize(m);
                break;
            case "support":
                Log.Write($"support window: {Links.Donate} opened");
                try { Process.Start(new ProcessStartInfo(Links.Donate) { UseShellExecute = true }); }
                catch (Exception ex) { Log.Write($"support window: could not open {Links.Donate} ({ex.Message})"); }
                ui.Post(_ => Close("support"), null);
                break;
            case "done":
            {
                var why = m.TryGetProperty("why", out var w) ? w.GetString() ?? "Done" : "Done";
                ui.Post(_ => Close(why), null);
                break;
            }
            case "layout":
                Layout?.Invoke(m);
                break;
        }
    }

    void OnSize(JsonElement m)
    {
        RectangleF R(JsonElement a) => new((float)a[0].GetDouble(), (float)a[1].GetDouble(), (float)a[2].GetDouble(), (float)a[3].GetDouble());
        try
        {
            if (m.TryGetProperty("card", out var c)) cardPt = R(c);
            buttonsPt.Clear();
            if (m.TryGetProperty("buttons", out var bs)) foreach (var b in bs.EnumerateArray()) buttonsPt.Add(R(b));
            double h = m.GetProperty("height").GetDouble();
            if (Math.Abs(h - heightPt) < 0.5 || h <= 0) return;
            heightPt = h;
            if (host == null) return;
            windowRect = Where();
            Apply(show: true);
        }
        catch (Exception e) { Log.Write($"support window: size not understood ({e.Message})"); }
    }

    /// In the middle of the main screen (the one with the taskbar's clock, Windows' primary); once the person has
    /// dragged it, where they left it.
    Rectangle Where()
    {
        IntPtr mon;
        if (moved && host != null && GetWindowRect(host.Handle, out var now))
            mon = MonitorFromRect(ref now, MONITOR_DEFAULTTONEAREST);
        else
            mon = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        GetMonitorInfo(mon, ref info);
        var work = info.rcWork;
        scale = GetDpiForMonitor(mon, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        int w = (int)Math.Round(Width * scale), h = (int)Math.Round(heightPt * scale);
        int side = (int)Math.Round(ShadowSide * scale), top = (int)Math.Round(ShadowTop * scale), bottom = (int)Math.Round(ShadowBottom * scale);
        if (moved && host != null && GetWindowRect(host.Handle, out var r))
            return new Rectangle(r.Left, r.Top, w + 2 * side, h + top + bottom);
        int x = work.Left + (work.Width - w) / 2, y = work.Top + (work.Height - h) / 2;
        return new Rectangle(x - side, y - top, w + 2 * side, h + top + bottom);
    }

    void Apply(bool show)
    {
        if (host == null) return;
        var r = windowRect;
        SetWindowPos(host.Handle, HWND_TOPMOST, r.X, r.Y, r.Width, r.Height, SWP_NOACTIVATE | (show ? SWP_SHOWWINDOW : 0));
        if (controller != null)
        {
            if (Math.Abs(controller.RasterizationScale - scale) > 0.001) controller.RasterizationScale = scale;
            var b = new Rectangle(0, 0, r.Width, r.Height);
            if (controller.Bounds != b) controller.Bounds = b;
        }
    }

    static void Cloak(IntPtr h, bool on)
    {
        int v = on ? 1 : 0;
        DwmSetWindowAttribute(h, DWMWA_CLOAK, ref v, 4);
    }

    /// Closes and throws the window away (Done, Escape, the button, the app quits).
    public void Close(string why)
    {
        if (host == null && controller == null) return;
        generation++;
        bool was = IsOpen;
        IsOpen = false;
        var h = host;
        var c = controller;
        var v = visual;
        var tg = target;
        host = null; controller = null; core = null; visual = null; target = null;
        painted?.TrySetResult(false);
        if (h != null)
        {
            Cloak(h.Handle, true);
            ShowWindow(h.Handle, SW_HIDE);
        }
        if (was || why != "it could not be shown") Log.Write($"support window closed: {why}");
        // the engine is closed after the page's own message has been handled
        ui.Post(_ =>
        {
            try { if (c != null) c.RootVisualTarget = null; } catch { }
            try { c?.Close(); } catch { }
            v?.Dispose();
            tg?.Dispose();
            try { Compositor.Device.Commit(); } catch { }
            try { h?.DestroyHandle(); } catch { }
        }, null);
    }

    public void Dispose() => Close("the app quits");

    /// A press on the card, not on its buttons: the window follows the mouse (Windows moves it as by a title bar).
    bool Drag(Point pt)
    {
        if (host == null || scale <= 0) return false;
        float x = (float)(pt.X / scale), y = (float)(pt.Y / scale);
        if (!cardPt.Contains(x, y)) return false;
        foreach (var b in buttonsPt) if (b.Contains(x, y)) return false;
        if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0) return false;
        moved = true;
        ReleaseCapture();
        SendMessage(host.Handle, (uint)WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
        return true;
    }

    /// Moved to a screen of another scale: the card keeps its size in points.
    void OnDpiChanged(int dpi)
    {
        if (host == null || dpi <= 0) return;
        scale = dpi / 96.0;
        windowRect = Where();
        Apply(show: IsOpen);
    }

    // ---------- the window ----------

    /// The window: no title bar, no taskbar button, not in Alt+Tab (a tool window), above ordinary windows,
    /// see-through around the card. Mouse and keyboard reach the page through the web engine's composition controller.
    sealed class Host : NativeWindow
    {
        readonly SupportWindow owner;
        bool tracking, captured;

        public Host(SupportWindow owner)
        {
            this.owner = owner;
            CreateHandle(new CreateParams
            {
                // what a screen reader says for the window; not "Hidamari", which the checks look for as the card
                Caption = Strings.SupportHeading,
                Style = unchecked((int)WS_POPUP),
                ExStyle = (int)(WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOREDIRECTIONBITMAP),
                X = -32000, Y = -32000, Width = 420, Height = 300,
            });
            int v = 1;
            DwmSetWindowAttribute(Handle, DWMWA_CLOAK, ref v, 4);
            // its own fade is the page's (0.18 s); none from Windows
            DwmSetWindowAttribute(Handle, DWMWA_TRANSITIONS_FORCEDISABLED, ref v, 4);
        }

        public void SetCursorFromWeb()
        {
            var c = owner.controller;
            if (c == null) return;
            GetCursorPos(out var p);
            if (WindowFromPoint(p) == Handle) SetCursor(c.Cursor);
        }

        protected override void WndProc(ref Message m)
        {
            try
            {
                var c = owner.controller;
                switch (m.Msg)
                {
                    case WM_MOUSEACTIVATE:
                        m.Result = new IntPtr(MA_ACTIVATE);
                        return;
                    case WM_ACTIVATE:
                        if (((int)(long)m.WParam & 0xFFFF) != WA_INACTIVE && c != null)
                            c.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
                        break;
                    case WM_SETCURSOR:
                        if (((int)(long)m.LParam & 0xFFFF) == HTCLIENT && c != null)
                        {
                            SetCursor(c.Cursor);
                            m.Result = new IntPtr(1);
                            return;
                        }
                        break;
                    case WM_LBUTTONDOWN:
                    {
                        long l = (long)m.LParam;
                        var pt = new Point((short)(l & 0xFFFF), (short)((l >> 16) & 0xFFFF));
                        if (owner.Drag(pt))
                        {
                            // the page never saw the press: nothing to let go of there
                            m.Result = IntPtr.Zero;
                            return;
                        }
                        if (c == null) break;
                        Mouse(c, ref m);
                        return;
                    }
                    case WM_MOUSEMOVE:
                    case WM_LBUTTONUP: case WM_LBUTTONDBLCLK:
                    case WM_RBUTTONDOWN: case WM_RBUTTONUP: case WM_RBUTTONDBLCLK:
                    case WM_MBUTTONDOWN: case WM_MBUTTONUP: case WM_MBUTTONDBLCLK:
                    case WM_XBUTTONDOWN: case WM_XBUTTONUP: case WM_XBUTTONDBLCLK:
                    case WM_MOUSEWHEEL: case WM_MOUSEHWHEEL:
                        if (c == null) break;
                        Mouse(c, ref m);
                        return;
                    case WM_MOUSELEAVE:
                        tracking = false;
                        c?.SendMouseInput(CoreWebView2MouseEventKind.Leave, CoreWebView2MouseEventVirtualKeys.None, 0, new Point());
                        return;
                    case WM_CAPTURECHANGED:
                        captured = false;
                        break;
                    case WM_DPICHANGED:
                        owner.OnDpiChanged((int)((long)m.WParam & 0xFFFF));
                        m.Result = IntPtr.Zero;
                        return;
                }
            }
            catch (Exception e) { Log.Write($"support window: {e.GetType().Name} in the window: {e.Message}"); }
            base.WndProc(ref m);
        }

        /// The mouse to the page, as the card's window does (Card.CardWindow).
        void Mouse(CoreWebView2CompositionController c, ref Message m)
        {
            int msg = m.Msg;
            long w = (long)m.WParam, l = (long)m.LParam;
            var keys = (CoreWebView2MouseEventVirtualKeys)(w & 0xFFFF);
            uint data = 0;
            var pt = new Point((short)(l & 0xFFFF), (short)((l >> 16) & 0xFFFF));
            if (msg is WM_MOUSEWHEEL or WM_MOUSEHWHEEL)
            {
                var sp = new POINT { X = pt.X, Y = pt.Y };
                ScreenToClient(Handle, ref sp);
                pt = new Point(sp.X, sp.Y);
                data = (uint)(short)((w >> 16) & 0xFFFF);
            }
            else if (msg is WM_XBUTTONDOWN or WM_XBUTTONUP or WM_XBUTTONDBLCLK) data = (uint)((w >> 16) & 0xFFFF);
            if (msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN && !captured)
            {
                SetCapture(Handle);
                captured = true;
            }
            else if (msg is WM_LBUTTONUP or WM_RBUTTONUP or WM_MBUTTONUP or WM_XBUTTONUP && captured && (w & 0x73) == 0)
            {
                ReleaseCapture();
                captured = false;
            }
            if (msg == WM_MOUSEMOVE && !tracking)
            {
                var t = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = TME_LEAVE, hwndTrack = Handle };
                tracking = TrackMouseEvent(ref t);
            }
            c.SendMouseInput((CoreWebView2MouseEventKind)msg, keys, data, pt);
        }
    }
}
