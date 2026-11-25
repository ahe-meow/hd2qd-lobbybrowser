using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Hd2Qd.LobbyBrowser
{
    /// <summary>The scan, in a process of its own that lives for one scan.
    ///
    /// It is the process Steam knows as Helldivers 2 — the one Steam's Stop ends — and it is the
    /// only one: the manager that starts it (HD2 Mod Quick Deploy, with this plugin's window and
    /// whatever deploy it is in the middle of) never initialises Steamworks, so closing the game
    /// can no longer close the manager.
    ///
    /// Reads the request as Json on stdin, writes Json lines on stdout: a progress line per
    /// planet, then the answer. See src/Helper.cs for the other half.</summary>
    internal static class Program
    {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            try
            {
                var request = Json.Deserialize<Dictionary<string, object>>(Console.In.ReadToEnd());
                var lang = request != null && request.TryGetValue("lang", out var l) ? l as string : "en";
                var home = request != null && request.TryGetValue("home", out var h) ? h as string : null;

                // A Winsock-only probe, asked for by Ping when the player enabled the disguised
                // helper. No Steamworks here: the only reason for this process to carry the
                // game's file name is an accelerator that routes by executable name.
                if (request != null && request.TryGetValue("winsock", out var targets) && targets is System.Collections.IList list)
                {
                    var answers = new Dictionary<string, object>();
                    Parallel.ForEach(list.Cast<object>().Select(Convert.ToString).Distinct(), ip =>
                    {
                        lock (answers) answers[ip] = Rtt(ip);
                    });
                    Say(new Dictionary<string, object> { ["winsock"] = answers });
                    return 0;
                }

                var filters = request != null && request.TryGetValue("filters", out var f) && f is Dictionary<string, object> d
                    ? Filters.FromJson(d)
                    : new Filters();
                // said at once: a process that stops here was ended from outside (Steam's Stop),
                // which is not the same as one that never started
                Say(new Dictionary<string, object> { ["ready"] = 1 });
                var result = Scanner.Scan(filters, lang, (done, total) =>
                    Console.Out.WriteLine("{\"progress\":[" + done + "," + total + "]}"));

                // the host platforms come from PlayFab, which wants the Steam ticket: taken while
                // the session is up; Steam is then closed and the lookups run on their own network
                // time, so the seconds Steam counts the game as running stay what they were
                var ticket = Steam.Ticket();
                Steam.Stop();
                Platforms.Enrich(result.Rooms, ticket, home);

                Say(new Dictionary<string, object>
                {
                    ["planets"] = result.Planets,
                    ["self"] = result.Self,
                    ["rooms"] = result.Rooms.ConvertAll(room => (object)room.ToJson()),
                    ["error"] = result.Error,
                });
                return result.Error == null ? 0 : 1;
            }
            catch (Exception e)
            {
                Say(new Dictionary<string, object>
                {
                    ["planets"] = 0,
                    ["rooms"] = new List<object>(),
                    ["error"] = e.GetBaseException().Message,
                });
                return 1;
            }
            finally
            {
                Steam.Stop();     // SteamAPI_Shutdown, so the Steam client sees this process go
            }
        }

        /// <summary>Time to a TCP handshake, in ms; a closed port answers with RST just as fast,
        /// so any of these ports gives the route's round trip. Winsock (not ICMP) is what a
        /// process-based accelerator hooks.</summary>
        static int? Rtt(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return null;
            foreach (var port in new[] { 443, 80, 27015 })
            {
                try
                {
                    var clock = Stopwatch.StartNew();
                    using (var client = new TcpClient())
                    {
                        if (!client.ConnectAsync(ip, port).Wait(900)) continue;
                        if (!client.Connected) continue;
                        clock.Stop();
                        // A name-based accelerator can accept the connect locally in well under a
                        // millisecond, before it has opened the remote leg. That is not a round
                        // trip: a whole-millisecond reading of zero is an invalid answer, not a
                        // 0 ms route. Try the next port; the caller falls back to ICMP if all fail.
                        if (clock.ElapsedMilliseconds < 1) continue;
                        return (int)Math.Min(int.MaxValue, clock.ElapsedMilliseconds);
                    }
                }
                catch
                {
                    // refused or timed out: try the next port
                }
            }
            return null;
        }

        static void Say(Dictionary<string, object> message)
        {
            Console.Out.WriteLine(Json.Serialize(message));
            Console.Out.Flush();
        }
    }
}
