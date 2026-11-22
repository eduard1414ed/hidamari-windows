using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Hidamari;

/// One line of the place search.
public sealed record CityMatch(string Title, string Subtitle, Place Place, int Population);

/// The app's own list of cities (Resources/cities.gz, made by tools/make-cities.py from GeoNames). Searched on this
/// computer only: what is typed never leaves it. Loaded only while it is needed (the search is open, or the computer
/// was just located) and let go afterwards, so it costs no memory while the card is closed.
public sealed class Cities
{
    readonly string[] countries, admins, zones;
    readonly string[] names, natives;
    readonly short[] country, admin, zone;
    readonly int[] lat, lng, pop;
    /// Every English and native name, folded (see Fold), sorted, with the city it belongs to; native names have the
    /// city's number plus Count, so the search knows which name was matched.
    readonly string[] keys;
    readonly int[] keyCity;

    public int Count => names.Length;
    public IReadOnlyList<string> TimeZones => zones;

    Cities(string[] countries, string[] admins, string[] zones, List<string[]> rows)
    {
        this.countries = countries; this.admins = admins; this.zones = zones;
        int n = rows.Count;
        names = new string[n]; natives = new string[n];
        country = new short[n]; admin = new short[n]; zone = new short[n];
        lat = new int[n]; lng = new int[n]; pop = new int[n];
        var k = new List<(string key, int city)>(n + n / 6);
        for (int i = 0; i < n; i++)
        {
            var r = rows[i];
            names[i] = r[0]; natives[i] = r[1];
            country[i] = short.Parse(r[2], CultureInfo.InvariantCulture);
            admin[i] = r[3].Length == 0 ? (short)-1 : short.Parse(r[3], CultureInfo.InvariantCulture);
            lat[i] = int.Parse(r[4], CultureInfo.InvariantCulture); lng[i] = int.Parse(r[5], CultureInfo.InvariantCulture);
            zone[i] = short.Parse(r[6], CultureInfo.InvariantCulture); pop[i] = int.Parse(r[7], CultureInfo.InvariantCulture);
            k.Add((Fold(r[0]), i));
            if (r[1].Length > 0) k.Add((Fold(r[1]), i + n));
        }
        k.Sort((a, b) => string.CompareOrdinal(a.key, b.key));
        keys = k.Select(x => x.key).ToArray();
        keyCity = k.Select(x => x.city).ToArray();
    }

    public static Cities Load(Stream gzipped)
    {
        using var gz = new GZipStream(gzipped, CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        if (reader.ReadLine() is not { } head || !head.StartsWith("hidamari-cities 1")) throw new InvalidDataException("not a list of cities");
        var countries = new List<string>(); var admins = new List<string>(); var zones = new List<string>();
        var rows = new List<string[]>(170_000);
        bool body = false;
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            if (!body)
            {
                if (line == "-") { body = true; continue; }
                var p = line.Split('\t');
                switch (p[0])
                {
                    case "C": countries.Add(p[2]); break;
                    case "A": admins.Add(p[1]); break;
                    case "Z": zones.Add(p[1]); break;
                }
                continue;
            }
            var r = line.Split('\t');
            if (r.Length >= 8) rows.Add(r);
        }
        return new Cities(countries.ToArray(), admins.ToArray(), zones.ToArray(), rows);
    }

    /// Without accents and case, the way people type: munchen finds München, sao paulo São Paulo. Hyphens are spaces,
    /// apostrophes and dots go, several spaces are one.
    public static string Fold(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var b = new StringBuilder(d.Length);
        bool space = true;
        foreach (var ch in d)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark) continue;
            var c = char.ToLowerInvariant(ch);
            string? more = c switch
            {
                'ß' => "ss", 'æ' => "ae", 'œ' => "oe", 'ø' => "o", 'đ' => "d", 'ð' => "d", 'ł' => "l", 'ı' => "i", 'þ' => "th",
                '\'' or '’' or 'ʻ' or 'ʼ' or '`' or '.' => "",
                '-' or '‐' or '–' or '_' or ',' => " ",
                _ => null,
            };
            var piece = more ?? (char.IsWhiteSpace(c) ? " " : c.ToString());
            foreach (var x in piece)
            {
                if (x == ' ') { if (!space) b.Append(' '); space = true; }
                else { b.Append(x); space = false; }
            }
        }
        return b.ToString().TrimEnd();
    }

    /// The places whose English or native name begins with what was typed, the biggest first. A place found by its
    /// native name is shown by it (Москва, not Moscow, when Москва was typed).
    public List<CityMatch> Search(string typed, int max = 20)
    {
        var q = Fold(typed);
        var found = new List<CityMatch>();
        if (q.Length == 0) return found;
        int lo = 0, hi = keys.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (string.CompareOrdinal(keys[mid], q) < 0) lo = mid + 1; else hi = mid;
        }
        // each city once; by its English name if that was matched too
        var byNative = new Dictionary<int, bool>();
        for (int i = lo; i < keys.Length && keys[i].StartsWith(q, StringComparison.Ordinal); i++)
        {
            int c = keyCity[i], city = c % Count;
            bool native = c >= Count;
            byNative[city] = byNative.TryGetValue(city, out var was) ? was && native : native;
        }
        foreach (var city in byNative.Keys.OrderByDescending(c => pop[c]).ThenBy(c => names[c], StringComparer.Ordinal).Take(max))
        {
            var p = PlaceOf(city);
            found.Add(new CityMatch(byNative[city] ? natives[city] : names[city], Subtitle(city), p, pop[city]));
        }
        return found;
    }

    /// The nearest place in the list and how far it is.
    public (Place place, double km, int population) Nearest(double latitude, double longitude)
    {
        int best = -1;
        double bestD = double.MaxValue, cos = Math.Cos(latitude * Math.PI / 180);
        for (int i = 0; i < Count; i++)
        {
            double dy = lat[i] / 100.0 - latitude, dx = lng[i] / 100.0 - longitude;
            if (dx > 180) dx -= 360; else if (dx < -180) dx += 360;
            double d = dy * dy + dx * dx * cos * cos;
            if (d < bestD) { bestD = d; best = i; }
        }
        var p = PlaceOf(best);
        return (p, Place.Km(latitude, longitude, p.Lat, p.Lng), pop[best]);
    }

    Place PlaceOf(int i) => new(names[i], countries[country[i]], lat[i] / 100.0, lng[i] / 100.0, zones[zone[i]]);

    string Subtitle(int i) => admin[i] >= 0 ? $"{admins[admin[i]]}, {countries[country[i]]}" : countries[country[i]];
}
