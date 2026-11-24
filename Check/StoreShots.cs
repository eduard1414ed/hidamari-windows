using System.Diagnostics;
using System.Runtime.InteropServices;

// Screenshots for the Microsoft Store: the real desktop of GitHub's Windows 11 machine with the installed package
// running. Desktop icons, the taskbar with Hidamari's icon, the wall under them, and the card open on one of them.
//   Check store-shots <Hidamari.msixbundle> <out folder>
// Writes the pictures to <out folder>/store and the journal; exit code 0 only if nothing failed. Nothing here checks
// the app: the full check does that (Program.cs).
// The five walls are those of the Mac's frames shown to Ed (Berlin, 21 June 2026). The taskbar's clock shows the same
// time as the wall: the machine's own clock is set to it for each picture, and set back to the real time at the end.
// Windows 11 on GitHub's machine writes "Test Mode" in the corner (the machine is set up that way); Ed takes it out
// of the pictures himself (24 November 2026).

static partial class Check
{
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);

    /// The machine's clock was moved for a picture: it is set back to the real time at the end.
    static bool clockMoved;

    sealed record StoreShot(string Name, int Scene, int H, int M, int Wall, int Plant, string Weather);

    static readonly StoreShot[] storeShots =
    {
        new("1-dapples-afternoon-beige", 0, 17, 30, 0, 0, "clear"),
        new("2-window-palm-morning-mint", 1, 9, 0, 2, 5, "clear"),
        new("3-blinds-golden-clay", 2, 19, 45, 5, 0, "clear"),
        new("4-water-noon-greyblue", 3, 13, 0, 1, 0, "clear"),
        new("5-window-branch-rain-taupe", 1, 11, 0, 3, 2, "rain"),
    };

    static int StoreShotsMain(string bundlePath, string output)
    {
        packaged = true;
        bundle = Path.GetFullPath(bundlePath);
        outDir = Path.GetFullPath(output);
        Pictures.OutDir = outDir;
        Directory.CreateDirectory(Path.Combine(outDir, "store"));
        var realAt = DateTime.UtcNow;
        var since = Stopwatch.StartNew();
        try
        {
            Part("package: install", Install);
            if (familyName == null) throw new InvalidOperationException("the package is not installed");
            Part("store: desktop", StoreDesk);
            Part("store: pictures", StoreShotsTake);
        }
        catch (Exception e) { Result("check", "the shots themselves", false, e.ToString()); }
        finally
        {
            if (clockMoved)
            {
                // the real time again: where it was at the start, plus the time since
                var now = realAt + since.Elapsed;
                var (code, o) = Ps($"Set-Date -Date ([DateTime]::Parse('{now:o}').ToLocalTime()) | Out-Null; Start-Service w32time -ErrorAction SilentlyContinue; Start-Service vmictimesync -ErrorAction SilentlyContinue; w32tm /resync /force; Get-Date -Format o");
                Say($"the machine's clock back to the real time: {o} ({code})");
            }
        }
        return Finish();
    }

    /// The desktop as a person's: a few icons, no windows of GitHub's own, Windows' first sign-in screen gone, a 4K
    /// screen at 200 % if the machine offers it (the Store asks for up to 3840x2160).
    static void StoreDesk()
    {
        const string g = "store: desktop";
        facts["windows"] = $"{Environment.OSVersion.Version}, {Desk.Reg(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName")} {Desk.Reg(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion")}";
        Say($"Windows {facts["windows"]}, screen {ScreenRect.Width}x{ScreenRect.Height}, scale {Native.Scale()}");
        try { File.Delete(SettingsFile); } catch { }
        var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        Directory.CreateDirectory(Path.Combine(desktopDir, "Photos"));
        Directory.CreateDirectory(Path.Combine(desktopDir, "Projects"));
        File.WriteAllText(Path.Combine(desktopDir, "Notes.txt"), "\r\n");
        // GitHub's machine puts its own tools on the desktop; a person's would not have Epic Games and R side by side.
        // Chrome, Firefox and Edge stay.
        var removed = new List<string>();
        foreach (var dir in new[] { desktopDir, Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) })
            foreach (var f in Directory.Exists(dir) ? Directory.GetFiles(dir, "*.lnk") : Array.Empty<string>())
            {
                var n = Path.GetFileNameWithoutExtension(f);
                if (n.StartsWith("Epic", StringComparison.OrdinalIgnoreCase) || n == "R" || n.StartsWith("R ", StringComparison.Ordinal))
                    try { File.Delete(f); removed.Add(n); } catch (Exception e) { Say($"could not remove {f}: {e.Message}"); }
            }
        Say($"desktop shortcuts removed: {(removed.Count == 0 ? "none" : string.Join(", ", removed))}");
        CloseFirstSignIn();
        FixedPhoto();
        Desk.CloseStrayDialogs();

        var sizes = Desk.Resolutions();
        facts["resolutions"] = sizes.Select(s => $"{s.w}x{s.h}").Distinct().ToArray();
        Say($"screen sizes offered: {string.Join(", ", (string[])facts["resolutions"]!)}");
        var pick = new[] { (w: 3840, h: 2160), (w: 2560, h: 1440), (w: 1920, h: 1080) }.FirstOrDefault(s => sizes.Contains(s));
        if (pick != default)
        {
            int r = Desk.SetResolution(pick.w, pick.h);
            Thread.Sleep(4000);
            var want = pick.w switch { 3840 => 200, 2560 => 150, _ => 100 };
            var sc = Native.Scale();
            bool scaled = sc != null && want <= sc.Value.max && (sc.Value.now == want || Native.SetScale(want));
            Thread.Sleep(4000);
            Result(g, "screen size and scale for the Store", r == 0 && ScreenRect.Width == pick.w,
                $"{ScreenRect.Width}x{ScreenRect.Height} (asked {pick.w}x{pick.h}, answer {r}), scale {Native.Scale()} (wanted {want}, set {scaled})");
        }
        else Result(g, "screen size and scale for the Store", false, $"none of 3840x2160, 2560x1440, 1920x1080 offered; stays {ScreenRect.Width}x{ScreenRect.Height}");

        // the taskbar's clock in Berlin's time, as the wall's
        var tz = Process.Start(new ProcessStartInfo("tzutil", "/s \"W. Europe Standard Time\"") { UseShellExecute = false })!;
        tz.WaitForExit(10000);
        Say($"time zone now {Tz("/g")}");
    }

    static void StoreShotsTake()
    {
        const string g = "store: pictures";
        // the machine keeps its clock by itself (Windows Time, and Hyper-V's own time service): both stopped while
        // the pictures are taken, started again at the end
        var (sc, so) = Ps("Stop-Service w32time -Force -ErrorAction SilentlyContinue; Stop-Service vmictimesync -Force -ErrorAction SilentlyContinue; Get-Service w32time, vmictimesync -ErrorAction SilentlyContinue | ForEach-Object { $_.Name + ' ' + $_.Status }");
        Say($"time services: {so.Replace("\r\n", ", ")} ({sc})");

        WriteSettings(new());
        using var run = StartApp("store-shots", new() { ["HIDAMARI_CHECK_PNG"] = "none" });
        try
        {
            if (Desk.Promote(TrayExe)) { Say("tray: the app's icon promoted out of the hidden icons"); Thread.Sleep(3000); }
            HideOtherWindows();
            Desk.MinimizeAll();
            Thread.Sleep(2000);
            if (!Desk.IconsShown()) { Desk.ToggleIcons(); Thread.Sleep(2000); }
            Say($"tray icon: {Desk.TrayIcon("Hidamari")?.ToString() ?? "not found"}");

            // the wall and the taskbar's clock at the moment of shot `s`; the journal's line of its frame
            (string? frame, string clock) Show(StoreShot s)
            {
                Command(run, new { @do = "choose", type = "set", change = new { scene = s.Scene, wall = s.Wall, plant = s.Plant, weather = s.Weather } });
                clockMoved = true;
                var (c, o) = Ps($"Set-Date -Date ([DateTime]::Today.AddHours({s.H}).AddMinutes({s.M})) | Out-Null; Get-Date -Format 'HH:mm'");
                run.Mark();
                Command(run, new { @do = "clock", ms = (double)new DateTimeOffset(2026, 6, 21, s.H, s.M, 0, TimeSpan.FromHours(2)).ToUnixTimeMilliseconds() });
                var frame = run.Next(x => x.Contains(" frame ") && JuneAt(x, s.H, s.M), TimeSpan.FromSeconds(180));
                Desk.CloseStrayDialogs();
                AwayMouse();
                Thread.Sleep(2500);
                return (frame, $"{o} ({c})");
            }

            foreach (var s in storeShots)
            {
                var (frame, clock) = Show(s);
                Pictures.Shot($"store/{s.Name}.png");
                Result(g, s.Name, frame != null, $"machine clock {clock}; {frame ?? "no frame of this moment"}");
            }

            // the first wall once more, with the card open as a click on the icon opens it. Last: once the card closes,
            // Windows hands the keyboard to the taskbar and the Start button shows its "Start" tooltip in what follows.
            var first = storeShots[0];
            var (f1, _) = Show(first);
            var at = DateTime.UtcNow;
            Command(run, new { @do = "open" });
            var l = LayoutAfter(run, at, x => x.Open, 60);
            AwayMouse();
            Thread.Sleep(2000);
            Pictures.Shot($"store/{first.Name}-card.png");
            Result(g, first.Name + "-card", f1 != null && l != null, l != null ? $"card at {l.Card}, scale {l.Scale}" : "the card did not open");
            Command(run, new { @do = "close" });
        }
        finally
        {
            Command(run, new { @do = "realClock" });
            Quit(run);
        }
    }

    /// The mouse over an empty part of the wall: over the Start button it left a "Start" tooltip in the pictures
    /// (the pointer itself is not in a screenshot).
    static void AwayMouse() { Native.SetCursorPos(ScreenRect.Width * 3 / 5, ScreenRect.Height * 2 / 5); Thread.Sleep(600); }

    static bool JuneAt(string line, int h, int m)
    {
        int i = line.IndexOf("Berlin 2026-06-21 ");
        if (i < 0 || !TimeSpan.TryParse(line.Substring(i + 18, 8), out var t)) return false;
        var d = t - new TimeSpan(h, m, 0);
        return d >= TimeSpan.Zero && d <= TimeSpan.FromMinutes(15);
    }

    /// GitHub's own windows (the machine's agent in a console) out of sight and off the taskbar; the machine is thrown
    /// away after the run.
    static void HideOtherWindows()
    {
        var hidden = new List<string>();
        Native.EnumWindows((h, _) =>
        {
            if (Native.IsWindowVisible(h) && Native.ClassOf(h) == "ConsoleWindowClass")
            {
                hidden.Add(Native.TitleOf(h));
                ShowWindow(h, 0);
            }
            return true;
        }, IntPtr.Zero);
        Say($"consoles hidden: {(hidden.Count == 0 ? "none" : string.Join("; ", hidden))}");
    }
}
