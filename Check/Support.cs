using System.Diagnostics;
using System.Text.Json;

// Windows 1.1 (the Mac's update 1.4): the support window, once after every update, never at the first installation.
// The GitHub checks keep it away from the other checks; here HIDAMARI_CHECK_SUPPORT=1 lets its own rule decide.
static partial class Check
{
    static readonly Dictionary<string, string> SupportRule = new() { ["HIDAMARI_CHECK_SUPPORT"] = "1" };

    sealed record SupportLayout(Rectangle Window, double Scale, bool Open, JsonElement Rects, DateTime At)
    {
        public Point At_(string name)
        {
            var r = Rects.GetProperty(name);
            return new Point((int)Math.Round(Window.X + (r[0].GetDouble() + 0.5 * r[2].GetDouble()) * Scale),
                             (int)Math.Round(Window.Y + (r[1].GetDouble() + 0.5 * r[3].GetDouble()) * Scale));
        }
        public Rectangle Rect(string name)
        {
            var r = Rects.GetProperty(name);
            return new Rectangle((int)Math.Round(Window.X + r[0].GetDouble() * Scale), (int)Math.Round(Window.Y + r[1].GetDouble() * Scale),
                                 (int)Math.Round(r[2].GetDouble() * Scale), (int)Math.Round(r[3].GetDouble() * Scale));
        }
    }

    static SupportLayout? ReadSupportLayout(Run run)
    {
        var path = Path.Combine(run.Folder, "support-layout.json");
        var text = Run.Read(path);
        if (text.Length == 0) return null;
        try
        {
            var d = JsonDocument.Parse(text).RootElement.Clone();
            var w = d.GetProperty("window");
            return new SupportLayout(new Rectangle(w.GetProperty("X").GetInt32(), w.GetProperty("Y").GetInt32(), w.GetProperty("Width").GetInt32(), w.GetProperty("Height").GetInt32()),
                d.GetProperty("scale").GetDouble(), d.GetProperty("open").GetBoolean(), d.GetProperty("rects"), File.GetLastWriteTimeUtc(path));
        }
        catch { return null; }
    }

    static SupportLayout? SupportLayoutSeen(Run run, double seconds = 30)
    {
        for (var sw = Stopwatch.StartNew(); sw.Elapsed < TimeSpan.FromSeconds(seconds); Thread.Sleep(200))
            // the page tells its places as it is drawn, just before the window is shown ("open" is still false then):
            // that it is shown, the journal says
            if (ReadSupportLayout(run) is { } l && l.Rects.TryGetProperty("done", out _)) { Thread.Sleep(600); return ReadSupportLayout(run) ?? l; }
        return null;
    }

    static string Shown(string settings) =>
        JsonDocument.Parse(settings).RootElement.TryGetProperty("supportShownVersion", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "none";

    static void Support()
    {
        const string g = "support window";

        // an earlier version left its settings (1.0 did not remember its version): the window comes once, 1.5 s after the start
        WriteSettings(new());
        using (var up = StartApp("support-update", SupportRule))
        {
            try
            {
                // the rule speaks before the first frame, which StartApp has already waited for: read the whole journal
                var said = up.Lines().FirstOrDefault(x => x.Contains("support window: an earlier version → "));
                var shown = up.Next("support window shown (after an update)", TimeSpan.FromSeconds(60));
                var l = SupportLayoutSeen(up);
                var saved = Run.Read(SettingsFile);
                Result(g, "after an update from 1.0 it opens by itself, in the middle of the main screen, and the version is remembered",
                    said != null && shown != null && l != null && Shown(saved) == AppVersion3(up),
                    $"{said ?? "no rule line"}; {shown ?? "not shown"}; settings.json supportShownVersion {Shown(saved)}");
                if (l != null)
                {
                    Pictures.Crop(Pictures.Shot(null), l.Window, "support/after-update.png");
                    var work = Work;
                    var c = l.Rect("card");
                    bool middle = Math.Abs(c.X + c.Width / 2 - (work.Left + work.Width / 2)) <= 4 && Math.Abs(c.Y + c.Height / 2 - (work.Top + work.Height / 2)) <= 4;
                    string version = l.Rects.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
                    Result(g, "its card in the middle of the work area, the version as the card shows it, both buttons there",
                        middle && version == $"Hidamari {AppShortVersion(up)}" && l.Rect("support").Width > 0 && l.Rect("done").Width > 0,
                        $"card {c}, work area {work}; version \"{version}\"; picture support/after-update.png");
                    // Done closes it (the Ko-fi button opens the browser: not pressed on GitHub's machines)
                    up.Mark();
                    Native.Click(l.At_("done"));
                    var closed = up.Next("support window closed: ", TimeSpan.FromSeconds(10));
                    Thread.Sleep(800);
                    Result(g, "Done closes it, the wall goes on", closed != null && ReadSupportLayout(up)?.Open != true && !up.Proc.HasExited,
                        closed ?? "not closed");
                }
            }
            finally { Quit(up); }
        }

        // the same version again: not shown
        using (var again = StartApp("support-again", SupportRule))
        {
            try
            {
                Thread.Sleep(5000);
                var lines = again.Lines().Where(x => x.Contains("support window")).ToList();
                Result(g, "the next start of the same version: not shown", !lines.Any(x => x.Contains("shown")) || lines.All(x => x.Contains("not shown")),
                    lines.Count == 0 ? "no line about it" : string.Join(" / ", lines));
            }
            finally { Quit(again); }
        }

        // the very first installation: not shown, the version remembered with the first save
        File.Delete(SettingsFile);
        using (var first = StartApp("support-first", SupportRule))
        {
            try
            {
                Thread.Sleep(4000);
                var said = first.Lines().FirstOrDefault(x => x.Contains("support window: first installation of "));
                var shown = first.Lines().FirstOrDefault(x => x.Contains("support window shown"));
                Result(g, "the very first installation: not shown", said != null && shown == null, $"{said ?? "no rule line"}{(shown != null ? "; but: " + shown : "")}");
                // the first launch's card answered, so the rest of the checks find the settings as they expect
                Esc();
            }
            finally { Quit(first); }
        }
        WriteSettings(new());
    }

    /// The app's own version from its first journal line ("Hidamari 1.1.0.0 for Windows").
    static string AppVersion3(Run run)
    {
        var line = run.Lines().FirstOrDefault(x => x.Contains(" for Windows, pid ")) ?? "";
        var m = System.Text.RegularExpressions.Regex.Match(line, @"Hidamari (\d+)\.(\d+)\.(\d+)");
        return m.Success ? $"{m.Groups[1]}.{m.Groups[2]}.{m.Groups[3]}" : "?";
    }

    static string AppShortVersion(Run run)
    {
        var v = AppVersion3(run);
        return v.EndsWith(".0") ? v[..^2] : v;
    }
}
