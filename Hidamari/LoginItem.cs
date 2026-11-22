using Microsoft.Win32;

namespace Hidamari;

/// Launch at login: a value "Hidamari" in the person's own Run key (HKCU\Software\Microsoft\Windows\CurrentVersion\Run)
/// with the app's path. No administrator rights, nothing for other people on the computer.
///
/// Windows lets the person turn such an entry off in Task Manager → Startup apps; it then keeps the entry and marks it
/// off in StartupApproved\Run. The switch shows that honestly as off. Turning the switch on again clears that mark, so
/// the app really opens at the next login.
static class LoginItem
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const string ValueName = "Hidamari";

    static string Command => $"\"{Environment.ProcessPath}\"";

    /// The entry is there and Task Manager has not turned it off.
    public static bool IsOn => Entry() != null && !TurnedOffInTaskManager();

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

    public static void Set(bool on, string why)
    {
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

    /// At start: the app may have been moved since the entry was written. The entry then points at the new place.
    public static void FollowMove()
    {
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
