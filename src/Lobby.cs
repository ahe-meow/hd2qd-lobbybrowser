using System;
using System.Collections.Generic;
using System.Globalization;

namespace Hd2Qd.LobbyBrowser
{
    /// <summary>One public lobby, read from Steam's lobby data (the keys HD2LobbyBrowser reads:
    /// 12 planet, 17 difficulty, 21 region, 19 privacy, 10 mission packet, 8 SOS, 18 created,
    /// 3 host, 16 front). It travels between the helper process and this one as a dictionary of
    /// strings and numbers (ToJson/FromJson).</summary>
    internal sealed class Room
    {
        internal ulong Lobby;
        internal string HostId = "";
        internal int Planet = -1;
        internal int Difficulty;
        internal int Members;
        internal int Limit;
        internal int Privacy = -1;
        internal bool Session;
        internal bool Sos;
        internal bool DifficultyGuess;
        internal string Front;
        internal string Region;
        internal string MissionHash;
        internal string Mission;
        internal string Host = "";
        internal long Age = -1;

        /// <summary>A crossplay lobby names its host by PlayFab entity id (lobby key 3, decimal,
        /// written here as 16 hexadecimal digits): the only way to ask PlayFab which platform the
        /// host plays on (Platforms.cs).</summary>
        internal string HostEntity = "";

        /// <summary>"steam", "psn", "xbox", or empty when it is not known (a crossplay lobby
        /// whose host PlayFab did not answer for).</summary>
        internal string Platform = "";

        /// <summary>This PC's latency to the lobby's region's Steam relay, in ms; null when the
        /// region is unknown or did not answer.</summary>
        internal int? Ping;

        // what the list shows, fixed in the manager's language when the window draws them
        public string PlanetText { get; set; }
        public string FrontText { get; set; }
        public string DifficultyText { get; set; }
        public string MembersText { get; set; }
        public string MissionText { get; set; }
        public string RegionText { get; set; }
        public string HostText { get; set; }
        public string AgeText { get; set; }
        public string SosText { get; set; }
        public string PingText { get; set; }
        public string PrivacyText { get; set; }
        public string PlatformText { get; set; }

        /// <summary>The ping column's words: measured, or a dash when the region is unknown.</summary>
        internal void ShowPing() => PingText = Ping.HasValue ? Ping.Value + " ms" : "—";

        internal void Dress(string lang)
        {
            PlanetText = Planet > 0 ? Tables.Planet(Planet, lang) : Words.Get(lang, "no_planet");
            FrontText = Tables.FrontShort(Front, lang);
            DifficultyText = Difficulty > 0 ? Words.F(lang, "diff_n", Difficulty) : "—";
            MembersText = Members + "/" + (Limit > 0 ? Limit : 4);
            RegionText = string.IsNullOrEmpty(Region) ? Words.Get(lang, "no_host") : Tables.RegionName(Region, lang);
            MissionText = !string.IsNullOrEmpty(Mission) ? Mission
                : Session ? Words.Get(lang, "unknown_mission") : "";
            HostText = Host != "" ? Host : Words.Get(lang, "no_host");
            SosText = Sos ? "SOS" : "";
            PrivacyText = Privacy == 0 ? Words.Get(lang, "priv_public")
                : Privacy == 1 ? Words.Get(lang, "priv_friends")
                : Privacy == 2 ? Words.Get(lang, "priv_private")
                : "—";
            PlatformText = Platform == "steam" ? Words.Get(lang, "plat_steam")
                : Platform == "psn" ? Words.Get(lang, "plat_psn")
                : Platform == "xbox" ? Words.Get(lang, "plat_xbox")
                : "—";
            if (Age < 0) AgeText = "";
            else if (Age < 60) AgeText = Words.Get(lang, "age_new");
            else if (Age < 3600) AgeText = Words.F(lang, "age_s", Age / 60);
            else if (Age < 86400) AgeText = Words.F(lang, "age_m", Age / 3600);
            else AgeText = Words.F(lang, "age_h", Age / 86400);
        }

        internal Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["lobby"] = Lobby.ToString(CultureInfo.InvariantCulture),
            ["hostId"] = HostId,
            ["planet"] = Planet,
            ["difficulty"] = Difficulty,
            ["members"] = Members,
            ["limit"] = Limit,
            ["privacy"] = Privacy,
            ["session"] = Session,
            ["sos"] = Sos,
            ["guess"] = DifficultyGuess,
            ["front"] = Front ?? "",
            ["region"] = Region ?? "",
            ["missionHash"] = MissionHash ?? "",
            ["mission"] = Mission ?? "",
            ["host"] = Host ?? "",
            ["age"] = Age,
            ["hostEntity"] = HostEntity ?? "",
            ["platform"] = Platform ?? "",
        };

        internal static Room FromJson(Dictionary<string, object> d)
        {
            var room = new Room();
            ulong lobby;
            if (d.TryGetValue("lobby", out var l) && ulong.TryParse(Convert.ToString(l, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out lobby))
                room.Lobby = lobby;
            room.HostId = Text(d, "hostId");
            room.Planet = Number(d, "planet", -1);
            room.Difficulty = Number(d, "difficulty", 0);
            room.Members = Number(d, "members", 0);
            room.Limit = Number(d, "limit", 0);
            room.Privacy = Number(d, "privacy", -1);
            room.Session = Flag(d, "session");
            room.Sos = Flag(d, "sos");
            room.DifficultyGuess = Flag(d, "guess");
            room.Front = Text(d, "front");
            room.Region = Text(d, "region");
            room.MissionHash = Text(d, "missionHash");
            room.Mission = Text(d, "mission");
            room.Host = Text(d, "host");
            room.Age = Number(d, "age", -1);
            room.HostEntity = Text(d, "hostEntity");
            room.Platform = Text(d, "platform");
            room.Ping = null;                 // measured by the window, not carried by the scan
            return room;
        }

        static string Text(Dictionary<string, object> d, string key) =>
            d.TryGetValue(key, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : "";

        static int Number(Dictionary<string, object> d, string key, int fallback) =>
            d.TryGetValue(key, out var v) && v != null ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : fallback;

        static bool Flag(Dictionary<string, object> d, string key) =>
            d.TryGetValue(key, out var v) && v is bool b && b;
    }

    internal sealed class Filters
    {
        internal List<string> Fronts = new List<string>();
        internal List<int> Planets = new List<int>();    // empty: the scan picks the active planets
        internal int Difficulty;                        // 0: any
        internal bool PublicOnly = true;
        internal bool HideNoSession = true;
        internal bool HideFull;
        internal bool SosOnly;

        internal Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["fronts"] = new List<object>(Fronts),
            ["planets"] = Planets.ConvertAll(p => (object)p),
            ["difficulty"] = Difficulty,
            ["publicOnly"] = PublicOnly,
            ["hideNoSession"] = HideNoSession,
            ["hideFull"] = HideFull,
            ["sosOnly"] = SosOnly,
        };

        internal static Filters FromJson(Dictionary<string, object> d)
        {
            var filters = new Filters();
            if (d.TryGetValue("fronts", out var fronts) && fronts is System.Collections.IList list)
                foreach (var f in list)
                    if (f is string s && s != "") filters.Fronts.Add(s);
            if (d.TryGetValue("planets", out var planets) && planets is System.Collections.IList picked)
                foreach (var entry in picked)
                    if (entry != null) filters.Planets.Add(Convert.ToInt32(entry, CultureInfo.InvariantCulture));
            if (d.TryGetValue("difficulty", out var diff) && diff != null) filters.Difficulty = Convert.ToInt32(diff, CultureInfo.InvariantCulture);
            if (d.TryGetValue("publicOnly", out var pub) && pub is bool p) filters.PublicOnly = p;
            if (d.TryGetValue("hideNoSession", out var hide) && hide is bool h) filters.HideNoSession = h;
            if (d.TryGetValue("hideFull", out var full) && full is bool f2) filters.HideFull = f2;
            if (d.TryGetValue("sosOnly", out var sos) && sos is bool s2) filters.SosOnly = s2;
            return filters;
        }
    }

    internal sealed class ScanResult
    {
        internal List<Room> Rooms = new List<Room>();
        internal int Planets;
        internal string Error;
        /// <summary>The Steam ID of the player whose client ran the scan: what a join names when
        /// the lobby publishes no Steam host (a crossplay lobby).</summary>
        internal string Self = "0";

        /// <summary>Steam's own relay latency (ms) per region, measured by the helper while its
        /// Steam session was up. Empty when Steam had no ping data — those regions use ICMP.</summary>
        internal Dictionary<string, int> SteamPings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    }

    internal static class JoinLink
    {
        internal const uint AppId = 553850;
        const ulong SteamIdMin = 76561197960265728;
        const ulong SteamIdMax = 76561202255233023;

        /// <summary>steam://joinlobby/&lt;app&gt;/&lt;lobby&gt;/&lt;steamid&gt;: the host when the
        /// lobby has one, else the player's own id (as the game's own invitations do).</summary>
        internal static string Url(ulong lobby, string host, string self) =>
            "steam://joinlobby/" + AppId + "/" + lobby + "/" + (string.IsNullOrEmpty(host) ? (string.IsNullOrEmpty(self) ? "0" : self) : host);

        /// <summary>A Steam ID a lobby may name: 17 digits in the individual-account range.</summary>
        internal static bool IsSteamId(string id)
        {
            ulong value;
            return id != null && id.Length >= 17 && ulong.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                && value >= SteamIdMin && value <= SteamIdMax;
        }
    }
}
