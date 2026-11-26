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

        internal static ScanResult Scan(Filters filters, string lang, Action<string> stage, Action<int, int> progress)
        {
            var result = new ScanResult();
            // the planets are named by the caller (the manager reads the game's own table, with
            // its rate limit, before starting this): a scan makes no request but Steam's
            var targets = filters.Planets != null && filters.Planets.Count > 0
                ? filters.Planets.Distinct().Take(MaxQueries).ToList()
                : new List<int>();
            if (targets.Count == 0) targets.Add(0);      // no planet named: one worldwide answer
            result.Planets = targets.Count(p => p > 0);

            var steam = Steam.Get();
            if (steam == null || !steam.Ok)
            {
                result.Error = steam != null && steam.ErrorMissing
                    ? Words.Get(lang, "steam_missing")
                    : Words.F(lang, "steam_init", steam != null ? steam.Error : "");
                return result;
            }

            result.Self = steam.SelfId.ToString(CultureInfo.InvariantCulture);

            var seen = new HashSet<ulong>();
            var ids = new List<ulong>();
            var done = 0;
            stage?.Invoke("scan");
            foreach (var planet in targets)
            {
                progress?.Invoke(++done, targets.Count);
                // like upstream: let Steam return every difficulty, then filter the rows here.
                // Filtering in Steam's query drops rooms whose published difficulty is stale.
                var batch = steam.Query(planet, 0, filters.PublicOnly, null, 0, 0);
                if (batch.Count >= SplitAt)
                    foreach (var range in new[] { new[] { "17", "5", "-2" }, new[] { "17", "6", "2" } })
                        foreach (var id in steam.Query(planet, 0, filters.PublicOnly, range[0],
                                     int.Parse(range[1], CultureInfo.InvariantCulture), int.Parse(range[2], CultureInfo.InvariantCulture)))
                            if (!batch.Contains(id)) batch.Add(id);
                foreach (var id in batch)
                    if (seen.Add(id)) ids.Add(id);
                if (ids.Count >= MaxRooms) break;
            }

            stage?.Invoke("rooms");
            steam.AskData(ids);
            var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var rooms = new List<Room>(ids.Count);
            foreach (var id in ids)
            {
                try { rooms.Add(steam.Read(id, lang, now)); }
                catch { }                                 // a lobby closed between the list and the read
            }

            var matched = rooms.Where(r => Match(r, filters)).ToList();
            var diffs = new HashSet<int>(filters.Difficulties);
            if (filters.Difficulty > 0) diffs.Add(filters.Difficulty);
            if (diffs.Count > 0) matched = matched.Where(r => diffs.Contains(r.Difficulty)).ToList();
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
}
