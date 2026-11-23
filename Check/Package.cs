using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

// Stage 5: the app installed from its MSIX package, as the Store installs it (Check package …). Everything of stages
// 1–4 runs against the installed app: it is started through its alias (%LOCALAPPDATA%\Microsoft\WindowsApps\
// Hidamari.exe), which gives it the package's identity and still passes the check's settings. Then what only a
// package has: the Start menu, Launch at login through the package's StartupTask, an update over the running app,
// and the removal in Settings.
static partial class Check
{
    const string PackageName = "EdTsarionov.Hidamari";
    /// The app runs installed from its package (Check package …), not as a plain Hidamari.exe.
    static bool packaged;
    static string? familyName, installedExe, bundle, updateBundle;

    /// The app's own folder: settings, journals, the card's page, the web engine's data.
    public static string DataFolder => packaged
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", familyName!, "LocalState")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hidamari");

    /// The exe Windows knows the tray icon by (Settings → Other system tray icons): the one in the package's folder.
    static string TrayExe => installedExe ?? app;

    static string Alias => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "Hidamari.exe");

    // ---------- PowerShell (Windows PowerShell has the Appx commands everywhere, also on ARM) ----------

    static (int code, string output) Ps(string script, int seconds = 300)
    {
        var psi = new ProcessStartInfo("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script })
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(seconds * 1000)) { try { p.Kill(true); } catch { } return (-1, "timed out"); }
        return (p.ExitCode, (o.Result + e.Result).Trim());
    }

    sealed record Installed(string FullName, string FamilyName, string Version, string Arch, string Location);

    static Installed? Find()
    {
        var (code, json) = Ps($"Get-AppxPackage -Name {PackageName} | Select-Object PackageFullName, PackageFamilyName, @{{n='Version';e={{$_.Version.ToString()}}}}, @{{n='Arch';e={{$_.Architecture.ToString()}}}}, InstallLocation | ConvertTo-Json -Compress");
        if (code != 0 || string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var j = JsonDocument.Parse(json).RootElement;
            if (j.ValueKind == JsonValueKind.Array) j = j[0];
            return new(j.GetProperty("PackageFullName").GetString()!, j.GetProperty("PackageFamilyName").GetString()!,
                j.GetProperty("Version").GetString()!, j.GetProperty("Arch").GetString()!, j.GetProperty("InstallLocation").GetString()!);
        }
        catch { return null; }
    }

    // ---------- Launch at login through the package's StartupTask ----------

    /// Where Windows keeps the startup task's state (Task Manager and Settings → Apps → Startup write it there too):
    /// State 0 off, 1 turned off by the person, 2 on.
    static string TaskKey => $@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\{familyName}\HidamariStartup";

    static int? TaskState()
    {
        using var k = Registry.CurrentUser.OpenSubKey(TaskKey);
        return k?.GetValue("State") as int?;
    }

    static string TaskSays(int? s) => s switch { 0 => "off (0)", 1 => "turned off by the person (1)", 2 => "on (2)", null => "no state yet", _ => $"state {s}" };

    static bool LoginOn() => packaged ? TaskState() == 2 : RunEntry() != null;
    static string LoginSays() => packaged ? TaskSays(TaskState()) : RunEntry() ?? "no Run entry";

    /// Settings → Launch at login in the package: on, turned off by the person (as Task Manager does), what the switch
    /// then shows and does, off. Mirrors the Run key's checks of a plain Hidamari.exe (RunKeyLogin).
    static Layout? PackageLogin(Run run, Layout l, string g)
    {
        run.Mark();
        Result(g, "the package: Launch at login starts off", TaskState() is null or 0 && !l.Is("login"), $"startup task: {TaskSays(TaskState())}");
        l = ClickCard(run, l, "login", until: x => x.Is("login")) ?? l;
        var said = run.Next("login: Launch at login asked on", TimeSpan.FromSeconds(10));
        Result(g, "Launch at login on: the package's startup task is on", TaskState() == 2 && l.Is("login"),
            $"startup task: {TaskSays(TaskState())}; journal: {said ?? "nothing"}");
        Result(g, "Launch at login on: no Run entry (the package uses its startup task)", RunEntry() == null, $"Run entry: {RunEntry() ?? "none"}");

        // the person turns it off in Task Manager → Startup apps: Windows writes State 1
        using (var k = Registry.CurrentUser.CreateSubKey(TaskKey)) k.SetValue("State", 1, RegistryValueKind.DWord);
        run.Mark();
        Command(run, new { @do = "push" });
        var off = LayoutAfter(run, DateTime.UtcNow.AddSeconds(-0.2), x => !x.Is("login"));
        var read = run.Next("login: Launch at login turned off by the person", TimeSpan.FromSeconds(5));
        Result(g, "turned off in Task Manager: the switch shows off", off != null, $"{(off != null ? "off" : "still on")}; journal: {read ?? "nothing"}");
        l = off ?? l;

        // the switch on again: only the person can, in Windows' list; the app opens it and the switch stays off
        run.Mark();
        var settingsBefore = Process.GetProcessesByName("SystemSettings").Select(p => p.Id).ToHashSet();
        l = ClickCard(run, l, "login") ?? l;
        var opened = run.Next("Settings → Apps → Startup opened", TimeSpan.FromSeconds(10));
        Thread.Sleep(4000);
        var settingsApp = Process.GetProcessesByName("SystemSettings").Where(p => !settingsBefore.Contains(p.Id)).ToList();
        Pictures.Shot("card/login-turned-off-by-person-settings.png");
        var after = ReadLayout(run);
        Result(g, "turned off by the person, switched on: Windows' Startup settings open, the switch stays off",
            opened != null && after != null && !after.Is("login") && TaskState() == 1,
            $"journal: {opened ?? "nothing"}; Settings app {(settingsApp.Count > 0 ? "opened" : "not seen")}; startup task {TaskSays(TaskState())}");
        foreach (var p in Process.GetProcessesByName("SystemSettings")) try { p.Kill(); } catch { }
        Thread.Sleep(1500);

        // the person turns it on there (State 2): the switch follows when the card is opened again
        using (var k = Registry.CurrentUser.CreateSubKey(TaskKey)) k.SetValue("State", 2, RegistryValueKind.DWord);
        var (l2, _) = OpenByIcon(run);
        if (l2 != null && !l2.Settings) l2 = ClickCard(run, l2, "gear", until: x => x.Settings);
        var on = l2 != null ? LayoutAfter(run, DateTime.UtcNow.AddSeconds(-1), x => x.Is("login")) ?? l2 : null;
        Result(g, "turned on again in Windows' list: the switch shows on", on != null && on.Is("login"), $"startup task: {TaskSays(TaskState())}");
        l = on ?? l2 ?? l;

        l = ClickCard(run, l, "login", until: x => !x.Is("login")) ?? l;
        Result(g, "Launch at login off: the startup task is off", TaskState() == 0 && !l.Is("login"), $"startup task: {TaskSays(TaskState())}");
        return l;
    }

    // ================= install =================

    static void Install()
    {
        const string g = "package: install";
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        facts["machine"] = arch;
        // nothing from an earlier package
        Ps($"Get-AppxPackage -Name {PackageName} | Remove-AppxPackage");
        var sw = Stopwatch.StartNew();
        var (code, output) = Ps($"Add-AppxPackage -Path '{bundle}'");
        var p = Find();
        Result(g, "the bundle installs as the Store would (Add-AppxPackage, signed for this run only)", code == 0 && p != null,
            $"{sw.Elapsed.TotalSeconds:F1} s; {(code == 0 ? "" : output)}{(p != null ? $"{p.FullName}" : "not installed")}");
        if (p == null) throw new InvalidOperationException("the package did not install; nothing else can be checked");
        familyName = p.FamilyName;
        installedExe = Path.Combine(p.Location, "Hidamari.exe");
        facts["package"] = p.FullName;
        facts["packageVersion"] = p.Version;
        Result(g, "Windows picked the package for this machine's processor from the bundle (ARM gets its own build)",
            p.Arch.Equals(arch, StringComparison.OrdinalIgnoreCase), $"machine {arch}, package {p.Arch}");
        Result(g, "the app inside is a build for this processor", Native.MachineOf(installedExe) is var m && m.Equals(arch, StringComparison.OrdinalIgnoreCase),
            $"Hidamari.exe: {Native.MachineOf(installedExe)}");
        var files = Directory.GetFiles(p.Location, "*", SearchOption.AllDirectories);
        facts["packageFiles"] = files.Length;
        facts["packageMB"] = Math.Round(files.Sum(f => new FileInfo(f).Length) / 1048576.0, 1);
        Result(g, "the alias Hidamari.exe is there (Win+R → Hidamari; the checks start the app with it)", File.Exists(Alias), Alias);
        var start = StartEntry();
        Result(g, "Hidamari is in the Start menu", start != null, start ?? "not in Get-StartApps");
        Result(g, "Launch at login is off after install", TaskState() is null or 0, $"startup task: {TaskSays(TaskState())}");
        app = Alias;
    }

    static string? StartEntry()
    {
        var (_, o) = Ps($"Get-StartApps | Where-Object {{ $_.AppID -like '{PackageName}*' }} | ForEach-Object {{ $_.Name + ' | ' + $_.AppID }}");
        return string.IsNullOrWhiteSpace(o) ? null : o.Trim();
    }

    /// The first run of the installed app: it runs as the package (its journal says so) and keeps its files in the
    /// package's folder. Part of the main run, right after it started.
    static void PackageIdentity(Run run)
    {
        const string g = "package: install";
        var head = run.Lines().FirstOrDefault(x => x.Contains(" Hidamari ") && x.Contains(" pid "));
        Result(g, "started through the alias, the app runs as the package", head != null && head.Contains($"package {familyName}"), head ?? "no journal");
        Result(g, "its settings and journal are in the package's own folder", File.Exists(Path.Combine(DataFolder, "hidamari.log")), DataFolder);
        var classic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hidamari");
        Result(g, "nothing in %LOCALAPPDATA%\\Hidamari (a plain Hidamari.exe's folder)", !Directory.Exists(classic), Directory.Exists(classic) ? string.Join(", ", Directory.GetFileSystemEntries(classic).Select(Path.GetFileName)) : "no such folder");
        var restart = run.Lines().FirstOrDefault(x => x.Contains("package: "));
        Result(g, "asks Windows to start it again after an update", restart != null && restart.Contains("starts Hidamari again"), restart ?? "nothing in the journal");
    }

    // ================= the Start menu =================

    static string AppsFolder => $@"shell:AppsFolder\{familyName}!App";

    static void StartMenuRun()
    {
        const string g = "package: Start menu";
        WriteSettings(new());
        var since = DateTime.UtcNow;
        Process.Start(new ProcessStartInfo("explorer.exe", AppsFolder) { UseShellExecute = true });
        var proc = WaitForApp(since, TimeSpan.FromSeconds(30));
        Result(g, "Hidamari starts from the Start menu", proc != null, proc != null ? $"pid {proc.Id}" : "no Hidamari process");
        if (proc == null) return;
        using var run = Run.Attach(proc, Path.Combine(outDir, "start-menu"));
        try
        {
            var frame = run.Next("frame 1 (", TimeSpan.FromSeconds(120));
            var head = run.Lines().FirstOrDefault();
            Result(g, "started from Start: the wall comes, as the package", frame != null && head != null && head.Contains($"package {familyName}"), $"{head}; {frame ?? "no frame"}");
            Thread.Sleep(3000);
            Pictures.Shot("package/started-from-start.png");
            run.Mark();
            Process.Start(new ProcessStartInfo("explorer.exe", AppsFolder) { UseShellExecute = true });
            var again = run.Next("app: started again while running, opening the card", TimeSpan.FromSeconds(20));
            var opened = run.Next("card: opened (Hidamari started again)", TimeSpan.FromSeconds(20));
            Thread.Sleep(1500);
            Pictures.Shot("package/start-again-opens-card.png");
            var others = Process.GetProcessesByName("Hidamari").Where(p => p.Id != proc.Id && !p.HasExited).Count();
            Result(g, "Start again while it runs: the card opens, no second Hidamari", again != null && opened != null && others == 0,
                $"{again ?? "nothing"}; {opened ?? "card not opened"}; other Hidamari processes: {others}");
            Esc();
        }
        finally
        {
            try { proc.Kill(); proc.WaitForExit(5000); } catch { }
            run.WaitGone(20);
            run.SaveLog("hidamari.log");
        }
    }

    static Process? WaitForApp(DateTime since, TimeSpan timeout, int? not = null)
    {
        for (var sw = Stopwatch.StartNew(); sw.Elapsed < timeout; Thread.Sleep(250))
            foreach (var p in Process.GetProcessesByName("Hidamari"))
                try { if (p.Id != not && p.StartTime.ToUniversalTime() >= since.AddSeconds(-1)) return p; } catch { }
        return null;
    }

    // ================= an update over the running app =================

    static void UpdateRun()
    {
        const string g = "package: update";
        if (updateBundle == null) { Result(g, "an update over the running app", "N/A", "no update bundle given"); return; }
        // the person's choices before: Water, umber wall, the palm, Launch at login on
        WriteSettings(new() { ["scene"] = 3, ["wall"] = 4, ["plant"] = 5 });
        var settingsBefore = File.ReadAllText(SettingsFile);
        using var run = Run.App(app, Path.Combine(outDir, "update-before"));
        Run? after = null;
        try
        {
            if (run.Next("frame 1 (", TimeSpan.FromSeconds(180)) == null) { Result(g, "an update over the running app", false, "no first frame before"); return; }
            Command(run, new { @do = "choose", type = "login", on = true });
            Thread.Sleep(2000);
            var loginBefore = TaskState();
            // Windows starts again only an app that has run at least a minute
            Thread.Sleep(TimeSpan.FromSeconds(65));
            Pictures.Shot("package/update-1-before.png");
            var since = DateTime.UtcNow;
            var sw = Stopwatch.StartNew();
            // as the Store does: the running app is closed for the update
            var (code, output) = Ps($"Add-AppxPackage -Path '{updateBundle}' -ForceApplicationShutdown");
            var installed = sw.Elapsed.TotalSeconds;
            var p = Find();
            Result(g, "the newer package installs over the running one", code == 0 && p != null && p.Version != (string?)facts["packageVersion"],
                $"{installed:F1} s; now {p?.Version ?? "none"} (was {facts["packageVersion"]}){(code == 0 ? "" : "; " + output)}");
            run.Proc.WaitForExit(30000);
            var closed = run.Lines().LastOrDefault(x => x.Contains("stopping:"));
            Result(g, "the running app was closed for it", run.Proc.HasExited, $"{(run.Proc.HasExited ? "closed" : "still running")}; journal: {closed ?? "no stopping line (ended without a word)"}");
            var proc = WaitForApp(since, TimeSpan.FromSeconds(90), not: run.Proc.Id);
            Result(g, "after the update Windows starts Hidamari again by itself", proc != null,
                proc != null ? $"pid {proc.Id}, {(proc.StartTime.ToUniversalTime() - since).TotalSeconds:F0} s after the update began" : "no Hidamari within 90 s");
            if (proc == null) { Pictures.Shot("package/update-2-after.png"); return; }
            after = Run.Attach(proc, Path.Combine(outDir, "update-after"));
            var frame = after.Next("frame 1 (", TimeSpan.FromSeconds(120));
            var head = after.Lines().FirstOrDefault();
            Thread.Sleep(3000);
            Pictures.Shot("package/update-2-after.png");
            Result(g, "the wall is back, drawn by the new version", frame != null && head != null && p != null && head.Contains(p.Version),
                $"{head}; {frame ?? "no frame"}");
            var settingsAfter = Run.Read(SettingsFile);
            var st = after.Lines().FirstOrDefault(x => x.Contains("settings: "));
            bool same = SameChoices(settingsBefore, settingsAfter);
            Result(g, "the settings are kept (Water, umber wall, the palm)", same, $"settings.json {(same ? "the same choices" : "changed")}; journal: {st}");
            Result(g, "Launch at login is kept", loginBefore == 2 && TaskState() == 2, $"before {TaskSays(loginBefore)}, after {TaskSays(TaskState())}");
        }
        finally
        {
            run.SaveLog("hidamari.log");
            if (after != null)
            {
                try { after.Proc.Kill(); after.Proc.WaitForExit(5000); } catch { }
                after.WaitGone(20);
                after.SaveLog("hidamari.log");
                after.Dispose();
            }
        }
    }

    static bool SameChoices(string a, string b)
    {
        try
        {
            var x = JsonDocument.Parse(a).RootElement;
            var y = JsonDocument.Parse(b).RootElement;
            return new[] { "scene", "wall", "plant" }.All(k => x.GetProperty(k).GetRawText() == y.GetProperty(k).GetRawText());
        }
        catch { return false; }
    }

    // ================= removal =================

    static void RemoveRun()
    {
        const string g = "package: removal";
        // removed while it runs, as from Settings → Apps → Installed apps → Uninstall
        WriteSettings(new());
        var since = DateTime.UtcNow;
        Process.Start(new ProcessStartInfo("explorer.exe", AppsFolder) { UseShellExecute = true });
        var proc = WaitForApp(since, TimeSpan.FromSeconds(30));
        if (proc != null)
        {
            using var r = Run.Attach(proc, Path.Combine(outDir, "removal"));
            r.Next("frame 1 (", TimeSpan.FromSeconds(120));
            r.SaveLog("hidamari.log");
        }
        Thread.Sleep(3000);
        Pictures.Shot("package/remove-1-before.png");
        var data = DataFolder;
        var sw = Stopwatch.StartNew();
        var (code, output) = Ps($"Get-AppxPackage -Name {PackageName} | Remove-AppxPackage");
        Result(g, "removed while running", code == 0 && Find() == null, $"{sw.Elapsed.TotalSeconds:F1} s{(code == 0 ? "" : "; " + output)}");
        Thread.Sleep(5000);
        var left = Process.GetProcessesByName("Hidamari").Length;
        var engines = Process.GetProcessesByName("msedgewebview2").Count(x => { try { return x.StartTime.ToUniversalTime() >= since.AddSeconds(-1); } catch { return false; } });
        Result(g, "nothing of Hidamari runs afterwards", left == 0 && engines == 0, $"Hidamari processes: {left}; web engine processes started since: {engines}");
        var shot = Pictures.Shot("package/remove-2-after.png");
        var iconsAfter = Desk.Icons();
        var back = Pictures.Diff(before!, shot, Work, skip: Desk.Around(iconsBefore.Concat(iconsAfter), 4));
        Result(g, "the desktop photo is back", back.Mean <= 1.0 && back.Within(8) >= 0.99, $"before vs after, around the icons: {back}");
        Result(g, "its folder is gone (settings, journals)", !Directory.Exists(Path.GetDirectoryName(data)!), Path.GetDirectoryName(data)!);
        Result(g, "the alias, the Start entry and Launch at login are gone", !File.Exists(Alias) && StartEntry() == null && TaskState() == null,
            $"alias {(File.Exists(Alias) ? "still there" : "gone")}; Start {StartEntry() ?? "gone"}; startup task {TaskSays(TaskState())}");
    }
}
