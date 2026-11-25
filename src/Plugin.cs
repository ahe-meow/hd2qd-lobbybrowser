using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;

namespace Hd2Qd.LobbyBrowser
{
    /// <summary>A plugin of HD2 Mod Quick Deploy: HD2LobbyBrowser's public lobby list as a window
    /// of the manager. The manager finds the one public class named Plugin in an HD2QD.*.dll and
    /// calls it by name; nothing is shared with it but strings, numbers, bools, delegates and
    /// Json shapes.</summary>
    public static class Plugin
    {
        /// <summary>The contract's version. A plugin of another one is refused.</summary>
        public const int Api = 2;

        /// <summary>"window": a window of its own, opened from the card (Open).</summary>
        public const string Kind = "window";

        public static string Title(string lang) => Words.Get(lang, "title");

        public static string Summary(string lang) => Words.Get(lang, "summary");

        /// <summary>Kind "window": the card's Open. host["window"](title, content) makes a window
        /// with the manager's own frame.</summary>
        public static void Open(Window owner, Dictionary<string, object> host) => BrowserWindow.Show(owner, host);

        /// <summary>Optional: commands added to the manager's command line, as rows of [name,
        /// help]. A name the manager has itself is left out.</summary>
        public static readonly string[][] Commands =
        {
            new[] { "lobbies", "print the public Helldivers 2 lobbies" },
        };
        /// <summary>Optional, with Commands: 0 done, 1 refused, 2 a wrong command line.</summary>
        public static int Run(string command, string[] args, TextWriter output, Dictionary<string, object> host)
        {
            var lang = Words.Lang(host) ?? "en";
            var home = host != null && host.TryGetValue("home", out var h) ? h as string : null;
            var filters = new Filters();
            var result = Helper.Scan(home, filters, lang, null, null);
            if (result.Error != null)
            {
                output.WriteLine(result.Error);
                return 1;
            }
            output.WriteLine("#\t" + string.Join("\t", new[]
            {
                Words.Get(lang, "col_planet"), Words.Get(lang, "col_front"), Words.Get(lang, "col_diff"),
                Words.Get(lang, "col_members"), Words.Get(lang, "col_mission"), Words.Get(lang, "col_region"),
                Words.Get(lang, "col_host"), Words.Get(lang, "col_age"),
            }));
            var n = 0;
            foreach (var room in result.Rooms)
                output.WriteLine(++n + "\t" + string.Join("\t", new[]
                {
                    room.PlanetText, room.FrontText, room.DifficultyText, room.MembersText,
                    room.MissionText, room.RegionText, room.HostText, room.AgeText,
                }));
            output.WriteLine(result.Planets == 0
                ? Words.F(lang, "found_world", result.Rooms.Count)
                : Words.F(lang, "found", result.Rooms.Count, result.Planets));
            return 0;
        }
    }
}
