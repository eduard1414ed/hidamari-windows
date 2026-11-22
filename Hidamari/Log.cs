namespace Hidamari;

/// The journal: %LOCALAPPDATA%\Hidamari\hidamari.log. Each launch starts a new one; the last few launches are kept
/// as hidamari.1.log (the one before) … hidamari.4.log, so a journal sent after a restart still shows what happened.
/// A launch that runs for weeks never grows its journal past a few megabytes: the oldest half is dropped.
static class Log
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hidamari");
    public static string FilePath => Path.Combine(Folder, "hidamari.log");
    const int Kept = 4;
    const long MaxBytes = 4 << 20;
    static readonly object gate = new();
    static long written;

    public static void Start()
    {
        Directory.CreateDirectory(Folder);
        try
        {
            for (int i = Kept; i >= 1; i--)
            {
                var from = i == 1 ? FilePath : Path.Combine(Folder, $"hidamari.{i - 1}.log");
                var to = Path.Combine(Folder, $"hidamari.{i}.log");
                if (!File.Exists(from)) continue;
                File.Copy(from, to, overwrite: true);
            }
            File.WriteAllText(FilePath, "");
        }
        catch { /* the journal never stops the wall */ }
        written = 0;
    }

    public static void Write(string line)
    {
        var text = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {line}{Environment.NewLine}";
        lock (gate)
        {
            try
            {
                if (written > MaxBytes) Trim();
                File.AppendAllText(FilePath, text);
                written += text.Length;
            }
            catch { /* the journal never stops the wall */ }
        }
    }

    /// Keeps the start of the launch (what the machine is) and the newer half of the rest.
    static void Trim()
    {
        var lines = File.ReadAllLines(FilePath);
        var head = lines.Take(40);
        var tail = lines.Skip(Math.Max(40, lines.Length / 2));
        File.WriteAllLines(FilePath, head.Append($"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} journal: older lines dropped to keep it small").Concat(tail));
        written = new FileInfo(FilePath).Length;
    }
}
