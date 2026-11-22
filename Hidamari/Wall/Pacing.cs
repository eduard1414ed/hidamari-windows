using System.Runtime.InteropServices;
using static Hidamari.Native;

namespace Hidamari;

/// How many frames a second the motion gets: 30 on mains power, 15 on battery or with Windows' battery saver or energy
/// saver on (Ed, 22 November 2026; MotionPace on the Mac). Windows tells when any of it changes (Messages); the checks
/// can pretend (`Pretend`).
static class Power
{
    public static readonly Guid AcDc = new("5d3e9a59-e9D5-4b00-a6bd-ff34ff516548");
    public static readonly Guid BatterySaver = new("e00958c0-c213-4ace-ac77-fecced2eeea5");
    public static readonly Guid EnergySaver = new("550E8400-E29B-41D4-A716-446655440000");

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

    /// Energy saver of Windows 11 (0 off, 1 on, 2 on with more savings), as Windows last told it.
    public static int EnergySaverLevel = -1;
    /// For the checks: "mains", "battery", "saver", or null for the real state.
    public static string? Pretend;

    public static (int fps, string why) Wanted()
    {
        switch (Pretend)
        {
            case "mains": return (30, "on mains power (pretended by the check)");
            case "battery": return (15, "on battery (pretended by the check)");
            case "saver": return (15, "energy saver on (pretended by the check)");
        }
        bool battery = false, saver = false;
        if (GetSystemPowerStatus(out var s)) { battery = s.ACLineStatus == 0; saver = s.SystemStatusFlag == 1; }
        if (EnergySaverLevel > 0) saver = true;
        if (saver) return (15, battery ? "on battery, energy saver on" : "energy saver on");
        if (battery) return (15, "on battery");
        return (30, "on mains power");
    }
}

/// Waits for the screen's next refresh (vertical blank), the way the graphics driver tells it. Where that is not
/// possible (no driver that tells, or the wait returns at once), a high-resolution timer at the screen's rate stands in.
sealed class VBlank : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct OPENADAPTERFROMHDC { public IntPtr hDc; public uint hAdapter; public uint LuidLow; public int LuidHigh; public uint VidPnSourceId; }
    [StructLayout(LayoutKind.Sequential)]
    struct WAITFORVBLANK { public uint hAdapter; public uint hDevice; public uint VidPnSourceId; }
    [StructLayout(LayoutKind.Sequential)]
    struct CLOSEADAPTER { public uint hAdapter; }
    [DllImport("gdi32.dll")] static extern int D3DKMTOpenAdapterFromHdc(ref OPENADAPTERFROMHDC a);
    [DllImport("gdi32.dll")] static extern int D3DKMTWaitForVerticalBlankEvent(ref WAITFORVBLANK w);
    [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(ref CLOSEADAPTER c);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateDC(string? driver, string device, string? output, IntPtr mode);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWaitableTimerExW(IntPtr attr, string? name, uint flags, uint access);
    [DllImport("kernel32.dll")] static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr routine, IntPtr arg, bool resume);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2, TIMER_ALL_ACCESS = 0x1F0003;

    WAITFORVBLANK wait;
    bool driver;
    IntPtr timer;
    readonly double timerHz;
    int quick;
    public string How { get; private set; }

    public VBlank(ScreenInfo screen)
    {
        timerHz = screen.Hz > 1 ? screen.Hz : 60;
        How = "";
        try
        {
            var dc = CreateDC(null, screen.Name, null, IntPtr.Zero);
            if (dc != IntPtr.Zero)
            {
                var o = new OPENADAPTERFROMHDC { hDc = dc };
                int r = D3DKMTOpenAdapterFromHdc(ref o);
                DeleteDC(dc);
                if (r == 0)
                {
                    wait = new WAITFORVBLANK { hAdapter = o.hAdapter, VidPnSourceId = o.VidPnSourceId };
                    driver = true;
                    How = "the screen's refresh";
                }
                else How = $"timer (the driver does not tell the refresh: 0x{r:X8})";
            }
            else How = "timer (no device context for the screen)";
        }
        catch (Exception e) { How = $"timer ({e.Message})"; }
        if (!driver) StartTimer();
    }

    void StartTimer()
    {
        driver = false;
        timer = CreateWaitableTimerExW(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        if (timer == IntPtr.Zero) timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TIMER_ALL_ACCESS);
        if (!How.StartsWith("timer")) How = "timer";
        How += $" at {timerHz:F0} Hz";
    }

    /// Blocks until the next refresh.
    public void Next()
    {
        if (driver)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int r = D3DKMTWaitForVerticalBlankEvent(ref wait);
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            // a driver that answers at once (or fails) would make the pacing spin: the timer takes over
            if (r != 0 || ms < 0.5) quick++; else quick = 0;
            if (quick < 20) return;
            Log.Write($"animation: the screen's refresh is not told properly ({(r != 0 ? $"0x{r:X8}" : "returns at once")}), a timer takes over");
            Close();
            How = "timer (the refresh returned at once)";
            StartTimer();
        }
        long due = -(long)(10_000_000 / timerHz);   // relative, in 100 ns
        if (timer != IntPtr.Zero && SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)) WaitForSingleObject(timer, 1000);
        else Thread.Sleep((int)Math.Max(1, 1000 / timerHz));
    }

    void Close()
    {
        if (!driver) return;
        var c = new CLOSEADAPTER { hAdapter = wait.hAdapter };
        D3DKMTCloseAdapter(ref c);
        driver = false;
    }

    public void Dispose()
    {
        Close();
        if (timer != IntPtr.Zero) { CloseHandle(timer); timer = IntPtr.Zero; }
    }
}
