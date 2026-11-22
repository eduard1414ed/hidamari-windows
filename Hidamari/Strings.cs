namespace Hidamari;

/// Every text of the Windows app a person can see or hear with a screen reader. The same texts as on the Mac
/// (shared/Strings.swift): each name here is the Mac's name with a capital first letter, and tools/check-strings.py
/// stops the build when a text differs from the Mac's. A text not approved yet is marked TEMP.
static class Strings
{
    /// The app's name (SPEC.md), shown when the pointer rests on the tray icon. The Mac's `menuBarIcon`.
    public const string Name = "Hidamari";

    // ---------- seen on the card ----------
    public const string FollowSun = "Live sun";
    public static readonly string[] Scenes = { "Dapples", "Window", "Blinds", "Water" };
    public const string LiveWeather = "Live weather";
    /// The button at the bottom of the card and the tray menu's only item.
    public const string Quit = "Quit";

    // settings (behind the gear) and the first launch's question
    public const string LaunchAtLogin = "Launch at login";
    public const string Screens = "Screens";
    public const string Desktops = "Desktops";
    public const string All = "All";
    public const string MainScreen = "Main";
    public const string ThisDesktop = "This one";
    public const string FirstLaunchQuestion = "Open Hidamari when you log in?";
    public const string NotNow = "Not now";
    public const string OpenAtLogin = "Open at login";
    /// Light from: the window and the blinds drawn mirrored (the Mac's update 1.3)
    public const string LightFrom = "Light from";
    public const string LightLeft = "Left";
    public const string LightRight = "Right";
    /// the city search: its first line goes back to finding the place by itself
    public const string CurrentLocation = "Current Location";
    public const string SearchCity = "Search for a city";
    /// TEMP: one of the ways offered when Windows does not give the place: a button to Windows' location settings
    public const string OpenLocationSettings = "Open location settings";
    /// TEMP: where the live weather comes from (MET Norway asks for it), small, in the settings
    public const string CreditWeather = "Data from MET Norway";
    /// TEMP: where the list of cities comes from (GeoNames asks for it), in the settings
    public const string CreditPlaces = "Places from GeoNames";

    // ---------- only heard with a screen reader ----------
    public const string TimeOfDay = "Time of day";
    public static readonly Dictionary<int, string> Plants = new() { [0] = "Bush", [2] = "Branch", [5] = "Palm" };
    public static readonly string[] Walls = { "Beige", "Grey-blue", "Mint", "Taupe", "Umber", "Clay" };
    public const string Location = "Location";
    public const string LocationUnavailable = "Location unavailable";
    public const string Back = "Back";
    public const string Settings = "Settings";
    public const string WeatherClear = "Clear";
    public const string WeatherCloudy = "Cloudy";
    public const string WeatherRain = "Rain";
    public const string WeatherFrost = "Frost";
    public const string OwnWall = "Own wall";
    public const string Open = "Open";
    public const string WallHandle = "Wall colour";
    public const string ShadeHandle = "Shade colour";
    public const string OwnShade = "Own shade";
    public const string DefaultShade = "Default shade";
    public const string Texture = "Texture";
    public const string LightStrength = "Light";

    /// All of them for the card's page, named as on the Mac (followSun, scenes, …).
    public static Dictionary<string, object> ForCard()
    {
        var all = new Dictionary<string, object>();
        foreach (var f in typeof(Strings).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            var key = char.ToLowerInvariant(f.Name[0]) + f.Name[1..];
            var value = f.GetValue(null);
            if (value is Dictionary<int, string> d) all[key] = d.ToDictionary(p => p.Key.ToString(), p => p.Value);
            else if (value != null) all[key] = value;
        }
        return all;
    }
}
