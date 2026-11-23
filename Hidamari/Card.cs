using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Vortice.DirectComposition;
using static Hidamari.Native;

namespace Hidamari;

/// What happens to the card's web engine while the card is closed (stage 2, measured on GitHub).
enum CardEngine
{
    /// stays, frozen (TrySuspendAsync) until the card opens again
    Sleep,
    /// is closed with the card and made again at the next open
    Close,
}

/// How the card's glass is made. Plain is the chosen one; the others stay for comparison pictures.
enum Glass
{
    /// Windows 11's own blur of what is behind (DWM acrylic) under the card's sky colours
    Acrylic,
    /// the sky colours as the design has them, no blur, with the design's shadow
    Plain,
    /// the site's lighter glass (its colours × 0.55), no blur
    Site,
}

/// The Hidamari card by the tray icon (design/panel/). A window of its own with a web engine shown through
/// DirectComposition, so everything around the card's rounded corners is see-through. The page is Card/card.html;
/// what it shows comes from the app (App.CardState), what is chosen on it goes back to the app (Chosen).
///
/// Opens by the icon, like Windows' own flyouts: above the tray, 12 points from the taskbar and the screen's edge, on
/// the screen with the taskbar. Closes on a click outside, Escape, a second click on the icon, or when another window
/// takes the keyboard. Never on the taskbar, never in Alt+Tab, always on top.
sealed class Card : IDisposable
{
    public const string Host = "card.hidamari", OwnHost = "own.hidamari";
    public static string Folder => Path.Combine(Log.Folder, "card");
    public static string ThumbsFolder => Path.Combine(Log.Folder, "Thumbs");
    const double Width = 300, Gap = 12;
    // room around the card for its shadow when Windows does not draw the glass (points)
    const double ShadowSide = 40, ShadowTop = 32, ShadowBottom = 76;

    readonly Func<Task<CoreWebView2Environment>> environment;
    readonly Func<Dictionary<string, object?>> state;
    readonly Func<RECT?> iconRect;
    readonly SynchronizationContext ui = SynchronizationContext.Current!;
    readonly CardWindow window;
    readonly MouseWatch mouse;
    readonly string colourScript;
    readonly bool check;

    CoreWebView2CompositionController? controller;
    CoreWebView2? core;
    IDCompositionTarget? target;
    IDCompositionVisual? visual;
    bool suspended, broken, opening, inkDark;
    TaskCompletionSource<bool>? painted;
    int paintId;
    // the last left press inside the card (the mouse watch's thread): for the double click
    uint lastPressTime;
    POINT lastPress = new() { X = -100000, Y = -100000 };
    RECT? iconAtOpen;
    DateTime swallowSelectUntil;
    Stopwatch? openClock;
    string openWhy = "";
    // the card's height in points: as shown now, and the window's room for it
    double cardNow = 420, cardRoom = 420;
    // read by the mouse watch's thread too: kept boxed, so it is never seen half written
    volatile object placeBox = default(Placement);
    Placement place { get => (Placement)placeBox; set => placeBox = value; }

    public CardEngine EnginePolicy { get; }
    public Glass Glass { get; private set; }
    public bool IsOpen { get; private set; }
    public bool SettingsOpen { get; private set; }
    public IntPtr Handle => window.Handle;
    /// Something was chosen on the card: (type, the message).
    public event Action<string, JsonElement>? Chosen;
    /// The card closed: why.
    public event Action<string>? Closed;
    /// The card opened.
    public event Action? Opened;
    /// Check mode: where every control is on the screen (in pixels).
    public event Action<JsonElement>? Layout;
    /// The time from the click to the card on the screen, of the last open.
    public long LastOpenMs { get; private set; }
    public bool EngineRunning => controller != null;
    public Rectangle WindowRect => place.Window;
    public Rectangle CardRect => place.Card;
    public double Scale => place.Scale;
    public uint BrowserPid => core?.BrowserProcessId ?? 0;

    public Card(Func<Task<CoreWebView2Environment>> environment, Func<Dictionary<string, object?>> state, Func<RECT?> iconRect, bool check)
    {
        this.environment = environment;
        this.state = state;
        this.iconRect = iconRect;
        this.check = check;
        EnginePolicy = Enum.TryParse<CardEngine>(Environment.GetEnvironmentVariable("HIDAMARI_CARD"), true, out var p) ? p : DefaultEngine;
        Glass = Enum.TryParse<Glass>(Environment.GetEnvironmentVariable("HIDAMARI_GLASS"), true, out var g) ? g : DefaultGlass;
        if (Glass == Glass.Acrylic && !AcrylicPossible) Glass = Glass.Plain;
        mouse = new MouseWatch(OnPress);
        window = new CardWindow(this);
        colourScript = ColourScript();
        WritePage();
        Log.Write($"card: engine while closed {EnginePolicy}, glass {Glass}{(AcrylicPossible ? "" : " (Windows draws no acrylic here)")}");
    }

    /// Chosen from the measurements on GitHub's machines.
    public static CardEngine DefaultEngine => CardEngine.Sleep;
    /// The chosen glass: the design's colours without blur. Windows 11's acrylic was tried (stage 2): on GitHub's
    /// machines it fills the window's square corners around the rounded card, not like the design. The site's glass
    /// (lighter colours, made for a blurred wall behind) looks washed out here: a page cannot blur the desktop under
    /// its window, only Windows' acrylic can. Both can still be tried (HIDAMARI_GLASS=acrylic or site).
    public static Glass DefaultGlass => Glass.Plain;
    /// Windows 11 22H2 and later can draw a system backdrop under a window of its own (DWMWA_SYSTEMBACKDROP_TYPE).
    public static bool AcrylicPossible => Environment.OSVersion.Version.Build >= 22621;

    // ---------- the page ----------

    /// The page and what it uses, written from the app's own copy into %LOCALAPPDATA%\Hidamari\card at every start.
    static void WritePage()
    {
        var asm = typeof(Card).Assembly;
        int n = 0;
        foreach (var name in asm.GetManifestResourceNames().Where(r => r.StartsWith("card/")))
        {
            var path = Path.Combine(Folder, name["card/".Length..].Replace('/', Path.DirectorySeparatorChar));
            using var s = asm.GetManifestResourceStream(name)!;
            if (File.Exists(path) && new FileInfo(path).Length == s.Length && !name.EndsWith(".js") && !name.EndsWith(".css") && !name.EndsWith(".html")) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var f = File.Create(path);
            s.CopyTo(f);
            n++;
        }
        Directory.CreateDirectory(ThumbsFolder);
        Log.Write($"card: page ready in {Folder} ({n} file(s) written)");
    }

    /// The sun and colour maths of wall.html, taken out of the page as it is (the prototype's numbers, never copied by
    /// hand): the card's sky colours, its wall dots and the own-wall picker use exactly what the wall uses.
    static string ColourScript()
    {
        using var s = typeof(Card).Assembly.GetManifestResourceStream("wall.html")!;
        var page = new StreamReader(s).ReadToEnd().Replace("\r\n", "\n");
        string Slice(string from, string to, bool keepTo = false)
        {
            int a = page.IndexOf(from, StringComparison.Ordinal);
            if (a < 0) throw new InvalidDataException($"wall.html has no \"{from}\"");
            int b = page.IndexOf(to, a + from.Length, StringComparison.Ordinal);
            if (b < 0) throw new InvalidDataException($"wall.html has no \"{to}\" after \"{from}\"");
            return page[a..(keepTo ? b + to.Length : b)];
        }
        var sun = Slice("// ---------- Sun & moon position", "// ---------- Scene settings");
        var walls = Slice("const WALLS = [", "\n];", keepTo: true);
        var colour = Slice("// ---------- Light: from the sun's position", "const state = {");
        return "window.HidamariColour = (function () {\n'use strict';\n" + sun + "\n" + walls + "\n" + colour +
               "\nconst state = { wall: 0 };\nreturn { WALLS, hexLin, linHex, lch2lin, chromaCap, padHue, hueToPad, customWall, customFrom, currentWall, custom, state, clamp, sky, RAD };\n})();\n";
    }

    async Task Start()
    {
        var env = await environment();
        controller = await env.CreateCoreWebView2CompositionControllerAsync(window.Handle);
        controller.ShouldDetectMonitorScaleChanges = false;
        controller.DefaultBackgroundColor = Color.FromArgb(0, 255, 255, 255);
        controller.CursorChanged += (_, _) => window.SetCursorFromWeb();
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
        core.SetVirtualHostNameToFolderMapping(Host, Folder, CoreWebView2HostResourceAccessKind.Deny);
        core.SetVirtualHostNameToFolderMapping(OwnHost, ThumbsFolder, CoreWebView2HostResourceAccessKind.Allow);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(
            "window.HidamariStrings = " + JsonSerializer.Serialize(Strings.ForCard()) + ";\n" + colourScript);
        core.WebMessageReceived += OnMessage;
        core.ProcessFailed += (_, e) =>
        {
            Log.Write($"card: web engine failed: {e.ProcessFailedKind}, {e.Reason}; made anew at the next open");
            broken = true;
            painted?.TrySetResult(false);
            if (IsOpen) ui.Post(_ => Close("the web engine failed"), null);
        };

        // the page's pictures go straight into the card's window (DirectComposition)
        var device = Compositor.Device;
        device.CreateTargetForHwnd(window.Handle, true, out target).CheckError();
        device.CreateVisual(out visual).CheckError();
        target!.SetRoot(visual);
        controller.RootVisualTarget = Marshal.GetObjectForIUnknown(visual!.NativePointer);
        device.Commit().CheckError();

        var nav = new TaskCompletionSource<(bool ok, CoreWebView2WebErrorStatus status)>();
        void done(object? _, CoreWebView2NavigationCompletedEventArgs e) => nav.TrySetResult((e.IsSuccess, e.WebErrorStatus));
        core.NavigationCompleted += done;
        core.Navigate($"https://{Host}/card.html");
        var (ok, status) = await nav.Task;
        core.NavigationCompleted -= done;
        if (!ok) throw new InvalidOperationException($"card.html did not load: {status}");
    }

    void Drop()
    {
        try { if (controller != null) controller.RootVisualTarget = null; } catch { }
        try { controller?.Close(); } catch { }
        controller = null;
        core = null;
        visual?.Dispose(); visual = null;
        target?.Dispose(); target = null;
        try { Compositor.Device.Commit(); } catch { }
        suspended = false;
        broken = false;
    }

    /// Sends the app's state to the page (`reset`: the card opens anew, with the settings and the picker closed).
    public void Push(bool reset = false, int id = 0)
    {
        if (core == null || suspended) return;
        var s = state();
        s["glass"] = Glass.ToString().ToLowerInvariant();
        s["anchor"] = place.Anchor;
        s["insets"] = new Dictionary<string, double> { ["left"] = place.InsetLeft, ["top"] = place.InsetTop, ["right"] = place.InsetRight, ["bottom"] = place.InsetBottom };
        s["check"] = check;
        try { core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "state", state = s, reset, id })); }
        catch (Exception e) { Log.Write($"card: could not send the state ({e.Message})"); }
    }

    /// Any other message to the page (the search's lines).
    public void Send(object message)
    {
        if (core == null || suspended) return;
        try { core.PostWebMessageAsJson(JsonSerializer.Serialize(message)); }
        catch (Exception e) { Log.Write($"card: could not send a message ({e.Message})"); }
    }

    void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonElement m;
        try { m = JsonDocument.Parse(e.WebMessageAsJson).RootElement.Clone(); } catch { return; }
        var type = m.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
        switch (type)
        {
            case "ready": break;
            case "dblclickSeen":
                Log.Write($"card: double click {(m.GetProperty("onArc").GetBoolean() ? "on the arc" : "beside the arc")} " +
                          $"at {m.GetProperty("x").GetDouble():F0},{m.GetProperty("y").GetDouble():F0} (arc {m.GetProperty("arc").GetRawText()})");
                break;
            case "painted":
                if (m.TryGetProperty("id", out var id) && id.GetInt32() == paintId) painted?.TrySetResult(true);
                break;
            case "size":
                OnSize(m.GetProperty("now").GetDouble(), m.GetProperty("target").GetDouble());
                break;
            case "close":
                Close(m.TryGetProperty("why", out var w) ? w.GetString() ?? "the card" : "the card");
                break;
            case "ink":
                inkDark = m.GetProperty("dark").GetBoolean();
                if (Glass == Glass.Acrylic) DarkFrame(inkDark);
                break;
            case "settings":
                SettingsOpen = m.GetProperty("open").GetBoolean();
                Log.Write($"card: settings {(SettingsOpen ? "opened" : "closed")}");
                break;
            case "picker":
                Log.Write($"card: own-wall picker {(m.GetProperty("open").GetBoolean() ? "opened" : "closed")}");
                break;
            case "layout":
                Layout?.Invoke(m);
                break;
            default:
                Chosen?.Invoke(type, m);
                break;
        }
    }

    // ---------- opening and closing ----------

    /// The tray icon was clicked (or chosen with the keyboard).
    public void Toggle(string why)
    {
        if (DateTime.UtcNow < swallowSelectUntil)
        {
            // the press on the icon already closed the card; this is the same click letting go
            swallowSelectUntil = default;
            return;
        }
        if (IsOpen) Close("the icon again");
        else _ = Open(why);
    }

    /// The card cannot open: the WebView2 runtime is not on this computer (it is part of Windows 11; Windows 10 gets it
    /// with Edge or Windows Update, but not always). The wall goes on without it (Direct3D). The app says so (App.cs).
    public event Action<string>? NoRuntime;

    /// The WebView2 runtime is there. Looked at each time the card's engine is to start, so that a runtime installed
    /// meanwhile is found without a restart. For the checks, HIDAMARI_CHECK_NO_WEBVIEW2=1 pretends it is not.
    static bool RuntimeThere()
    {
        if (Environment.GetEnvironmentVariable("HIDAMARI_CHECK") != null && Environment.GetEnvironmentVariable("HIDAMARI_CHECK_NO_WEBVIEW2") == "1")
        {
            Log.Write("card: no WebView2 runtime (pretended for the check)");
            return false;
        }
        try
        {
            var v = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return !string.IsNullOrEmpty(v);
        }
        catch (Exception e)
        {
            Log.Write($"card: no WebView2 runtime ({e.GetType().Name}: {e.Message.Split('\n')[0].Trim()})");
            return false;
        }
    }

    public async Task Open(string why)
    {
        if (IsOpen)
        {
            SetForegroundWindow(window.Handle);
            return;
        }
        if (opening) return;
        if (controller == null && !RuntimeThere())
        {
            NoRuntime?.Invoke(why);
            return;
        }
        opening = true;
        openClock = Stopwatch.StartNew();
        openWhy = why;
        try
        {
            if (broken) Drop();
            bool started = false;
            if (controller == null) { await Start(); started = true; }
            else if (suspended) { core!.Resume(); suspended = false; core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal; }
            iconAtOpen = iconRect();
            place = Where(cardRoom);
            Apply(place, show: true);
            controller!.IsVisible = true;
            painted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int id = ++paintId;
            Push(reset: true, id);
            await Task.WhenAny(painted.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            if (!painted.Task.IsCompleted) Log.Write("card: the page did not say it was drawn within 3 s, showing it anyway");
            place = Where(cardRoom);
            Apply(place, show: true);
            Cloak(false);
            IsOpen = true;
            SetForegroundWindow(window.Handle);
            controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
            Hook(true);
            LastOpenMs = openClock.ElapsedMilliseconds;
            bool keys = GetForegroundWindow() == window.Handle;
            Log.Write($"card: opened ({why}) in {LastOpenMs} ms{(started ? ", its web engine started" : "")}, {place}" +
                      $"{(keys ? "" : "; Windows did not give it the keyboard")}");
            Opened?.Invoke();
        }
        catch (Exception e)
        {
            Log.Write($"card: could not open ({e.GetType().Name}: {e.Message})");
            broken = true;
            Hide();
        }
        finally { opening = false; }
    }

    public void Close(string why)
    {
        if (!IsOpen) return;
        IsOpen = false;
        SettingsOpen = false;
        Hook(false);
        Hide();
        Log.Write($"card: closed ({why})");
        Closed?.Invoke(why);
        _ = Rest();
    }

    void Hide()
    {
        Cloak(true);
        ShowWindow(window.Handle, SW_HIDE);
        if (controller != null) try { controller.IsVisible = false; } catch { }
    }

    /// After closing: what the measurements chose (CardEngine).
    async Task Rest()
    {
        if (controller == null) return;
        if (broken) { Drop(); return; }
        try
        {
            if (EnginePolicy == CardEngine.Close) { Drop(); Log.Write("card: its web engine closed"); return; }
            core!.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
            await Task.Delay(200);
            if (IsOpen || opening || core == null) return;
            suspended = await core.TrySuspendAsync();
            // the card may have been opened again while it was being frozen
            if (suspended && (IsOpen || opening) && core != null) { core.Resume(); suspended = false; }
        }
        catch (Exception e) { Log.Write($"card: could not put its web engine to rest ({e.Message})"); }
    }

    /// Another glass (for the checks' comparison pictures).
    public void SetGlass(Glass g)
    {
        if (g == Glass.Acrylic && !AcrylicPossible) g = Glass.Plain;
        Glass = g;
        int none = DWMSBT_NONE;
        DwmSetWindowAttribute(window.Handle, DWMWA_SYSTEMBACKDROP_TYPE, ref none, 4);
        var zero = new MARGINS();
        DwmExtendFrameIntoClientArea(window.Handle, ref zero);
        SetWindowRgn(window.Handle, IntPtr.Zero, true);
        Log.Write($"card: glass {g}");
    }

    // ---------- where it stands ----------

    readonly record struct Placement(Rectangle Window, Rectangle Card, double Scale, string Edge, bool Overflow, string Anchor,
                                     double InsetLeft, double InsetTop, double InsetRight, double InsetBottom, string Screen)
    {
        public override string ToString() =>
            $"card {Card.X},{Card.Y} {Card.Width}x{Card.Height} px at {Scale * 100:F0} % on {Screen}, taskbar {Edge}{(Overflow ? ", icon under ^" : "")}, " +
            $"window {Window.X},{Window.Y} {Window.Width}x{Window.Height}";
    }

    /// The card's place for a card `heightPt` points tall.
    Placement Where(double heightPt)
    {
        var bar = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        bool haveBar = SHAppBarMessage(ABM_GETTASKBARPOS, ref bar) != IntPtr.Zero;
        string edge = !haveBar ? "bottom" : bar.uEdge switch { 0 => "left", 1 => "top", 2 => "right", _ => "bottom" };
        var barRect = haveBar ? bar.rc : default;
        var icon = iconAtOpen;
        var probe = haveBar ? barRect : icon ?? default;
        var mon = MonitorFromRect(ref probe, MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        GetMonitorInfo(mon, ref info);
        var work = info.rcWork;
        double scale = GetDpiForMonitor(mon, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        int w = (int)Math.Round(Width * scale), h = (int)Math.Round(heightPt * scale), gap = (int)Math.Round(Gap * scale);
        // the icon is in the taskbar itself, or hidden under the "^" (then Windows gives no place or one off the taskbar)
        bool overflow = icon is not { } ir || ir.Width <= 0 || !haveBar ||
                        ir.Right <= barRect.Left || ir.Left >= barRect.Right || ir.Bottom <= barRect.Top || ir.Top >= barRect.Bottom ||
                        Tray.HiddenUnderArrow() == true;
        int x, y;
        string anchor;
        if (edge is "bottom" or "top")
        {
            int cx = overflow ? work.Right - gap - w / 2 : (icon!.Value.Left + icon.Value.Right) / 2;
            x = Math.Clamp(cx - w / 2, work.Left + gap, Math.Max(work.Left + gap, work.Right - gap - w));
            anchor = edge;
            y = edge == "bottom" ? work.Bottom - gap - h : work.Top + gap;
        }
        else
        {
            x = edge == "left" ? work.Left + gap : work.Right - gap - w;
            int cy = overflow ? work.Bottom : (icon!.Value.Top + icon.Value.Bottom) / 2;
            anchor = cy >= (work.Top + work.Bottom) / 2 ? "bottom" : "top";
            y = anchor == "bottom" ? work.Bottom - gap - h : work.Top + gap;
        }
        var cardRect = new Rectangle(x, y, w, h);
        Rectangle win = cardRect;
        if (Glass != Glass.Acrylic)
        {
            // room for the shadow, never over the taskbar or off the screen
            win = Rectangle.FromLTRB(
                Math.Max(work.Left, x - (int)Math.Round(ShadowSide * scale)),
                Math.Max(work.Top, y - (int)Math.Round(ShadowTop * scale)),
                Math.Min(work.Right, x + w + (int)Math.Round(ShadowSide * scale)),
                Math.Min(work.Bottom, y + h + (int)Math.Round(ShadowBottom * scale)));
        }
        return new Placement(win, cardRect, scale, edge, overflow, anchor,
            (cardRect.Left - win.Left) / scale, (cardRect.Top - win.Top) / scale, (win.Right - cardRect.Right) / scale, (win.Bottom - cardRect.Bottom) / scale,
            new string(info.szDevice.Where(char.IsLetterOrDigit).ToArray()));
    }

    void Apply(Placement p, bool show)
    {
        var r = p.Window;
        SetWindowPos(window.Handle, HWND_TOPMOST, r.X, r.Y, r.Width, r.Height, SWP_NOACTIVATE | (show ? SWP_SHOWWINDOW : 0));
        if (controller != null)
        {
            if (Math.Abs(controller.RasterizationScale - p.Scale) > 0.001) controller.RasterizationScale = p.Scale;
            var b = new Rectangle(0, 0, r.Width, r.Height);
            if (controller.Bounds != b) controller.Bounds = b;
        }
        Frame(p);
    }

    /// The window's own look: Windows' acrylic under the card, its rounded corners.
    void Frame(Placement p)
    {
        if (Glass != Glass.Acrylic) return;
        int type = DWMSBT_TRANSIENTWINDOW;
        DwmSetWindowAttribute(window.Handle, DWMWA_SYSTEMBACKDROP_TYPE, ref type, 4);
        var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(window.Handle, ref margins);
        int none = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(window.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref none, 4);
        Round(p);
    }

    /// The window is cut to the card's rounded shape where the card is now (its height changes while a row unfolds).
    void Round(Placement p)
    {
        if (Glass != Glass.Acrylic) return;
        int r = (int)Math.Round(30 * p.Scale);
        int h = (int)Math.Round(cardNow * p.Scale), H = p.Window.Height;
        int top = p.Anchor == "bottom" ? Math.Max(0, H - h) : 0, bottom = p.Anchor == "bottom" ? H : Math.Min(H, h);
        var rgn = CreateRoundRectRgn(0, top, p.Window.Width + 1, bottom + 1, 2 * r, 2 * r);
        SetWindowRgn(window.Handle, rgn, true);
    }

    void DarkFrame(bool dark)
    {
        int v = dark ? 1 : 0;
        DwmSetWindowAttribute(window.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, 4);
    }

    void Cloak(bool on)
    {
        int v = on ? 1 : 0;
        DwmSetWindowAttribute(window.Handle, DWMWA_CLOAK, ref v, 4);
    }

    /// The page says how tall the card is now and how tall it will be once its rows have unfolded: the window grows
    /// at once, and becomes smaller only once the card has.
    void OnSize(double now, double target)
    {
        cardNow = now;
        double room = target > cardRoom ? target : Math.Abs(now - target) < 0.5 ? target : cardRoom;
        bool moved = Math.Abs(room - cardRoom) >= 0.5;
        cardRoom = room;
        if (!IsOpen && !opening) return;
        if (moved)
        {
            var was = place;
            place = Where(cardRoom);
            Apply(place, show: IsOpen);
            if (Math.Abs(was.InsetTop - place.InsetTop) > 0.01 || Math.Abs(was.InsetBottom - place.InsetBottom) > 0.01 || was.Anchor != place.Anchor) Push();
        }
        else Round(place);
    }

    // ---------- clicks outside ----------

    void Hook(bool on) => mouse.Watch(on);

    /// A press anywhere (on the mouse watch's thread: quick, the rest goes to the card's thread). Outside the card it
    /// closes the card. Two left presses inside it, in Windows' double-click time and distance, are a double click:
    /// counted here from the presses' own times, since the card window may get them late while its page is busy.
    void OnPress(int msg, POINT pt, uint time)
    {
        if (!IsOpen) return;
        var p = place;
        var card = p.Card;
        bool inside = pt.X >= card.Left && pt.X < card.Right && pt.Y >= card.Top && pt.Y < card.Bottom;
        if (!inside)
        {
            bool onIcon = iconAtOpen is { } r && pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom;
            if (onIcon && msg == WM_LBUTTONDOWN) swallowSelectUntil = DateTime.UtcNow.AddSeconds(1.5);
            ui.Post(_ => Close(onIcon ? "the icon again" : "click outside"), null);
            return;
        }
        if (msg != WM_LBUTTONDOWN) return;
        uint apart = time - lastPressTime;
        int dx = Math.Abs(pt.X - lastPress.X), dy = Math.Abs(pt.Y - lastPress.Y);
        bool doubled = Doubled(apart, dx, dy, "the mouse watch");
        lastPressTime = time;
        lastPress = doubled ? new POINT { X = -100000, Y = -100000 } : pt;
        if (doubled) ui.Post(_ => DoubleClick(pt.X - place.Window.Left, pt.Y - place.Window.Top, "the mouse watch"), null);
    }

    /// Two presses this far apart (milliseconds, pixels) are a double click by Windows' rules. A pair that is close
    /// but not counted goes to the journal, with Windows' own limits, so a slow machine's misses can be seen.
    static bool Doubled(uint apart, int dx, int dy, string by)
    {
        uint limit = GetDoubleClickTime();
        int cx = GetSystemMetrics(SM_CXDOUBLECLK) / 2, cy = GetSystemMetrics(SM_CYDOUBLECLK) / 2;
        bool doubled = apart <= limit && dx <= cx && dy <= cy;
        if (!doubled && apart <= 2000 && dx <= 20 && dy <= 20)
            Log.Write($"card: two presses {apart} ms and {dx},{dy} px apart, not a double click (Windows: {limit} ms, {cx},{cy} px; seen by {by})");
        return doubled;
    }

    DateTime lastDouble;

    /// A double click at x,y (pixels in the window, whose top left is the page's): the page is told and checks
    /// whether it was on the arc. Both the mouse watch and the window may see the same one; it is told once.
    void DoubleClick(int x, int y, string by)
    {
        if (DateTime.UtcNow - lastDouble < TimeSpan.FromSeconds(1)) return;
        lastDouble = DateTime.UtcNow;
        double k = place.Scale > 0 ? place.Scale : 1;
        Log.Write($"card: Windows counts a double click at {x},{y} px (seen by {by})");
        try { core?.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "dblclick", x = x / k, y = y / k })); } catch { }
    }

    public void OnScreensChanged()
    {
        if (!IsOpen) return;
        place = Where(cardRoom);
        Apply(place, show: true);
        Push();
    }

    public void Dispose()
    {
        Hook(false);
        mouse.Dispose();
        Drop();
        window.DestroyHandle();
    }

    // ---------- the window ----------

    /// The card's own window: no taskbar button, not in Alt+Tab (a tool window), on top, see-through around the card.
    /// Mouse and keyboard reach the page through the web engine's composition controller.
    sealed class CardWindow : NativeWindow
    {
        readonly Card card;
        bool tracking, captured;
        int lastDownTime;
        Point lastDown = new(-10000, -10000);

        public CardWindow(Card card)
        {
            this.card = card;
            CreateHandle(new CreateParams
            {
                Caption = Strings.Name,
                ClassStyle = CS_DBLCLKS,
                Style = unchecked((int)WS_POPUP),
                ExStyle = (int)(WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOREDIRECTIONBITMAP),
                X = -32000, Y = -32000, Width = 300, Height = 400,
            });
            int v = 1;
            DwmSetWindowAttribute(Handle, DWMWA_CLOAK, ref v, 4);
            // no fade of its own when shown or hidden (stage 2: no animation of our own)
            DwmSetWindowAttribute(Handle, DWMWA_TRANSITIONS_FORCEDISABLED, ref v, 4);
        }

        public void SetCursorFromWeb()
        {
            var c = card.controller;
            if (c == null) return;
            GetCursorPos(out var p);
            if (WindowFromPoint(p) == Handle) SetCursor(c.Cursor);
        }

        protected override void WndProc(ref Message m)
        {
            try
            {
                var c = card.controller;
                switch (m.Msg)
                {
                    case WM_MOUSEACTIVATE:
                        m.Result = new IntPtr(MA_ACTIVATE);
                        return;
                    case WM_ACTIVATE:
                        if (((int)(long)m.WParam & 0xFFFF) == WA_INACTIVE && card.IsOpen)
                            card.ui.Post(_ => card.Close("another window took the keyboard"), null);
                        else if (((int)(long)m.WParam & 0xFFFF) != WA_INACTIVE && c != null)
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
                    case WM_MOUSEMOVE:
                    case WM_LBUTTONDOWN: case WM_LBUTTONUP: case WM_LBUTTONDBLCLK:
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
                        card.OnScreensChanged();
                        m.Result = IntPtr.Zero;
                        return;
                }
            }
            catch (Exception e) { Log.Write($"card: {e.GetType().Name} in the window: {e.Message}"); }
            base.WndProc(ref m);
        }

        void Mouse(CoreWebView2CompositionController c, ref Message m)
        {
            int msg = m.Msg;
            long w = (long)m.WParam, l = (long)m.LParam;
            var keys = (CoreWebView2MouseEventVirtualKeys)(w & 0xFFFF);
            uint data = 0;
            var pt = new Point((short)(l & 0xFFFF), (short)((l >> 16) & 0xFFFF));
            if (msg is WM_MOUSEWHEEL or WM_MOUSEHWHEEL)
            {
                // the wheel comes with the place on the screen
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
            // the double click is also counted here, from the time each press happened (in case the mouse watch
            // misses it); before the press goes on, since handing it to a busy page can take long
            if (msg is WM_LBUTTONDOWN or WM_LBUTTONDBLCLK)
            {
                int t = GetMessageTime();
                bool doubled = msg == WM_LBUTTONDBLCLK
                               || Doubled(unchecked((uint)(t - lastDownTime)), Math.Abs(pt.X - lastDown.X), Math.Abs(pt.Y - lastDown.Y), "the card window");
                lastDownTime = t;
                lastDown = doubled ? new Point(-10000, -10000) : pt;
                if (doubled) card.DoubleClick(pt.X, pt.Y, "the card window");
            }
            c.SendMouseInput((CoreWebView2MouseEventKind)msg, keys, data, pt);
        }
    }
}
