namespace Hidamari;

/// What the wall shows: the place, the moment, the weather and what the person chose on the card. The same values the
/// Mac app sends to komorebi.render() (AppModel.state(now:), WebRenderer.swift).
static class Wall
{
    /// Every five minutes on the clock, half a second after the mark, as AppClock.nextTick() on the Mac.
    public static TimeSpan UntilNextTick(DateTimeOffset now)
    {
        const double period = 300;
        var t = now.ToUnixTimeMilliseconds() / 1000.0;
        var next = (Math.Floor(t / period) + 1) * period + 0.5;
        return TimeSpan.FromSeconds(next - t);
    }

    /// The moment the wall shows when the real moment is `now`: now while following the sun, otherwise the pinned
    /// time of today at the place (AppModel.state(now:)).
    public static DateTimeOffset Moment(DateTimeOffset now, Settings s)
    {
        if (s.FollowsSun) return now;
        var tz = s.Place.TimeZone;
        var local = TimeZoneInfo.ConvertTime(now, tz);
        var midnight = new DateTime(local.Year, local.Month, local.Day, 0, 0, 0, DateTimeKind.Unspecified);
        // as the Mac: the start of the day plus the minutes, counted in real time
        var start = new DateTimeOffset(midnight, tz.GetUtcOffset(midnight));
        return start.AddMinutes(s.PinnedMinutes);
    }

    /// The argument of komorebi.render(), see the comment above renderNow() in wall.html. `weather` is the live
    /// weather or the one chosen by hand, as it is; here it is drawn for the scene.
    public static Dictionary<string, object?> State(DateTimeOffset now, Settings s, WeatherNow weather)
    {
        var moment = Moment(now, s);
        var place = s.Place;
        var w = weather.DrawnIn(s.Scene);
        return new()
        {
            ["time"] = (double)moment.ToUnixTimeMilliseconds(),
            ["utcOffset"] = (int)place.TimeZone.GetUtcOffset(moment).TotalMinutes,
            ["lat"] = place.Lat,
            ["lng"] = place.Lng,
            ["scene"] = s.Scene,
            ["wall"] = s.Wall,
            // its texture is used for every wall
            ["custom"] = s.Custom.ForPage(),
            ["plant"] = s.Plant,
            ["cloud"] = w.Cloud,
            // wall.html draws rain and frost only in the window scene; elsewhere the clouds are the weather
            ["weather"] = (int)w.Kind,
            // Light from: Right mirrors only the scenes with a window (WallpaperState.mirrored on the Mac)
            ["mirror"] = s.WindowOnRight && s.Scene is 1 or 2,
        };
    }
}
