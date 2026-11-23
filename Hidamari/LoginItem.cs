using Microsoft.Win32;
using Windows.ApplicationModel;

namespace Hidamari;

/// Launch at login, two ways:
///
/// **From the Store (the MSIX package):** the package's StartupTask "HidamariStartup" (Package/AppxManifest.xml).
/// Windows keeps its state; the person can also turn it off in Task Manager → Startup apps or Settings → Apps →
/// Startup, and then only the person can turn it on again there (Windows does not let the app). The switch shows that
/// honestly as off; turning it on then opens Settings → Apps → Startup, where Hidamari is in the list.
///
/// **A plain Hidamari.exe** (builds for friends, the checks): a value "Hidamari" in the person's own Run key
/// (HKCU\Software\Microsoft\Windows\CurrentVersion\Run) with the app's path. No administrator rights, nothing for
/// other people on the computer. Task Manager can turn such an entry off; it then keeps the entry and marks it off in
/// StartupApproved\Run. The switch shows that as off; turning the switch on again clears that mark.
static class LoginItem
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const string ValueName = "Hidamari";
    public const string TaskId = "HidamariStartup";

    static string Command => $"\"{Environment.ProcessPath}\"";

    /// The package's startup task as last read (Refresh), null before or when Windows would not say.
    static StartupTaskState? task;

    /// On and not turned off by the person (or by the organisation's policy).
    public static bool IsOn => Package.IsPackaged
        ? task is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy
        : Entry() != null && !TurnedOffInTaskManager();

    static string? Entry()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(ValueName) as string;
    }

    /// Task Manager keeps 12 bytes per entry: an odd first byte means "turned off".
    public static bool TurnedOffInTaskManager()
    {
        using var k = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        return k?.GetValue(ValueName) is byte[] b && b.Length > 0 && (b[0] & 1) == 1;
    }

    static string Describe(StartupTaskState? s) => s switch
    {
        StartupTaskState.Enabled => "on",
        StartupTaskState.Disabled => "off",
        StartupTaskState.DisabledByUser => "turned off by the person in Task Manager or Settings",
        StartupTaskState.DisabledByPolicy => "turned off by the organisation's policy",
        StartupTaskState.EnabledByPolicy => "on by the organisation's policy",
        null => "unknown",
        _ => s.ToString()!,
    };

    /// Reads the startup task's state again (the person may have changed it in Task Manager meanwhile). True if it changed.
    public static async Task<bool> Refresh(string why)
    {
        if (!Package.IsPackaged) return false;
        var before = task;
        try
        {
            var t = await StartupTask.GetAsync(TaskId);
            task = t.State;
        }
        catch (Exception e) { Log.Write($"login: could not read the startup task ({e.GetType().Name}: {e.Message})"); task = null; }
        if (before != task) Log.Write($"login: Launch at login {Describe(task)} ({why})");
        return before != task;
    }

    /// Turns Launch at login on or off; done when the new state is known.
    public static async Task Set(bool on, string why)
    {
        if (Package.IsPackaged) { await SetTask(on, why); return; }
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on)
            {
                k.SetValue(ValueName, Command, RegistryValueKind.String);
                using var a = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
                bool wasOff = a?.GetValue(ValueName) is byte[] b && b.Length > 0 && (b[0] & 1) == 1;
                a?.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Write($"login: Launch at login on ({why}), {Command}{(wasOff ? ", the Task Manager's off mark cleared" : "")}");
            }
            else
            {
                k.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Write($"login: Launch at login off ({why})");
            }
        }
        catch (Exception e) { Log.Write($"login: could not change Launch at login ({e.Message})"); }
    }

    static async Task SetTask(bool on, string why)
    {
        try
        {
            var t = await StartupTask.GetAsync(TaskId);
            var was = t.State;
            if (on)
            {
                if (was is StartupTaskState.Disabled)
                {
                    // a desktop app's request is granted at once, without a question from Windows
                    task = await t.RequestEnableAsync();
                    Log.Write($"login: Launch at login asked on ({why}): {Describe(task)}");
                }
                else if (was is StartupTaskState.DisabledByUser)
                {
                    // only the person can turn it on again, in Windows' own list
                    task = was;
                    Log.Write($"login: Launch at login was turned off by the person; Windows does not let the app turn it on, Settings → Apps → Startup opened ({why})");
                    OpenStartupSettings();
                }
                else
                {
                    task = was;
                    Log.Write($"login: Launch at login stays {Describe(was)} ({why})");
                }
            }
            else
            {
                if (was is StartupTaskState.Enabled) t.Disable();
                task = (await StartupTask.GetAsync(TaskId)).State;
                Log.Write($"login: Launch at login off ({why}): {Describe(task)}");
            }
        }
        catch (Exception e) { Log.Write($"login: could not change Launch at login ({e.GetType().Name}: {e.Message})"); }
    }

    /// Windows' list of apps that start at login (Settings → Apps → Startup).
    public static void OpenStartupSettings()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true }); }
        catch (Exception e) { Log.Write($"login: could not open Windows' startup settings ({e.Message})"); }
    }

    /// At start. The package: reads the task. A plain Hidamari.exe: it may have been moved since the entry was written;
    /// the entry then points at the new place.
    public static async Task Start()
    {
        if (Package.IsPackaged)
        {
            await Refresh("start");
            if (task == null) Log.Write("login: Launch at login unknown");
            return;
        }
        try
        {
            var entry = Entry();
            string state = entry == null ? "off" : TurnedOffInTaskManager() ? "turned off in Task Manager" : "on";
            if (entry != null && !string.Equals(entry, Command, StringComparison.OrdinalIgnoreCase))
            {
                using var k = Registry.CurrentUser.CreateSubKey(RunKey);
                k.SetValue(ValueName, Command, RegistryValueKind.String);
                Log.Write($"login: the app was moved, the entry now points here ({entry} → {Command})");
            }
            Log.Write($"login: Launch at login {state}");
        }
        catch (Exception e) { Log.Write($"login: could not read Launch at login ({e.Message})"); }
    }
}
