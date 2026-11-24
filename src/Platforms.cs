using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Hd2Qd.LobbyBrowser
{
    /// <summary>Which platform a crossplay lobby's host plays on, as HD2LobbyBrowser reads it: the
    /// lobby's key 3 is the host's PlayFab entity id, and PlayFab's public profile object
    /// `testament_backing_platform` says 0 = PlayStation, 1 = Steam, 4 = Xbox.
    ///
    /// Reading it needs a PlayFab entity token, which needs a Steam session ticket — so this runs
    /// in the helper, with the ticket taken before SteamAPI_Shutdown (the ticket outlives the
    /// session; the interface does not). Answers are kept 30 minutes (5 minutes for a host with no
    /// profile) in a file beside the helper, so a refresh asks only about new hosts.
    ///
    /// It runs after the Steam session is closed, so the seconds Steam shows the game as running
    /// do not grow by a single PlayFab round trip.</summary>
    internal static class Platforms
    {
        const string Url = "https://974a9.playfabapi.com";
        const string Title = "974A9";
        const int TtlKnown = 1800;
        const int TtlUnknown = 300;
        const int Limit = 400;
        const int Workers = 16;
        const int BudgetSeconds = 8;      // a scan is not held hostage by 400 lookups
        static readonly Dictionary<int, string> Kinds = new Dictionary<int, string> { { 0, "psn" }, { 1, "steam" }, { 4, "xbox" } };
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        static string _token;
        static DateTime _expires;

        sealed class Known
        {
            internal DateTime At;
            internal string Platform = "";
        }

        internal static void Enrich(IList<Room> rooms, string ticket, string home)
        {
            var cache = Load(home);
            var now = DateTime.UtcNow;
            var todo = new List<Room>();
            foreach (var room in rooms)
            {
                if (room.HostEntity == "" || room.Platform != "") continue;
                Known hit;
                if (cache.TryGetValue(room.HostEntity, out hit) &&
                    now - hit.At < TimeSpan.FromSeconds(hit.Platform == "" ? TtlUnknown : TtlKnown))
                {
                    room.Platform = hit.Platform;
                    continue;
                }
                if (todo.Count < Limit) todo.Add(room);
            }
            if (todo.Count == 0) return;
            if (string.IsNullOrEmpty(ticket)) return;
            if (!Login(ticket)) return;

            var found = new ConcurrentDictionary<string, Known>();
            var deadline = DateTime.UtcNow.AddSeconds(BudgetSeconds);
            Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = Workers }, room =>
            {
                if (DateTime.UtcNow > deadline) return;        // the rest keep their cache for later
                var info = Profile(room.HostEntity);
                if (info == null) return;                      // network trouble: ask again next scan
                found[room.HostEntity] = new Known { At = now, Platform = info.Value.Key };
                room.Platform = info.Value.Key;
                if (room.Host == "" && !string.IsNullOrEmpty(info.Value.Value)) room.Host = info.Value.Value;
            });
            if (found.Count == 0) return;
            foreach (var pair in found) cache[pair.Key] = pair.Value;
            Save(home, cache);
        }

        static string FileOf(string home)
        {
            var folder = string.IsNullOrEmpty(home)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HD2QD.LobbyBrowser")
                : Path.Combine(home, "lobbybrowser");
            return Path.Combine(folder, "platforms.json");
        }

        static Dictionary<string, Known> Load(string home)
        {
            var cache = new Dictionary<string, Known>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var path = FileOf(home);
                if (!System.IO.File.Exists(path)) return cache;
                foreach (var pair in Json.Deserialize<Dictionary<string, object>>(System.IO.File.ReadAllText(path)))
                {
                    var entry = pair.Value as Dictionary<string, object>;
                    if (entry == null) continue;
                    DateTime at;
                    long ticks;
                    if (!entry.TryGetValue("at", out var stamp) || !long.TryParse(Convert.ToString(stamp), out ticks)) continue;
                    at = new DateTime(ticks, DateTimeKind.Utc);
                    cache[pair.Key] = new Known
                    {
                        At = at,
                        Platform = entry.TryGetValue("p", out var platform) ? Convert.ToString(platform) ?? "" : "",
                    };
                }
            }
            catch
            {
                // a broken cache is no cache
            }
            return cache;
        }

        static void Save(string home, Dictionary<string, Known> cache)
        {
            try
            {
                var path = FileOf(home);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var plain = new Dictionary<string, object>();
                foreach (var pair in cache)
                    plain[pair.Key] = new Dictionary<string, object> { ["at"] = pair.Value.At.Ticks, ["p"] = pair.Value.Platform };
                var temporary = path + ".tmp";
                System.IO.File.WriteAllText(temporary, Json.Serialize(plain));
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                System.IO.File.Move(temporary, path);
            }
            catch
            {
                // a cache that cannot be written costs a lookup, nothing else
            }
        }

        static bool Login(string ticket)
        {
            if (_token != null && DateTime.UtcNow < _expires) return true;
            try
            {
                var answer = Post("/Client/LoginWithSteam", new Dictionary<string, object>
                {
                    ["TitleId"] = Title,
                    ["SteamTicket"] = ticket,
                    ["CreateAccount"] = false,
                    ["InfoRequestParameters"] = new Dictionary<string, object> { ["GetUserAccountInfo"] = false },
                }, null);
                var data = answer != null && answer.TryGetValue("data", out var d) ? d as Dictionary<string, object> : null;
                var token = data != null && data.TryGetValue("EntityToken", out var t) ? t as Dictionary<string, object> : null;
                _token = token != null && token.TryGetValue("EntityToken", out var value) ? value as string : null;
                _expires = DateTime.UtcNow.AddMinutes(50);
                if (string.IsNullOrEmpty(_token)) { _token = null; return false; }
                return true;
            }
            catch
            {
                _token = null;
                return false;
            }
        }

        static KeyValuePair<string, string>? Profile(string entity)
        {
            var asked = Ask(entity);
            if (asked == null) return null;
            if (asked.Value.Key == 401 || asked.Value.Key == 403)
            {
                _token = null;                                        // a stale token: one more try
                return null;
            }
            if (asked.Value.Key == 404) return new KeyValuePair<string, string>("", "");
            var data = asked.Value.Value != null && asked.Value.Value.TryGetValue("data", out var d) ? d as Dictionary<string, object> : null;
            var profile = data != null && data.TryGetValue("Profile", out var p) ? p as Dictionary<string, object> : null;
            var objects = profile != null && profile.TryGetValue("Objects", out var o) ? o as Dictionary<string, object> : null;
            var kind = 0;
            var name = "";
            var platform = objects != null && objects.TryGetValue("testament_backing_platform", out var backing)
                ? (backing as Dictionary<string, object>) : null;
            if (platform != null && platform.TryGetValue("DataObject", out var value) && value != null)
                kind = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            var who = objects != null && objects.TryGetValue("testament_platform_user_name", out var person)
                ? (person as Dictionary<string, object>) : null;
            if (who != null && who.TryGetValue("DataObject", out var text) && text is string)
                name = (string)text;
            string known;
            return new KeyValuePair<string, string>(Kinds.TryGetValue(kind, out known) ? known : "", name);
        }

        /// <summary>The profile call: (status, answer); status 0 with a null answer means the
        /// network failed, which is not cached.</summary>
        static KeyValuePair<int, Dictionary<string, object>>? Ask(string entity)
        {
            try
            {
                var answer = Post("/Profile/GetProfile", new Dictionary<string, object>
                {
                    ["Entity"] = new Dictionary<string, object> { ["Id"] = entity, ["Type"] = "title_player_account" },
                    ["IncludeStatistics"] = false,
                    ["DataAsObject"] = true,
                }, _token);
                return new KeyValuePair<int, Dictionary<string, object>>(answer == null ? 404 : 200, answer);
            }
            catch (WebException e)
            {
                var response = e.Response as HttpWebResponse;
                if (response != null) return new KeyValuePair<int, Dictionary<string, object>>((int)response.StatusCode, null);
                return null;
            }
            catch
            {
                return null;
            }
        }

        static Dictionary<string, object> Post(string path, Dictionary<string, object> body, string token)
        {
            var request = (HttpWebRequest)WebRequest.Create(Url + path);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Timeout = 8000;
            request.ReadWriteTimeout = 8000;
            request.UserAgent = "hd2qd-lobbybrowser";
            if (token != null) request.Headers["X-EntityToken"] = token;
            var payload = Encoding.UTF8.GetBytes(Json.Serialize(body));
            request.ContentLength = payload.Length;
            using (var stream = request.GetRequestStream()) stream.Write(payload, 0, payload.Length);
            using (var response = request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream()))
                return Json.Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
        }
    }
}
