namespace HidamariProbe;

/// The probe's journal: %LOCALAPPDATA%\HidamariProbe\probe.log, started anew at every launch.
static class Log
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HidamariProbe");
    public static string FilePath => Path.Combine(Folder, "probe.log");
    static readonly object gate = new();

    public static void Start()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, "");
        Write($"Hidamari probe {typeof(Log).Assembly.GetName().Version}, Windows {Environment.OSVersion.Version}, " +
              $"build {Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", "?")}, " +
              $"product {Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName", "?")}, " +
              $"pid {Environment.ProcessId}");
    }

    public static void Write(string line)
    {
        var text = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {line}{Environment.NewLine}";
        lock (gate)
        {
            try { File.AppendAllText(FilePath, text); } catch { /* the journal never stops the wall */ }
        }
    }
}
