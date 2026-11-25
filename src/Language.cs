using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace Hd2Qd.LobbyBrowser
{
    /// <summary>The window's language: the one HD2QD is showing, and the one the player may pick
    /// for this window instead.
    ///
    /// HD2QD keeps its language in its own settings file (`gui.json`, "lang", in the folder it
    /// hands us as host["home"]), and hands a snapshot of it over at every open. Reading the file
    /// says what the manager shows *now* — so a window opened after the manager's language was
    /// changed follows it, and the plugin's own choice is kept beside it as a small text file
    /// (absence of that file meaning "follow the manager", which is where it starts out).</summary>
    internal static class Language
    {
        internal const string Zh = "zh-CN";
        internal const string En = "en";

        /// <summary>The two tags this plugin has, from any tag the manager may use: zh, zh-CN,
        /// zh-TW and zh-Hans are all Chinese here; anything else is English.</summary>
        internal static string Tag(string tag) =>
            string.IsNullOrEmpty(tag) ? En
            : tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? Zh : En;

        internal static string PathOf(string home) => Path.Combine(Folder(home), "language.txt");

        /// <summary>The player's own choice, or null when the window follows the manager.</summary>
        internal static string Chosen(string home)
        {
            try
            {
                var path = PathOf(home);
                if (!File.Exists(path)) return null;
                var text = File.ReadAllText(path).Trim();
                return text == "" ? null : Tag(text);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Keep a language for this window; null or empty goes back to following HD2QD.</summary>
        internal static void Choose(string home, string tag)
        {
            try
            {
                var path = PathOf(home);
                if (string.IsNullOrEmpty(tag))
                {
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, Tag(tag));
            }
            catch
            {
                // a language that cannot be kept lasts for this window only
            }
        }

        /// <summary>What HD2QD shows now, from its own gui.json; the snapshot the manager handed
        /// over when the file says nothing.</summary>
        internal static string Manager(string home, string handed)
        {
            try
            {
                if (!string.IsNullOrEmpty(home))
                {
                    var path = Path.Combine(home, "gui.json");
                    if (File.Exists(path))
                    {
                        var settings = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
                        if (settings != null && settings.TryGetValue("lang", out var value) && value is string tag && tag != "")
                            return Tag(tag);
                    }
                }
            }
            catch
            {
                // an unreadable settings file leaves the snapshot the manager gave
            }
            return Tag(handed);
        }

        static string Folder(string home)
        {
            return string.IsNullOrEmpty(home)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HD2QD.LobbyBrowser")
                : Path.Combine(home, "lobbybrowser");
        }
    }
}
