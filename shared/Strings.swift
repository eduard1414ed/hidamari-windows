// Every text the user can see or hear lives here.
// All of them approved by Ed on 18 November 2026 (stage 8). A new text that Ed has not approved yet is marked TEMP.

enum Strings {
    // MARK: Seen in the panel

    /// the switch under the time slider. On: the wall shows the current moment.
    static let followSun = "Live sun"
    /// names under the four scene pictures, in the prototype's order (0–3).
    static let scenes = ["Dapples", "Window", "Blinds", "Water"]
    /// Approved by Ed: the switch for the real weather.
    static let liveWeather = "Live weather"
    /// Approved by Ed (18 November 2026, update 1.1): the switch of the live motion, third under Live weather.
    /// Also what VoiceOver says for it.
    static let animation = "Animation"
    /// first line of the city search, goes back to finding the Mac's place by itself.
    /// Also the place's name when the Mac was located but the town's name is not known (offline).
    static let currentLocation = "Current Location"
    /// grey text in the empty city search field.
    static let searchCity = "Search for a city"
    /// the button at the bottom of the panel, quits the app.
    static let quit = "Quit"

    // Settings (behind the gear) and the first launch's question, approved by Ed on 18 November 2026
    static let launchAtLogin = "Launch at login"
    static let screens = "Screens"
    static let desktops = "Desktops"
    static let all = "All"
    static let mainScreen = "Main"
    static let thisDesktop = "This one"
    static let firstLaunchQuestion = "Open Hidamari when you log in?"
    static let notNow = "Not now"
    static let openAtLogin = "Open at login"
    /// under Launch at login, when macOS waits for Ed to allow it; a click opens System Settings → Login Items.
    static let allowInSystemSettings = "Allow in System Settings"

    // Light from in the settings (update 1.3): the window and the blinds drawn mirrored. Approved by Ed on 21 November 2026
    // (first "Window side", which read like the side of an app's window).
    static let lightFrom = "Light from"
    /// the two halves under it
    static let lightLeft = "Left"
    static let lightRight = "Right"

    // The screen saver block at the bottom of the settings (update 1.2), approved by Ed on 18 November 2026
    /// the caption above the button
    static let screenSaver = "Screensaver"
    /// the button while Hidamari is not installed as a screen saver: macOS then asks whether to install it
    static let addScreenSaver = "Add screen saver"
    /// the button once it is installed: opens System Settings → Wallpaper
    static let chooseScreenSaver = "Choose in System Settings"

    // MARK: Only heard with VoiceOver (nothing on screen)

    /// The menu bar icon: the app's name, chosen by Ed on 18 November 2026.
    static let menuBarIcon = "Hidamari"
    /// the arc of the sun, the panel's time slider.
    static let timeOfDay = "Time of day"
    /// the three plant pictures, for plants 0, 2, 5 of the prototype.
    static let plants = [0: "Bush", 2: "Branch", 5: "Palm"]
    /// the six wall colours, in the prototype's order.
    static let walls = ["Beige", "Grey-blue", "Mint", "Taupe", "Umber", "Clay"]
    /// the place line in the panel, before the place's name.
    static let location = "Location"
    /// the crossed-out arrow next to the place when the Mac could not be located.
    static let locationUnavailable = "Location unavailable"
    /// the arrow that leaves the city search, and the settings.
    static let back = "Back"
    /// the gear next to Quit, opens the settings.
    static let settings = "Settings"
    /// the four weather icons next to the Live weather switch while it is off.
    static let weatherClear = "Clear"
    static let weatherCloudy = "Cloudy"
    static let weatherRain = "Rain"
    static let weatherFrost = "Frost"
    /// the rainbow button under the wall colours, opens the own-wall picker.
    static let ownWall = "Own wall"
    /// said after "Own wall" while the picker is open.
    static let open = "Open"
    /// the big handle on the picker's field, the wall's colour.
    static let wallHandle = "Wall colour"
    /// the small handle on the picker's field, the shade's own colour.
    static let shadeHandle = "Shade colour"
    /// "+" under the field, adds an own shade colour.
    static let ownShade = "Own shade"
    /// "−" under the field, the shade works itself out again.
    static let defaultShade = "Default shade"
    /// the wave, the wall's texture.
    static let texture = "Texture"
    /// the dial, the strength of light.
    static let lightStrength = "Light"

    // MARK: The desktop widget (update 1.3). Approved by Ed on 20 November 2026.

    /// The two widgets in the widget gallery: the sun's path, small and medium, and the day's light alone, small.
    /// Each with the line under it.
    static let widgetGraphName = "Graph"
    static let widgetGraphDescription = "Today's weather and the sun's path."
    static let widgetSimpleName = "Simple"
    static let widgetSimpleDescription = "How long the day is today."
    /// Edit Widget: the setting and its two choices. The widget's settings need these as written words in
    /// Sources/HidamariWidget/WidgetSettings.swift too; scripts/build.sh checks the two places say the same.
    static let widgetBackground = "Background"
    static let widgetGlass = "Glass"
    static let widgetWall = "Scene"
    /// The caption over the day's light, shown in capitals (Ed approved it, 20 November 2026).
    static let widgetDaylight = "Daylight"
    /// The wallpaper sign above the colours, for VoiceOver: on or off. Off, the app only feeds the widget (Ed, 20 November 2026)
    static let wallpaper = "Wallpaper"

    // MARK: Shown by macOS

    /// the reason macOS shows when it asks whether the app may use the Mac's location.
    /// The same text must be in Support/Info.plist under NSLocationUsageDescription (scripts/build.sh checks it).
    static let locationUsage = "So the light on your wall follows the real sun in your city."
}
