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
    /// <summary>Planet-level variants for HD2LobbyBrowser V27/V28. Variants are war effects, not
    /// lobby data: they come from WarSeason status (community proxy, then the game's own API) and
    /// are cached beside the presets for five minutes. One request per refresh at most; a failed
    /// read keeps the last table and waits a minute before trying again.</summary>
    internal static class War
    {
        const string Community = "https://api.helldivers2.dev/raw/api/WarSeason/{0}/status";
        const string Official = "https://api.live.prod.thehelldiversgame.com/api/WarSeason/{0}/status";
        const string Client = "HD2QD.LobbyBrowser";
        const string Contact = "https://github.com/ahe-meow/hd2qd-lobbybrowser";
        const int FreshSeconds = 300;
        const int FailedSeconds = 60;

        static readonly object Gate = new object();
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        static Timer _warm;                  // upstream's WAR.warm(): read once at startup, then every TTL
        static Dictionary<int, List<string>> _planets = new Dictionary<int, List<string>>();
        static DateTime _at = DateTime.MinValue;
        static DateTime _until = DateTime.MinValue;
        static int _war = 801;                 // a season change is found by trying four ids
        static bool _loaded;
        internal static string LastError;          // why the last war-status read failed, for diagnostics

        /// <summary>HD2LobbyBrowser starts WAR.warm() as soon as it starts: the variant table is
        /// ready before the first scan, and refreshed every five minutes. Do the same here, once
        /// per process, so opening the variant picker does not depend on a scan having run.</summary>
        internal static void Warm(string home)
        {
            lock (Gate)
                if (_warm == null) _warm = new Timer(_ => Effects(home), null, 0, FreshSeconds * 1000);
        }

        internal static Dictionary<int, List<string>> Effects(string home)
        {
            lock (Gate)
            {
                Load(home);
                var now = DateTime.UtcNow;
                if (!_loaded || (now - _at).TotalSeconds >= FreshSeconds)
                {
                    if (now >= _until && !Fetch(home))
                    {
                        _until = now.AddSeconds(FailedSeconds);
                        _at = now;               // do not hammer the API on every Refresh
                    }
                }
                return _planets;
            }
        }

        /// <summary>Planets carrying any of the selected variants. A variant is planet-level, so a
        /// scan that only picks the busiest planets can miss it entirely; those planets join the
        /// scan targets when a variant is ticked.</summary>
        internal static List<int> PlanetsFor(Dictionary<int, List<string>> effects, IEnumerable<string> selected)
        {
            var wanted = new HashSet<string>(selected ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            if (wanted.Count == 0) return new List<int>();
            var planets = new List<int>();
            if (effects != null)
                foreach (var pair in effects)
                    if (pair.Value != null && pair.Value.Any(wanted.Contains)) planets.Add(pair.Key);
            planets.Sort();
            return planets;
        }

        static string FileOf(string home) => Path.Combine(string.IsNullOrEmpty(home)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HD2QD.LobbyBrowser")
            : home, "war_effects.json");

        static void Load(string home)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                var path = FileOf(home);
                if (!File.Exists(path)) return;
                var saved = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
                if (saved == null) return;
                if (saved.TryGetValue("war", out var war))
                    _war = Convert.ToInt32(war, CultureInfo.InvariantCulture);
                if (saved.TryGetValue("at", out var at))
                    _at = new DateTime(Convert.ToInt64(at, CultureInfo.InvariantCulture), DateTimeKind.Utc);
                if (saved.TryGetValue("planets", out var planets) && planets is Dictionary<string, object> map)
                    foreach (var kv in map)
                    {
                        var list = new List<string>();
                        if (kv.Value is System.Collections.IList values)
                            foreach (var v in values) if (v != null) list.Add(Convert.ToString(v, CultureInfo.InvariantCulture));
                        _planets[Convert.ToInt32(kv.Key, CultureInfo.InvariantCulture)] = list;
                    }
            }
            catch { }
        }

        static bool Fetch(string home)
        {
            for (var id = _war; id < _war + 4; id++)
            {
                var data = Get(string.Format(CultureInfo.InvariantCulture, Community, id))
                    ?? Get(string.Format(CultureInfo.InvariantCulture, Official, id));
                if (data == null || !data.ContainsKey("planetActiveEffects")) continue;
                var by = new Dictionary<int, List<string>>();
                var effects = data["planetActiveEffects"] as System.Collections.IList;
                if (effects != null)
                    foreach (var item in effects)
                    {
                        var effect = item as Dictionary<string, object>;
                        if (effect == null || !effect.TryGetValue("index", out var index) || !effect.TryGetValue("galacticEffectId", out var eid)) continue;
                        var key = Tables.VariantKey(Convert.ToString(eid, CultureInfo.InvariantCulture));
                        if (key == null) continue;
                        var planet = Convert.ToInt32(index, CultureInfo.InvariantCulture);
                        if (!by.TryGetValue(planet, out var list)) by[planet] = list = new List<string>();
                        if (!list.Contains(key)) list.Add(key);
                    }
                _planets = by;
                _war = id;
                _at = DateTime.UtcNow;
                Save(home);
                return true;
            }
            return false;
        }

        static Dictionary<string, object> Get(string url)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Timeout = 8000;
                request.ReadWriteTimeout = 8000;
                request.UserAgent = Client;
                // The community proxy rejects requests without these two headers; upstream sends
                // them. The game's own status wants a language instead.
                if (url.Contains("api.helldivers2.dev"))
                {
                    request.Headers["X-Super-Client"] = Client;
                    request.Headers["X-Super-Contact"] = Contact;
                }
                else request.Headers["Accept-Language"] = "en-US";
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                    return Json.Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
            }
            catch (Exception e)
            {
                LastError = e.GetBaseException().Message;
                return null;
            }
        }

        static void Save(string home)
        {
            try
            {
                var map = new Dictionary<string, object>();
                foreach (var kv in _planets) map[kv.Key.ToString(CultureInfo.InvariantCulture)] = kv.Value;
                File.WriteAllText(FileOf(home), Json.Serialize(new Dictionary<string, object>
                {
                    ["war"] = _war,
                    ["at"] = _at.Ticks,
                    ["planets"] = map,
                }));
            }
            catch { }
        }
    }
}
