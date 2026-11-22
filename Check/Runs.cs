using System.Diagnostics;
using System.Text.RegularExpressions;

/// One run of the app (or of the probe): started with its settings for the check, its journal read as it grows.
sealed class Run : IDisposable
{
    public static readonly string AppLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hidamari", "hidamari.log");
    public static readonly string ProbeLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HidamariProbe", "probe.log");

    public Process Proc { get; }
    public string Exe { get; }
    public string LogPath { get; }
    public string Folder { get; }
    public Stopwatch Since { get; } = Stopwatch.StartNew();
    int mark;   // lines already seen by Next()

    readonly DateTime started = DateTime.UtcNow;

    Run(Process p, string exe, string log, string folder) { Proc = p; Exe = exe; LogPath = log; Folder = folder; }

    /// The app with `policy` (null: its default) saving its frames into `folder`.
    public static Run App(string exe, string folder, string? policy = null, string png = "all", Dictionary<string, string>? env = null)
    {
        Directory.CreateDirectory(folder);
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.Environment["HIDAMARI_CHECK"] = folder;
        psi.Environment["HIDAMARI_CHECK_PNG"] = png;
        if (policy != null) psi.Environment["HIDAMARI_ENGINE"] = policy;
        // stage 4: Direct3D unless a run asks for something else (the drawing is remembered in the settings; a run
        // that passes its own `env`, even an empty one, gets what the settings say)
        if (env == null) psi.Environment["HIDAMARI_RENDERER"] = "direct3d";
        foreach (var (k, v) in env ?? new()) psi.Environment[k] = v;
        var p = Process.Start(psi)!;
        Check.Say($"app started{(policy != null ? $" ({policy})" : "")}, pid {p.Id}");
        return new Run(p, exe, AppLog, folder);
    }

    public static Run Probe(string exe, string folder)
    {
        Directory.CreateDirectory(folder);
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.Environment["HIDAMARI_PROBE_CHECK"] = Path.Combine(folder, "canvas");
        var p = Process.Start(psi)!;
        Check.Say($"probe started, pid {p.Id}");
        return new Run(p, exe, ProbeLog, folder);
    }

    public string Text() => Read(LogPath);
    /// The journal's lines from this start on. The app starts a new journal when it starts, but until then the file
    /// holds the previous run, whose lines must not count (the memory variants once read the last run's frame).
    public string[] Lines()
    {
        var lines = Text().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        int from = Array.FindIndex(lines, l => l.Length >= 24
            && DateTime.TryParse(l[..24], System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
            && t >= started.AddSeconds(-2));
        return from < 0 ? Array.Empty<string>() : lines[from..];
    }

    public static string Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new StreamReader(fs).ReadToEnd();
        }
        catch { return ""; }
    }

    /// From now on, Next() sees only lines written after this moment.
    public void Mark() => mark = Lines().Length;

    /// The first line after the mark that contains `text`, waiting up to `timeout`; the mark moves past it.
    public string? Next(string text, TimeSpan timeout) => Next(l => l.Contains(text), timeout);

    public string? Next(Func<string, bool> match, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        do
        {
            var lines = Lines();
            for (int i = mark; i < lines.Length; i++)
                if (match(lines[i])) { mark = i + 1; return lines[i]; }
            if (Proc.HasExited && sw.Elapsed > TimeSpan.FromSeconds(1)) return null;
            Thread.Sleep(250);
        } while (sw.Elapsed < timeout);
        return null;
    }

    /// Lines after the mark that contain `text`, without moving the mark.
    public List<string> Since_(string text) => Lines().Skip(mark).Where(l => l.Contains(text)).ToList();

    /// The newest frame of the page the app saved for `screen` (DISPLAY1 …).
    public Bitmap? Frame(string screen = "DISPLAY1")
    {
        var a = Path.Combine(Folder, "canvas", $"canvas-{screen}.png");
        return Pictures.Load(a);
    }

    /// Signals one of the app's check events (Draw, Measure, Quit, Command).
    public static bool Signal(string name)
    {
        if (!EventWaitHandle.TryOpenExisting($@"Local\Hidamari.Check.{name}", out var ev)) return false;
        ev.Set(); ev.Dispose();
        return true;
    }

    /// The process and everything it started, still running.
    public List<int> Tree() => Tree(Proc.Id);

    public static List<int> Tree(int root)
    {
        var all = Native.Processes();
        var ids = new List<int>();
        var queue = new Queue<int>(new[] { root });
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            try { using var p = Process.GetProcessById(id); if (!p.HasExited && !ids.Contains(id)) ids.Add(id); } catch { }
            // only the web engine's processes: Windows hands out the numbers of ended processes again, so a system
            // process can name as its parent a number that is now one of ours (the whole machine was counted once)
            foreach (var (child, parent, exe) in all)
                if (parent == id && child != id && !ids.Contains(child)
                    && Path.GetFileNameWithoutExtension(exe).Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase))
                    queue.Enqueue(child);
        }
        return ids;
    }

    /// Waits until the process and its web engine are gone; seconds it took, or null if they stayed.
    public double? WaitGone(int seconds = 20)
    {
        var sw = Stopwatch.StartNew();
        var ids = Tree();
        while (sw.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (ids.All(id => { try { using var p = Process.GetProcessById(id); return p.HasExited; } catch { return true; } }))
                return Math.Round(sw.Elapsed.TotalSeconds, 1);
            Thread.Sleep(250);
        }
        return null;
    }

    /// Memory and processor of the process and everything it started, over `span` of doing nothing.
    public Dictionary<string, object> Usage(TimeSpan span)
    {
        (Dictionary<int, double> cpu, long ws, long priv, int n, string names) Sample()
        {
            var cpu = new Dictionary<int, double>(); long ws = 0, priv = 0; var names = new List<string>();
            foreach (var id in Tree())
                try { using var p = Process.GetProcessById(id); cpu[id] = p.TotalProcessorTime.TotalSeconds; ws += p.WorkingSet64; priv += p.PrivateMemorySize64; names.Add(p.ProcessName); }
                catch { }
            return (cpu, ws, priv, names.Count, string.Join(",", names.GroupBy(x => x).Select(g => $"{g.Key}×{g.Count()}")));
        }
        var a = Sample();
        var sw = Stopwatch.StartNew();
        long peakWs = a.ws;
        while (sw.Elapsed < span) { Thread.Sleep(5000); peakWs = Math.Max(peakWs, Sample().ws); }
        var b = Sample();
        return new()
        {
            ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds),
            ["processes"] = b.n,
            ["names"] = b.names,
            ["workingSetMB"] = Math.Round(b.ws / 1048576.0),
            ["peakWorkingSetMB"] = Math.Round(peakWs / 1048576.0),
            ["privateMB"] = Math.Round(b.priv / 1048576.0),
            // per process, so that processes that came or went in between do not count as negative or whole
            ["cpuPercentOfOneCore"] = Math.Round(100 * b.cpu.Sum(kv => kv.Value - a.cpu.GetValueOrDefault(kv.Key, kv.Value)) / sw.Elapsed.TotalSeconds, 2),
        };
    }

    /// A number from a journal line: `name` followed by digits (e.g. "processor 123 ms" with name "processor ").
    public static double? Number(string line, string name)
    {
        var m = Regex.Match(line, Regex.Escape(name) + @"(-?\d+(\.\d+)?)");
        return m.Success ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    /// Copies the app's journals (this run and the ones before) next to the results.
    public void SaveLog(string name)
    {
        try { File.WriteAllText(Path.Combine(Folder, name), Text()); } catch { }
    }

    public void Dispose()
    {
        try { if (!Proc.HasExited) { Proc.Kill(true); Proc.WaitForExit(5000); } } catch { }
    }
}
