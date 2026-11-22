using System.Diagnostics;
using System.Text.Json;

// Stage 3: the place and the weather, checked as a person uses them: Windows' own answer for the place, the city
// search with the mouse and with the keyboard, the chosen city's sun and time, Current Location, the live weather
// from the test source (no request leaves the machine), and what survives a restart.
static partial class Check
{
    static Dictionary<string, object?> PlaceOf(string name, string country, double lat, double lng, string tz) =>
        new() { ["name"] = name, ["country"] = country, ["lat"] = lat, ["lng"] = lng, ["tz"] = tz };

    static JsonElement SearchOf(Layout l) => l.Rects.GetProperty("search");
    static bool SearchOpen(Layout l) => SearchOf(l).GetProperty("open").GetBoolean();
    static List<(string text, bool lit, JsonElement rect)> SearchLines(Layout l) =>
        SearchOf(l).GetProperty("lines").EnumerateArray().Select(x => (x.GetProperty("text").GetString() ?? "", x.GetProperty("lit").GetBoolean(), x.GetProperty("rect"))).ToList();
    static int? Highlight(Layout l) => SearchOf(l).GetProperty("highlight").ValueKind == JsonValueKind.Number ? SearchOf(l).GetProperty("highlight").GetInt32() : null;
    static Point LineAt(Layout l, int i)
    {
        var r = SearchLines(l)[i].rect;
        return new Point((int)Math.Round(l.Window.X + (r[0].GetDouble() + r[2].GetDouble() / 2) * l.Scale),
                         (int)Math.Round(l.Window.Y + (r[1].GetDouble() + r[3].GetDouble() / 2) * l.Scale));
    }
    static string Lines_(Layout? l) => l == null ? "no layout" : string.Join(" | ", SearchLines(l).Take(5).Select(x => (x.lit ? "*" : "") + x.text));

    /// The wall's state of the next frame drawn for `why` (as the journal names it).
    static JsonElement? FrameFor(Run run, string why, double seconds = 150)
    {
        var f = run.Next(x => x.Contains(" frame ") && x.Contains($"({why})"), TimeSpan.FromSeconds(seconds));
        if (f == null) return null;
        Thread.Sleep(500);
        return WallState(run)?.GetProperty("state");
    }

    static string StateText(JsonElement? s) => s == null ? "no frame" : JsonSerializer.Serialize(s.Value);
    static double Num(JsonElement? s, string k) => s != null && s.Value.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN;

    /// Opens the search with a click on the place; the layout once the field is ready for typing.
    static Layout? OpenSearch(Run run, Layout l)
    {
        var s = ClickCard(run, l, "place", until: x => SearchOpen(x));
        Thread.Sleep(300);
        return s != null && SearchOpen(s) ? s : null;
    }

    /// Types `text` into the open search; the layout once the app's lines for it are shown (`first` begins the first city).
    static Layout? TypeCity(Run run, string text, string first)
    {
        var since = DateTime.UtcNow;
        Native.Type(text);
        return LayoutAfter(run, since, x => SearchOpen(x) && SearchLines(x).Count > 1 && SearchLines(x)[1].text.StartsWith(first), 30);
    }

    // ================= the place =================

    static void PlaceRun()
    {
        const string g = "place";
        // a first launch as on a new computer: Current Location, live weather on
        WriteSettings(new() { ["placeMode"] = "automatic", ["chosenPlace"] = null, ["liveWeather"] = true });
        var run = StartApp("place");
        try
        {
            // ---- what Windows says on this machine
            // written before or after the first frame: looked for in the whole journal
            string? said = null;
            for (var sw0 = Stopwatch.StartNew(); said == null && sw0.Elapsed < TimeSpan.FromSeconds(60); Thread.Sleep(500))
                said = run.Lines().FirstOrDefault(x => x.Contains("place: located (launch") || x.Contains("place: not located (launch"));
            facts["locationOnThisMachine"] = said;
            bool located = said?.Contains("place: located") == true;
            var st = WallState(run)?.GetProperty("state");
            var (l, _) = OpenByIcon(run);
            if (l == null) { Result(g, "the card opens", false, "not open"); return; }
            if (!located)
            {
                Result(g, "Windows gives no place here: the wall stays at Berlin, the card says Location unavailable",
                    said != null && l.Str("placeLabel").Contains("Location unavailable") && Math.Abs(Num(st, "lat") - 52.52) < 0.01,
                    $"journal: {said ?? "nothing within 60 s"}; the place's label: {l.Str("placeLabel")}; wall at {Num(st, "lat")}, {Num(st, "lng")}");
                Pictures.Crop(Pictures.Shot(null), l.Window, "card/location-unavailable.png");
                // one of the ways offered: a button to Windows' location settings in the search
                Command(run, new { @do = "locationButton", on = true });
                Thread.Sleep(800);
                l = ReadLayout(run) ?? l;
                var s = OpenSearch(run, l);
                if (s != null)
                {
                    Pictures.Crop(Pictures.Shot(null), s.Window, "card/location-unavailable-button.png");
                    Result(g, "the button to Windows' location settings, when it is offered", s.Has("locationSettings"), s.Has("locationSettings") ? "shown under Current Location" : "not shown");
                    Esc();
                    l = LayoutAfter(run, DateTime.UtcNow.AddSeconds(-0.2), x => !SearchOpen(x)) ?? s;
                }
                Command(run, new { @do = "locationButton", on = false });
            }
            else
                Result(g, "Windows gives a place here: it is named after the nearest city", "INFO", $"{said}; the card shows {l.Str("place")}");

            // ---- the search with the mouse
            var open = OpenSearch(run, l);
            bool focused = open != null && SearchOf(open).GetProperty("focused").GetBoolean();
            Result(g, "a click on the place opens the search, the field takes the keyboard", open != null && focused && Native.GetForegroundWindow() == CardHwnd(),
                open == null ? "not open" : $"open, typing goes to the field: {focused}; first line {Lines_(open)}");
            if (open == null) return;
            Pictures.Crop(Pictures.Shot(null), open.Window, "card/search-empty.png");
            var typed = TypeCity(run, "munchen", "München");
            Result(g, "typing munchen finds München first, without the accent, shown by the name typed, highlighted", typed != null && Highlight(typed) == 1,
                $"lines: {Lines_(typed ?? ReadLayout(run))}");
            if (typed == null) { Esc(); return; }
            Pictures.Crop(Pictures.Shot(null), typed.Window, "card/search-typing.png");
            run.Mark();
            Native.Click(LineAt(typed, 1));
            var chosen = run.Next("settings: place chosen: München", TimeSpan.FromSeconds(10));
            st = FrameFor(run, "a place was chosen");
            var after = LayoutAfter(run, DateTime.UtcNow.AddSeconds(-1), x => !SearchOpen(x)) ?? ReadLayout(run);
            Result(g, "a click on München: the wall's sun is Munich's, the card says München, the search closes",
                chosen != null && Math.Abs(Num(st, "lat") - 48.14) < 0.05 && Math.Abs(Num(st, "lng") - 11.58) < 0.05 && after != null && !SearchOpen(after) && after.Str("place") == "München",
                $"journal: {chosen ?? "nothing"}; wall {StateText(st)}; card {after?.Str("place")}");
            KeepForMac(run, "place-munich");
            // the weather is asked for the new place (more than 20 km from Berlin)
            Result(g, "a new place: the weather is asked for it", run.Lines().Any(x => x.Contains("weather: test source for München")),
                string.Join(" / ", run.Lines().Where(x => x.Contains("weather: test source for")).TakeLast(1)));
            l = after ?? l;

            // ---- the keyboard: the arrows go through Current Location and the cities, Enter picks
            open = OpenSearch(run, l);
            if (open != null && TypeCity(run, "new york", "New York") is { } ny)
            {
                Native.Keys(Native.VK_DOWN);
                var down = LayoutAfter(run, DateTime.UtcNow.AddSeconds(-0.1), x => Highlight(x) == 2, 5);
                Native.Keys(Native.VK_UP); Native.Keys(Native.VK_UP); Native.Keys(Native.VK_UP);
                var top = LayoutAfter(run, DateTime.UtcNow.AddSeconds(-0.1), x => Highlight(x) == 0, 5);
                Result(g, "the arrows move the highlight, up to Current Location and no further", down != null && top != null,
                    $"down: line {(down != null ? Highlight(down) : null)}; up three times: line {(top != null ? Highlight(top) : null)}; lines {Lines_(ny)}");
                run.Mark();
                Native.Keys(Native.VK_RETURN);
                var current = run.Next("settings: place: Current Location", TimeSpan.FromSeconds(10));
                var reply = Command(run, new { @do = "push" });
                Result(g, "Enter on Current Location: the place is found by itself again", current != null && reply.Contains("\"placeMode\":\"automatic\""),
                    $"journal: {current ?? "nothing"}; app: {reply}");
                st = FrameFor(run, "Current Location was chosen", 150);
                Result(g, "Current Location: the wall goes back to the last place Windows gave (Berlin until it gives one)",
                    st != null && (located || Math.Abs(Num(st, "lat") - 52.52) < 0.01), StateText(st));
            }
            else { Result(g, "the arrows and Enter", false, "the search did not open or found nothing"); Esc(); }
            Thread.Sleep(800);
            l = ReadLayout(run) ?? l;

            // ---- Escape and the back arrow leave the search, not the card
            open = OpenSearch(run, l);
            if (open != null)
            {
                Esc();
                var left = LayoutAfter(run, DateTime.UtcNow.AddSeconds(-0.2), x => !SearchOpen(x), 5);
                Result(g, "Escape leaves the search, the card stays open", left != null && CardSeen(), left != null ? "search closed, card open" : "still in the search");
                l = left ?? l;
                open = OpenSearch(run, l);
                if (open != null)
                {
                    var back = ClickCard(run, open, "searchBack", until: x => !SearchOpen(x));
                    Result(g, "the back arrow leaves the search", back != null && !SearchOpen(back) && CardSeen(), back != null && !SearchOpen(back) ? "closed" : "still open");
                    l = back ?? l;
                }
            }

            // ---- native names, regions, speed
            open = OpenSearch(run, l);
            if (open != null)
            {
                var moscow = TypeCity(run, "москва", "Москва");
                Result(g, "typed in Russian: Москва, shown by the name typed", moscow != null, Lines_(moscow ?? ReadLayout(run)));
                Esc(); l = LayoutAfter(run, DateTime.UtcNow.AddSeconds(-0.2), x => !SearchOpen(x), 5) ?? l;
            }
            open = OpenSearch(run, l);
            if (open != null)
            {
                var spring = TypeCity(run, "springfield", "Springfield");
                var lines = spring != null ? SearchLines(spring).Skip(1).Select(x => x.text).ToList() : new();
                Result(g, "several cities of one name: their regions tell them apart", lines.Count >= 3 && lines.Distinct().Count() == lines.Count && lines.Take(3).All(x => x.Count(c => c == ',') >= 2),
                    string.Join(" | ", lines.Take(6)));
                Pictures.Crop(Pictures.Shot(null), (spring ?? open).Window, "card/search-regions.png");
                Esc(); l = LayoutAfter(run, DateTime.UtcNow.AddSeconds(-0.2), x => !SearchOpen(x), 5) ?? l;
            }
            var speeds = run.Lines().Where(x => x.Contains("card: search ")).Select(x => Run.Number(x, " in ") ?? -1).Where(x => x >= 0).ToList();
            facts["searchMs"] = speeds;
            Result(g, "each letter finds its lines quickly", speeds.Count > 10 && speeds.Skip(1).Max() < 100,
                speeds.Count == 0 ? "no search in the journal" : $"{speeds.Count} searches; the first (the list is read) {speeds[0]} ms, then at most {speeds.Skip(1).DefaultIfEmpty(0).Max()} ms, mean {speeds.Skip(1).DefaultIfEmpty(0).Average():F1} ms");
            var read = run.Lines().FirstOrDefault(x => x.Contains("place: the list of"));
            facts["citiesRead"] = read;
            var typedText = run.Lines().Where(x => x.Contains("munchen") || x.Contains("springfield") || x.Contains("москва")).ToList();
            Result(g, "what is typed is never written in the journal", typedText.Count == 0, typedText.Count == 0 ? "not there" : typedText[0]);

            // ---- Tokyo with the keyboard only: its time on the card and on the wall; kept for the restart
            open = OpenSearch(run, l);
            if (open != null && TypeCity(run, "tokyo", "Tokyo") != null)
            {
                run.Mark();
                Native.Keys(Native.VK_RETURN);
                var said2 = run.Next("settings: place chosen: Tokyo", TimeSpan.FromSeconds(10));
                st = FrameFor(run, "a place was chosen");
                var t = LayoutAfter(run, DateTime.UtcNow.AddSeconds(-1), x => !SearchOpen(x) && x.Str("place") == "Tokyo", 10) ?? ReadLayout(run);
                var tokyoNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time"));
                var shown = t?.Rects.GetProperty("time").GetString() ?? "";
                bool timeOk = TimeSpan.TryParse(shown, out var ts) && Math.Abs((ts - tokyoNow.TimeOfDay).TotalMinutes) is < 3 or > 1437;
                Result(g, "Tokyo with the keyboard: its time on the card and on the wall",
                    said2 != null && Num(st, "utcOffset") == 540 && Math.Abs(Num(st, "lat") - 35.69) < 0.05 && timeOk,
                    $"journal: {said2 ?? "nothing"}; wall {StateText(st)}; card {t?.Str("place")} {shown} (Tokyo now {tokyoNow:HH:mm})");
                Pictures.Crop(Pictures.Shot(null), (t ?? open).Window, "card/place-tokyo.png");
                KeepForMac(run, "place-tokyo");
            }
            else { Result(g, "Tokyo with the keyboard", false, "the search did not open or found nothing"); Esc(); }

            // ---- every time zone of the list is known to this Windows
            File.Delete(Path.Combine(run.Folder, "zones.json"));
            Command(run, new { @do = "zones" });
            var zones = "";
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(30) && (zones = Run.Read(Path.Combine(run.Folder, "zones.json"))).Length == 0) Thread.Sleep(300);
            if (zones.Length > 0)
            {
                using var z = JsonDocument.Parse(zones);
                var unknown = z.RootElement.GetProperty("unknown");
                Result(g, "every time zone of the list is known to this Windows", unknown.GetArrayLength() == 0,
                    $"{z.RootElement.GetProperty("total")} zones: {z.RootElement.GetProperty("direct")} by their own name, {z.RootElement.GetProperty("viaWindows").GetArrayLength()} by Windows' name, " +
                    $"{unknown.GetArrayLength()} unknown{(unknown.GetArrayLength() > 0 ? ": " + string.Join(", ", unknown.EnumerateArray().Take(20)) : "")}; this computer's zone {z.RootElement.GetProperty("local")}");
                File.Copy(Path.Combine(run.Folder, "zones.json"), Path.Combine(outDir, "zones.json"), true);
            }
            else Result(g, "every time zone of the list is known to this Windows", false, "no answer");
            Esc();
            Thread.Sleep(1500);
        }
        finally { Quit(run); run.Dispose(); }

        // ---- the chosen place survives a restart
        using (var again = StartApp("place-restart"))
        {
            try
            {
                var s = WallState(again)?.GetProperty("state");
                var (l2, _) = OpenByIcon(again);
                Result(g, "after a restart: still Tokyo, on the wall and on the card", Num(s, "utcOffset") == 540 && Math.Abs(Num(s, "lat") - 35.69) < 0.05 && l2?.Str("place") == "Tokyo",
                    $"first frame {StateText(s)}; card {l2?.Str("place")}");
                Esc();
            }
            finally { Quit(again); }
        }

        // ---- a settings file of stage 2 (no place, no live weather in it) is read without complaint
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        File.WriteAllText(SettingsFile, "{\"version\":1,\"scene\":0,\"wall\":0,\"custom\":{\"L\":0.88,\"x\":0.5,\"y\":0.8,\"k\":0.1,\"rough\":1.0,\"shade\":null}," +
                                        "\"plant\":2,\"followsSun\":true,\"pinnedMinutes\":720.0,\"manualWeather\":\"clear\",\"screens\":\"all\",\"windowOnRight\":false,\"firstLaunchAnswered\":true}");
        using (var old = StartApp("place-stage2-file"))
        {
            try
            {
                var lines = old.Lines();
                var broken = lines.FirstOrDefault(x => x.Contains("settings: the file is broken"));
                var placeLine = lines.FirstOrDefault(x => x.Contains("place: automatic"));
                Result(g, "a settings file of stage 2: read as it is, Current Location and live weather on", broken == null && placeLine != null && placeLine.Contains("live weather on"),
                    $"{broken ?? placeLine ?? "nothing about the place"}");
            }
            finally { Quit(old); }
        }
    }

    // ================= the live weather (the test source) =================

    static void WeatherRun()
    {
        const string g = "weather";
        WriteSettings(new() { ["liveWeather"] = true });
        using var run = StartApp("weather");
        try
        {
            // ---- at launch: asked once; the point in the journal to one decimal
            var first = run.Lines().FirstOrDefault(x => x.Contains("weather: test source for Berlin (Germany) 52.5, 13.4") && x.Contains("(launch)"));
            var exact = run.Lines().Where(x => x.Contains("weather:") && x.Contains("52.52")).ToList();
            Result(g, "at launch the weather is asked once; the journal has the place to one decimal only", first != null && exact.Count == 0,
                $"{first ?? "not asked"}{(exact.Count > 0 ? $"; but: {exact[0]}" : "")}");

            // ---- what the source says, on the wall as the Mac draws it (Weather.drawn(in:))
            var cases = new (string name, int scene, double cloud, string kind, double wantCloud, int wantKind)[]
            {
                ("cloud-0.6-dapples", 0, 0.6, "clear", 0.6, 0),
                ("rain-window", 1, 0.9, "rain", 0.2, 1),
                ("snow-window", 1, 0.8, "snow", 0.3, 2),
                ("rain-blinds", 2, 0.9, "rain", 0.9, 1),
                ("overcast-water", 3, 1.0, "clear", 1.0, 0),
            };
            foreach (var c in cases)
            {
                Command(run, new { @do = "choose", type = "set", change = new { scene = c.scene } });
                FrameFor(run, "a scene was chosen");
                run.Mark();
                Command(run, new { @do = "weather", cloud = c.cloud, kind = c.kind, wind = 4.5 });
                var asked = run.Next("weather: test source for", TimeSpan.FromSeconds(10));
                var st = FrameFor(run, "the weather was asked");
                bool ok = Math.Abs(Num(st, "cloud") - c.wantCloud) < 0.001 && Num(st, "weather") == c.wantKind;
                if (ok) KeepForMac(run, $"weather-{c.name}");
                Result(g, $"{c.kind} {c.cloud} in scene {c.scene}: cloud {c.wantCloud}, weather {c.wantKind} on the wall", ok,
                    $"{asked ?? "not asked"}; wall {StateText(st)}");
            }

            // ---- a failed request: the wall is clear until an answer comes
            run.Mark();
            Command(run, new { @do = "weather", offline = true });
            var failed = run.Next("failed: no network", TimeSpan.FromSeconds(10));
            var clear = FrameFor(run, "the weather was asked");
            Result(g, "no network: the request fails, the wall shows a clear sky", failed != null && Num(clear, "cloud") == 0 && Num(clear, "weather") == 0,
                $"{failed ?? "no failure in the journal"}; wall {StateText(clear)}");

            // ---- an answer older than six hours: clear
            Command(run, new { @do = "choose", type = "set", change = new { scene = 0 } });
            FrameFor(run, "a scene was chosen");
            run.Mark();
            Command(run, new { @do = "weather", cloud = 0.8, kind = "clear" });
            var cloudy = FrameFor(run, "the weather was asked");
            Command(run, new { @do = "weather", offline = true, ask = false });
            run.Mark();
            Command(run, new { @do = "clock", ms = (double)DateTimeOffset.UtcNow.AddHours(7).ToUnixTimeMilliseconds() });
            var later = FrameFor(run, "check: the clock");
            Result(g, "seven hours later with no answer: the wall is clear again", Num(cloudy, "cloud") == 0.8 && Num(later, "cloud") == 0,
                $"before: cloud {Num(cloudy, "cloud")}; seven hours on: {StateText(later)}");
            Command(run, new { @do = "realClock" });
            FrameFor(run, "check: the real clock");

            // ---- Live weather off: the weather by hand, and nothing is asked whatever happens
            Command(run, new { @do = "weather", cloud = 0.5, kind = "clear", ask = false });
            Command(run, new { @do = "choose", type = "set", change = new { live = false } });
            var byHand = FrameFor(run, "Live weather off");
            int AskedNow() { var r = Command(run, new { @do = "push" }); var i = r.IndexOf("\"asked\":"); return i < 0 ? -1 : int.Parse(new string(r[(i + 8)..].TakeWhile(char.IsDigit).ToArray())); }
            int before = AskedNow();
            run.Mark();
            Command(run, new { @do = "choose", type = "currentLocation" });
            Command(run, new { @do = "fix", lat = 48.85, lng = 2.35 });
            Command(run, new { @do = "choose", type = "set", change = new { scene = 2 } });
            Command(run, new { @do = "clock", ms = (double)DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds() });
            Thread.Sleep(20000);
            Command(run, new { @do = "realClock" });
            int afterEvents = AskedNow();
            var asks = run.Since_("weather: test source for");
            Result(g, "Live weather off: the weather by hand, nothing asked after a new place, a new scene or hours later",
                Num(byHand, "cloud") == 0 && before >= 0 && afterEvents == before && asks.Count == 0,
                $"wall {StateText(byHand)}; requests {before} → {afterEvents}{(asks.Count > 0 ? $"; but: {asks[0]}" : "")}");
            if (full)
            {
                Thread.Sleep(TimeSpan.FromMinutes(10));
                int tenMinutes = AskedNow();
                Result(g, "Live weather off for ten minutes: no request", tenMinutes == before && run.Since_("weather: test source for").Count == 0, $"requests {before} → {tenMinutes}");
            }
            // the journal line of each request, for the report
            facts["weatherLines"] = run.Lines().Where(x => x.Contains("weather: ")).Take(30).ToArray();
        }
        finally { Quit(run); }
    }
}
