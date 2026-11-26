using System;
using System.Collections.Generic;
using System.Text;
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

                var filters = request != null && request.TryGetValue("filters", out var f) && f is Dictionary<string, object> d
                    ? Filters.FromJson(d)
                    : new Filters();
                // said at once: a process that stops here was ended from outside (Steam's Stop),
                // which is not the same as one that never started
                Say(new Dictionary<string, object> { ["ready"] = 1 });
                var result = Scanner.Scan(filters, lang,
                    stage => Say(new Dictionary<string, object> { ["stage"] = stage }),
                    (done, total) => Console.Out.WriteLine("{\"progress\":[" + done + "," + total + "]}"));

                // both the PlayFab ticket and Steam's own relay measurements need the session:
                // take them now, then close Steam so the seconds it counts as game time stay short
                var ticket = Steam.Ticket();
                // V28: rich presence is only readable while the Steam session is up; read it
                // before Stop along with the ticket and the relay pings.
                var selfLobby = Steam.SelfLobby();
                Say(new Dictionary<string, object> { ["stage"] = "pings" });
                var pings = Steam.RegionPings();
                Steam.Stop();
                Say(new Dictionary<string, object> { ["stage"] = "platforms" });
                Platforms.Enrich(result.Rooms, ticket, home);

                Say(new Dictionary<string, object>
                {
                    ["planets"] = result.Planets,
                    ["self"] = result.Self,
                    ["selfLobby"] = selfLobby,
                    ["rooms"] = result.Rooms.ConvertAll(room => (object)room.ToJson()),
                    ["steamPings"] = pings ?? new Dictionary<string, int>(),
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

        static void Say(Dictionary<string, object> message)
        {
            Console.Out.WriteLine(Json.Serialize(message));
            Console.Out.Flush();
        }
    }
}
