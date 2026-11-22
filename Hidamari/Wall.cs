namespace Hidamari;

/// What the wall shows: Berlin (until stage 3 brings the place), the moment, and what the person chose on the card.
/// The same values the Mac app sends to komorebi.render() (AppModel.state(now:), WebRenderer.swift).
static class Wall
{
    public const double Latitude = 52.52, Longitude = 13.405;   // Berlin, as in wall.html and the Mac app
    public static readonly TimeZoneInfo Berlin = FindBerlin();

    static TimeZoneInfo FindBerlin()
    {
        foreach (var id in new[] { "Europe/Berlin", "W. Europe Standard Time" })
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz)) return tz;
        Log.Write("wall: no Berlin time zone on this machine, using Central European time without summer time");
        return TimeZoneInfo.CreateCustomTimeZone("Berlin", TimeSpan.FromHours(1), "Berlin", "Berlin");
    }

    /// Every five minutes on the clock, half a second after the mark, as AppClock.nextTick() on the Mac.
    public static TimeSpan UntilNextTick(DateTimeOffset now)
    {
        const double period = 300;
        var t = now.ToUnixTimeMilliseconds() / 1000.0;
        var next = (Math.Floor(t / period) + 1) * period + 0.5;
        return TimeSpan.FromSeconds(next - t);
    }

    /// The moment the wall shows when the real moment is `now`: now while following the sun, otherwise the pinned
    /// time of today in Berlin (AppModel.state(now:)).
    public static DateTimeOffset Moment(DateTimeOffset now, Settings s)
    {
        if (s.FollowsSun) return now;
        var local = TimeZoneInfo.ConvertTime(now, Berlin);
        var midnight = new DateTime(local.Year, local.Month, local.Day, 0, 0, 0, DateTimeKind.Unspecified);
        // as the Mac: the start of the day plus the minutes, counted in real time
        var start = new DateTimeOffset(midnight, Berlin.GetUtcOffset(midnight));
        return start.AddMinutes(s.PinnedMinutes);
    }

    /// The weather by hand as the scene draws it (ManualWeather on the Mac, design/panel/README.md): rain and frost
    /// only in the window scene, where they come with thin enough clouds (0.2 and 0.3) to stay sharp; elsewhere they
    /// show as cloudy.
    public static (double cloud, int weather) Weather(string manual, int scene)
    {
        bool glass = scene == 1;
        return manual switch
        {
            "cloudy" => (0.8, 0),
            "rain" when glass => (0.2, 1),       // 0.9 thinned to 0.2
            "frost" when glass => (0.3, 2),      // 0.7 thinned to 0.3
            "rain" or "frost" => (0.8, 0),
            _ => (0.0, 0),
        };
    }

    /// The argument of komorebi.render(), see the comment above renderNow() in wall.html.
    public static Dictionary<string, object?> State(DateTimeOffset now, Settings s)
    {
        var moment = Moment(now, s);
        var (cloud, weather) = Weather(s.ManualWeather, s.Scene);
        return new()
        {
            ["time"] = (double)moment.ToUnixTimeMilliseconds(),
            ["utcOffset"] = (int)Berlin.GetUtcOffset(moment).TotalMinutes,
            ["lat"] = Latitude,
            ["lng"] = Longitude,
            ["scene"] = s.Scene,
            ["wall"] = s.Wall,
            // its texture is used for every wall
            ["custom"] = s.Custom.ForPage(),
            ["plant"] = s.Plant,
            ["cloud"] = cloud,
            ["weather"] = weather,
        };
    }
}
