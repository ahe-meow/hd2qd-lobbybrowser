using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Web.Script.Serialization;

namespace Hd2Qd.LobbyBrowser
{
    /// <summary>The scan of HD2LobbyBrowser: Steam's lobby list is asked once per active planet
    /// (50 lobbies an answer at most, so a full planet is split by difficulty), the results are
    /// filtered locally, and the host names are asked for.</summary>
    internal static class Scanner
    {
        const int PerFront = 4;           // planets scanned per front
        const int PerFrontWithDifficulty = 5;
        const int MaxQueries = 20;
        const int MaxRooms = 900;
        const int SplitAt = 50;           // Steam's hard limit per request

        internal static ScanResult Scan(Filters filters, string lang, Action<int, int> progress)
        {
            var result = new ScanResult();
            var steam = Steam.Get();
            if (steam == null || !steam.Ok)
            {
                result.Error = steam != null && steam.ErrorMissing
                    ? Words.Get(lang, "steam_missing")
                    : Words.F(lang, "steam_init", steam != null ? steam.Error : "");
                return result;
            }

            result.Self = steam.SelfId.ToString(CultureInfo.InvariantCulture);
            var targets = Live.Pick(filters.Fronts, filters.Difficulty);
            if (targets.Count == 0) targets.Add(0);      // no planet filter: one worldwide answer
            targets = targets.Take(MaxQueries).ToList();
            result.Planets = targets.Count(p => p > 0);

            var seen = new HashSet<ulong>();
            var ids = new List<ulong>();
            var done = 0;
            foreach (var planet in targets)
            {
                progress?.Invoke(++done, targets.Count);
                var batch = steam.Query(planet, filters.Difficulty, filters.PublicOnly, null, 0, 0);
                if (filters.Difficulty == 0 && batch.Count >= SplitAt)
                    foreach (var range in new[] { new[] { "17", "5", "-2" }, new[] { "17", "6", "2" } })
                        foreach (var id in steam.Query(planet, 0, filters.PublicOnly, range[0],
                                     int.Parse(range[1], CultureInfo.InvariantCulture), int.Parse(range[2], CultureInfo.InvariantCulture)))
                            if (!batch.Contains(id)) batch.Add(id);
                foreach (var id in batch)
                    if (seen.Add(id)) ids.Add(id);
                if (ids.Count >= MaxRooms) break;
            }

            steam.AskData(ids);
            var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var rooms = new List<Room>(ids.Count);
            foreach (var id in ids)
            {
                try { rooms.Add(steam.Read(id, lang, now)); }
                catch { }                                 // a lobby closed between the list and the read
            }

            var matched = rooms.Where(r => Match(r, filters)).ToList();
            if (filters.Difficulty > 0)
                matched = matched.Where(r => r.Difficulty == filters.Difficulty).ToList();
            steam.Names(matched);
            foreach (var r in matched) r.Dress(lang);
            matched.Sort((a, b) => a.Members != b.Members ? b.Members.CompareTo(a.Members)
                : a.Planet != b.Planet ? a.Planet.CompareTo(b.Planet) : b.Difficulty.CompareTo(a.Difficulty));
            result.Rooms = matched;
            return result;
        }

        static bool Match(Room r, Filters f)
        {
            if (f.HideNoSession && !r.Session) return false;
            if (f.HideFull && r.Members >= (r.Limit > 0 ? r.Limit : 4)) return false;
            if (f.SosOnly && !r.Sos) return false;
            if (f.Fronts.Count > 0 && (r.Front == null || !f.Fronts.Contains(r.Front))) return false;
            return true;
        }
    }

    /// <summary>The planets the game reports as active (api.helldivers2.dev, the public API the
    /// game's community uses), so the scan spends its 20 requests on planets that have lobbies.</summary>
    internal static class Live
    {
        const string Url = "https://api.helldivers2.dev/api/v1/planets";
        static readonly object Gate = new object();
        static Dictionary<int, int> _counts = new Dictionary<int, int>();
        static Dictionary<int, string> _fronts = new Dictionary<int, string>();
        static DateTime _at = DateTime.MinValue;
        internal static string LastError;

        internal static List<int> Pick(List<string> fronts, int difficulty)
        {
            Refresh();
            var picks = new List<int>();
            var lines = fronts != null && fronts.Count > 0 ? fronts : new List<string> { "Terminids", "Automatons", "Illuminate" };
            var per = difficulty > 0 ? 5 : 4;
            foreach (var front in lines)
            {
                var candidates = _counts
                    .Where(kv => kv.Key > 0 && kv.Value > 0 && (_fronts.TryGetValue(kv.Key, out var f2) ? f2 : null) == front)
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => kv.Key)
                    .Take(per);
                picks.AddRange(candidates);
            }
            return picks.Take(20).ToList();
        }

        static void Refresh()
        {
            lock (Gate)
            {
                if ((DateTime.UtcNow - _at).TotalSeconds < 180) return;
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(Url);
                    request.Timeout = 8000;
                    request.ReadWriteTimeout = 8000;
                    request.UserAgent = "hd2qd-lobbybrowser";
                    request.Headers["X-Super-Client"] = "hd2qd-lobbybrowser";
                    request.Headers["X-Super-Contact"] = "local";
                    string text;
                    using (var response = request.GetResponse())
                    using (var reader = new StreamReader(response.GetResponseStream()))
                        text = reader.ReadToEnd();
                    var counts = new Dictionary<int, int>();
                    var fronts = new Dictionary<int, string>();
                    foreach (var item in new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<List<object>>(text))
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
                    _at = DateTime.UtcNow;
                }
                catch (Exception e)
                {
                    // offline, or the API is down: the scan falls back to one worldwide request,
                    // and the table is asked for again on the next refresh after a short wait
                    LastError = e.GetBaseException().Message;
                    _at = DateTime.UtcNow.AddSeconds(-135);
                }
            }
        }

        static long Value(Dictionary<string, object> stats, string key) =>
            stats.TryGetValue(key, out var v) && v != null ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : 0;
    }
}
