using System.Runtime.Versioning;
using Windows.Devices.Geolocation;

namespace Hidamari;

/// Where the computer is, from Windows' own location service (Windows.Devices.Geolocation), once when asked: at
/// launch, after waking up, after the network changed, when Current Location is chosen. Never followed continuously
/// (LocationModel on the Mac). Windows itself asks the person for permission; nothing here goes around it.
[SupportedOSPlatform("windows10.0.17763.0")]
sealed class Locator
{
    public enum Outcome { Located, NoPermission, Failed }

    public sealed record Fix(Outcome Outcome, double Lat, double Lng, double AccuracyMetres, string Why);

    /// One fix at city precision (3 km, as kCLLocationAccuracyThreeKilometers on the Mac): the cheapest Windows has,
    /// usually Wi-Fi or the network, never a long search. A fix up to 10 minutes old will do.
    public static async Task<Fix> Locate()
    {
        try
        {
            // the first call shows Windows' own question; afterwards it answers at once
            var access = await Geolocator.RequestAccessAsync();
            if (access != GeolocationAccessStatus.Allowed)
                return new(Outcome.NoPermission, 0, 0, 0, access == GeolocationAccessStatus.Denied
                    ? "no permission (location is off in Windows' settings, or not allowed for desktop apps)"
                    : "Windows did not say whether location is allowed");
            var g = new Geolocator { DesiredAccuracyInMeters = 3000 };
            var pos = await g.GetGeopositionAsync(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(30));
            var c = pos.Coordinate;
            return new(Outcome.Located, c.Point.Position.Latitude, c.Point.Position.Longitude, c.Accuracy, $"from {c.PositionSource}");
        }
        catch (UnauthorizedAccessException) { return new(Outcome.NoPermission, 0, 0, 0, "no permission (Windows refused)"); }
        catch (Exception e)
        {
            // 0x80004004 "aborted": the 30 seconds ran out; others: no location device or service
            return new(Outcome.Failed, 0, 0, 0, $"{e.GetType().Name}: {e.Message.Split('\n')[0].Trim()} (0x{e.HResult:X8})");
        }
    }

    /// Windows' page Settings → Privacy & security → Location.
    public static void OpenSettings()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:privacy-location") { UseShellExecute = true }); }
        catch (Exception e) { Log.Write($"place: could not open Windows' location settings ({e.Message})"); }
    }
}
