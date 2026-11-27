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
    /// in the film's order, the scene's number as on the Mac: the prototype's four, then the four of update 1.4 (Windows 1.1)
    public static readonly string[] Scenes = { "Dapples", "Window", "Blinds", "Water", "Prism", "Moon Gate", "Tree", "Glass Blocks" };
    public const string LiveWeather = "Live weather";
    /// the third switch, only at the scenes that move (dapples, water): the live motion (the Mac's update 1.1)
    public const string Animation = "Animation";
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
    // settings 1.4 (the Mac's design/panel/settings-v2): the caption at the top is Settings, now also seen
    /// at the bottom left, {0} is the app's version: "Ver 1.1.0". The Mac's settingsVersion(_:)
    public const string SettingsVersion = "Ver {0}";
    /// at the bottom right, opens Links.Author
    public const string MadeBy = "Made by Ed Tsarionov";
    /// the city search: its first line goes back to finding the place by itself
    public const string CurrentLocation = "Current Location";
    public const string SearchCity = "Search for a city";
    /// Windows only: when Windows does not give the place, the button to Windows' location settings in the city search
    public const string OpenLocationSettings = "Open location settings";
    /// Windows only: where the live weather and the list of cities come from (MET Norway and GeoNames ask for it),
    /// "MET Norway, GeoNames" as the settings' last line
    public const string CreditWeather = "MET Norway";
    /// Windows only: see CreditWeather
    public const string CreditPlaces = "GeoNames";

    // Windows only, when the WebView2 runtime is missing (the card needs it, the wall does not): a Windows dialog with
    // these texts and two buttons, WebView2Get (opens Microsoft's download) and NotNow. Approved by Ed on 24 November 2026.
    /// Windows only: the dialog's heading
    public const string WebView2Heading = "Hidamari needs Microsoft Edge WebView2 to open its card";
    /// Windows only: the dialog's text under the heading
    public const string WebView2Text = "The wall works without it. WebView2 is free from Microsoft and installs in a minute. Then click the Hidamari icon again.";
    /// Windows only: the dialog's button that opens Microsoft's download of WebView2
    public const string WebView2Get = "Get WebView2";

    // the support window after an update (the Mac's update 1.4, design/support/)
    /// above the heading, {0} is the app's version. The Mac's supportVersion(_:)
    public const string SupportVersion = "Hidamari {0}";
    public const string SupportHeading = "Help Hidamari keep growing";
    public const string SupportText = "Hidamari is free, with no ads and no tracking. If you enjoy it, you can help fund its development.";
    /// the wide button, opens Links.Donate
    public const string SupportButton = "Support and Buy me a coffee";
    public const string SupportThanks = "Thank you for being here.";
    public const string SupportDone = "Done";

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
    /// the stack at the end of the scene film that opens the second row, and the arrow under that row that folds it
    public const string AllScenes = "All scenes";
    public const string FewerScenes = "Fewer scenes";
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

/// The two links of the Mac's update 1.4 (shared/Strings.swift, Links), opened in the browser.
static class Links
{
    /// the support window's button: Ed's one Ko-fi page for all his projects
    public const string Donate = "https://ko-fi.com/tsarionov";
    /// "Made by Ed Tsarionov" in the settings: Ed's own site
    public const string Author = "https://tsarionov.com/";
}
