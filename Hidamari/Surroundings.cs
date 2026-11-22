using System.Net.NetworkInformation;
using System.Runtime;
using System.Text.Json;

namespace Hidamari;

/// The place and the weather the wall follows (LocationModel and WeatherModel on the Mac).
///
/// The place: the computer is located by Windows (with its permission) at launch, after waking up, after the network
/// changed, and when Current Location is chosen; never followed continuously. The point is named after the nearest
/// city of the app's own list and takes that city's time zone (Windows' own zone when the nearest city is far).
/// Or a city is chosen in the search, which runs on this computer only.
///
/// The weather: asked once an hour (a few minutes later at random, so that many copies do not ask together), at
/// launch, after waking up, after the network or the place changed; never while the screen is locked or the
/// computer sleeps (the wall is not seen then: asked once it is seen again). No answer for six hours, or a failed
/// request, makes the wall clear. With Live weather off nothing is asked at all.
sealed class Surroundings : IDisposable
{
    /// The nearest city names the place and gives its time zone if it is at most this far.
    const double NearKm = 50;

    readonly Settings settings;
    readonly Func<DateTimeOffset> now;
    readonly Action<string> redraw, changed;
    readonly Action push;
    readonly IWeatherSource source;
    readonly SynchronizationContext ui = SynchronizationContext.Current!;
    readonly string weatherFile = Path.Combine(Log.Folder, "weather.json");
    readonly System.Windows.Forms.Timer hourly = new();
    readonly System.Windows.Forms.Timer networkSoon = new() { Interval = 5000 };
    readonly System.Windows.Forms.Timer letCitiesGo = new() { Interval = 2 * 60_000 };
    readonly bool checkMode;

    WeatherReading? reading;
    bool lastFailed, asking, askAgain, locating, locateAgain, waitingToBeSeen, quiet;
    string? askAgainWhy, locateAgainWhy;
    DateTime lastLocate;
    Cities? cities;
    Task<Cities>? loading;
    List<CityMatch> found = new();
    bool networkUp = NetworkInterface.GetIsNetworkAvailable();

    /// The last answer from Windows' location service, null before the first.
    public Locator.Outcome? Located { get; private set; }
    /// Current Location is chosen and Windows does not give the place (no permission, no service).
    public bool LocationUnavailable => settings.PlaceMode == "automatic" && Located is Locator.Outcome.NoPermission or Locator.Outcome.Failed;
    public string SourceName => source.Name;
    /// Requests to the weather source since launch (whether or not an answer came).
    public int Asked { get; private set; }
    public TestSource? Test => source as TestSource;

    public Surroundings(Settings settings, Func<DateTimeOffset> now, Action<string> redraw, Action<string> changed, Action push, bool checkMode)
    {
        this.settings = settings; this.now = now; this.redraw = redraw; this.changed = changed; this.push = push;
        this.checkMode = checkMode;
        // MET's terms say the person who makes the app accepts them; until then, and always in the checks, the test source
        source = MetSource.Accepted && !checkMode ? new MetSource(Path.Combine(Log.Folder, "met.json")) : new TestSource();
        Zones.Note = s => Log.Write($"place: {s}");
        LoadWeather();
        hourly.Tick += (_, _) => { hourly.Stop(); Ask("the hourly request", scheduled: true); };
        networkSoon.Tick += (_, _) => { networkSoon.Stop(); OnNetworkSettled(); };
        letCitiesGo.Tick += (_, _) => { letCitiesGo.Stop(); LetCitiesGo(); };
        NetworkChange.NetworkAddressChanged += OnNetwork;
        NetworkChange.NetworkAvailabilityChanged += OnNetwork;
        Log.Write($"place: {settings.PlaceMode}, now {settings.Place.ForJournal}; live weather {(settings.LiveWeather ? "on" : "off")}, from the {source.Name}" +
                  (reading != null ? $", last answer {reading.Received.ToLocalTime():yyyy-MM-dd HH:mm} ({reading.Weather})" : ", no answer kept"));
    }

    /// Launch: the place first (if it is located), then the weather for it.
    public async void Start()
    {
        if (settings.PlaceMode == "automatic") await Locate("launch");
        Ask("launch");
    }

    /// What the wall shows: the live weather, or the one chosen by hand while Live weather is off.
    public WeatherNow ForWall(int scene) => settings.LiveWeather
        ? WeatherRules.Current(reading, lastFailed, settings.Place, now())
        : WeatherNow.Manual(settings.ManualWeather, scene);

    // ---------- the place ----------

    public async Task Locate(string why)
    {
        if (locating) { locateAgain = true; locateAgainWhy = why; return; }
        locating = true;
        lastLocate = DateTime.UtcNow;
        try
        {
            var ask = Locator.Locate();
            // Windows gives up after 30 seconds itself; this only keeps a stuck service from holding the place for ever
            var first = await Task.WhenAny(ask, Task.Delay(TimeSpan.FromSeconds(45)));
            var fix = first == ask ? ask.Result : new Locator.Fix(Locator.Outcome.Failed, 0, 0, 0, "no answer from Windows in 45 seconds");
            await Take(fix, why);
        }
        catch (Exception e) { Log.Write($"place: locating failed ({why}): {e.Message}"); }
        finally { locating = false; }
        if (locateAgain) { locateAgain = false; await Locate(locateAgainWhy ?? "again"); }
    }

    /// An answer of the location service (or, in the checks, a point given by the check).
    public async Task Take(Locator.Fix fix, string why)
    {
        var was = Located;
        Located = fix.Outcome;
        if (fix.Outcome != Locator.Outcome.Located)
        {
            Log.Write($"place: not located ({why}): {fix.Why}; the wall stays at {settings.Place.ForJournal}" +
                      (settings.PlaceMode == "automatic" ? ", the card says Location unavailable" : ""));
            if (was != fix.Outcome) push();
            return;
        }
        var list = await UseCities();
        var (near, km, _) = list.Nearest(fix.Lat, fix.Lng);
        var zone = km <= NearKm ? near.Zone : Zones.LocalName();
        // two decimals (about 1 km) are all the wall needs: the settings file never holds the exact point
        var place = new Place(near.Name, near.Country, Math.Round(fix.Lat, 2), Math.Round(fix.Lng, 2), zone);
        LetCitiesGoSoon();
        Log.Write($"place: located ({why}, {fix.Why}, within {fix.AccuracyMetres:F0} m): {place.ForJournal}; nearest city {near.Name} " +
                  $"{km:F0} km away{(km <= NearKm ? "" : $", too far for its time zone: the computer's own ({zone})")}");
        var old = settings.AutoPlace;
        if (old != null && old.Name == place.Name && old.Zone == place.Zone && old.Km(place) < 1)
        {
            if (was != Locator.Outcome.Located) push();
            return;
        }
        settings.AutoPlace = place;
        changed($"located place {place.ForJournal}");
        if (settings.PlaceMode != "automatic") return;
        push();
        redraw("the place changed");
        Ask("the place changed");
    }

    /// A city from the search.
    public void Choose(int index)
    {
        if (index < 0 || index >= found.Count) return;
        var p = found[index].Place;
        settings.ChosenPlace = p;
        settings.PlaceMode = "chosen";
        changed($"place chosen: {p.ForJournal}");
        push();
        redraw("a place was chosen");
        Ask("the place changed");
    }

    /// Current Location in the search: the last located place at once, then the computer is located anew.
    public async void ChooseCurrent()
    {
        bool was = settings.PlaceMode == "automatic";
        settings.PlaceMode = "automatic";
        if (!was)
        {
            changed($"place: Current Location ({(settings.AutoPlace?.ForJournal ?? "not located yet")})");
            push();
            redraw("Current Location was chosen");
        }
        await Locate("Current Location was chosen");
        Ask("the place changed");
    }

    /// The card opened while the place is unavailable: perhaps the person has just allowed it in Windows.
    public void OnCardOpened()
    {
        if (LocationUnavailable && DateTime.UtcNow - lastLocate > TimeSpan.FromSeconds(30)) _ = Locate("the card opened");
    }

    // ---------- the search ----------

    /// The search opened: the list is read now, so the first letter finds at once.
    public void SearchOpened() { letCitiesGo.Stop(); _ = UseCities(); }

    public void SearchClosed() => LetCitiesGoSoon();

    /// What the search shows for `typed`: at most 20 places, the biggest first.
    public async Task<List<CityMatch>> Search(string typed)
    {
        var list = await UseCities();
        found = list.Search(typed);
        return found;
    }

    async Task<Cities> UseCities()
    {
        letCitiesGo.Stop();
        if (cities != null) return cities;
        loading ??= Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var s = typeof(Surroundings).Assembly.GetManifestResourceStream("cities.gz") ?? throw new FileNotFoundException("cities.gz is not in the app");
            var c = Cities.Load(s);
            Log.Write($"place: the list of {c.Count} places read in {sw.ElapsedMilliseconds} ms");
            return c;
        });
        try { cities = await loading; }
        finally { loading = null; }
        return cities;
    }

    /// For the checks: does this computer know every time zone of the list (by its IANA name, or by its Windows name)?
    public async Task<object> ZoneReport()
    {
        var list = await UseCities();
        int direct = 0;
        var viaWindows = new List<string>();
        var unknown = new List<string>();
        foreach (var z in list.TimeZones)
        {
            Zones.Lookup(z, out var how);
            if (how == null) direct++;
            else if (how.StartsWith("found as")) viaWindows.Add($"{z} {how}");
            else unknown.Add(z);
        }
        LetCitiesGoSoon();
        return new { total = list.TimeZones.Count, direct, viaWindows, unknown, local = Zones.LocalName() };
    }

    void LetCitiesGoSoon() { letCitiesGo.Stop(); letCitiesGo.Start(); }

    /// Two minutes after the search closed (or the place was named) the list goes: about 25 MB nobody needs.
    void LetCitiesGo()
    {
        if (cities == null) return;
        cities = null;
        found = new();
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect();
        Log.Write("place: the list of places let go");
    }

    // ---------- the weather ----------

    /// Asks the source if the rules allow it now (`scheduled`: the hourly request, which always asks).
    public async void Ask(string why, bool scheduled = false)
    {
        if (!settings.LiveWeather) { hourly.Stop(); return; }
        if (quiet)
        {
            // locked or asleep: nobody sees the wall, so nothing is asked; once it is seen again
            if (!waitingToBeSeen) Log.Write($"weather: not asked ({why}): the screen is locked or the computer sleeps; asked when it is seen again");
            waitingToBeSeen = true;
            return;
        }
        var place = settings.Place;
        if (!WeatherRules.ShouldAsk(reading, lastFailed, place, now(), scheduled))
        {
            Log.Write($"weather: not asked ({why}): the last answer is {(now() - reading!.Received).TotalMinutes:F0} min old, less than {WeatherRules.MinimumGap.TotalMinutes:F0}");
            return;
        }
        if (asking) { askAgain = true; askAgainWhy = why; return; }
        asking = true;
        hourly.Stop();
        Asked++;
        var at = now();
        try
        {
            var a = await source.Ask(place, at, why);
            reading = new WeatherReading(a.Weather, at, Math.Round(place.Lat, 2), Math.Round(place.Lng, 2), source.Name);
            lastFailed = false;
            // the point in the journal to one decimal, as everywhere in it
            Log.Write($"weather: {source.Name} for {place.ForJournal} ({why}): {a.Journal}; {a.Weather}");
        }
        catch (Exception e)
        {
            lastFailed = true;
            Log.Write($"weather: {source.Name} for {place.ForJournal} ({why}) failed: {e.Message}; the wall shows a clear sky until an answer comes");
        }
        finally { asking = false; }
        SaveWeather();
        if (settings.LiveWeather)
        {
            redraw("the weather was asked");
            push();
            Schedule();
        }
        if (askAgain) { askAgain = false; Ask(askAgainWhy ?? "again"); }
    }

    /// The next hourly request: an hour after this one and up to five minutes more, never in the first minutes of an
    /// hour (MET asks apps not to come at XX:00, when everyone does).
    void Schedule()
    {
        var wait = WeatherRules.Period + TimeSpan.FromSeconds(Random.Shared.NextDouble() * WeatherRules.Spread.TotalSeconds);
        var at = DateTimeOffset.Now + wait;
        if (at.Minute < 2) wait += TimeSpan.FromMinutes(2 - at.Minute) + TimeSpan.FromSeconds(Random.Shared.Next(0, 60));
        hourly.Stop();
        hourly.Interval = (int)wait.TotalMilliseconds;
        hourly.Start();
    }

    /// Live weather on or off on the card.
    public void SetLive(bool on)
    {
        if (on == settings.LiveWeather) return;
        settings.LiveWeather = on;
        changed(on ? "Live weather on" : $"Live weather off (weather by hand {settings.ManualWeather}); nothing is asked any more");
        redraw(on ? "Live weather on" : "Live weather off");
        if (on) Ask("Live weather on");
        else { hourly.Stop(); waitingToBeSeen = false; }
    }

    void LoadWeather()
    {
        try
        {
            if (!File.Exists(weatherFile)) return;
            var k = JsonSerializer.Deserialize<Kept>(File.ReadAllText(weatherFile));
            if (k?.Reading != null) { reading = k.Reading; lastFailed = k.LastFailed; }
        }
        catch (Exception e) { Log.Write($"weather: the kept answer could not be read ({e.Message}); the wall is clear until an answer comes"); }
    }

    void SaveWeather()
    {
        try
        {
            var temp = weatherFile + ".new";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Kept(reading, lastFailed)));
            File.Move(temp, weatherFile, true);
        }
        catch (Exception e) { Log.Write($"weather: could not keep the answer ({e.Message})"); }
    }

    sealed record Kept(WeatherReading? Reading, bool LastFailed);

    // ---------- news from Windows ----------

    /// Locked, asleep or the screens off: nothing is asked. Seen again: what waited is asked now.
    public void SetQuiet(bool q, string why)
    {
        bool was = quiet;
        quiet = q;
        if (was && !q && waitingToBeSeen)
        {
            waitingToBeSeen = false;
            Ask(why);
        }
    }

    public async void OnWake()
    {
        SetQuiet(false, "woke up");
        if (settings.PlaceMode == "automatic") await Locate("woke up");
        Ask("woke up");
    }

    void OnNetwork(object? sender, EventArgs e) => ui.Post(_ => { networkSoon.Stop(); networkSoon.Start(); }, null);

    /// The network changed (another Wi-Fi, a cable, back online): perhaps another place, and the weather can be asked.
    async void OnNetworkSettled()
    {
        bool up = NetworkInterface.GetIsNetworkAvailable();
        bool wasUp = networkUp;
        networkUp = up;
        Log.Write($"network: changed, {(up ? "connected" : "no network")}");
        if (!up) return;
        if (settings.PlaceMode == "automatic") await Locate("the network changed");
        // back online after a failed request: asked again at once
        Ask(wasUp ? "the network changed" : "the network is back");
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetwork;
        NetworkChange.NetworkAvailabilityChanged -= OnNetwork;
        foreach (var t in new[] { hourly, networkSoon, letCitiesGo }) { t.Stop(); t.Dispose(); }
    }
}
