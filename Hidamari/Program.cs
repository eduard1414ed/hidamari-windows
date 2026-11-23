namespace Hidamari;

/// Windows. Starting Hidamari puts the wall under the desktop icons on every screen and an icon in the tray.
/// Only one runs at a time: starting it again while it runs opens the card of the one running. Quit is on the card and
/// in the tray icon's menu.
static class Program
{
    const string MutexName = @"Local\Hidamari";
    const string AgainName = @"Local\Hidamari.Again";
    public static string Version => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0";

    [STAThread]
    static int Main()
    {
        using var mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            // tell the running one; it may still be starting
            for (int i = 0; i < 20; i++)
            {
                if (EventWaitHandle.TryOpenExisting(AgainName, out var ev))
                {
                    // the running one may bring its card to the front: this start was the person's own
                    Native.AllowSetForegroundWindow(Native.ASFW_ANY);
                    ev.Set();
                    ev.Dispose();
                    return 0;
                }
                Thread.Sleep(250);
            }
            return 0;
        }
        using var again = new EventWaitHandle(false, EventResetMode.AutoReset, AgainName);
        Log.Start();
        Log.Write($"Hidamari {typeof(Program).Assembly.GetName().Version} for Windows, pid {Environment.ProcessId}, {Environment.ProcessPath}" +
                  (Package.IsPackaged ? $", package {Package.FamilyName} {Package.Version}" : ", not packaged"));
        Log.Write($"files: {Log.Folder}");
        App? app = null;
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write($"error: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => app?.Crash(e.ExceptionObject?.ToString() ?? "unknown");
        ApplicationConfiguration.Initialize();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        app = new App(again);
        Application.Run(app);
        return app.ExitCode;
    }
}
