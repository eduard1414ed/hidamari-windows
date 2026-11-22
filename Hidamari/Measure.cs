using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using static Hidamari.Native;

namespace Hidamari;

/// The measurements in the journal, so a friend's journal brings the numbers with it: what the machine is, and every
/// ten minutes the memory and processor of the whole app with the web engine's processes, and the graphics card's load.
sealed class Measure : IDisposable
{
    readonly int root = Environment.ProcessId;
    IntPtr query, gpuEngine, gpuDedicated, gpuShared;
    bool gpuReady;
    DateTime lastAt = DateTime.UtcNow;
    double lastCpu;
    /// What else the line tells: which drawing, the motion (App).
    public Func<string>? Extra;

    public Measure()
    {
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out query) == 0
                && PdhAddEnglishCounter(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out gpuEngine) == 0)
            {
                PdhAddEnglishCounter(query, @"\GPU Process Memory(*)\Dedicated Usage", IntPtr.Zero, out gpuDedicated);
                PdhAddEnglishCounter(query, @"\GPU Process Memory(*)\Shared Usage", IntPtr.Zero, out gpuShared);
                gpuReady = PdhCollectQueryData(query) == 0;
            }
        }
        catch (Exception e) { Log.Write($"measure: no graphics card counters ({e.Message})"); }
        lastCpu = Cpu(out _);
    }

    /// What the machine is: Windows, graphics cards, screens.
    public static void Machine(List<ScreenInfo> screens)
    {
        const string nt = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var build = Registry.GetValue(nt, "CurrentBuild", "?")?.ToString() ?? "?";
        var ubr = Registry.GetValue(nt, "UBR", null)?.ToString();
        var product = Registry.GetValue(nt, "ProductName", "?")?.ToString() ?? "?";
        // Windows 11 still names itself "Windows 10" in this place; the build number tells them apart
        if (int.TryParse(build, out int b) && b >= 22000 && product.Contains("Windows 10")) product = product.Replace("Windows 10", "Windows 11");
        Log.Write($"machine: {product} {Registry.GetValue(nt, "DisplayVersion", "")}, build {build}{(ubr != null ? "." + ubr : "")}, " +
                  $"{Environment.ProcessorCount} logical processors, {Math.Round(Memory() / 1073741824.0, 1)} GB memory");
        Log.Write($"machine: graphics {Screens.Cards()}");
        Log.Write($"machine: screens {string.Join("; ", screens)}");
    }

    static ulong Memory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref m) ? m.ullTotalPhys : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    /// Processor seconds of the app and all its processes; `pids` gets who they are.
    public double Cpu(out List<(int pid, string exe)> pids)
    {
        pids = Native.Tree(root);
        double sum = 0;
        foreach (var (pid, _) in pids)
            try { using var p = Process.GetProcessById(pid); sum += p.TotalProcessorTime.TotalSeconds; }
            catch { }
        return sum;
    }

    /// Processor seconds per process now, to subtract from later (frame cost).
    public Dictionary<int, double> CpuByPid()
    {
        var d = new Dictionary<int, double>();
        foreach (var (pid, _) in Native.Tree(root))
            try { using var p = Process.GetProcessById(pid); d[pid] = p.TotalProcessorTime.TotalSeconds; } catch { }
        return d;
    }

    /// Processor seconds spent since `before` by the processes alive now (new ones count whole).
    public static double CpuSince(Dictionary<int, double> before, Dictionary<int, double> now) =>
        now.Sum(kv => kv.Value - before.GetValueOrDefault(kv.Key, 0));

    /// One line: memory, processor since the last line, graphics card.
    public void Write(string why)
    {
        var now = DateTime.UtcNow;
        double cpu = Cpu(out var pids);
        long ws = 0, priv = 0;
        var names = new List<string>();
        foreach (var (pid, exe) in pids)
            try { using var p = Process.GetProcessById(pid); ws += p.WorkingSet64; priv += p.PrivateMemorySize64; names.Add(Path.GetFileNameWithoutExtension(exe)); }
            catch { }
        double secs = Math.Max(1, (now - lastAt).TotalSeconds);
        double core = 100 * (cpu - lastCpu) / secs;
        lastAt = now; lastCpu = cpu;
        var gpu = Gpu(pids.Select(p => p.pid).ToHashSet());
        Log.Write($"measure ({why}): memory {ws / 1048576} MB (own {priv / 1048576} MB) in {pids.Count} processes " +
                  $"[{string.Join(", ", names.GroupBy(n => n).Select(g => g.Count() > 1 ? $"{g.Key}×{g.Count()}" : g.Key))}], " +
                  $"processor {core:F2} % of one core ({core / Environment.ProcessorCount:F2} % of all) over {secs / 60:F1} min, " +
                  $"processor time since start {cpu:F1} s, {gpu}{(Extra != null ? "; " + Extra() : "")}");
    }

    /// The graphics card's load caused by the app over the same span, and its memory there.
    string Gpu(HashSet<int> pids)
    {
        if (!gpuReady) return "graphics card load unknown";
        try
        {
            if (PdhCollectQueryData(query) != 0) return "graphics card load unknown";
            // like Task Manager: per kind of engine (3D, copy, video …) the sum of the app's processes; the busiest kind
            var byKind = new Dictionary<string, double>();
            foreach (var (name, value) in Items(gpuEngine))
            {
                if (!Mine(name, pids)) continue;
                var kind = name.Contains("engtype_") ? name[(name.IndexOf("engtype_") + 8)..] : "?";
                byKind[kind] = byKind.GetValueOrDefault(kind) + value;
            }
            double dedicated = Items(gpuDedicated).Where(i => Mine(i.name, pids)).Sum(i => i.value);
            double shared = Items(gpuShared).Where(i => Mine(i.name, pids)).Sum(i => i.value);
            var busiest = byKind.Count == 0 ? "0" : string.Join(", ", byKind.Where(k => k.Value >= 0.005).OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value:F2} %"));
            return $"graphics card {(busiest.Length == 0 ? "0 %" : busiest)}, its memory {dedicated / 1048576:F0} MB own + {shared / 1048576:F0} MB shared";
        }
        catch (Exception e) { return $"graphics card load unknown ({e.Message})"; }
    }

    static bool Mine(string instance, HashSet<int> pids)
    {
        // instances are named pid_1234_luid_…
        int i = instance.IndexOf("pid_");
        if (i < 0) return false;
        int j = i + 4, k = j;
        while (k < instance.Length && char.IsDigit(instance[k])) k++;
        return int.TryParse(instance[j..k], out int pid) && pids.Contains(pid);
    }

    static List<(string name, double value)> Items(IntPtr counter)
    {
        var list = new List<(string, double)>();
        if (counter == IntPtr.Zero) return list;
        uint size = 0;
        int r = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out uint count, IntPtr.Zero);
        if (r != PDH_MORE_DATA || size == 0) return list;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, buf) != 0) return list;
            // PDH_FMT_COUNTERVALUE_ITEM: name pointer, then { status, padding, double }
            int item = IntPtr.Size + 16;
            for (int n = 0; n < count; n++)
            {
                var at = buf + n * item;
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(at)) ?? "";
                int status = Marshal.ReadInt32(at + IntPtr.Size);
                if (status != 0 && status != 1) continue;   // valid or new data
                double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(at + IntPtr.Size + 8));
                list.Add((name, value));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    public void Dispose()
    {
        if (query != IntPtr.Zero) PdhCloseQuery(query);
        query = IntPtr.Zero;
    }
}
