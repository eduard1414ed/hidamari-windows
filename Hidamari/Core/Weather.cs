using System.Globalization;
using System.Text.Json;

namespace Hidamari;

/// Numbers as wall.html's `weather`: 0 clear, 1 rain (drops on the glass), 2 snow (frost on the glass).
public enum WeatherKind { Clear = 0, Rain = 1, Snow = 2 }

/// What the wall takes from the weather: clouds (0 clear … 1 overcast), rain or snow on the glass, and the wind
/// (metres per second; kept for the animation of stage 4, not drawn yet). As Weather on the Mac.
public sealed record WeatherNow(double Cloud, WeatherKind Kind, double? Wind)
{
    public static readonly WeatherNow Clear = new(0, WeatherKind.Clear, null);

    /// While rain or frost is drawn on the glass (the window scene), the clouds are kept at most this thick, or they
    /// blur the drops and the frost away (Weather.cloudOverGlass on the Mac).
    public WeatherNow DrawnIn(int scene) =>
        scene == 1 && Kind != WeatherKind.Clear ? this with { Cloud = Math.Min(Cloud, Kind == WeatherKind.Rain ? 0.2 : 0.3) } : this;

    /// The weather by hand (ManualWeather on the Mac): the same values as the test weather source.
    public static WeatherNow Manual(string choice, int scene)
    {
        // rain and frost are offered only in the window scene; chosen there and shown elsewhere they are cloudy
        if (scene != 1 && choice is "rain" or "frost") choice = "cloudy";
        return choice switch
        {
            "cloudy" => new(0.8, WeatherKind.Clear, null),
            "rain" => new(0.9, WeatherKind.Rain, null),
            "frost" => new(0.7, WeatherKind.Snow, null),
            _ => Clear,
        };
    }

    public override string ToString() =>
        $"cloud {Cloud.ToString("0.00", CultureInfo.InvariantCulture)}, {Kind.ToString().ToLowerInvariant()}, wind {(Wind is { } w ? w.ToString("0.0", CultureInfo.InvariantCulture) + " m/s" : "unknown")}";
}

/// MET Norway's forecast (api.met.no, locationforecast/2.0/compact): the hour that is now, read the way the Mac reads
/// Apple's weather now.
public static class MetForecast
{
    /// What a symbol puts on the glass, by its name without _day, _night or _polartwilight. Built after
    /// WeatherKitSource.kind(of:) on the Mac: drizzle, rain, showers and thunderstorms with rain are rain; snow, sleet
    /// (Apple's sleet and wintry mix) and their showers are frost; everything else is a dry sky, its clouds from the
    /// cloud cover. The whole list is MET's (github.com/metno/weathericons).
    public static readonly IReadOnlyDictionary<string, WeatherKind> Symbols = new Dictionary<string, WeatherKind>
    {
        ["clearsky"] = WeatherKind.Clear, ["fair"] = WeatherKind.Clear, ["partlycloudy"] = WeatherKind.Clear,
        ["cloudy"] = WeatherKind.Clear, ["fog"] = WeatherKind.Clear,
        ["lightrain"] = WeatherKind.Rain, ["rain"] = WeatherKind.Rain, ["heavyrain"] = WeatherKind.Rain,
        ["lightrainshowers"] = WeatherKind.Rain, ["rainshowers"] = WeatherKind.Rain, ["heavyrainshowers"] = WeatherKind.Rain,
        ["lightrainandthunder"] = WeatherKind.Rain, ["rainandthunder"] = WeatherKind.Rain, ["heavyrainandthunder"] = WeatherKind.Rain,
        ["lightrainshowersandthunder"] = WeatherKind.Rain, ["rainshowersandthunder"] = WeatherKind.Rain, ["heavyrainshowersandthunder"] = WeatherKind.Rain,
        ["lightsleet"] = WeatherKind.Snow, ["sleet"] = WeatherKind.Snow, ["heavysleet"] = WeatherKind.Snow,
        ["lightsleetshowers"] = WeatherKind.Snow, ["sleetshowers"] = WeatherKind.Snow, ["heavysleetshowers"] = WeatherKind.Snow,
        ["lightsleetandthunder"] = WeatherKind.Snow, ["sleetandthunder"] = WeatherKind.Snow, ["heavysleetandthunder"] = WeatherKind.Snow,
        // MET's own spelling, kept by them for old apps
        ["lightssleetshowersandthunder"] = WeatherKind.Snow, ["sleetshowersandthunder"] = WeatherKind.Snow, ["heavysleetshowersandthunder"] = WeatherKind.Snow,
        ["lightsnow"] = WeatherKind.Snow, ["snow"] = WeatherKind.Snow, ["heavysnow"] = WeatherKind.Snow,
        ["lightsnowshowers"] = WeatherKind.Snow, ["snowshowers"] = WeatherKind.Snow, ["heavysnowshowers"] = WeatherKind.Snow,
        ["lightsnowandthunder"] = WeatherKind.Snow, ["snowandthunder"] = WeatherKind.Snow, ["heavysnowandthunder"] = WeatherKind.Snow,
        ["lightssnowshowersandthunder"] = WeatherKind.Snow, ["snowshowersandthunder"] = WeatherKind.Snow, ["heavysnowshowersandthunder"] = WeatherKind.Snow,
    };

    public static WeatherKind KindOf(string symbol)
    {
        var s = symbol;
        foreach (var end in new[] { "_day", "_night", "_polartwilight" })
            if (s.EndsWith(end, StringComparison.Ordinal)) { s = s[..^end.Length]; break; }
        return Symbols.TryGetValue(s, out var k) ? k : WeatherKind.Clear;
    }

    /// One hour of the forecast as read, for the journal.
    public sealed record Hour(DateTimeOffset Time, WeatherNow Weather, string Symbol, double? Precipitation);

    /// The hour that `now` falls in: the last step of the forecast at or before now (the first one if now is before
    /// them all). Clouds and wind of that moment; rain or snow from the symbol of the hour that follows it.
    /// Null when the forecast has nothing for now (it ended more than an hour ago, or it cannot be read).
    public static Hour? At(string json, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("properties", out var props) || !props.TryGetProperty("timeseries", out var series)) return null;
        JsonElement? pick = null;
        DateTimeOffset pickTime = default;
        foreach (var step in series.EnumerateArray())
        {
            if (!step.TryGetProperty("time", out var t) || !DateTimeOffset.TryParse(t.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)) continue;
            if (pick == null || time <= now) { pick = step; pickTime = time; }
            if (time > now) break;
        }
        if (pick is not { } p) return null;
        if (now - pickTime > TimeSpan.FromHours(1) && !Covers(p, now - pickTime)) return null;
        var data = p.GetProperty("data");
        var details = data.GetProperty("instant").GetProperty("details");
        double? Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        double cloud = Math.Clamp((Num(details, "cloud_area_fraction") ?? 0) / 100.0, 0, 1);
        double? wind = Num(details, "wind_speed") is { } w && double.IsFinite(w) ? Math.Max(0, w) : null;
        string symbol = "";
        double? precipitation = null;
        foreach (var span in new[] { "next_1_hours", "next_6_hours", "next_12_hours" })
        {
            if (!data.TryGetProperty(span, out var next)) continue;
            if (next.TryGetProperty("summary", out var sum) && sum.TryGetProperty("symbol_code", out var sc)) symbol = sc.GetString() ?? "";
            if (next.TryGetProperty("details", out var nd)) precipitation = Num(nd, "precipitation_amount");
            if (symbol.Length > 0) break;
        }
        return new Hour(pickTime, new WeatherNow(cloud, KindOf(symbol), wind), symbol, precipitation);
    }

    /// Far ahead the forecast comes in steps of 6 hours: such a step still covers the hours after it.
    static bool Covers(JsonElement step, TimeSpan after)
    {
        var data = step.GetProperty("data");
        if (data.TryGetProperty("next_1_hours", out _)) return after <= TimeSpan.FromHours(1);
        if (data.TryGetProperty("next_6_hours", out _)) return after <= TimeSpan.FromHours(6);
        return false;
    }

    /// The query for a point: two decimals (about 1 km) say enough for the weather and little about the person;
    /// MET allows at most four.
    public static string Query(double lat, double lng) =>
        $"lat={Math.Round(lat, 2).ToString("0.##", CultureInfo.InvariantCulture)}&lon={Math.Round(lng, 2).ToString("0.##", CultureInfo.InvariantCulture)}";
}

/// The rules of the live weather, as WeatherModel on the Mac: asked once an hour, at launch, after waking and after the
/// place changes, never sooner than 15 minutes after the last answer for the same place; no answer for six hours,
/// or a failed request, makes the wall clear; an answer counts for places within 20 km.
public static class WeatherRules
{
    public static readonly TimeSpan Period = TimeSpan.FromHours(1);
    public static readonly TimeSpan MinimumGap = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan FreshFor = TimeSpan.FromHours(6);
    public const double SamePlaceKm = 20;
    /// Added at random to each hourly request, so that copies of the app started together do not ask together.
    public static readonly TimeSpan Spread = TimeSpan.FromMinutes(5);

    /// What the wall shows at `place` at the real moment `now`.
    public static WeatherNow Current(WeatherReading? reading, bool lastFailed, Place place, DateTimeOffset now)
    {
        if (lastFailed || reading == null) return WeatherNow.Clear;
        if (now - reading.Received >= FreshFor || now < reading.Received - TimeSpan.FromMinutes(5)) return WeatherNow.Clear;
        if (Place.Km(reading.Lat, reading.Lng, place.Lat, place.Lng) >= SamePlaceKm) return WeatherNow.Clear;
        return reading.Weather;
    }

    /// Whether to ask now; `scheduled` is the hourly timer, which always asks.
    public static bool ShouldAsk(WeatherReading? reading, bool lastFailed, Place place, DateTimeOffset now, bool scheduled) =>
        scheduled || reading == null || lastFailed
        || Place.Km(reading.Lat, reading.Lng, place.Lat, place.Lng) >= SamePlaceKm
        || now - reading.Received >= MinimumGap;
}

/// The last answer: the weather of its hour, when it came, and for which point (already rounded).
public sealed record WeatherReading(WeatherNow Weather, DateTimeOffset Received, double Lat, double Lng, string Source);
