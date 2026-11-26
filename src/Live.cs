using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Web.Script.Serialization;

namespace Hd2Qd.LobbyBrowser
{
    /// <summary>The game's own planet table (api.helldivers2.dev): which planets are active, with
    /// how many players, so a scan can ask Steam about those rather than everything.
    ///
    /// The API allows <b>5 requests per 10 seconds</b> and hands back the counters that say so
    /// (X-RateLimit-Limit, X-RateLimit-Remaining), plus Retry-After on a 429. This keeps to it:
    /// one request per five minutes at most, whatever the player does with Refresh; the table and
    /// a cooldown are kept in a file beside the helper, so restarts do not ask again either, and a
    /// 429 is obeyed to the second and reported instead of being retried.
    ///
    /// It runs in the manager, before the helper starts: the scan itself now asks the API for
    /// nothing at all, and the seconds the game shows as running do not include the table.</summary>
    internal static class Live
    {
        const string Url = "https://api.helldivers2.dev/api/v1/planets";
        const string Official = "https://api.live.prod.thehelldiversgame.com/api/WarSeason/{0}/status";
        const string Client = "HD2QD.LobbyBrowser";
        const string Contact = "https://github.com/ahe-meow/hd2qd-lobbybrowser";
        const int FreshSeconds = 300;        // far under 5/10 s: the table changes over minutes
        const int FailedSeconds = 60;        // one failure is not an invitation to retry now
        const int WindowSeconds = 10;        // the API's window, said by its own headers
        const int ZeroRetrySeconds = 30;     // V30: an all-zero table is not "no planets"

        static readonly object Gate = new object();
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        static Dictionary<int, int> _counts = new Dictionary<int, int>();
        static Dictionary<int, string> _fronts = new Dictionary<int, string>();
        static double[] _here;                       // this PC's latitude/longitude, for per-room estimates
        static DateTime _at = DateTime.MinValue;
        static DateTime _until = DateTime.MinValue;
        static bool _loaded;

        /// <summary>The planets of these fronts with the most players, and a word for the status
        /// bar when the table could not be had (null when it could): the pick falls back to the
        /// last table read, or to nothing — one worldwide request — if there is none.</summary>
        internal static List<int> Pick(List<string> fronts, int difficulty, string home, string lang, out string note)
        {
            lock (Gate)
            {
                Load(home);
                note = null;
                var now = DateTime.UtcNow;
                if (now < _until)
                {
                    note = Words.F(lang, _counts.Count > 0 ? "api_cooling" : "api_cooling_empty",
                        _until.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture));
                }
                else if (!_loaded || (now - _at).TotalSeconds >= FreshSeconds
                    || (_counts.Count > 0 && _counts.Values.All(v => v <= 0)
                        && (now - _at).TotalSeconds >= ZeroRetrySeconds))
                {
                    Fetch(home, lang, out note);
                }
                return Choose(fronts, difficulty);
            }
        }

        /// <summary>Where this PC is, as [latitude, longitude]. One lookup per week, cached beside
        /// the other tables; null when neither service answered. Used only to turn the host
        /// coordinates the lobby publishes into per-room latency estimates — no host IP exists in
        /// lobby data, so this is an estimate, never a measured round trip to the host.</summary>
        internal static double[] Where(string home)
        {
            lock (Gate)
            {
                if (_here != null) return _here;
                var path = Path.Combine(Path.GetDirectoryName(FileOf(home)), "location.json");
                try
                {
                    if (File.Exists(path))
                    {
                        var saved = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
                        if (saved != null && saved.TryGetValue("lat", out var a) && saved.TryGetValue("lon", out var b))
                        {
                            var lat = Convert.ToDouble(a, CultureInfo.InvariantCulture);
                            var lon = Convert.ToDouble(b, CultureInfo.InvariantCulture);
                            if (Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180) return _here = new[] { lat, lon };
                        }
                    }
                }
                catch { }
                foreach (var url in new[] { "https://ipinfo.io/json", "https://ipwho.is/" })
                    try
                    {
                        var request = (HttpWebRequest)WebRequest.Create(url);
                        request.Timeout = 5000;
                        request.ReadWriteTimeout = 5000;
                        request.UserAgent = Client;
                        using (var response = (HttpWebResponse)request.GetResponse())
                        using (var reader = new StreamReader(response.GetResponseStream()))
                        {
                            var data = Json.Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                            double lat, lon;
                            if (data == null) continue;
                            if (data.TryGetValue("loc", out var loc))
                            {
                                var parts = Convert.ToString(loc).Split(',');
                                if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float,
                                        CultureInfo.InvariantCulture, out lat) || !double.TryParse(parts[1],
                                        NumberStyles.Float, CultureInfo.InvariantCulture, out lon)) continue;
                            }
                            else if (data.TryGetValue("latitude", out var a) && data.TryGetValue("longitude", out var b))
                            {
                                lat = Convert.ToDouble(a, CultureInfo.InvariantCulture);
                                lon = Convert.ToDouble(b, CultureInfo.InvariantCulture);
                            }
                            else continue;
                            if (Math.Abs(lat) > 90 || Math.Abs(lon) > 180) continue;
                            _here = new[] { lat, lon };
                            try
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(path));
                                File.WriteAllText(path, Json.Serialize(new Dictionary<string, object>
                                {
                                    ["lat"] = lat, ["lon"] = lon, ["at"] = DateTime.UtcNow.Ticks,
                                }));
                            }
                            catch { }
                            return _here;
                        }
                    }
                    catch { }
                return null;
            }
        }

        static List<int> Choose(List<string> fronts, int difficulty)
        {
            var picks = new List<int>();
            var lines = fronts != null && fronts.Count > 0
                ? fronts
                : new List<string> { "Terminids", "Automatons", "Illuminate" };
            var per = difficulty > 0 ? 5 : 4;
            foreach (var front in lines)
            {
                var candidates = _counts
                    .Where(kv => kv.Key > 0 && kv.Value > 0 && Front(kv.Key) == front)
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => kv.Key)
                    .Take(per);
                picks.AddRange(candidates);
            }
            return picks.Take(20).ToList();
        }

        /// <summary>The live table wins; the game's own static faction table fills in a planet
        /// the API did not label. Without this, one missing owner drops a whole front's planets
        /// and the scan falls back to a single worldwide query.</summary>
        static string Front(int planet) =>
            _fronts.TryGetValue(planet, out var live) && !string.IsNullOrEmpty(live) ? live
            : Tables.PlanetFront.TryGetValue(planet, out var known) ? known : null;

        static void Fetch(string home, string lang, out string note)
        {
            note = null;
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(Url);
                request.Timeout = 8000;
                request.ReadWriteTimeout = 8000;
                request.UserAgent = Client;
                request.Headers["X-Super-Client"] = Client;      // what the API asks for, by name
                request.Headers["X-Super-Contact"] = Contact;
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    using (var reader = new StreamReader(response.GetResponseStream()))
                        Parse(reader.ReadToEnd());
                    _at = DateTime.UtcNow;
                    _loaded = true;
                    _until = DateTime.MinValue;
                    // the window is spent: wait it out rather than bump into a 429
                    if (response.Headers["X-RateLimit-Remaining"] == "0")
                        _until = DateTime.UtcNow.AddSeconds(WindowSeconds);
                    Save(home);
                }
            }
            catch (WebException e)
            {
                string official;
                if (FetchOfficial(home, lang, out official)) { note = official; return; }
                var response = e.Response as HttpWebResponse;
                var status = response != null ? (int)response.StatusCode : 0;
                if (status == 429)
                {
                    var wait = 60;
                    int said;
                    if (response != null && int.TryParse(response.Headers["Retry-After"], NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out said) && said > 0) wait = said;
                    _until = DateTime.UtcNow.AddSeconds(wait);
                    note = Words.F(lang, _counts.Count > 0 ? "api_cooling" : "api_cooling_empty",
                        _until.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture));
                }
                else
                {
                    _until = DateTime.UtcNow.AddSeconds(FailedSeconds);
                    note = Words.F(lang, _counts.Count > 0 ? "api_failed" : "api_failed_empty", e.GetBaseException().Message);
                }
                Save(home);
            }
            catch (Exception e)
            {
                string official;
                if (FetchOfficial(home, lang, out official)) { note = official; return; }
                _until = DateTime.UtcNow.AddSeconds(FailedSeconds);
                note = Words.F(lang, _counts.Count > 0 ? "api_failed" : "api_failed_empty", e.GetBaseException().Message);
                Save(home);
            }
        }

        /// <summary>V30: the community table has a tight rate limit. When it fails — including a
        /// 429 — use the game's own WarSeason status, which carries planetStatus[].players and a
        /// numeric owner. The war id is the same one War.cs probes and writes, so a season change
        /// is picked up by both.</summary>
        static bool FetchOfficial(string home, string lang, out string note)
        {
            note = null;
            var start = 801;
            try
            {
                var path = Path.Combine(Path.GetDirectoryName(FileOf(home)), "war_effects.json");
                if (File.Exists(path))
                {
                    var saved = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
                    if (saved != null && saved.TryGetValue("war", out var war))
                        start = Convert.ToInt32(war, CultureInfo.InvariantCulture);
                }
            }
            catch { }
            for (var id = start; id < start + 4; id++)
            {
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(string.Format(CultureInfo.InvariantCulture, Official, id));
                    request.Timeout = 8000;
                    request.ReadWriteTimeout = 8000;
                    request.UserAgent = Client;
                    request.Headers["Accept-Language"] = "en-US";
                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (var reader = new StreamReader(response.GetResponseStream()))
                    {
                        var data = Json.Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                        if (data == null || !data.TryGetValue("planetStatus", out var raw) || !(raw is System.Collections.IList rows)) continue;
                        var counts = new Dictionary<int, int>();
                        var fronts = new Dictionary<int, string>();
                        foreach (var item in rows)
                        {
                            var planet = item as Dictionary<string, object>;
                            if (planet == null || !planet.TryGetValue("index", out var index)) continue;
                            var key = Convert.ToInt32(index, CultureInfo.InvariantCulture);
                            counts[key] = planet.TryGetValue("players", out var players) && players != null
                                ? (int)Math.Min(int.MaxValue, Convert.ToInt64(players, CultureInfo.InvariantCulture)) : 0;
                            if (!planet.TryGetValue("owner", out var owner)) continue;
                            var f = Convert.ToString(owner, CultureInfo.InvariantCulture);
                            if (f == "2" || f == "Terminids" || f == "Terminid") fronts[key] = "Terminids";
                            else if (f == "3" || f == "Automatons" || f == "Automaton") fronts[key] = "Automatons";
                            else if (f == "4" || f == "Illuminate") fronts[key] = "Illuminate";
                        }
                        _counts = counts;
                        _fronts = fronts;
                        _at = DateTime.UtcNow;
                        _loaded = true;
                        _until = DateTime.MinValue;
                        Save(home);
                        note = Words.Get(lang, "api_official");
                        return true;
                    }
                }
                catch { }
            }
            note = Words.Get(lang, "api_official_failed");
            return false;
        }

        static void Parse(string text)
        {
            var counts = new Dictionary<int, int>();
            var fronts = new Dictionary<int, string>();
            foreach (var item in Json.Deserialize<List<object>>(text))
            {
                var planet = item as Dictionary<string, object>;
                if (planet == null || !planet.ContainsKey("index")) continue;
                var index = Convert.ToInt32(planet["index"], CultureInfo.InvariantCulture);
                var stats = planet.TryGetValue("statistics", out var s) ? s as Dictionary<string, object> : null;
                counts[index] = stats != null && stats.TryGetValue("playerCount", out var c) && c != null
                    ? (int)Math.Min(int.MaxValue, Convert.ToInt64(c, CultureInfo.InvariantCulture)) : 0;
                var owner = planet.TryGetValue("currentOwner", out var o) ? o as string : null;
                if (owner == "Terminid" || owner == "Terminids") fronts[index] = "Terminids";
                else if (owner == "Automaton" || owner == "Automatons") fronts[index] = "Automatons";
                else if (owner == "Illuminate") fronts[index] = "Illuminate";
                else if (stats != null)
                {
                    var kills = new Dictionary<string, long>
                    {
                        ["Terminids"] = Value(stats, "terminidKills"),
                        ["Automatons"] = Value(stats, "automatonKills"),
                        ["Illuminate"] = Value(stats, "illuminateKills"),
                    };
                    var best = kills.OrderByDescending(kv => kv.Value).First();
                    if (best.Value > 0) fronts[index] = best.Key;
                }
            }
            _counts = counts;
            _fronts = fronts;
        }

        static long Value(Dictionary<string, object> stats, string key) =>
            stats.TryGetValue(key, out var v) && v != null ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : 0;

        static string FileOf(string home)
        {
            var folder = string.IsNullOrEmpty(home)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HD2QD.LobbyBrowser")
                : Path.Combine(home, "lobbybrowser");
            return Path.Combine(folder, "planets.json");
        }

        static void Load(string home)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                var path = FileOf(home);
                if (!File.Exists(path)) return;
                var state = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
                if (state == null) return;
                long ticks;
                if (state.TryGetValue("until", out var until) && long.TryParse(Convert.ToString(until), out ticks))
                    _until = new DateTime(ticks, DateTimeKind.Utc);
                if (state.TryGetValue("at", out var at) && long.TryParse(Convert.ToString(at), out ticks))
                    _at = new DateTime(ticks, DateTimeKind.Utc);
                if (state.TryGetValue("counts", out var counts) && counts is Dictionary<string, object> table)
                    foreach (var pair in table) _counts[Convert.ToInt32(pair.Key, CultureInfo.InvariantCulture)] = Convert.ToInt32(pair.Value, CultureInfo.InvariantCulture);
                if (state.TryGetValue("fronts", out var fronts) && fronts is Dictionary<string, object> names)
                    foreach (var pair in names) _fronts[Convert.ToInt32(pair.Key, CultureInfo.InvariantCulture)] = Convert.ToString(pair.Value);
            }
            catch
            {
                // a broken file is an empty table
            }
        }

        static void Save(string home)
        {
            try
            {
                var path = FileOf(home);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var counts = new Dictionary<string, object>();
                foreach (var pair in _counts) counts[pair.Key.ToString(CultureInfo.InvariantCulture)] = pair.Value;
                var fronts = new Dictionary<string, object>();
                foreach (var pair in _fronts) fronts[pair.Key.ToString(CultureInfo.InvariantCulture)] = pair.Value;
                var state = new Dictionary<string, object>
                {
                    ["until"] = _until.Ticks,
                    ["at"] = _at.Ticks,
                    ["counts"] = counts,
                    ["fronts"] = fronts,
                };
                var temporary = path + ".tmp";
                File.WriteAllText(temporary, Json.Serialize(state));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temporary, path);
            }
            catch
            {
                // the table lives in memory for this run, nothing else is lost
            }
        }
    }
}
