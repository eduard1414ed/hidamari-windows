using System.Runtime.InteropServices;
using System.Text;

namespace Hidamari;

/// Whether this Hidamari runs from its MSIX package (installed from the Microsoft Store, Package/AppxManifest.xml) or
/// as a plain Hidamari.exe (the builds for friends and the checks of stages 1–4). Three things differ:
/// - where its files are: the package's own folder (%LOCALAPPDATA%\Packages\EdTsarionov.Hidamari_…\LocalState),
///   which Windows removes with the app, instead of %LOCALAPPDATA%\Hidamari;
/// - Launch at login: the package's StartupTask instead of the Run key (LoginItem.cs);
/// - after a Store update Windows starts it again (RegisterApplicationRestart).
static class Package
{
    /// The package's family name (EdTsarionov.Hidamari_…), null for a plain Hidamari.exe.
    public static string? FamilyName { get; } = ReadFamilyName();

    public static bool IsPackaged => FamilyName != null;

    /// The package's version (1.0.0.0), null for a plain Hidamari.exe.
    public static string? Version { get; } = IsPackaged ? ReadVersion() : null;

    /// The folder for settings, journals, the card's page and the web engine's data.
    public static string DataFolder => IsPackaged
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", FamilyName!, "LocalState")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hidamari");

    const int APPMODEL_ERROR_NO_PACKAGE = 15700, ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetCurrentPackageFamilyName(ref int length, StringBuilder? name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetCurrentPackageFullName(ref int length, StringBuilder? name);

    static string? ReadFamilyName()
    {
        try
        {
            int len = 0;
            int r = GetCurrentPackageFamilyName(ref len, null);
            if (r == APPMODEL_ERROR_NO_PACKAGE || r != ERROR_INSUFFICIENT_BUFFER) return null;
            var sb = new StringBuilder(len);
            return GetCurrentPackageFamilyName(ref len, sb) == 0 ? sb.ToString() : null;
        }
        catch { return null; }   // Windows before 8 has no packages
    }

    static string? ReadVersion()
    {
        try
        {
            int len = 0;
            if (GetCurrentPackageFullName(ref len, null) != ERROR_INSUFFICIENT_BUFFER) return null;
            var sb = new StringBuilder(len);
            if (GetCurrentPackageFullName(ref len, sb) != 0) return null;
            // EdTsarionov.Hidamari_1.0.0.0_x64__publisherid
            var parts = sb.ToString().Split('_');
            return parts.Length > 1 ? parts[1] : null;
        }
        catch { return null; }
    }

    const int RESTART_NO_CRASH = 1, RESTART_NO_HANG = 2, RESTART_NO_REBOOT = 8;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int RegisterApplicationRestart(string? commandLine, int flags);

    /// The Store closes the app to put a new version in. With this, Windows starts it again when the update is in (and
    /// only then: not after a crash, a hang or a restart of the computer, where Launch at login decides). Windows does
    /// it only for an app that has run at least a minute.
    public static void RestartAfterUpdate()
    {
        if (!IsPackaged) return;
        int hr = RegisterApplicationRestart(null, RESTART_NO_CRASH | RESTART_NO_HANG | RESTART_NO_REBOOT);
        Log.Write(hr == 0 ? "package: Windows starts Hidamari again after an update" : $"package: could not ask to be started again after an update (0x{hr:X8})");
    }
}
