using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace Hd2Qd.LobbyBrowser
{
    /// <summary>A set of filters worth keeping: the planets, fronts, difficulty, switches, the
    /// missions and the sort, as HD2LobbyBrowser's preset holds them (that program keeps one; this
    /// keeps several, each with the name the player gave it).</summary>
    internal sealed class Preset
    {
        internal string Name = "";
        internal List<int> Planets = new List<int>();
        internal List<string> Fronts = new List<string>();
        internal int Difficulty;
        internal bool PublicOnly = true;
        internal bool HideNoSession = true;
        internal bool HideFull;
        internal bool SosOnly;
        internal List<string> Missions = new List<string>();
        internal int Sort;
        internal bool SortDesc;

        internal Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["name"] = Name,
            ["planets"] = Planets.ConvertAll(p => (object)p),
            ["fronts"] = new List<object>(Fronts),
            ["difficulty"] = Difficulty,
            ["publicOnly"] = PublicOnly,
            ["hideNoSession"] = HideNoSession,
            ["hideFull"] = HideFull,
            ["sosOnly"] = SosOnly,
            ["missions"] = new List<object>(Missions),
            ["sort"] = Sort,
            ["sortDesc"] = SortDesc,
        };

        internal static Preset FromJson(Dictionary<string, object> d)
        {
            var preset = new Preset();
            preset.Name = Text(d, "name");
            preset.Difficulty = Number(d, "difficulty");
            preset.PublicOnly = Flag(d, "publicOnly", true);
            preset.HideNoSession = Flag(d, "hideNoSession", true);
            preset.HideFull = Flag(d, "hideFull", false);
            preset.SosOnly = Flag(d, "sosOnly", false);
            preset.Sort = Number(d, "sort");
            preset.SortDesc = Flag(d, "sortDesc", false);
            if (d.TryGetValue("planets", out var planets) && planets is System.Collections.IList picked)
                foreach (var p in picked)
                    if (p != null) preset.Planets.Add(Convert.ToInt32(p));
            if (d.TryGetValue("fronts", out var fronts) && fronts is System.Collections.IList list)
                foreach (var f in list)
                    if (f is string s && s != "") preset.Fronts.Add(s);
            if (d.TryGetValue("missions", out var missions) && missions is System.Collections.IList hashes)
                foreach (var m in hashes)
                    if (m is string s) preset.Missions.Add(s);
            return preset;
        }

        static string Text(Dictionary<string, object> d, string key) =>
            d.TryGetValue(key, out var v) && v != null ? Convert.ToString(v) : "";

        static int Number(Dictionary<string, object> d, string key) =>
            d.TryGetValue(key, out var v) && v != null ? Convert.ToInt32(v) : 0;

        static bool Flag(Dictionary<string, object> d, string key, bool fallback) =>
            d.TryGetValue(key, out var v) && v is bool b ? b : fallback;
    }

    /// <summary>The presets on disk: the manager's settings folder keeps them beside the helper
    /// (a plugin's own files live there), a JSON array written whole, never a database.</summary>
    internal static class Presets
    {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        internal static string PathOf(string home)
        {
            var folder = string.IsNullOrEmpty(home)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HD2QD.LobbyBrowser")
                : Path.Combine(home, "lobbybrowser");
            return Path.Combine(folder, "presets.json");
        }

        internal static List<Preset> Load(string home)
        {
            var list = new List<Preset>();
            var path = PathOf(home);
            try
            {
                if (!File.Exists(path)) return list;
                var text = File.ReadAllText(path);
                foreach (var item in Json.Deserialize<List<object>>(text))
                    if (item is Dictionary<string, object> d) list.Add(Preset.FromJson(d));
            }
            catch
            {
                // a broken file is no presets, not a broken window
            }
            return list;
        }

        internal static string Save(string home, List<Preset> presets)
        {
            var path = PathOf(home);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var temporary = path + ".tmp";
                var items = new List<object>();
                foreach (var preset in presets) items.Add(preset.ToJson());
                File.WriteAllText(temporary, Json.Serialize(items));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temporary, path);
                return null;
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }
    }
}
