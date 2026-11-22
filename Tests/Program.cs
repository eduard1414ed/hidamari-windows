using System.Diagnostics;
using System.Globalization;
using System.Text;
using Hidamari;

// Checks that need no Windows, run on Linux before the Windows machines start:
//   Tests <out folder>
// The city list and its search, the time zones of all its places, reading MET Norway's forecast, the weather's rules.
// Writes report.md (and symbols.md, the whole table of MET's symbols) into the out folder; exit code 0 only if
// nothing failed.

var outDir = args.Length > 0 ? args[0] : "results";
Directory.CreateDirectory(outDir);
var report = new StringBuilder("# Checks without Windows\n\n| | check | detail |\n|---|---|---|\n");
int failed = 0;
void Result(string name, bool pass, string detail)
{
    if (!pass) failed++;
    Console.WriteLine($"{(pass ? "PASS" : "FAIL")} {name}: {detail}");
    report.AppendLine($"| {(pass ? "PASS" : "**FAIL**")} | {name} | {detail.Replace("|", "\\|").Replace("\n", " ")} |");
}
var here = AppContext.BaseDirectory;
var inv = CultureInfo.InvariantCulture;

// ---------------- the city list ----------------
long memBefore = GC.GetTotalMemory(true);
var sw = Stopwatch.StartNew();
Cities cities;
using (var f = File.OpenRead(Path.Combine(here, "cities.gz"))) cities = Cities.Load(f);
var loadMs = sw.ElapsedMilliseconds;
long memLoaded = GC.GetTotalMemory(true) - memBefore;
Result("the list loads", cities.Count > 150_000, $"{cities.Count} places in {loadMs} ms, {memLoaded / 1e6:F0} MB while loaded ({new FileInfo(Path.Combine(here, "cities.gz")).Length / 1e6:F2} MB in the app)");

string First(string q) { var r = cities.Search(q); return r.Count == 0 ? "nothing" : $"{r[0].Title} ({r[0].Subtitle})"; }
string List(string q, int n = 5) => string.Join("; ", cities.Search(q, n).Select(m => $"{m.Title} ({m.Subtitle})"));
void Finds(string typed, string title, string subtitleHas)
{
    var r = cities.Search(typed);
    bool ok = r.Count > 0 && r[0].Title == title && r[0].Subtitle.Contains(subtitleHas);
    Result($"\"{typed}\" finds {title}, {subtitleHas} first", ok, List(typed));
}
Finds("munchen", "München", "Germany");
Finds("München", "München", "Germany");
Finds("Munich", "Munich", "Germany");
Finds("moscow", "Moscow", "Russia");
Finds("Москва", "Москва", "Russia");
Finds("mos", "Moscow", "Russia");
Finds("tok", "Tokyo", "Japan");
Finds("東京", "東京都", "Japan");
Finds("sao paulo", "São Paulo", "Brazil");
Finds("SÃO PAU", "São Paulo", "Brazil");
Finds("krakow", "Kraków", "Poland");
Finds("berlin", "Berlin", "Germany");
Finds("new york", "New York City", "United States");
Finds("zurich", "Zürich", "Switzerland");
Finds("reykjav", "Reykjavík", "Iceland");
Finds("ho chi", "Ho Chi Minh City", "Vietnam");
{
    var r = cities.Search("springfield", 8);
    bool regions = r.Count >= 3 && r.All(m => m.Subtitle.Contains(", ")) && r.Select(m => m.Subtitle).Distinct().Count() == r.Count;
    Result("places of one name in one country are told apart by their region", regions, List("springfield", 4));
}
{
    var r = cities.Search("paris", 3);
    Result("bigger places first", r.Count >= 2 && r[0].Subtitle == "France" && r[0].Population >= r[1].Population, List("paris", 3));
}
Result("nothing typed, nothing found; nonsense finds nothing", cities.Search("").Count == 0 && cities.Search("   ").Count == 0 && cities.Search("qqqzzzx").Count == 0, "empty");
{
    // one letter, every letter: how long the search takes
    var times = new List<double>();
    foreach (var c in "abcdefghijklmnopqrstuvwxyz")
    {
        var t = Stopwatch.StartNew();
        cities.Search(c.ToString());
        times.Add(t.Elapsed.TotalMilliseconds);
    }
    var t2 = Stopwatch.StartNew();
    cities.Search("ber");
    Result("the search answers quickly", times.Max() < 100, $"one letter: {times.Average():F1} ms on average, {times.Max():F1} ms at most (26 letters); three letters: {t2.Elapsed.TotalMilliseconds:F1} ms");
}
{
    var t = Stopwatch.StartNew();
    var (p, km, _) = cities.Nearest(52.52, 13.40);
    var (p2, km2, _) = cities.Nearest(-33.87, 151.21);
    var (p3, km3, _) = cities.Nearest(48.1374, 11.5755);
    Result("the nearest place to a point", p.Name == "Berlin" && p2.Name == "Sydney" && p3.Name == "Munich" && km < 5,
        $"Berlin → {p.Name} {km:F1} km; Sydney → {p2.Name} {km2:F1} km; Munich → {p3.Name} {km3:F1} km; {t.ElapsedMilliseconds} ms for three");
}
{
    // the place is only a point and a zone: Munich's own row
    var m = cities.Search("munich", 1)[0].Place;
    Result("a place carries its point and time zone", Math.Abs(m.Lat - 48.14) < 0.02 && Math.Abs(m.Lng - 11.58) < 0.02 && m.Zone == "Europe/Berlin", $"{m}");
}

// ---------------- time zones ----------------
{
    var unknown = cities.TimeZones.Where(z => !TimeZoneInfo.TryFindSystemTimeZoneById(z, out _)).ToList();
    Result("every time zone of the list is known (Linux; the Windows machines check theirs)", unknown.Count == 0,
        $"{cities.TimeZones.Count} zones{(unknown.Count > 0 ? ", unknown: " + string.Join(", ", unknown) : "")}");
}
// summer time, both halves of the world: offsets in January and July 2026, hours from UTC
var dst = new (string zone, double jan, double jul)[]
{
    ("Europe/Berlin", 1, 2), ("Europe/London", 0, 1), ("America/New_York", -5, -4), ("America/Los_Angeles", -8, -7),
    ("Australia/Sydney", 11, 10), ("Pacific/Auckland", 13, 12), ("America/Santiago", -3, -4), ("America/Sao_Paulo", -3, -3),
    ("Africa/Johannesburg", 2, 2), ("Asia/Tehran", 3.5, 3.5), ("Asia/Kolkata", 5.5, 5.5), ("Asia/Tokyo", 9, 9), ("Europe/Moscow", 3, 3),
};
foreach (var (zone, jan, jul) in dst)
{
    var tz = Zones.Lookup(zone, out var how);
    double a = tz.GetUtcOffset(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero)).TotalHours;
    double b = tz.GetUtcOffset(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero)).TotalHours;
    Result($"summer time: {zone}", a == jan && b == jul && how == null, $"January {a:+0.#;-0.#;0}, July {b:+0.#;-0.#;0} (should be {jan:+0.#;-0.#;0}, {jul:+0.#;-0.#;0})");
}
{
    // the change itself: Sydney goes to summer time on the first Sunday of October, Berlin on the last of March
    var syd = Zones.Find("Australia/Sydney");
    var ber = Zones.Find("Europe/Berlin");
    double s1 = syd.GetUtcOffset(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero)).TotalHours, s2 = syd.GetUtcOffset(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero)).TotalHours;
    double b1 = ber.GetUtcOffset(new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.Zero)).TotalHours, b2 = ber.GetUtcOffset(new DateTimeOffset(2026, 3, 29, 12, 0, 0, TimeSpan.Zero)).TotalHours;
    Result("summer time begins on the right day", s1 == 10 && s2 == 11 && b1 == 1 && b2 == 2, $"Sydney 3 → 4 October: {s1} → {s2}; Berlin 28 → 29 March: {b1} → {b2}");
}

// ---------------- MET Norway's forecast ----------------
var sample = File.ReadAllText(Path.Combine(here, "met", "sample.json"));
void At(string when, string expect, Func<MetForecast.Hour?, bool> ok)
{
    var h = MetForecast.At(sample, DateTimeOffset.Parse(when, inv));
    Result($"MET: the hour of {when}", ok(h), h == null ? "nothing for that moment" : $"step {h.Time:HH:mm}, {h.Weather}, symbol {h.Symbol}, precipitation {h.Precipitation?.ToString(inv) ?? "none"} mm; expected {expect}");
}
At("2026-11-22T10:20:00Z", "cloud 0.875, rain, wind 5.2", h => h != null && Math.Abs(h.Weather.Cloud - 0.875) < 1e-9 && h.Weather.Kind == WeatherKind.Rain && h.Weather.Wind == 5.2);
At("2026-11-22T11:59:00Z", "cloud 0.40, snow (sleet showers), wind 4.0", h => h != null && Math.Abs(h.Weather.Cloud - 0.402) < 1e-9 && h.Weather.Kind == WeatherKind.Snow);
At("2026-11-22T12:30:00Z", "clear (fair, night)", h => h != null && h.Weather.Cloud == 0 && h.Weather.Kind == WeatherKind.Clear);
At("2026-11-22T08:10:00Z", "before the first step: the first one", h => h != null && h.Time.Hour == 9);
At("2026-11-22T14:00:00Z", "nothing: more than an hour after the last hourly step", h => h == null);
At("2026-11-25T03:00:00Z", "a 6-hour step: snow from next_6_hours", h => h != null && h.Weather.Kind == WeatherKind.Snow && h.Symbol == "heavysnow");
At("2026-11-25T07:00:00Z", "nothing: past the 6 hours of the last step", h => h == null);
// a real answer: Chicago, asked on 22 Nov 2026 at 18:13 UTC (data: MET Norway, CC BY 4.0); it was raining there then
var real = File.ReadAllText(Path.Combine(here, "met", "real-chicago.json"));
var rh = MetForecast.At(real, DateTimeOffset.Parse("2026-11-22T18:13:00Z", inv));
Result("MET: a real answer is read (Chicago, 22 Nov 2026 18:13 UTC)",
    rh != null && rh.Time == DateTimeOffset.Parse("2026-11-22T18:00:00Z", inv) && rh.Symbol == "lightrain" && Math.Abs(rh.Weather.Cloud - 1.0) < 1e-9 && rh.Weather.Kind == WeatherKind.Rain && rh.Weather.Wind is > 0,
    rh == null ? "nothing for that moment" : $"step {rh.Time:HH:mm}, {rh.Weather}, symbol {rh.Symbol}, precipitation {rh.Precipitation?.ToString(inv) ?? "none"} mm; expected 18:00, cloud 1.00, rain, lightrain");
Result("MET: the point goes out with two decimals", MetForecast.Query(52.520008, 13.404954) == "lat=52.52&lon=13.4" && MetForecast.Query(-33.8688, 151.2093) == "lat=-33.87&lon=151.21",
    $"{MetForecast.Query(52.520008, 13.404954)}; {MetForecast.Query(-33.8688, 151.2093)}");
Result("MET: a broken answer is no weather, not a crash", Try(() => MetForecast.At("{\"type\":\"Feature\"}", DateTimeOffset.UtcNow) == null), "read as nothing");
static bool Try(Func<bool> f) { try { return f(); } catch { return false; } }

// the whole table of symbols, to read
{
    var all = new[] { "clearsky", "fair", "partlycloudy", "cloudy", "fog", "lightrain", "rain", "heavyrain", "lightrainshowers", "rainshowers", "heavyrainshowers",
        "lightrainandthunder", "rainandthunder", "heavyrainandthunder", "lightrainshowersandthunder", "rainshowersandthunder", "heavyrainshowersandthunder",
        "lightsleet", "sleet", "heavysleet", "lightsleetshowers", "sleetshowers", "heavysleetshowers", "lightsleetandthunder", "sleetandthunder", "heavysleetandthunder",
        "lightssleetshowersandthunder", "sleetshowersandthunder", "heavysleetshowersandthunder", "lightsnow", "snow", "heavysnow", "lightsnowshowers", "snowshowers",
        "heavysnowshowers", "lightsnowandthunder", "snowandthunder", "heavysnowandthunder", "lightssnowshowersandthunder", "snowshowersandthunder", "heavysnowshowersandthunder" };
    var md = new StringBuilder("| MET symbol_code | on the wall |\n|---|---|\n");
    foreach (var s in all) md.AppendLine($"| `{s}` | {MetForecast.KindOf(s) switch { WeatherKind.Rain => "rain (drops on the glass in Window)", WeatherKind.Snow => "frost (on the glass in Window)", _ => "dry: clouds from the cloud cover" }} |");
    File.WriteAllText(Path.Combine(outDir, "symbols.md"), md.ToString());
    bool every = all.All(MetForecast.Symbols.ContainsKey) && all.Length == MetForecast.Symbols.Count;
    Result("MET: every symbol has its place in the table", every && MetForecast.KindOf("rainshowers_polartwilight") == WeatherKind.Rain && MetForecast.KindOf("snow") == WeatherKind.Snow,
        $"{all.Length} symbols (symbols.md); _day, _night and _polartwilight are the same weather");
}

// ---------------- the weather's rules ----------------
{
    var t0 = new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
    var berlin = Place.Berlin;
    var reading = new WeatherReading(new WeatherNow(0.6, WeatherKind.Rain, 5), t0, 52.52, 13.4, "test");
    var potsdam = berlin with { Name = "Potsdam", Lat = 52.40, Lng = 13.07 };     // about 26 km
    var near = berlin with { Lat = 52.6, Lng = 13.3 };                           // about 11 km
    Result("rules: a fresh answer is shown", WeatherRules.Current(reading, false, berlin, t0.AddHours(5.9)).Cloud == 0.6, "5.9 h after: cloud 0.6");
    Result("rules: an answer older than six hours makes the wall clear", WeatherRules.Current(reading, false, berlin, t0.AddHours(6)) == WeatherNow.Clear, "6 h after: clear");
    Result("rules: a failed request makes the wall clear", WeatherRules.Current(reading, true, berlin, t0.AddMinutes(1)) == WeatherNow.Clear, "clear");
    Result("rules: an answer counts within 20 km", WeatherRules.Current(reading, false, near, t0).Cloud == 0.6 && WeatherRules.Current(reading, false, potsdam, t0) == WeatherNow.Clear,
        $"{Place.Km(52.52, 13.4, near.Lat, near.Lng):F0} km: kept; Potsdam {Place.Km(52.52, 13.4, potsdam.Lat, potsdam.Lng):F0} km: clear");
    Result("rules: not asked again within 15 minutes for the same place", !WeatherRules.ShouldAsk(reading, false, berlin, t0.AddMinutes(14), false)
        && WeatherRules.ShouldAsk(reading, false, berlin, t0.AddMinutes(15), false) && WeatherRules.ShouldAsk(reading, false, potsdam, t0.AddMinutes(1), false)
        && WeatherRules.ShouldAsk(reading, false, berlin, t0.AddMinutes(1), true), "14 min: no; 15 min: yes; another place: yes; the hourly timer: yes");
    Result("rules: rain and frost on the glass thin the clouds (as on the Mac)", new WeatherNow(0.9, WeatherKind.Rain, null).DrawnIn(1).Cloud == 0.2
        && new WeatherNow(0.9, WeatherKind.Snow, null).DrawnIn(1).Cloud == 0.3 && new WeatherNow(0.9, WeatherKind.Rain, null).DrawnIn(0).Cloud == 0.9
        && new WeatherNow(0.1, WeatherKind.Rain, null).DrawnIn(1).Cloud == 0.1, "window: rain ≤ 0.2, frost ≤ 0.3; other scenes as they are");
    Result("rules: the weather by hand as on the Mac", WeatherNow.Manual("cloudy", 0).Cloud == 0.8 && WeatherNow.Manual("rain", 1) == new WeatherNow(0.9, WeatherKind.Rain, null)
        && WeatherNow.Manual("frost", 1).DrawnIn(1) == new WeatherNow(0.3, WeatherKind.Snow, null) && WeatherNow.Manual("rain", 0) == new WeatherNow(0.8, WeatherKind.Clear, null),
        "clear 0, cloudy 0.8, rain 0.9 → 0.2 in the window, frost 0.7 → 0.3, rain elsewhere cloudy");
}

File.WriteAllText(Path.Combine(outDir, "report.md"), report.ToString());
Console.WriteLine(failed == 0 ? "all passed" : $"{failed} failed");
return failed == 0 ? 0 : 1;
