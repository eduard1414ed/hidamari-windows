import Foundation

// Every text the user can see or hear lives here.
// All of them approved by Ed on 18 November 2026 (stage 8). A new text that Ed has not approved yet is marked TEMP.

enum Strings {
    // MARK: Seen in the panel

    /// the switch under the time slider. On: the wall shows the current moment.
    static let followSun = "Live sun"
    /// names under the scene pictures, in the film's order (Scene.allCases, use Scene.index): the prototype's four,
    /// then the four of update 1.4 (approved by Ed with the update's task, 25 November 2026).
    static let scenes = ["Dapples", "Window", "Blinds", "Water", "Prism", "Moon Gate", "Tree", "Glass Blocks"]
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

    // Settings 1.4 (design/panel/settings-v2.*), approved by Ed on 25 November 2026. The caption at the top is
    // `settings` below, now also seen.
    /// at the bottom left, with the app's version: "Ver 1.4"
    static func settingsVersion(_ version: String) -> String { "Ver \(version)" }
    /// at the bottom right, opens `Links.author`
    static let madeBy = "Made by Ed Tsarionov"

    // MARK: The support window (update 1.4, design/support/), approved by Ed on 25 November 2026

    /// above the heading, with the app's version: "Hidamari 1.4"
    static func supportVersion(_ version: String) -> String { "Hidamari \(version)" }
    static let supportHeading = "Help Hidamari keep growing"
    static let supportText = "Hidamari is free, with no ads and no tracking. If you enjoy it, you can help fund its development."
    /// the wide button, opens `Links.donate`. Ed, 25 November 2026: the link goes to Ko-fi, so the mockup's
    /// "Support on Buy Me a Coffee" became this.
    static let supportButton = "Support and Buy me a coffee"
    static let supportThanks = "Thank you for being here."
    static let supportDone = "Done"

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
    /// the gear next to Quit, opens the settings. Since 1.4 also seen: the caption next to the back arrow.
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
    /// Approved by Ed (25 November 2026, update 1.4): VoiceOver's word for the stack at the end of the scene film,
    /// which opens and closes the second row; the mockup's own word (design/panel/more-scenes-stack.html).
    static let allScenes = "All scenes"
    /// Approved by Ed (25 November 2026, update 1.4): VoiceOver's word for the up arrow under the second row that folds it.
    static let fewerScenes = "Fewer scenes"
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

/// The two links of update 1.4, given by Ed on 25 November 2026. Opened in the browser.
enum Links {
    /// the support window's button: Ed's one Ko-fi page for all his projects
    static let donate = URL(string: "https://ko-fi.com/tsarionov")!
    /// "Made by Ed Tsarionov" in the settings: Ed's own site
    static let author = URL(string: "https://tsarionov.com/")!
}
