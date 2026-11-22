namespace Hidamari;

/// Where the wall's sun is: a point, its time zone (an IANA name such as Europe/Berlin, the same the card's page uses)
/// and the name the card shows. Plain .NET, no Windows: the checks on Linux use it too.
public sealed record Place(string Name, string Country, double Lat, double Lng, string Zone)
{
    /// Until the computer is located or a city chosen, as on the Mac and as in wall.html.
    public static readonly Place Berlin = new("Berlin", "Germany", 52.52, 13.405, "Europe/Berlin");

    /// Kilometres along the ground.
    public double Km(Place other) => Km(Lat, Lng, other.Lat, other.Lng);

    public static double Km(double lat1, double lng1, double lat2, double lng2)
    {
        const double r = 6371.0, rad = Math.PI / 180;
        double dLat = (lat2 - lat1) * rad, dLng = (lng2 - lng1) * rad;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1 * rad) * Math.Cos(lat2 * rad) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return 2 * r * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// The time zone as .NET knows it. On Windows .NET reads IANA names through ICU; an older Windows that does not
    /// know a new name gets its Windows name, and failing that the computer's own clock (the journal says so).
    public TimeZoneInfo TimeZone => Zones.Find(Zone);

    /// For the journal, which friends send to Ed: the point to one decimal (about 10 km), never more.
    public string ForJournal => $"{Name} ({Country}) {Lat:F1}, {Lng:F1}, {Zone}";

    public Dictionary<string, object?> ForPage() => new()
    {
        ["name"] = Name, ["country"] = Country, ["lat"] = Lat, ["lng"] = Lng, ["tz"] = Zone,
    };
}

public static class Zones
{
    static readonly Dictionary<string, TimeZoneInfo> known = new();
    /// Written once per name the first time it is not found, so the journal shows it.
    public static Action<string> Note = _ => { };

    public static TimeZoneInfo Find(string id)
    {
        lock (known)
        {
            if (known.TryGetValue(id, out var tz)) return tz;
            tz = Lookup(id, out var how);
            if (how != null) Note($"time zone {id}: {how}");
            known[id] = tz;
            return tz;
        }
    }

    /// The zone and, when it is not the zone asked for, what was taken instead.
    public static TimeZoneInfo Lookup(string id, out string? how)
    {
        how = null;
        if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz)) return tz;
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var win) && TimeZoneInfo.TryFindSystemTimeZoneById(win, out tz))
        {
            how = $"found as {win}";
            return tz;
        }
        how = "not known to this computer, its own clock is used";
        return TimeZoneInfo.Local;
    }

    /// The IANA name of the computer's own zone (Windows names its zones differently).
    public static string LocalName()
    {
        var local = TimeZoneInfo.Local;
        if (local.HasIanaId) return local.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out var iana) ? iana : "Etc/UTC";
    }
}
