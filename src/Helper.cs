using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Hd2Qd.LobbyBrowser
{
    /// <summary>Runs the scan in a small helper program, never in this process.
    ///
    /// Steam treats every process that initialises Steamworks for an App ID as part of that game:
    /// its processes are listed for the app, and Steam's Stop (the button in the library, or the
    /// close in the game's own window) ends them. Initialising it here would make the manager —
    /// and with it this window, and whatever deploy it is in the middle of — part of Helldivers 2,
    /// to be closed with the game. The helper takes that identity instead: it lives for one scan,
    /// answers, and exits, so the worst a Stop can take is one scan.
    ///
    /// The helper is this plugin's own file, embedded in the DLL (HD2QD.LobbyBrowser.Helper.exe,
    /// a 64-bit .NET Framework program, as steam_api64.dll is 64-bit) and written to the
    /// manager's settings folder on first use.</summary>
    internal static class Helper
    {
        const string Resource = "HD2QD.LobbyBrowser.Helper.exe";
        const int TimeoutSeconds = 180;
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        internal static ScanResult Scan(string home, Filters filters, string lang, Action<int, int> progress)
        {
            var result = new ScanResult();
            string exe;
            try
            {
                exe = Ensure(home);
            }
            catch (Exception e)
            {
                result.Error = Words.F(lang, "helper_failed", e.Message);
                return result;
            }

            var request = Json.Serialize(new Dictionary<string, object> { ["lang"] = lang, ["filters"] = filters.ToJson() });
            var errors = new StringBuilder();
            Process process = null;
            try
            {
                process = Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = Path.GetDirectoryName(exe),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                });
                if (process == null)
                {
                    result.Error = Words.F(lang, "helper_failed", "the process did not start");
                    return result;
                }
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) errors.AppendLine(e.Data); };
                process.BeginErrorReadLine();
                process.StandardInput.Write(request);
                process.StandardInput.Close();

                // a helper that hangs, or one Steam ended, must not hold the window: it is killed
                var watchdog = new Thread(() =>
                {
                    try
                    {
                        if (!process.WaitForExit(TimeoutSeconds * 1000)) process.Kill();
                    }
                    catch { }
                }) { IsBackground = true };
                watchdog.Start();

                string line;
                while ((line = process.StandardOutput.ReadLine()) != null)
                {
                    var message = Parse(line);
                    if (message == null) continue;
                    // a Json array comes back as an ArrayList, not a List<object>
                    if (message.TryGetValue("progress", out var p) && p is System.Collections.IList pair && pair.Count == 2 && progress != null)
                        progress(Convert.ToInt32(pair[0]), Convert.ToInt32(pair[1]));
                    else if (message.TryGetValue("rooms", out var rooms) && rooms is System.Collections.IList list)
                        Read(result, message, list, lang);
                    else if (message.TryGetValue("error", out var error) && !string.IsNullOrEmpty(Convert.ToString(error)))
                        result.Error = Convert.ToString(error);
                }
                process.WaitForExit(5000);
            }
            catch (Exception e)
            {
                result.Error = Words.F(lang, "helper_failed", e.Message);
            }
            finally
            {
                try { process?.Dispose(); } catch { }
            }
            if (result.Error == null && result.Rooms.Count == 0 && errors.Length > 0)
                result.Error = Words.F(lang, "helper_failed", errors.ToString().Trim());
            return result;
        }

        static Dictionary<string, object> Parse(string line)
        {
            if (string.IsNullOrWhiteSpace(line) || line[0] != '{') return null;
            try { return Json.Deserialize<Dictionary<string, object>>(line); }
            catch { return null; }
        }

        static void Read(ScanResult result, Dictionary<string, object> message, System.Collections.IList rooms, string lang)
        {
            if (message.TryGetValue("planets", out var planets) && planets != null)
                result.Planets = Convert.ToInt32(planets);
            if (message.TryGetValue("self", out var self) && self != null)
                result.Self = Convert.ToString(self);
            foreach (var item in rooms)
                if (item is Dictionary<string, object> d)
                {
                    var room = Room.FromJson(d);
                    room.Dress(lang);                       // the words the list shows, in this language
                    result.Rooms.Add(room);
                }
        }

        /// <summary>The helper of this version, written beside the manager's settings when it is
        /// not there yet (or is another version's). Old helpers are removed.</summary>
        static string Ensure(string home)
        {
            var directory = string.IsNullOrEmpty(home)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HD2QD.LobbyBrowser")
                : Path.Combine(home, "lobbybrowser");
            Directory.CreateDirectory(directory);
            var version = typeof(Helper).Assembly.GetName().Version.ToString(3);
            var exe = Path.Combine(directory, "HD2QD.LobbyBrowser.Helper." + version + ".exe");

            var assembly = typeof(Helper).Assembly;
            using (var resource = assembly.GetManifestResourceStream(Resource))
            {
                if (resource == null) throw new InvalidOperationException(Resource + " is not in this plugin");
                var length = (int)resource.Length;
                if (File.Exists(exe) && new FileInfo(exe).Length == length) return exe;
                var temporary = exe + ".tmp";
                using (var file = File.Create(temporary)) resource.CopyTo(file);
                if (File.Exists(exe)) File.Delete(exe);
                File.Move(temporary, exe);
            }

            foreach (var old in Directory.GetFiles(directory, "HD2QD.LobbyBrowser.Helper.*.exe"))
                if (!string.Equals(old, exe, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(old); } catch { }
                }
            return exe;
        }
    }
}
