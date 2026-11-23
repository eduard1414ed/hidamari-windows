using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hidamari;

/// Everything the person chose, kept in settings.json in the app's own folder (Package.DataFolder). The same choices as on the Mac
/// (AppSettings.swift, WeatherModel, SettingsView.swift), with the same defaults on a first launch.
///
/// A broken or incomplete file never stops the app: what can be read is kept, the rest takes its default, the journal
/// says which, and the broken file is kept beside as settings.broken.json for a look later.
sealed class Settings
{
    public const int Version = 1;
    public const int OwnWall = -1;
    public static string FilePath => Path.Combine(Log.Folder, "settings.json");

    /// 0 dapples, 1 window, 2 blinds, 3 water
    public int Scene = 0;
    /// 0–5 an approved wall, -1 the own wall
    public int Wall = 0;
    public CustomWall Custom = CustomWall.Standard;
    /// the window scene's plant: 0 bush, 2 branch, 5 palm
    public int Plant = 2;
    /// Live sun: the wall shows the real moment. Off, it shows `PinnedMinutes` of each day.
    public bool FollowsSun = true;
    public double PinnedMinutes = 720;
    /// the weather by hand while Live weather is off: clear, cloudy, rain, frost
    public string ManualWeather = "clear";
    /// "all" or "main"
    public string Screens = "all";
    /// Light from: Right draws the window and the blinds mirrored (windowOnRight on the Mac). Not in the files of
    /// stage 2: missing means Left, and that is no fault of the file.
    public bool WindowOnRight;
    /// Where the wall's sun is (LocationModel on the Mac): "automatic" (the computer is located; the first launch's
    /// choice) or "chosen" (a city from the search). Both places are kept: going back to Current Location shows the last
    /// located place at once. Not in the files of stage 2, where the place was always Berlin: missing is automatic.
    public string PlaceMode = "automatic";
    public Place? ChosenPlace;
    public Place? AutoPlace;
    /// Live weather (WeatherModel.isOn on the Mac): on at the first launch, as on the Mac. Stage 2 could not switch it
    /// on, so a stage 2 file says nothing about it: missing is on.
    public bool LiveWeather = true;
    /// Animation (the Mac's update 1.1): the dapples and the water move. Off at the first launch (Ed, 22 November 2026);
    /// the files of stage 3 say nothing about it: missing is off.
    public bool Animates;
    /// Which drawing the walls use: "direct3d" (the graphics card, the default) or "web" (the web engine of stages 1–3).
    /// Hidden: no switch on the card, as RendererChoice on the Mac. Set by starting the app with HIDAMARI_RENDERER=web
    /// (or direct3d), which is remembered.
    public string Renderer = "direct3d";

    /// The place the wall shows now.
    public Place Place => PlaceMode == "chosen" && ChosenPlace != null ? ChosenPlace : AutoPlace ?? Place.Berlin;
    /// the first launch's question was answered (or the person chose in the settings before answering)
    public bool FirstLaunchAnswered;

    public static readonly string[] Weathers = { "clear", "cloudy", "rain", "frost" };
    public static readonly int[] PlantChoices = { 0, 2, 5 };
    public static readonly string[] Renderers = { "direct3d", "web" };

    /// Reads the file. Missing: a first launch, all defaults.
    public static Settings Load(out bool firstLaunch)
    {
        var s = new Settings();
        firstLaunch = false;
        string text;
        try
        {
            if (!File.Exists(FilePath)) { firstLaunch = true; Log.Write("settings: no settings yet (first launch), all as on a new Mac"); return s; }
            text = File.ReadAllText(FilePath);
        }
        catch (Exception e) { Log.Write($"settings: could not read the file ({e.Message}), using the defaults"); return s; }

        var problems = new List<string>();
        JsonObject? o = null;
        try { o = JsonNode.Parse(text) as JsonObject; } catch (Exception e) { problems.Add($"not readable ({e.Message.Split('\n')[0]})"); }
        if (o == null)
        {
            if (problems.Count == 0) problems.Add("not a settings file");
            KeepBroken(text, problems);
            return s;
        }
        JsonObject obj = o;
        T? Get<T>(string key, Func<JsonNode, T?> read, Func<T, bool>? ok = null) where T : struct
        {
            if (!obj.TryGetPropertyValue(key, out var n) || n == null) { problems.Add($"{key} missing"); return null; }
            try
            {
                var v = read(n);
                if (v is { } x && (ok == null || ok(x))) return v;
            }
            catch { }
            problems.Add($"{key} not valid ({n.ToJsonString()})");
            return null;
        }
        string? GetString(string key, string[] allowed)
        {
            if (!obj.TryGetPropertyValue(key, out var n) || n == null) { problems.Add($"{key} missing"); return null; }
            try { var v = n.GetValue<string>(); if (allowed.Contains(v)) return v; } catch { }
            problems.Add($"{key} not valid ({n.ToJsonString()})");
            return null;
        }
        s.Scene = Get("scene", n => (int?)n.GetValue<int>(), v => v is >= 0 and <= 3) ?? s.Scene;
        s.Wall = Get("wall", n => (int?)n.GetValue<int>(), v => v is >= -1 and <= 5) ?? s.Wall;
        s.Plant = Get("plant", n => (int?)n.GetValue<int>(), v => PlantChoices.Contains(v)) ?? s.Plant;
        s.FollowsSun = Get("followsSun", n => (bool?)n.GetValue<bool>()) ?? s.FollowsSun;
        s.PinnedMinutes = Get("pinnedMinutes", n => (double?)n.GetValue<double>(), v => double.IsFinite(v) && v >= 0 && v < 1440) ?? s.PinnedMinutes;
        s.ManualWeather = GetString("manualWeather", Weathers) ?? s.ManualWeather;
        s.Screens = GetString("screens", new[] { "all", "main" }) ?? s.Screens;
        s.FirstLaunchAnswered = Get("firstLaunchAnswered", n => (bool?)n.GetValue<bool>()) ?? true;
        if (obj.ContainsKey("windowOnRight")) s.WindowOnRight = Get("windowOnRight", n => (bool?)n.GetValue<bool>()) ?? false;
        if (obj.ContainsKey("animation")) s.Animates = Get("animation", n => (bool?)n.GetValue<bool>()) ?? false;
        if (obj.ContainsKey("renderer")) s.Renderer = GetString("renderer", Renderers) ?? "direct3d";
        if (obj.ContainsKey("liveWeather")) s.LiveWeather = Get("liveWeather", n => (bool?)n.GetValue<bool>()) ?? true;
        if (obj.ContainsKey("placeMode")) s.PlaceMode = GetString("placeMode", new[] { "automatic", "chosen" }) ?? "automatic";
        if (obj.ContainsKey("chosenPlace")) s.ChosenPlace = ReadPlace(obj["chosenPlace"], "chosenPlace", problems);
        if (obj.ContainsKey("autoPlace")) s.AutoPlace = ReadPlace(obj["autoPlace"], "autoPlace", problems);
        if (s.PlaceMode == "chosen" && s.ChosenPlace == null) s.PlaceMode = "automatic";
        if (o.TryGetPropertyValue("custom", out var c) && c is JsonObject co)
        {
            try
            {
                double N(string k) => co[k]!.GetValue<double>();
                (double, double)? shade = co["shade"] is JsonObject sh ? (sh["x"]!.GetValue<double>(), sh["y"]!.GetValue<double>()) : null;
                s.Custom = new CustomWall(N("L"), N("x"), N("y"), N("k"), N("rough"), shade).Clamped();
            }
            catch { problems.Add($"custom not valid ({c.ToJsonString()})"); }
        }
        else problems.Add("custom missing");
        if (problems.Count > 0) KeepBroken(text, problems);
        Log.Write($"settings: read, {s.Describe()}");
        return s;
    }

    static Place? ReadPlace(JsonNode? n, string key, List<string> problems)
    {
        if (n == null) return null;
        try
        {
            var p = new Place(n["name"]!.GetValue<string>(), n["country"]?.GetValue<string>() ?? "", n["lat"]!.GetValue<double>(),
                n["lng"]!.GetValue<double>(), n["tz"]!.GetValue<string>());
            if (double.IsFinite(p.Lat) && double.IsFinite(p.Lng) && Math.Abs(p.Lat) <= 90 && Math.Abs(p.Lng) <= 180 && p.Zone.Length > 0) return p;
        }
        catch { }
        problems.Add($"{key} not valid ({n.ToJsonString()})");
        return null;
    }

    static void KeepBroken(string text, List<string> problems)
    {
        Log.Write($"settings: the file is broken or incomplete: {string.Join(", ", problems)}; those take their defaults " +
                  $"(the file is kept as settings.broken.json)");
        try { File.WriteAllText(Path.Combine(Log.Folder, "settings.broken.json"), text); } catch { }
    }

    /// Writes the whole file anew: first beside, then in place, so a cut-off write never leaves half a file.
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.Folder);
            var json = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["version"] = Version,
                ["scene"] = Scene, ["wall"] = Wall, ["custom"] = Custom.ForPage(), ["plant"] = Plant,
                ["followsSun"] = FollowsSun, ["pinnedMinutes"] = PinnedMinutes, ["manualWeather"] = ManualWeather,
                ["screens"] = Screens, ["windowOnRight"] = WindowOnRight,
                ["liveWeather"] = LiveWeather, ["animation"] = Animates, ["renderer"] = Renderer, ["placeMode"] = PlaceMode,
                ["chosenPlace"] = ChosenPlace?.ForPage(), ["autoPlace"] = AutoPlace?.ForPage(),
                ["firstLaunchAnswered"] = FirstLaunchAnswered,
            }, new JsonSerializerOptions { WriteIndented = true });
            var temp = FilePath + ".new";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception e) { Log.Write($"settings: could not save ({e.Message})"); }
    }

    public string Describe() =>
        $"scene {Scene}, wall {Wall}, own wall L {Custom.L:F3} x {Custom.X:F3} y {Custom.Y:F3} k {Custom.K:F3} texture {Custom.Rough:F2} " +
        $"shade {(Custom.Shade is { } s ? $"{s.X:F3},{s.Y:F3}" : "none")}, plant {Plant}, live sun {(FollowsSun ? "on" : $"off at {Clock(PinnedMinutes)}")}, " +
        $"weather by hand {ManualWeather}, screens {Screens}, light from {(WindowOnRight ? "right" : "left")}, live weather {(LiveWeather ? "on" : "off")}, animation {(Animates ? "on" : "off")}, drawing {Renderer}, place {PlaceMode} ({Place.ForJournal}), first launch answered {FirstLaunchAnswered}";

    public static string Clock(double minutes)
    {
        int m = ((int)Math.Floor(minutes) % 1440 + 1440) % 1440;
        return $"{m / 60:D2}:{m % 60:D2}";
    }
}
