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
        const string Client = "HD2QD.LobbyBrowser";
        const string Contact = "https://github.com/ahe-meow/hd2qd-lobbybrowser";
        const int FreshSeconds = 300;        // far under 5/10 s: the table changes over minutes
        const int FailedSeconds = 60;        // one failure is not an invitation to retry now
        const int WindowSeconds = 10;        // the API's window, said by its own headers

        static readonly object Gate = new object();
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        static Dictionary<int, int> _counts = new Dictionary<int, int>();
        static Dictionary<int, string> _fronts = new Dictionary<int, string>();
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
                else if (!_loaded || (now - _at).TotalSeconds >= FreshSeconds)
                {
                    Fetch(home, lang, out note);
                }
                return Choose(fronts, difficulty);
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
                    .Where(kv => kv.Key > 0 && kv.Value > 0 && (_fronts.TryGetValue(kv.Key, out var f) ? f : null) == front)
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => kv.Key)
                    .Take(per);
                picks.AddRange(candidates);
            }
            return picks.Take(20).ToList();
        }

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
                _until = DateTime.UtcNow.AddSeconds(FailedSeconds);
                note = Words.F(lang, _counts.Count > 0 ? "api_failed" : "api_failed_empty", e.GetBaseException().Message);
                Save(home);
            }
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
