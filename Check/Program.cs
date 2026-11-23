using System.Diagnostics;
using System.Text;
using System.Text.Json;

// The check of the Windows app on a GitHub Windows machine:
//   Check app <Hidamari.exe> <HidamariProbe.exe> <out folder> <quick|full>
//   Check package <Hidamari.msixbundle> <newer .msixbundle> <HidamariProbe.exe> <out folder> <quick|full>
// The second installs the package as the Store would and checks everything against the installed app, then the
// Start menu, an update over the running app and the removal (Package.cs).
// Everything the probe's check did (the wall under the icons, the mouse reaches the icons, the screen shows the page's
// frame, the desktop comes back), plus the tray icon and Quit in its menu, a second start, ordinary Windows life
// (a new wallpaper, light and dark, another virtual desktop, Win+D, a taskbar that hides itself, a window over the
// whole screen, a new time zone, Explorer restarting, a new screen size and scale), the end of a session and the end
// in Task Manager. "full" adds the four ways of keeping the web engine between frames, measured against the probe,
// and ten minutes of the app's own measurements in its journal.
// Writes report.json, report.md and the pictures; exit code 0 only if nothing failed.

static partial class Check
{
    static string outDir = "", app = "", probe = "";
    static bool full;
    static readonly StringBuilder log = new();
    static readonly List<(string group, string name, string status, string detail)> results = new();
    static readonly Dictionary<string, object?> facts = new();
    static readonly List<Dictionary<string, object?>> variants = new();
    static Bitmap? before;
    static List<(string name, Rectangle rect)> iconsBefore = new();
    static string wallpaperBefore = "", wallpaperPathBefore = "";
    static bool lightBefore;
    static int taskbarBefore;
    static string? zoneBefore;

    static int Main(string[] args)
    {
        Native.SetProcessDpiAwarenessContext(new IntPtr(-4));   // per monitor v2: pixels, not points
        if (args.Length == 5 && args[0] == "app")
        {
            app = Path.GetFullPath(args[1]);
            args = args[1..];
        }
        else if (args.Length == 6 && args[0] == "package")
        {
            packaged = true;
            bundle = Path.GetFullPath(args[1]);
            updateBundle = args[2] == "-" ? null : Path.GetFullPath(args[2]);
            args = args[2..];
        }
        else
        {
            Console.Error.WriteLine("usage: Check app <Hidamari.exe> <HidamariProbe.exe> <out folder> <quick|full>\n" +
                                    "       Check package <Hidamari.msixbundle> <newer .msixbundle or -> <HidamariProbe.exe> <out folder> <quick|full>");
            return 64;
        }
        probe = Path.GetFullPath(args[1]);
        outDir = Path.GetFullPath(args[2]);
        full = args[3] == "full";
        Pictures.OutDir = outDir;
        Directory.CreateDirectory(outDir);
        try
        {
            if (packaged)
            {
                Directory.CreateDirectory(Path.Combine(outDir, "package"));
                Part("package: install", Install);
                if (familyName == null) throw new InvalidOperationException("the package is not installed");
            }
            Part("setup", Setup);
            Part("main", MainRun);
            Part("card", CardRun);
            Part("card placement", CardPlaces);
            Part("card pictures", CardShots);
            Part("card memory", CardMemory);
            Part("place", PlaceRun);
            Part("weather", WeatherRun);
            Part("stage 4", Stage4Run);
            Part("no WebView2", NoWebView2Run);
            if (full) Part("memory", Variants);
            Part("session end", EndSession);
            Part("Task Manager", KillRun);
            if (packaged)
            {
                Part("package: Start menu", StartMenuRun);
                Part("package: update", UpdateRun);
                Part("package: removal", RemoveRun);
            }
        }
        catch (Exception e) { Result("check", "the check itself", false, e.ToString()); }
        finally { Part("putting the machine back", PutBack); }
        return Finish();
    }

    public static void Say(string s) { var line = $"{DateTime.UtcNow:HH:mm:ss.fff} {s}"; Console.WriteLine(line); lock (log) log.AppendLine(line); }

    static void Result(string group, string name, bool pass, string detail) => Result(group, name, pass ? "PASS" : "FAIL", detail);
    static void Result(string group, string name, string status, string detail)
    {
        results.Add((group, name, status, detail));
        Say($"{status} [{group}] {name}: {detail}");
    }

    static void Part(string name, Action a)
    {
        Say($"---- {name}");
        try { a(); }
        catch (Exception e) { Result(name, "the part ran to its end", false, e.ToString()); }
    }

    static Rectangle ScreenRect => new(0, 0, SystemInformation.PrimaryMonitorSize.Width, SystemInformation.PrimaryMonitorSize.Height);
    static Rectangle Work => SystemInformation.WorkingArea;

    // ================= setup =================

    static void Setup()
    {
        facts["windows"] = $"{Environment.OSVersion.Version}, {Desk.Reg(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName")} {Desk.Reg(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion")}";
        facts["screen"] = $"{ScreenRect.Width}x{ScreenRect.Height}, work area {Work}";
        facts["scale"] = Native.Scale()?.ToString();
        Say($"Windows {facts["windows"]}, screen {facts["screen"]}, scale {facts["scale"]}");

        Directory.CreateDirectory(Path.Combine(outDir, "card"));
        // the app's first launch on this machine (the GitHub machine is new; nothing is left from an earlier run)
        try { File.Delete(SettingsFile); } catch { }
        facts["taskbarEdgeBefore"] = Desk.TaskbarEdge();

        var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        Directory.CreateDirectory(Path.Combine(desktopDir, "Photos"));
        File.WriteAllText(Path.Combine(desktopDir, "Notes.txt"), "Hidamari check\r\n");
        File.WriteAllText(Path.Combine(desktopDir, "Shopping list.txt"), "milk\r\n");
        Directory.CreateDirectory(Path.Combine(desktopDir, "Projects"));

        if (Environment.OSVersion.Version.Build < 22000)
        {
            // Windows 10's tray hides new icons under the "^" arrow; as a person would, show them all
            Microsoft.Win32.Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer", "EnableAutoTray", 0, Microsoft.Win32.RegistryValueKind.DWord);
            RestartExplorer();
            Thread.Sleep(4000);
        }
        CloseFirstSignIn();
        FixedPhoto();
        Desk.CloseStrayDialogs();
        Desk.MinimizeAll();   // the GitHub machine's own console window covers most of the screen
        Thread.Sleep(3000);
        if (!Desk.IconsShown()) { Say("desktop icons were hidden, showing them"); Desk.ToggleIcons(); Thread.Sleep(2000); }
        iconsBefore = Desk.WaitForIcons(3, TimeSpan.FromSeconds(20));
        facts["icons"] = iconsBefore.Select(i => $"{i.name} {i.rect}").ToArray();
        wallpaperBefore = Desk.Wallpaper();
        wallpaperPathBefore = Desk.WallpaperPath();
        lightBefore = Desk.LightTaskbar();
        taskbarBefore = Desk.TaskbarState();
        zoneBefore = Tz("/g");
        facts["wallpaperBefore"] = wallpaperBefore;
        facts["lightTaskbarBefore"] = lightBefore;
        facts["timeZoneBefore"] = zoneBefore;
        before = Pictures.Shot("1-before.png");
        Result("setup", "desktop ready", iconsBefore.Count >= 3, $"{iconsBefore.Count} icons");
    }

    /// GitHub's Windows 11 machine (windows-11-arm) greets its first sign-in with Windows' privacy screen ("Choose
    /// privacy settings for your device") over the whole screen, in the band above all windows (as Windows' location
    /// question): a Windows.UI.Core.CoreWindow, hosted by Explorer's Shell_OOBEProxy. Hidamari rightly pauses under it, as
    /// under any window that covers the screen, so the check gets past it as a person would once: switched off by
    /// Microsoft's own policy, its process ended, and if it is still there, its blue button (Next, then Accept) clicked.
    static void CloseFirstSignIn()
    {
        var b = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        var center = new Point(b.X + b.Width / 2, b.Y + b.Height / 2);
        (string cls, string what, Process? p) OnTop()
        {
            var h = Native.RealWindowFromPoint(center);
            var cls = Native.ClassOf(h);
            Native.GetWindowThreadProcessId(h, out uint pid);
            Process? p = null; try { p = Process.GetProcessById((int)pid); } catch { }
            return (cls, $"{cls} \"{Native.TitleOf(h)}\" ({p?.ProcessName ?? "?"})", p);
        }
        // the Start menu is a CoreWindow too (StartMenuExperienceHost); Windows opens it once the first sign-in
        // screen is gone, and it is closed below, as a person would
        bool Covers(string cls, Process? p) => cls == "Shell_OOBEProxy" || (cls == "Windows.UI.Core.CoreWindow" && p?.ProcessName == "WWAHost");
        var proxy = Native.FindWindow("Shell_OOBEProxy", null);
        var first = OnTop();
        if (!Covers(first.cls, first.p) && !(proxy != IntPtr.Zero && Native.IsWindowVisible(proxy))) return;
        Say($"Windows' first sign-in screen covers the desktop: {first.what}; Shell_OOBEProxy {(proxy != IntPtr.Zero ? "there" : "none")}");
        try
        {
            Microsoft.Win32.Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\OOBE", "DisablePrivacyExperience", 1, Microsoft.Win32.RegistryValueKind.DWord);
            Microsoft.Win32.Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0, Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch (Exception e) { Say($"could not write the policy: {e.Message}"); }
        var steps = new List<string>();
        for (int i = 0; i < 6; i++)
        {
            var top = OnTop();
            if (!Covers(top.cls, top.p)) break;
            if (i == 0 && top.p != null && top.p.ProcessName != "explorer")
            {
                try { top.p.Kill(); top.p.WaitForExit(5000); steps.Add($"ended {top.what}"); } catch (Exception e) { steps.Add($"could not end {top.what}: {e.Message}"); }
            }
            else if (Desk.BlueButton() is { } blue)
            {
                Native.Click(new Point(blue.X + blue.Width / 2, blue.Y + blue.Height / 2));
                steps.Add($"clicked its blue button {blue} ({top.what})");
            }
            else steps.Add($"no blue button on {top.what}");
            Thread.Sleep(4000);
        }
        // what Windows opens after it, in the first minute of a first sign-in: the Start menu, a folder window
        Thread.Sleep(10000);
        if (OnTop().p?.ProcessName == "StartMenuExperienceHost") { Native.Keys(0x1B); steps.Add("the Start menu closed (Esc)"); Thread.Sleep(1500); }
        Desk.CloseFolderWindows();
        var last = OnTop();
        Pictures.Shot("0-first-sign-in-closed.png");
        Say($"first sign-in screen: {string.Join("; ", steps)}; now on top: {last.what}");
        Result("setup", "Windows' first sign-in privacy screen out of the way", !Covers(last.cls, last.p),
            $"{string.Join("; ", steps)}; on top now: {last.what}");
    }

    /// Windows 11 (not Server) shows Windows Spotlight on the desktop: a new photo from Microsoft now and then, which
    /// would change the "before" picture under the checks' feet. The person's own fixed photo instead (Windows' own
    /// img0.jpg), Spotlight off by Microsoft's policy for it, Explorer started again.
    static void FixedPhoto()
    {
        const string spotlight = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\DesktopSpotlight\Settings";
        bool on = Desk.Reg(spotlight, "EnabledState") is "1" || Desk.Wallpaper().Contains("Spotlight", StringComparison.OrdinalIgnoreCase);
        var photo = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Web\Wallpaper\Windows\img0.jpg");
        if (!on || !File.Exists(photo)) return;
        try
        {
            Microsoft.Win32.Registry.SetValue(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\CloudContent", "DisableSpotlightCollectionOnDesktop", 1, Microsoft.Win32.RegistryValueKind.DWord);
            Microsoft.Win32.Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers", "BackgroundType", 0, Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch (Exception e) { Say($"Spotlight: could not write the policy: {e.Message}"); }
        bool set = Desk.SetWallpaper(photo);
        RestartExplorer();
        Thread.Sleep(3000);
        Say($"Windows Spotlight was on the desktop: a fixed photo instead ({photo}, set {set}); now {Desk.Wallpaper()}");
    }

    static void RestartExplorer()
    {
        foreach (var p in Process.GetProcessesByName("explorer")) try { p.Kill(); p.WaitForExit(5000); } catch { }
        var sw = Stopwatch.StartNew();
        // Windows Server 2025 starts the shell again by itself; starting it by hand as well then opens a folder
        // window over the desktop. So first wait for Windows, and only then start it.
        while (Native.FindWindow("Shell_TrayWnd", null) == IntPtr.Zero && sw.Elapsed < TimeSpan.FromSeconds(8)) Thread.Sleep(250);
        bool byItself = Native.FindWindow("Shell_TrayWnd", null) != IntPtr.Zero;
        if (!byItself) Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        while (Native.FindWindow("Shell_TrayWnd", null) == IntPtr.Zero && sw.Elapsed < TimeSpan.FromSeconds(30)) Thread.Sleep(500);
        Say($"Explorer started again after {sw.Elapsed.TotalSeconds:F1} s ({(byItself ? "by itself" : "started by the check")})");
        Thread.Sleep(1500);
        Desk.CloseFolderWindows();
    }

    static string? Tz(string arg)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("tzutil", arg) { UseShellExecute = false, RedirectStandardOutput = true })!;
            var text = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(10000);
            return text;
        }
        catch { return null; }
    }

    // ================= the app as a person meets it =================

    static void MainRun()
    {
        using var run = Run.App(app, Path.Combine(outDir, "main"));
        try { MainSteps(run); }
        finally { run.SaveLog("hidamari.log"); }
    }

    static void MainSteps(Run run)
    {
        const string g = "start";
        var first = run.Next("frame 1 (", TimeSpan.FromSeconds(120));
        facts["firstFrameSeconds"] = Math.Round(run.Since.Elapsed.TotalSeconds, 1);
        Result(g, "first frame", first != null, first != null ? $"after {run.Since.Elapsed.TotalSeconds:F1} s: {first}" : run.Proc.HasExited ? $"app exited with {run.Proc.ExitCode}" : "no frame within 120 s");
        if (first == null) return;
        FirstLaunch(run);
        if (packaged) PackageIdentity(run);
        Thread.Sleep(3000);
        var screen = ScreenRect; var work = Work;

        var with = Pictures.Shot("2-with-app.png");
        var iconsWith = Desk.Icons();
        var moved = iconsBefore.Where(i => !iconsWith.Any(j => j.name == i.name && j.rect == i.rect)).Select(i => i.name).ToList();
        Result(g, "the app leaves the icons where they were", moved.Count == 0 && iconsWith.Count == iconsBefore.Count,
            moved.Count == 0 ? $"{iconsWith.Count} icons in place" : $"moved: {string.Join(", ", moved)}");
        facts["desktopTree"] = Desk.Tree();
        Say($"desktop windows: {facts["desktopTree"]}");

        SecondStart(run);

        // the mouse reaches the icons
        const string d = "desktop";
        var hits = iconsWith.Select(i => (i.name, cls: Native.ClassOf(Native.RealWindowFromPoint(Desk.Center(i.rect))))).ToList();
        var empty = Desk.EmptyPoint(iconsWith, work);
        var emptyHit = Native.ClassOf(Native.RealWindowFromPoint(empty));
        // Windows 11 25H2 (windows-11-arm) has a window of its own over the empty desktop, inside the icons' view
        // (Microsoft.UI.Content.DesktopChildSiteBridge): still the desktop's, not the wall
        bool emptyDesktop = emptyHit == "SysListView32" || Desk.InIconsView(Native.RealWindowFromPoint(empty));
        Result(d, "mouse goes to the icons, not to the wall", hits.All(h => h.cls == "SysListView32") && emptyDesktop,
            $"window under each icon: {string.Join(", ", hits.Select(h => h.cls).Distinct())}; under empty desktop: {emptyHit}");

        // the wall alone: icons hidden for a moment
        Desk.ToggleIcons(); Thread.Sleep(2500);
        var wallOnly = Pictures.Shot("3-wall-only.png");
        Desk.ToggleIcons(); Thread.Sleep(2500);

        using (var frame = run.Frame())
        {
            if (frame == null) Result(d, "the screen shows the frame the page drew", false, "the app saved no frame");
            else
            {
                facts["frameSize"] = $"{frame.Width}x{frame.Height}";
                bool size = frame.Width == screen.Width && frame.Height == screen.Height;
                var s = Pictures.VsFrame(wallOnly, frame, screen, work);
                facts["screenVsFrame"] = s.ToString();
                Result(d, "the frame fills the screen exactly", size, $"frame {frame.Width}x{frame.Height}, screen {screen.Width}x{screen.Height}");
                Result(d, "the screen shows the frame the page drew", s.Same, s.ToString());
            }
        }
        // the same moment for the comparison with the Mac (tools/windows-compare.swift <out>/mac-compare): later
        // frames overwrite main/canvas
        var compare = Path.Combine(outDir, "mac-compare");
        Directory.CreateDirectory(Path.Combine(compare, "canvas"));
        foreach (var f in Directory.GetFiles(Path.Combine(run.Folder, "canvas")))
            File.Copy(f, Path.Combine(compare, "canvas", Path.GetFileName(f)), true);
        File.Copy(Path.Combine(outDir, "3-wall-only.png"), Path.Combine(compare, "3-wall-only.png"), true);
        var replaced = Pictures.Diff(before!, wallOnly, work);
        Result(d, "the wall replaced the wallpaper", replaced.Mean >= 5, $"before vs wall alone: {replaced}");
        var rects = iconsWith.Select(i => i.rect).ToList();
        var inside = Pictures.Diff(with, wallOnly, work, only: rects);
        var around = Pictures.Diff(with, wallOnly, work, skip: Desk.Around(iconsWith));
        Result(d, "icons are drawn over the wall", inside.Mean >= 8 && around.Mean <= 1.0 && around.Within(8) >= 0.995, $"inside icons: {inside}; around them: {around}");

        var target = iconsWith[0];
        Native.Click(Desk.Center(target.rect));
        Thread.Sleep(1200);
        bool selected = Desk.IsSelected(target.name);
        Pictures.Shot("4-icon-clicked.png");
        Result(d, "a click on the wall selects the icon under it", selected, $"clicked \"{target.name}\", selected: {selected}");
        Native.Click(empty);
        Thread.Sleep(1200);

        Tray(run, "100");
        Life(run);
        ScreenSize(run);
        QuitFromTray(run);
    }

    /// The wall is on the screen as the page drew it, the icons left out. Saved as `file`.
    static Pictures.Stats WallStill(Run run, string file, Rectangle? area = null)
    {
        var shot = Pictures.Shot(file);
        using var frame = run.Frame();
        if (frame == null) return new Pictures.Stats(255, new double[256], 0);
        var icons = Desk.Icons();
        return Pictures.VsFrame(shot, frame, ScreenRect, area ?? Work, Desk.Around(icons));
    }

    // ================= the tray =================

    static Rectangle? FindTray()
    {
        var r = Desk.TrayIcon("Hidamari");
        if (r != null) return r;
        if (Desk.Promote(TrayExe)) { Say("tray: the app's icon promoted out of the hidden icons"); Thread.Sleep(3000); r = Desk.TrayIcon("Hidamari"); }
        else Say($"tray: the app's icon not found in Windows' list of tray icons ({TrayExe}): {Desk.TrayPaths()}");
        return r;
    }

    static void Tray(Run run, string scale)
    {
        const string g = "tray";
        var rect = FindTray();
        var appSays = run.Lines().LastOrDefault(l => l.Contains("tray: icon"));
        Result(g, $"icon on the taskbar at {scale} %", rect != null,
            rect != null ? $"button \"Hidamari\" at {rect}; app: {appSays}" : $"not found; taskbar buttons: {Desk.TrayNames()}; app: {appSays}");
        if (rect == null) return;

        foreach (var light in new[] { false, true })
        {
            run.Mark();
            Desk.SetLight(light);
            var said = run.Next($"tray: {(light ? "light" : "dark")} taskbar", TimeSpan.FromSeconds(6));
            Thread.Sleep(2500);
            var r = Desk.TrayIcon("Hidamari") ?? rect.Value;
            var shot = Pictures.Shot(null);
            var bar = Desk.Taskbar();
            var crop = new Rectangle(r.X - 4 * r.Width, bar.Y, 9 * r.Width, bar.Height);
            Pictures.Crop(shot, crop, $"tray-{(light ? "light" : "dark")}-{scale}.png");
            Pictures.Crop(shot, bar, $"taskbar-{(light ? "light" : "dark")}-{scale}.png");
            var inner = Rectangle.Inflate(r, -r.Width / 4, -r.Height / 4);
            var (dark, lightest) = Pictures.Range(shot, inner);
            var bg = Pictures.Brightness(shot, new Rectangle(r.X, bar.Y + 1, r.Width, 2));
            bool ok = light ? dark < bg - 80 : lightest > bg + 80;
            Result(g, $"{(light ? "black" : "white")} icon on the {(light ? "light" : "dark")} taskbar at {scale} %", ok,
                $"taskbar {bg:F0}, icon from {dark:F0} to {lightest:F0} (of 255); app: {said ?? (light == lightBefore ? "already this colour" : "said nothing")}");
        }
        Desk.SetLight(lightBefore);
        Thread.Sleep(2500);
        var still = WallStill(run, $"5-after-theme-{scale}.png");
        Result("life", $"light and dark switched: the wall stays ({scale} %)", still.Same, still.ToString());
    }

    static void QuitFromTray(Run run)
    {
        const string g = "exit";
        var rect = FindTray();
        bool quitClicked = false;
        if (rect != null)
        {
            Native.Click(Desk.Center(rect.Value) with { Y = rect.Value.Y + rect.Value.Height / 2 }, right: true);
            Thread.Sleep(1500);
            var item = Desk.MenuItem("Quit");
            Pictures.Shot("6-tray-menu.png");
            Result(g, "right click on the icon: a menu with Quit", item != null, item != null ? $"Quit at {item}" : "no menu item named Quit");
            if (item != null)
            {
                Native.Click(new Point(item.Value.X + item.Value.Width / 2, item.Value.Y + item.Value.Height / 2));
                quitClicked = true;
            }
        }
        if (!quitClicked) { Say("quitting through the check's own event instead"); Run.Signal("Quit"); }
        bool exited = run.Proc.WaitForExit(20000);
        var gone = run.WaitGone();
        Result(g, "Quit closes the app and its web engine", quitClicked && exited && gone != null,
            $"{(quitClicked ? "Quit clicked" : "Quit not reachable")}, app {(exited ? $"left with {run.Proc.ExitCode}" : "still running")}, web engine gone after {gone?.ToString() ?? "never"} s");
        Thread.Sleep(3000);
        var after = Pictures.Shot("7-after-quit.png");
        DesktopBack(g, "after Quit", after);
        Result(g, "after Quit the icon left the tray", Desk.TrayIcon("Hidamari") == null, "");
    }

    static void DesktopBack(string g, string when, Bitmap after)
    {
        var iconsAfter = Desk.Icons();
        var skip = Desk.Around(iconsBefore.Concat(iconsAfter), 4);
        var back = Pictures.Diff(before!, after, Work, skip: skip);
        Result(g, $"{when}: the wallpaper is back as before", back.Mean <= 1.0 && back.Within(8) >= 0.99, $"before vs after, around the icons: {back}");
        var wp = Desk.Wallpaper();
        Result(g, $"{when}: wallpaper setting unchanged", wp == wallpaperBefore, $"before \"{wallpaperBefore}\", after \"{wp}\"");
    }

    // ================= ordinary Windows life =================

    static void Life(Run run)
    {
        const string g = "life";

        // a new wallpaper while the app runs
        var bmpPath = Path.Combine(Path.GetTempPath(), "hidamari-check-wallpaper.bmp");
        using (var bmp = new Bitmap(ScreenRect.Width, ScreenRect.Height))
        {
            using (var gr = Graphics.FromImage(bmp)) gr.Clear(System.Drawing.Color.FromArgb(40, 90, 160));
            bmp.Save(bmpPath, System.Drawing.Imaging.ImageFormat.Bmp);
        }
        run.Mark();
        Desk.SetWallpaper(bmpPath);
        Thread.Sleep(5000);
        var s = WallStill(run, "8-new-wallpaper.png");
        Result(g, "a new wallpaper while the app runs: the wall stays on top", s.Same, $"{s}; app: {string.Join(" / ", run.Since_("desktop:").Concat(run.Since_("build")))}");
        Desk.SetWallpaper(wallpaperPathBefore);
        Thread.Sleep(4000);

        // another virtual desktop
        run.Mark();
        Native.Keys(Native.VK_CONTROL, Native.VK_LWIN, 0x44);   // Ctrl+Win+D: a new desktop, shown at once
        Thread.Sleep(3500);
        s = WallStill(run, "9-new-virtual-desktop.png");
        Result(g, "a new virtual desktop: the wall is there too", s.Same, s.ToString());
        Native.Keys(Native.VK_CONTROL, Native.VK_LWIN, Native.VK_F4);   // Ctrl+Win+F4: close it
        Thread.Sleep(2500);

        // Win+D
        Native.Keys(Native.VK_LWIN, 0x44);
        Thread.Sleep(2500);
        s = WallStill(run, "10-win-d.png");
        Result(g, "Win+D (show the desktop): the wall stays", s.Same, s.ToString());
        Native.Keys(Native.VK_LWIN, 0x44);
        Thread.Sleep(1500);

        // a taskbar that hides itself
        Desk.SetTaskbarState(1);
        Native.SetCursorPos(ScreenRect.Width / 2, ScreenRect.Height / 2);
        Thread.Sleep(3500);
        var full = new Rectangle(0, 0, ScreenRect.Width, ScreenRect.Height - 4);
        s = WallStill(run, "11-taskbar-hidden.png", full);
        Result(g, "the taskbar hides itself: the wall fills the screen under it", s.Same, s.ToString());
        Desk.SetTaskbarState(taskbarBefore);
        Thread.Sleep(2500);

        // a window over the whole screen (a game, a video)
        const string v = "visibility";
        run.Mark();
        string? pause, early;
        string missed = "";
        using (var cover = new Desk.Cover(ScreenRect))
        {
            Thread.Sleep(1500);
            Run.Signal("Draw");
            pause = run.Next("pause on DISPLAY1", TimeSpan.FromSeconds(10));
            early = run.Next(l => l.Contains("frame ") && l.Contains("(check)"), TimeSpan.FromSeconds(3));
            if (pause == null)
            {
                // once (windows-2025, 23 November) the window was over the screen and the app drew all the same: what
                // Windows said of the window is written down, and the app is asked once more
                missed = $"the first look after 1.5 s missed it ({cover.State()}); ";
                Thread.Sleep(2000);
                run.Mark();
                Run.Signal("Draw");
                pause = run.Next("pause on DISPLAY1", TimeSpan.FromSeconds(10));
                early = run.Next(l => l.Contains("frame ") && l.Contains("(check)"), TimeSpan.FromSeconds(3));
            }
            Pictures.Shot("12-covered.png");
            run.Mark();
        }
        Result(v, "a window over the whole screen: no frame is drawn", pause != null && early == null, $"{missed}journal: {pause ?? "no pause"}{(early != null ? $"; but: {early}" : "")}");
        var fresh = run.Next("(a wall can be seen again)", TimeSpan.FromSeconds(10));
        Thread.Sleep(2000);
        s = WallStill(run, "13-uncovered.png");
        Result(v, "the window closes: a fresh frame at once", fresh != null && s.Same, $"journal: {fresh ?? "no frame"}; screen vs frame: {s}");

        // a new time zone
        if (zoneBefore != null)
        {
            var other = zoneBefore == "Tokyo Standard Time" ? "UTC" : "Tokyo Standard Time";
            run.Mark();
            Tz($"/s \"{other}\"");
            var f = run.Next(l => l.Contains("frame ") && (l.Contains("time zone") || l.Contains("clock")), TimeSpan.FromSeconds(10));
            Tz($"/s \"{zoneBefore}\"");
            Result(g, "a new time zone: a new frame at once", f != null, f ?? "no frame within 10 s");
            run.Next(l => l.Contains("frame ") && (l.Contains("time zone") || l.Contains("clock")), TimeSpan.FromSeconds(10));
        }

        // Explorer starts again (it crashed, or an update restarted it)
        run.Mark();
        RestartExplorer();
        var heard = run.Next("Explorer started again", TimeSpan.FromSeconds(30));
        var again = run.Next("frame ", TimeSpan.FromSeconds(40));
        Thread.Sleep(4000);
        Desk.MinimizeAll();
        Thread.Sleep(1500);
        Desk.WaitForIcons(3, TimeSpan.FromSeconds(20));
        s = WallStill(run, "14-explorer-again.png");
        Result(g, "Explorer started again: the wall is back", heard != null && again != null && s.Same, $"journal: {heard ?? "nothing"} / {again ?? "no frame"}; screen vs frame: {s}");
        var tray = FindTray();
        Result(g, "Explorer started again: the tray icon is back", tray != null, tray?.ToString() ?? $"taskbar buttons: {Desk.TrayNames()}");
        facts["desktopTreeAfterExplorer"] = Desk.Tree();
    }

    // ================= the screen's size and scale =================

    static void ScreenSize(Run run)
    {
        const string g = "screens";
        var (w0, h0) = Desk.Resolution();
        var sizes = Desk.Resolutions();
        facts["resolutions"] = sizes.Select(s => $"{s.w}x{s.h}").ToArray();
        var pick = sizes.Where(s => s.w >= 1920 && s.h >= 1080).OrderBy(s => s.w * s.h).Cast<(int w, int h)?>().FirstOrDefault()
                   ?? sizes.Where(s => s.w != w0).OrderByDescending(s => s.w * s.h).Cast<(int w, int h)?>().FirstOrDefault();
        if (pick == null) { Result(g, "a new screen size", "SKIP", $"the machine offers only {w0}x{h0}"); return; }
        run.Mark();
        int r = Desk.SetResolution(pick.Value.w, pick.Value.h);
        if (r != 0) { Result(g, "a new screen size", "SKIP", $"Windows refused {pick.Value.w}x{pick.Value.h} ({r})"); return; }
        var built = run.Next("build (screens changed", TimeSpan.FromSeconds(20));
        var frameLine = run.Next("frame ", TimeSpan.FromSeconds(30));
        Thread.Sleep(3000);
        var s = WallStill(run, "15-new-size.png");
        using (var frame = run.Frame())
            Result(g, $"a new screen size ({w0}x{h0} → {pick.Value.w}x{pick.Value.h}): a new wall that fills it",
                built != null && frame != null && frame.Width == ScreenRect.Width && frame.Height == ScreenRect.Height && s.Same,
                $"journal: {built ?? "no new walls"}; frame {frame?.Width}x{frame?.Height}; screen vs frame: {s}");

        var scale = Native.Scale();
        facts["scaleAtNewSize"] = scale?.ToString();
        if (scale == null || scale.Value.max < 150) Result(g, "scale 150 %", "SKIP", scale == null ? "the scale cannot be read" : $"the screen allows at most {scale.Value.max} %");
        else
        {
            run.Mark();
            bool set = Native.SetScale(150);
            var rebuilt = run.Next("build (", TimeSpan.FromSeconds(30));
            var f = run.Next("frame ", TimeSpan.FromSeconds(30));
            Thread.Sleep(4000);
            var now = Native.Scale();
            var st = WallStill(run, "16-scale-150.png");
            var state = Run.Read(Path.Combine(run.Folder, "canvas", "state-DISPLAY1.json"));
            using (var frame = run.Frame())
                Result(g, "scale 150 %: the frame fills the screen pixel for pixel",
                    set && rebuilt != null && frame != null && frame.Width == ScreenRect.Width && frame.Height == ScreenRect.Height && st.Same && state.Contains("\"devicePixelRatio\": 1.5"),
                    $"set {set}, now {now}; journal: {rebuilt ?? "no new walls"} / {f ?? "no frame"}; frame {frame?.Width}x{frame?.Height} on {ScreenRect.Width}x{ScreenRect.Height}; screen vs frame: {st}");
            Tray(run, "150");
            run.Mark();
            Native.SetScale(scale.Value.now);
            run.Next("build (screens changed", TimeSpan.FromSeconds(30));
            run.Next("frame ", TimeSpan.FromSeconds(30));
        }
        run.Mark();
        Desk.ResetResolution();
        run.Next("frame ", TimeSpan.FromSeconds(30));
        Thread.Sleep(3000);
        var back = WallStill(run, "17-size-back.png");
        Result(g, "the old size again: the wall fits again", back.Same && ScreenRect.Width == w0, $"{ScreenRect.Width}x{ScreenRect.Height}; screen vs frame: {back}");
    }

    // ================= the web engine between frames =================

    static void Variants()
    {
        foreach (var v in new[] { "probe", "keep", "low", "suspend", "close" })
        {
            var dir = Path.Combine(outDir, "memory", v);
            // the four ways of keeping the web engine between frames: the web engine draws (stage 1's comparison)
            using var run = v == "probe" ? Run.Probe(probe, dir) : Run.App(app, dir, v, png: "first", env: new() { ["HIDAMARI_RENDERER"] = "web" });
            var row = new Dictionary<string, object?> { ["variant"] = v };
            try
            {
                var first = run.Next(" frame 1 (", TimeSpan.FromSeconds(120));
                row["firstFrameSeconds"] = first != null ? Math.Round(run.Since.Elapsed.TotalSeconds, 1) : null;
                if (first == null) { Result("memory", $"{v}: first frame", false, "no frame within 120 s"); continue; }
                if (v != "probe")
                {
                    var b1 = run.Next("batch 1 ", TimeSpan.FromSeconds(30));
                    row["firstBatch"] = b1;
                }
                Thread.Sleep(4000);
                var shot = Pictures.Shot(Path.Combine("memory", v, "screen.png"));
                using (var frame = run.Frame())
                {
                    var s = frame == null ? new Pictures.Stats(255, new double[256], 0) : Pictures.VsFrame(shot, frame, ScreenRect, Work, Desk.Around(Desk.Icons()));
                    row["screenVsFrame"] = s.ToString();
                    Result("memory", $"{v}: the screen shows the page's frame", s.Same, s.ToString());
                }
                row["idle"] = run.Usage(TimeSpan.FromSeconds(60));
                if (v != "probe")
                {
                    var costs = new List<Dictionary<string, object?>>();
                    for (int i = 0; i < 3; i++)
                    {
                        run.Mark();
                        Run.Signal("Draw");
                        var b = run.Next("batch ", TimeSpan.FromSeconds(60));
                        if (b != null) costs.Add(new()
                        {
                            ["engineStartMs"] = Run.Number(b, "engine start "),
                            ["framesReadyMs"] = Run.Number(b, "frames ready after "),
                            ["processorMs"] = Run.Number(b, "processor "),
                            ["restMs"] = Run.Number(b, $") "),
                            ["line"] = b,
                        });
                        Thread.Sleep(10000);
                    }
                    row["frames"] = costs;
                    row["afterFrames"] = run.Usage(TimeSpan.FromSeconds(30));
                    Run.Signal("Quit");
                }
                else Process.Start(new ProcessStartInfo(probe, "--stop") { UseShellExecute = false })!.WaitForExit(20000);
                row["exited"] = run.Proc.WaitForExit(20000);
                row["webEngineGoneSeconds"] = run.WaitGone();
            }
            finally
            {
                run.SaveLog(v == "probe" ? "probe.log" : "hidamari.log");
                variants.Add(row);
                Say($"memory {v}: {JsonSerializer.Serialize(row)}");
            }
        }
    }

    // ================= the end of a session, Task Manager =================

    static void EndSession()
    {
        const string g = "exit";
        using var run = Run.App(app, Path.Combine(outDir, "session-end"), png: "none");
        try
        {
            if (run.Next("frame 1 (", TimeSpan.FromSeconds(120)) == null) { Result(g, "sign-out or shutdown", false, "no first frame"); return; }
            Thread.Sleep(2000);
            var hwnd = Native.FindWindow(null, "Hidamari messages");
            var sw = Stopwatch.StartNew();
            Native.SendMessageTimeout(hwnd, 0x0011, IntPtr.Zero, new IntPtr(unchecked((int)0x80000000)), 0x2, 5000, out var ok);   // WM_QUERYENDSESSION, logoff
            var asked = sw.ElapsedMilliseconds;
            sw.Restart();
            Native.SendMessageTimeout(hwnd, 0x0016, new IntPtr(1), new IntPtr(unchecked((int)0x80000000)), 0x2, 5000, out _);   // WM_ENDSESSION
            bool exited = run.Proc.WaitForExit(10000);
            var left = sw.ElapsedMilliseconds;
            Result(g, "sign-out or shutdown: the app agrees at once and leaves", hwnd != IntPtr.Zero && ok == new IntPtr(1) && asked < 1000 && exited,
                $"window {(hwnd != IntPtr.Zero ? "found" : "NOT found")}, answered {ok} in {asked} ms, {(exited ? $"left {left} ms after" : "still running")}");
            run.WaitGone();
            Thread.Sleep(3000);
            DesktopBack(g, "after sign-out", Pictures.Shot("18-after-session-end.png"));
        }
        finally { run.SaveLog("hidamari.log"); }
    }

    /// The app ended in Task Manager (or crashing): what is left on the screen. With "full", the app first runs for
    /// ten minutes doing nothing, and its own measurements are read from its journal.
    static void KillRun()
    {
        const string g = "exit";
        using var run = Run.App(app, Path.Combine(outDir, "task-manager"), png: "none");
        try
        {
            if (run.Next("frame 1 (", TimeSpan.FromSeconds(120)) == null) { Result(g, "ended in Task Manager", false, "no first frame"); return; }
            if (full)
            {
                var m = run.Next("measure (every 10 minutes)", TimeSpan.FromMinutes(12.5));
                var lines = run.Lines().Where(l => l.Contains("measure (") || l.Contains("batch ") || l.Contains("pause")).ToArray();
                facts["tenMinutes"] = lines;
                Result("memory", "ten minutes: the app's own measurements in its journal", m != null, m ?? "no line after 12.5 min");
            }
            Thread.Sleep(3000);
            Pictures.Shot("19-before-task-manager.png");
            run.Proc.Kill();   // as Task Manager's "End task": the process is stopped at once, it cannot clean up
            run.Proc.WaitForExit(5000);
            var gone = run.WaitGone(30);
            Thread.Sleep(4000);
            var after = Pictures.Shot("20-after-task-manager.png");
            var iconsAfter = Desk.Icons();
            var back = Pictures.Diff(before!, after, Work, skip: Desk.Around(iconsBefore.Concat(iconsAfter), 4));
            Result(g, "ended in Task Manager: the wallpaper is back at once", back.Mean <= 1.0 && back.Within(8) >= 0.99, $"before vs after, around the icons: {back}");
            Result(g, "ended in Task Manager: the web engine leaves by itself", gone != null, $"after {gone?.ToString() ?? "more than 30"} s");
        }
        finally { run.SaveLog("hidamari.log"); }
    }

    // ================= the end =================

    static void PutBack()
    {
        foreach (var p in Process.GetProcessesByName("Hidamari").Concat(Process.GetProcessesByName("HidamariProbe"))) try { p.Kill(); } catch { }
        if (Desk.LightTaskbar() != lightBefore) Desk.SetLight(lightBefore);
        if (Desk.WallpaperPath() != wallpaperPathBefore) Desk.SetWallpaper(wallpaperPathBefore);
        if (Desk.TaskbarState() != taskbarBefore) Desk.SetTaskbarState(taskbarBefore);
        if (zoneBefore != null && Tz("/g") != zoneBefore) Tz($"/s \"{zoneBefore}\"");
        Desk.ResetResolution();
        try { using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey); k.DeleteValue("Hidamari", false); } catch { }
        try { using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(ApprovedKey); k.DeleteValue("Hidamari", false); } catch { }
        if (Desk.TaskbarEdge() != 3 && Environment.OSVersion.Version.Build < 22000) { Desk.SetTaskbarEdge(3); RestartExplorer(); }
    }

    static int Finish()
    {
        bool pass = results.Count > 0 && results.All(r => r.status != "FAIL");
        var md = new StringBuilder("# Hidamari check\n\n");
        md.AppendLine($"{facts.GetValueOrDefault("windows")}, {(full ? "full" : "quick")} run\n");
        foreach (var grp in results.GroupBy(r => r.group))
        {
            md.AppendLine($"## {grp.Key}\n");
            foreach (var r in grp) md.AppendLine($"- {r.status} {r.name}: {r.detail}");
            md.AppendLine();
        }
        if (variants.Count > 0)
        {
            md.AppendLine("## The web engine between frames\n");
            md.AppendLine("| | first frame, s | memory idle, MB (own) | processor idle, % of a core | memory after frames, MB (own) | one frame: ready after, ms | processor, ms |");
            md.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var v in variants)
            {
                var idle = v.GetValueOrDefault("idle") as Dictionary<string, object>;
                var after = v.GetValueOrDefault("afterFrames") as Dictionary<string, object>;
                var frames = v.GetValueOrDefault("frames") as List<Dictionary<string, object?>>;
                string avg(string k) => frames == null || frames.Count == 0 ? "" : $"{frames.Select(f => f.GetValueOrDefault(k) as double? ?? 0).Average():F0}";
                md.AppendLine($"| {v["variant"]} | {v.GetValueOrDefault("firstFrameSeconds")} | {idle?["workingSetMB"]} ({idle?["privateMB"]}) | {idle?["cpuPercentOfOneCore"]} | " +
                              $"{after?["workingSetMB"]} ({after?["privateMB"]}) | {avg("framesReadyMs")} | {avg("processorMs")} |");
            }
            md.AppendLine();
        }
        if (cardVariants.Count > 0)
        {
            md.AppendLine("## The card's web engine while it is closed\n");
            md.AppendLine("| | click → seen, ms (4 opens) | memory before, MB | open | just closed | 10 min after | processor while dragging, % of a core |");
            md.AppendLine("|---|---|---|---|---|---|---|");
            static string Mb(object? u) => u is Dictionary<string, object> d ? $"{d["workingSetMB"]} ({d["privateMB"]})" : "";
            foreach (var v in cardVariants)
            {
                var opens = v.GetValueOrDefault("opens") is List<object> o ? string.Join(", ", o.Select(x => JsonDocument.Parse(JsonSerializer.Serialize(x)).RootElement.GetProperty("seenMs").ToString())) : "";
                var drag = v.GetValueOrDefault("dragging") as Dictionary<string, object>;
                md.AppendLine($"| {v["variant"]} | {opens} | {Mb(v.GetValueOrDefault("before"))} | {Mb(v.GetValueOrDefault("open"))} | {Mb(v.GetValueOrDefault("justClosed"))} | " +
                              $"{Mb(v.GetValueOrDefault("tenMinutes"))} | {drag?["cpuPercentOfOneCore"]} |");
            }
            md.AppendLine();
        }
        md.AppendLine("## Facts\n\n```json\n" + JsonSerializer.Serialize(facts, new JsonSerializerOptions { WriteIndented = true }) + "\n```");
        File.WriteAllText(Path.Combine(outDir, "report.md"), md.ToString());
        File.WriteAllText(Path.Combine(outDir, "report.json"), JsonSerializer.Serialize(new
        {
            pass,
            results = results.Select(r => new { r.group, r.name, r.status, r.detail }),
            variants,
            cardVariants,
            facts,
        }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(outDir, "check.log"), log.ToString());
        Say($"{results.Count(r => r.status == "PASS")} passed, {results.Count(r => r.status == "FAIL")} failed, {results.Count(r => r.status == "SKIP")} not possible here");
        return pass ? 0 : 1;
    }
}
