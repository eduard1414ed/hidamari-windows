namespace HidamariProbe;

/// What the probe draws: Berlin, the moment now, clear sky, the default scene and wall.
/// The same values the Mac app sends to komorebi.render() on a first launch (AppModel.swift, WebRenderer.swift).
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

    /// The argument of komorebi.render(), see the comment above renderNow() in wall.html.
    public static Dictionary<string, object?> State(DateTimeOffset now) => new()
    {
        ["time"] = (double)now.ToUnixTimeMilliseconds(),
        ["utcOffset"] = (int)Berlin.GetUtcOffset(now).TotalMinutes,
        ["lat"] = Latitude,
        ["lng"] = Longitude,
        ["scene"] = 0,            // dapples
        ["wall"] = 0,             // beige
        // the prototype's starting own wall (CustomWall.standard); its texture is used for every wall
        ["custom"] = new Dictionary<string, object?> { ["L"] = 0.88, ["x"] = 0.5, ["y"] = 0.8, ["k"] = 0.1, ["rough"] = 1.0, ["shade"] = null },
        ["plant"] = 2,            // hanging branch, the Mac default (the window scene only)
        ["cloud"] = 0.0,          // clear
        ["weather"] = 0,
    };
}
