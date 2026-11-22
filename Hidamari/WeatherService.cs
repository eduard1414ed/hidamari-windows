using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Hidamari;

/// Where the live weather comes from: MET Norway, or the test source for the checks.
interface IWeatherSource
{
    string Name { get; }
    /// The weather of the hour that is now at `place`. Throws when there is no answer (no network, an error, a refusal).
    Task<WeatherAnswer> Ask(Place place, DateTimeOffset now, string why);
}

/// One answer, with what the journal says about it.
sealed record WeatherAnswer(WeatherNow Weather, string Journal);

/// MET Norway's forecast (api.met.no/weatherapi/locationforecast/2.0/compact), asked directly from this computer, no
/// key and no server of our own. Their terms (api.met.no/doc/TermsOfService): every request signed with the app's name,
/// version and a contact; the point to at most 4 decimals (we send 2); never again before the answer's Expires, and
/// with If-Modified-Since; HTTPS and gzip; a 429 slows down at once.
sealed class MetSource : IWeatherSource
{
    /// MET's terms must be read and accepted by the person who makes the app before it may ask MET at all (their
    /// rule). Ed accepted them on 22 Nov 2026. The checks still use the test source.
    public const bool Accepted = true;

    const string Url = "https://api.met.no/weatherapi/locationforecast/2.0/compact?";
    /// The app's name, its version and where MET can reach whoever made it: the site, whose Imprint has the contact (Ed, 22 Nov).
    public static readonly string UserAgent = $"Hidamari/{Program.Version} (Windows; +https://hidamari.tsarionov.com)";

    static readonly HttpClient http = MakeClient();
    readonly string cacheFile;
    Cache? cache;
    /// After a 429 or a 403: no request before this moment.
    DateTimeOffset quietUntil;

    /// What MET last said for a point: kept on disk, so a restart does not ask again before Expires.
    sealed record Cache(string Query, string Body, string? LastModified, DateTimeOffset Expires, DateTimeOffset Received);

    public MetSource(string cacheFile)
    {
        this.cacheFile = cacheFile;
        try { if (File.Exists(cacheFile)) cache = JsonSerializer.Deserialize<Cache>(File.ReadAllText(cacheFile)); }
        catch (Exception e) { Log.Write($"weather: MET's kept answer could not be read ({e.Message}), asking anew"); }
    }

    public string Name => "MET Norway";

    static HttpClient MakeClient()
    {
        var h = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        }) { Timeout = TimeSpan.FromSeconds(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return h;
    }

    public async Task<WeatherAnswer> Ask(Place place, DateTimeOffset now, string why)
    {
        var query = MetForecast.Query(place.Lat, place.Lng);
        var c = cache?.Query == query ? cache : null;
        string body, how;
        int status = 0;
        var sw = Stopwatch.StartNew();
        if (c != null && now < c.Expires)
        {
            body = c.Body;
            how = $"from the kept answer (MET said: not before {c.Expires.ToLocalTime():HH:mm})";
        }
        else
        {
            if (now < quietUntil) throw new WeatherException($"MET asked us to wait until {quietUntil.ToLocalTime():HH:mm}, not asking");
            using var req = new HttpRequestMessage(HttpMethod.Get, Url + query);
            if (c?.LastModified is { } lm) req.Headers.TryAddWithoutValidation("If-Modified-Since", lm);
            HttpResponseMessage res;
            try { res = await http.SendAsync(req); }
            catch (Exception e) { throw new WeatherException($"no answer from MET after {sw.ElapsedMilliseconds} ms: {e.GetBaseException().Message}"); }
            using (res)
            {
                status = (int)res.StatusCode;
                var expires = res.Content.Headers.Expires ?? now.AddMinutes(30);
                var lastModified = res.Content.Headers.LastModified?.ToString("R", CultureInfo.InvariantCulture);
                switch (status)
                {
                    case 200:
                    case 203:
                        body = await res.Content.ReadAsStringAsync();
                        cache = new Cache(query, body, lastModified, expires, now);
                        how = status == 203 ? "a new answer, but MET says this version is deprecated (203): update the app" : "a new answer";
                        break;
                    case 304 when c != null:
                        body = c.Body;
                        cache = c with { Expires = expires, Received = now };
                        how = "not changed since the last answer (304)";
                        break;
                    case 429:
                        quietUntil = now + (res.Headers.RetryAfter?.Delta ?? TimeSpan.FromHours(1));
                        throw new WeatherException($"MET says too many requests (429), quiet until {quietUntil.ToLocalTime():HH:mm}");
                    case 403:
                        quietUntil = now + TimeSpan.FromHours(6);
                        throw new WeatherException($"MET refused (403): the signature or the point is not accepted; quiet for 6 hours");
                    default:
                        throw new WeatherException($"MET answered {status} {res.ReasonPhrase} after {sw.ElapsedMilliseconds} ms");
                }
                how += $", {sw.ElapsedMilliseconds} ms, not again before {expires.ToLocalTime():HH:mm}";
            }
            Save();
        }
        MetForecast.Hour? hour;
        try { hour = MetForecast.At(body, now); }
        catch (Exception e) { throw new WeatherException($"MET's answer could not be read ({e.Message})"); }
        if (hour == null) throw new WeatherException("MET's forecast has nothing for this hour");
        var journal = $"MET {(status == 0 ? "" : status + " ")}{how}; hour {hour.Time.ToLocalTime():HH:mm}, symbol {hour.Symbol}, " +
                      $"precipitation {(hour.Precipitation is { } p ? p.ToString("0.0", CultureInfo.InvariantCulture) + " mm" : "unknown")}";
        return new WeatherAnswer(hour.Weather, journal);
    }

    void Save()
    {
        try
        {
            var temp = cacheFile + ".new";
            File.WriteAllText(temp, JsonSerializer.Serialize(cache));
            File.Move(temp, cacheFile, true);
        }
        catch (Exception e) { Log.Write($"weather: could not keep MET's answer ({e.Message})"); }
    }
}

/// For the checks (and until MET's terms are accepted): the weather is whatever was last set, or no network
/// (TestWeatherSource on the Mac).
sealed class TestSource : IWeatherSource
{
    WeatherNow weather = WeatherNow.Clear;
    bool offline;
    public int Asked { get; private set; }

    public string Name => "test source";

    public void Set(WeatherNow w) { weather = w; offline = false; }
    public void GoOffline() => offline = true;

    public Task<WeatherAnswer> Ask(Place place, DateTimeOffset now, string why)
    {
        Asked++;
        if (offline) throw new WeatherException("no network (test source)");
        return Task.FromResult(new WeatherAnswer(weather, "test source"));
    }
}

sealed class WeatherException(string message) : Exception(message);
