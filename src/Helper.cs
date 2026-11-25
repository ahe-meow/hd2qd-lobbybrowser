using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

            var request = Json.Serialize(new Dictionary<string, object>
            {
                ["lang"] = lang,
                ["home"] = home ?? "",
                ["filters"] = filters.ToJson(),
            });
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
                var answered = false;         // the helper said something final: rooms or an error
                var progressed = false;       // it had begun: Steam was up and planets were asked
                while ((line = process.StandardOutput.ReadLine()) != null)
                {
                    var message = Parse(line);
                    if (message == null) continue;
                    // a Json array comes back as an ArrayList, not a List<object>
                    if (message.ContainsKey("ready"))
                    {
                        progressed = true;                  // it is up; what follows is the scan
                    }
                    else if (message.TryGetValue("progress", out var p) && p is System.Collections.IList pair && pair.Count == 2)
                    {
                        progressed = true;
                        if (progress != null) progress(Convert.ToInt32(pair[0]), Convert.ToInt32(pair[1]));
                    }
                    else if (message.TryGetValue("rooms", out var rooms) && rooms is System.Collections.IList list)
                    {
                        answered = true;
                        Read(result, message, list, lang);
                    }
                    else if (message.TryGetValue("error", out var error) && !string.IsNullOrEmpty(Convert.ToString(error)))
                    {
                        answered = true;
                        result.Error = Convert.ToString(error);
                    }
                }
                process.WaitForExit(5000);

                // No answer at all: Steam ended the process (its Stop ends what it counts as the
                // game, and a scan is such a thing), or the program failed before it could speak.
                // Steam's own banner on stderr is not an error to show a player.
                if (result.Error == null && !answered)
                    result.Error = progressed
                        ? Words.Get(lang, "helper_stopped")
                        : Words.F(lang, "helper_nothing", Exit(process, errors));
            }
            catch (Exception e)
            {
                result.Error = Words.F(lang, "helper_failed", e.Message);
            }
            finally
            {
                try { process?.Dispose(); } catch { }
            }
            return result;
        }

        /// <summary>Why the helper said nothing: its exit code, and whatever it wrote to stderr
        /// that is not Steam's own banner.</summary>
        static string Exit(Process process, StringBuilder errors)
        {
            var said = new List<string>();
            foreach (var line in errors.ToString().Split('\n'))
            {
                var text = line.Trim();
                if (text == "" || text.StartsWith("Setting breakpad minidump") || text.StartsWith("SteamInternal_SetMinidumpSteamID")) continue;
                said.Add(text);
            }
            var code = "";
            try { if (process.HasExited) code = "exit " + process.ExitCode; } catch { }
            var detail = string.Join(" ", said.ToArray());
            return detail != "" ? detail : code != "" ? code : "no answer";
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

        /// <summary>An opt-in probe through Winsock: the helper carries the game's file name so
        /// an accelerator that routes by process name intercepts the connection. It never starts
        /// Steamworks, and this method returns nothing if the enabling file is absent.</summary>
        internal static Dictionary<string, int?> Winsock(string home, IEnumerable<string> addresses)
        {
            var result = new Dictionary<string, int?>();
            var ips = (addresses ?? new string[0]).Where(a => !string.IsNullOrEmpty(a)).Distinct().ToList();
            if (ips.Count == 0 || !File.Exists(Path.Combine(Folder(home), "winsock.txt")))
            {
                // opting out removes the lookalike too: no reason to leave a game-named file behind
                try { File.Delete(Path.Combine(Folder(home), "Helldivers2.exe")); } catch { }
                return result;
            }
            string exe;
            try { exe = Disguised(home); }
            catch { return result; }

            var request = Json.Serialize(new Dictionary<string, object> { ["winsock"] = ips });
            try
            {
                using (var process = Process.Start(new ProcessStartInfo
                {
                    FileName = exe, WorkingDirectory = Path.GetDirectoryName(exe),
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                }))
                {
                    if (process == null) return result;
                    process.StandardInput.Write(request);
                    process.StandardInput.Close();
                    string line;
                    while ((line = process.StandardOutput.ReadLine()) != null)
                    {
                        var message = Parse(line);
                        if (message == null || !message.TryGetValue("winsock", out var values) ||
                            !(values is Dictionary<string, object> table)) continue;
                        foreach (var pair in table)
                        {
                            var value = pair.Value == null ? 0 : Convert.ToInt32(pair.Value);
                            result[pair.Key] = value > 0 ? (int?)value : null;   // a 0 ms handshake is not a route
                        }
                    }
                    process.WaitForExit(5000);
                }
            }
            catch
            {
                // a failed probe is an empty answer, never a broken window
            }
            return result;
        }

        /// <summary>The probe helper under the game's name. Kept in the plugin's own folder —
        /// putting a lookalike in the game's install directory would be a much worse idea.</summary>
        static string Disguised(string home)
        {
            var directory = Folder(home);
            Directory.CreateDirectory(directory);
            var exe = Path.Combine(directory, "Helldivers2.exe");
            var assembly = typeof(Helper).Assembly;
            byte[] bytes;
            using (var resource = assembly.GetManifestResourceStream(Resource))
            {
                if (resource == null) throw new InvalidOperationException(Resource + " is not in this plugin");
                using (var memory = new MemoryStream())
                {
                    resource.CopyTo(memory);
                    bytes = memory.ToArray();
                }
            }
            // this file has a fixed name, so a newer helper can have exactly the same length:
            // compare the bytes, not the size, or a stale probe keeps answering 0 ms forever
            if (File.Exists(exe) && File.ReadAllBytes(exe).SequenceEqual(bytes)) return exe;
            var temporary = exe + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            if (File.Exists(exe)) File.Delete(exe);
            File.Move(temporary, exe);
            return exe;
        }

        /// <summary>The helper of this version, written beside the manager's settings when it is
        /// not there yet (or is another version's). Old helpers are removed.</summary>
        static string Folder(string home) => string.IsNullOrEmpty(home)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HD2QD.LobbyBrowser")
            : Path.Combine(home, "lobbybrowser");

        static string Ensure(string home)
        {
            var directory = Folder(home);
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
