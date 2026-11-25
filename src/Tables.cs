using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;

namespace Hd2Qd.LobbyBrowser
{
    /// <summary>The game's names, from the tables of HD2LobbyBrowser (github.com/QLongYan/
    /// HD2LobbyBrowser, MIT), embedded as Data\&lt;name&gt;.json: planets, their Chinese names and
    /// sectors, their front, and the mission hash names (Chinese and English).</summary>
    static class Tables
    {
        internal static readonly Dictionary<int, string> PlanetName = new Dictionary<int, string>();
        internal static readonly Dictionary<int, string> PlanetZh = new Dictionary<int, string>();
        internal static readonly Dictionary<int, string> PlanetSector = new Dictionary<int, string>();
        internal static readonly Dictionary<int, string> PlanetFront = new Dictionary<int, string>();
        internal static readonly Dictionary<string, string> SectorZh = new Dictionary<string, string>();
        internal static readonly Dictionary<string, string> MissionZh = new Dictionary<string, string>();
        internal static readonly Dictionary<string, string> MissionEn = new Dictionary<string, string>();

        static Tables()
        {
            try
            {
                foreach (var p in List("Data.planets.json"))
                {
                    var o = p as Dictionary<string, object>;
                    if (o == null) continue;
                    var i = Convert.ToInt32(o["i"], CultureInfo.InvariantCulture);
                    PlanetName[i] = o.TryGetValue("n", out var n) ? n as string : null;
                    PlanetSector[i] = o.TryGetValue("s", out var s) ? s as string : null;
                }
                foreach (var kv in Object("Data.planets_zh.json"))
                    PlanetZh[Convert.ToInt32(kv.Key, CultureInfo.InvariantCulture)] = kv.Value as string;
                foreach (var kv in Object("Data.factions.json"))
                {
                    var o = kv.Value as Dictionary<string, object>;
                    var f = o != null && o.TryGetValue("f", out var v) ? v as string : null;
                    if (!string.IsNullOrEmpty(f)) PlanetFront[Convert.ToInt32(kv.Key, CultureInfo.InvariantCulture)] = f;
                }
                foreach (var kv in Object("Data.sectors_zh.json")) SectorZh[kv.Key] = kv.Value as string;
                foreach (var kv in Object("Data.mission_names.json")) MissionZh[kv.Key] = kv.Value as string;
                foreach (var kv in Object("Data.mission_names_en.json")) MissionEn[kv.Key] = kv.Value as string;
            }
            catch
            {
                // a missing or broken table only costs names, never the scan
            }
            PlanetFront[0] = null;                      // Super Earth: no front
        }

        static string Read(string name)
        {
            var asm = typeof(Tables).Assembly;
            using (var r = new StreamReader(asm.GetManifestResourceStream(name)))
                return r.ReadToEnd();
        }

        static List<object> List(string name) =>
            new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<List<object>>(Read(name));

        static Dictionary<string, object> Object(string name) =>
            new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(Read(name));

        internal static string Planet(int index, string lang)
        {
            if (Words.IsChinese(lang) && PlanetZh.TryGetValue(index, out var zh)) return zh;
            return PlanetName.TryGetValue(index, out var en) ? en : "";
        }

        internal static string Sector(int index, string lang)
        {
            if (!PlanetSector.TryGetValue(index, out var s)) return "";
            return Words.IsChinese(lang) && SectorZh.TryGetValue(s, out var zh) ? zh : s;
        }

        internal static string Mission(string hash, string lang)
        {
            if (string.IsNullOrEmpty(hash)) return "";
            var table = Words.IsChinese(lang) ? MissionZh : MissionEn;
            return table.TryGetValue(hash, out var name) ? name : "";
        }

        internal static string FrontName(string front, string lang)
        {
            switch (front)
            {
                case "Terminids": return Words.Get(lang, "front_term");
                case "Automatons": return Words.Get(lang, "front_auto");
                case "Illuminate": return Words.Get(lang, "front_illu");
                default: return "";
            }
        }

        /// <summary>The front alone, for the table's narrow column: the chips may say "East ·
        /// Terminids", a row has only room for "Terminids".</summary>
        internal static string FrontShort(string front, string lang)
        {
            switch (front)
            {
                case "Terminids": return Words.Get(lang, "front_term_short");
                case "Automatons": return Words.Get(lang, "front_auto_short");
                case "Illuminate": return Words.Get(lang, "front_illu_short");
                default: return "";
            }
        }

        internal static string RegionName(string region, string lang)
        {
            switch ((region ?? "").Trim().ToLowerInvariant())
            {
                case "as": return Words.Get(lang, "r_as");
                case "na": return Words.Get(lang, "r_na");
                case "eu": return Words.Get(lang, "r_eu");
                case "oc": return Words.Get(lang, "r_oc");
                case "sa": return Words.Get(lang, "r_sa");
                case "me": return Words.Get(lang, "r_me");
                case "af": return Words.Get(lang, "r_af");
                default: return region ?? "";
            }
        }

        /// <summary>Every planet the game has, for the planet picker: named as the manager's
        /// language names it, with its sector and front, biggest names last within a sector.</summary>
        internal static List<PlanetRow> Planets(string lang)
        {
            var rows = new List<PlanetRow>();
            foreach (var pair in PlanetName)
            {
                var name = Planet(pair.Key, lang);
                if (string.IsNullOrEmpty(name)) continue;
                rows.Add(new PlanetRow
                {
                    Index = pair.Key,
                    Name = name,
                    Sector = Sector(pair.Key, lang),
                    Front = PlanetFront.TryGetValue(pair.Key, out var front) ? front : null,
                });
            }
            var order = new Dictionary<string, int> { ["Terminids"] = 0, ["Automatons"] = 1, ["Illuminate"] = 2 };
            rows.Sort((a, b) =>
            {
                var front = (a.Front == null ? 9 : order.TryGetValue(a.Front, out var x) ? x : 8)
                    .CompareTo(b.Front == null ? 9 : order.TryGetValue(b.Front, out var y) ? y : 8);
                if (front != 0) return front;
                var sector = string.Compare(a.Sector, b.Sector, StringComparison.CurrentCulture);
                return sector != 0 ? sector : string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
            });
            return rows;
        }
    }

    internal sealed class PlanetRow
    {
        internal int Index;
        internal string Name;
        internal string Sector;
        internal string Front;
    }

    /// <summary>A lobby's mission packet (lobby key 10), as HD2LobbyBrowser reads it: the front's
    /// number, the difficulty, and the mission hash, at fixed places in the hexadecimal text.</summary>
    static class Packet
    {
        internal static void Parse(string packet, out int front, out int difficulty, out string hash)
        {
            front = 0;
            difficulty = 0;
            hash = "";
            if (string.IsNullOrEmpty(packet) || packet.Length < 64 ||
                !packet.StartsWith("f3", StringComparison.Ordinal)) return;
            int f, d;
            if (!int.TryParse(packet.Substring(20, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out f)) return;
            if (!int.TryParse(packet.Substring(22, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out d)) return;
            if (f < 2 || f > 4) f = 0;
            if (d < 1 || d > 10) d = 0;
            var h = packet.Substring(52, 8).ToLowerInvariant();
            foreach (var c in h)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return;
            front = f;
            difficulty = d;
            hash = h;
        }

        internal static string Front(int number) =>
            number == 2 ? "Terminids" : number == 3 ? "Automatons" : number == 4 ? "Illuminate" : null;
    }
}
