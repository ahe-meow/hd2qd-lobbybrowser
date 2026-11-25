using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace Hd2Qd.LobbyBrowser
{
    /// <summary>The Steam client's own matchmaking, through the game's steam_api64.dll and the
    /// flat interface of the Steamworks SDK (the same calls HD2LobbyBrowser makes with ctypes).
    /// Nothing of the game is loaded or read: this is a second Steam API client, like the game's
    /// own, talking to the Steam client on this PC. SteamAPI_Init and SteamAPI_RunCallbacks both
    /// stay on one thread of their own; the plugin's own threads only add filters and read
    /// results, as the interface allows.</summary>
    internal sealed class Steam : IDisposable
    {
        internal const uint AppId = 553850;
        const uint LobbyMatchList = 510;                 // k_iSteamMatchmakingCallbacks_LobbyMatchList_t
        const int ResultCount = 100;
        const int MaxLobbies = 120;

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);

        [DllImport("kernel32", SetLastError = true)]
        static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32", SetLastError = true)]
        static extern bool FreeLibrary(IntPtr module);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitFlatFn(IntPtr error);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void VoidFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr InterfaceFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void AddDistanceFn(IntPtr self, int distance);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void AddCountFn(IntPtr self, int count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void AddNumberFn(IntPtr self, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, int value, int comparison);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate ulong RequestListFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate ulong LobbyAtFn(IntPtr self, int index);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int MembersFn(IntPtr self, ulong lobby);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int LimitFn(IntPtr self, ulong lobby);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr DataFn(IntPtr self, ulong lobby, [MarshalAs(UnmanagedType.LPUTF8Str)] string key);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate ulong SteamIdFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr PersonaFn(IntPtr self, ulong steamId);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)] delegate bool RequestDataFn(IntPtr self, ulong lobby);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)] delegate bool RequestInfoFn(IntPtr self, ulong steamId, [MarshalAs(UnmanagedType.I1)] bool nameOnly);
        // GetAuthSessionTicket( void *pTicket, int cbMaxTicket, uint32 *pcbTicket,
        //                       const SteamNetworkingIdentity *pIdentity ) — the identity is read
        // by the callee, so it must be a real (null) argument, not left out
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate uint TicketFn(IntPtr self, byte[] buffer, int max, out uint size, IntPtr identity);

        [return: MarshalAs(UnmanagedType.I1)] delegate bool CompletedFn(IntPtr self, ulong call, out byte failed);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)] delegate bool ResultFn(IntPtr self, ulong call, IntPtr buffer, int size, int expected, out byte failed);

        // ISteamNetworkingUtils: Steam's own, real measurements to its relay POPs. Optional —
        // an older steam_api64.dll simply has no such exports and the ICMP fallback stays.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void NetInitFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)] delegate bool NetFreshFn(IntPtr self, float maxAge);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NetPingFn(IntPtr self, uint pop, IntPtr via);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NetDirectFn(IntPtr self, uint pop);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NetCountFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NetListFn(IntPtr self, uint[] list, int size);

        static readonly object One = new object();
        static Steam _instance;
        static bool _tried;

        /// <summary>The one session of the process, started on first use; null when Steam could
        /// not be started, with the reason in <see cref="Error"/>.</summary>
        internal static Steam Get()
        {
            lock (One)
            {
                if (_tried) return _instance;
                _tried = true;
                _instance = new Steam();
                _instance.Start();
                return _instance;
            }
        }

        /// <summary>A Steam session ticket, as hexadecimal, or null: what PlayFab's
        /// LoginWithSteam wants to read a host's public profile (Platforms.cs). Asked for while
        /// the session is up — the ticket outlives SteamAPI_Shutdown, the interface does not.</summary>
        internal static string Ticket()
        {
            var steam = _instance;
            if (steam == null || !steam.Ok || steam._ticket == null) return null;
            try
            {
                var buffer = new byte[4096];
                uint size;
                var handle = steam._ticket(steam._user, buffer, buffer.Length, out size, IntPtr.Zero);
                if (handle == 0 || size == 0) return null;
                var text = new System.Text.StringBuilder((int)size * 2);
                for (var i = 0; i < size; i++) text.Append(buffer[i].ToString("X2"));
                return text.ToString();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>A three- or four-character POP code packed the way Steam packs it. The fourth
        /// character lives in the high byte — "bom2" is not "bom" (steamnetworkingtypes.h).</summary>
        internal static uint PopId(string code)
        {
            if (string.IsNullOrEmpty(code)) return 0;
            var id = (uint)(byte)code[0] << 16;
            if (code.Length > 1)
            {
                id |= (uint)(byte)code[1] << 8;
                if (code.Length > 2)
                {
                    id |= (byte)code[2];
                    if (code.Length > 3) id |= (uint)(byte)code[3] << 24;
                }
            }
            return id;
        }

        /// <summary>Steam's measured ping (ms) to each known relay POP, by packed POP id.
        /// GetDirectPingToPOP is the same kind of measurement the ICMP column tried to make;
        /// GetPingToDataCenter is Steam's best route through the relay network. Both are real
        /// measurements made by Steam, not a local TCP accept. Null when unavailable.</summary>
        internal static Dictionary<uint, int> Pings()
        {
            var steam = _instance;
            if (steam == null || !steam.Ok || steam._net == IntPtr.Zero) return null;
            try
            {
                if (steam._netFresh != null) steam._netFresh(steam._net, 60);   // refresh if stale, never block
                var count = steam._netCount != null ? steam._netCount(steam._net) : 0;
                if (count <= 0 || steam._netList == null) return null;
                var list = new uint[count];
                var got = steam._netList(steam._net, list, list.Length);
                var result = new Dictionary<uint, int>();
                for (var i = 0; i < got && i < list.Length; i++)
                {
                    var id = list[i];
                    var ms = steam._netDirect != null ? steam._netDirect(steam._net, id) : 0;
                    if (ms <= 0 && steam._netPing != null) ms = steam._netPing(steam._net, id, IntPtr.Zero);
                    if (ms > 0) result[id] = ms;
                }
                return result.Count > 0 ? result : null;
            }
            catch
            {
                return null;
            }
        }

        static Dictionary<string, List<uint>> _popRegions;

        /// <summary>The region table as POP ids, read from the same sdr_pops.json the ICMP
        /// fallback uses. Kept here so the helper can turn Steam's measurements into region
        /// latencies while its session is still up, and so Verify can check the encoding offline.</summary>
        internal static Dictionary<string, List<uint>> PopRegions()
        {
            if (_popRegions != null) return _popRegions;
            var table = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var reader = new StreamReader(typeof(Steam).Assembly.GetManifestResourceStream("Data.sdr_pops.json")))
                {
                    var data = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                        .Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                    foreach (var pair in data)
                    {
                        var ids = new List<uint>();
                        if (pair.Value is System.Collections.IList list)
                            foreach (var node in list)
                                if (node is Dictionary<string, object> d && d.TryGetValue("id", out var value)
                                    && value is string code && code.Length > 0) ids.Add(PopId(code));
                        if (ids.Count > 0) table[pair.Key] = ids;
                    }
                }
            }
            catch
            {
                // no table: no Steam region latencies, the manager's ICMP probes still work
            }
            _popRegions = table;
            return table;
        }

        /// <summary>Steam's own measured latency (ms) to the closest relay of each region. Called
        /// in the helper, before SteamAPI_Shutdown: only that process has a Steam session. Null
        /// when the DLL has no NetworkingUtils or Steam gathered no data in time.</summary>
        internal static Dictionary<string, int> RegionPings()
        {
            var pings = Pings();
            if (pings == null) return null;
            var table = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in PopRegions())
            {
                var best = 0;
                foreach (var id in pair.Value)
                    if (pings.TryGetValue(id, out var ms) && ms > 0 && (best == 0 || ms < best)) best = ms;
                if (best > 0) table[pair.Key] = best;
            }
            return table.Count > 0 ? table : null;
        }

        /// <summary>SteamAPI_Shutdown, and a moment for the pump thread to call it: a process the
        /// Steam client still holds would be one it may end with the game.</summary>
        internal static void Stop()
        {
            var steam = _instance;
            if (steam == null) return;
            steam.Dispose();
            Thread.Sleep(250);
        }

        readonly ManualResetEventSlim _started = new ManualResetEventSlim();
        Thread _pump;
        volatile bool _stop;

        IntPtr _module, _matchmaking, _friends, _user, _utils, _net;
        InitFlatFn _init;
        VoidFn _runCallbacks, _shutdown;
        InterfaceFn _getMatchmaking, _getFriends, _getUser, _getUtils, _getNet;
        NetInitFn _netInit;
        NetFreshFn _netFresh;
        NetPingFn _netPing;
        NetDirectFn _netDirect;
        NetCountFn _netCount;
        NetListFn _netList;
        AddDistanceFn _addDistance;
        AddCountFn _addCount;
        AddNumberFn _addNumber;
        RequestListFn _requestList;
        LobbyAtFn _lobbyAt;
        MembersFn _members, _limit;
        RequestDataFn _requestData;
        DataFn _getData;
        SteamIdFn _getSteamId;
        TicketFn _ticket;
        PersonaFn _persona;
        RequestInfoFn _requestInfo;
        CompletedFn _completed;
        ResultFn _result;

        internal bool Ok { get; private set; }
        internal string Error { get; private set; }
        internal ulong SelfId { get; private set; }
        internal string DllPath { get; private set; }

        void Start()
        {
            _pump = new Thread(Pump) { IsBackground = true, Name = "HD2QD.LobbyBrowser.Steam" };
            _pump.Start();
            if (!_started.Wait(TimeSpan.FromSeconds(30)))
                Error = "Steam did not answer within 30 seconds.";
        }

        void Pump()
        {
            try
            {
                Init();
            }
            catch (Exception e)
            {
                Error = e.Message;
            }
            finally
            {
                _started.Set();
            }
            if (!Ok) return;
            AppDomain.CurrentDomain.ProcessExit += (s, e) => Dispose();
            while (!_stop)
            {
                try { _runCallbacks(); } catch { }
                Thread.Sleep(20);
            }
            try { _shutdown(); } catch { }
        }

        void Init()
        {
            DllPath = FindGameApi();
            if (DllPath == null)
            {
                Error = "steam_api64.dll not found";
                ErrorMissing = true;
                return;
            }
            Environment.SetEnvironmentVariable("SteamAppId", AppId.ToString());
            Environment.SetEnvironmentVariable("SteamGameId", AppId.ToString());
            _module = LoadLibraryEx(DllPath, IntPtr.Zero, 0x8 /* LOAD_WITH_ALTERED_SEARCH_PATH */);
            if (_module == IntPtr.Zero) _module = LoadLibraryEx(DllPath, IntPtr.Zero, 0);
            if (_module == IntPtr.Zero)
            {
                Error = "LoadLibrary failed (" + Marshal.GetLastWin32Error() + ")";
                return;
            }

            _init = Fn<InitFlatFn>("SteamAPI_InitFlat");
            _runCallbacks = Fn<VoidFn>("SteamAPI_RunCallbacks");
            _shutdown = Fn<VoidFn>("SteamAPI_Shutdown");
            _getMatchmaking = Fn<InterfaceFn>("SteamAPI_SteamMatchmaking_v009");
            _getFriends = Fn<InterfaceFn>("SteamAPI_SteamFriends_v018");
            _getUser = Fn<InterfaceFn>("SteamAPI_SteamUser_v023");
            _getUtils = Fn<InterfaceFn>("SteamAPI_SteamUtils_v010");
            _addDistance = Fn<AddDistanceFn>("SteamAPI_ISteamMatchmaking_AddRequestLobbyListDistanceFilter");
            _addCount = Fn<AddCountFn>("SteamAPI_ISteamMatchmaking_AddRequestLobbyListResultCountFilter");
            _addNumber = Fn<AddNumberFn>("SteamAPI_ISteamMatchmaking_AddRequestLobbyListNumericalFilter");
            _requestList = Fn<RequestListFn>("SteamAPI_ISteamMatchmaking_RequestLobbyList");
            _lobbyAt = Fn<LobbyAtFn>("SteamAPI_ISteamMatchmaking_GetLobbyByIndex");
            _members = Fn<MembersFn>("SteamAPI_ISteamMatchmaking_GetNumLobbyMembers");
            _limit = Fn<MembersFn>("SteamAPI_ISteamMatchmaking_GetLobbyMemberLimit");
            _requestData = Fn<RequestDataFn>("SteamAPI_ISteamMatchmaking_RequestLobbyData");
            _getData = Fn<DataFn>("SteamAPI_ISteamMatchmaking_GetLobbyData");
            _getSteamId = Fn<SteamIdFn>("SteamAPI_ISteamUser_GetSteamID");
            _ticket = Fn<TicketFn>("SteamAPI_ISteamUser_GetAuthSessionTicket");
            _persona = Fn<PersonaFn>("SteamAPI_ISteamFriends_GetFriendPersonaName");
            _requestInfo = Fn<RequestInfoFn>("SteamAPI_ISteamFriends_RequestUserInformation");
            _completed = Fn<CompletedFn>("SteamAPI_ISteamUtils_IsAPICallCompleted");
            _result = Fn<ResultFn>("SteamAPI_ISteamUtils_GetAPICallResult");
            // the game's newer steam_api64.dll exports the v004 getter under this name; older
            // SDKs use v003. Without either, Pings() simply returns null and ICMP stays.
            _getNet = MaybeFn<InterfaceFn>("SteamAPI_SteamNetworkingUtils_v003");
            if (_getNet == null) _getNet = MaybeFn<InterfaceFn>("SteamAPI_SteamNetworkingUtils_SteamAPI_v004");
            if (_getNet != null)
            {
                _netInit = MaybeFn<NetInitFn>("SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess");
                _netFresh = MaybeFn<NetFreshFn>("SteamAPI_ISteamNetworkingUtils_CheckPingDataUpToDate");
                _netPing = MaybeFn<NetPingFn>("SteamAPI_ISteamNetworkingUtils_GetPingToDataCenter");
                _netDirect = MaybeFn<NetDirectFn>("SteamAPI_ISteamNetworkingUtils_GetDirectPingToPOP");
                _netCount = MaybeFn<NetCountFn>("SteamAPI_ISteamNetworkingUtils_GetPOPCount");
                _netList = MaybeFn<NetListFn>("SteamAPI_ISteamNetworkingUtils_GetPOPList");
            }

            var message = Marshal.AllocHGlobal(1024);
            try
            {
                for (var i = 0; i < 1024; i++) Marshal.WriteByte(message, i, 0);
                var rc = _init(message);
                if (rc != 0)
                {
                    Error = "rc=" + rc + " " + Utf8(message);
                    return;
                }
            }
            finally { Marshal.FreeHGlobal(message); }

            _matchmaking = _getMatchmaking();
            _friends = _getFriends();
            _user = _getUser();
            _utils = _getUtils();
            _net = _getNet != null ? _getNet() : IntPtr.Zero;
            if (_net != IntPtr.Zero && _netInit != null) _netInit(_net);   // start the real ping measurement
            SelfId = _getSteamId(_user);
            Ok = true;
        }

        internal bool ErrorMissing { get; private set; }

        T Fn<T>(string name) where T : class
        {
            var p = GetProcAddress(_module, name);
            if (p == IntPtr.Zero) throw new MissingMethodException(name + " is missing from " + Path.GetFileName(DllPath));
            return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }

        T MaybeFn<T>(string name) where T : class
        {
            var p = GetProcAddress(_module, name);
            return p == IntPtr.Zero ? null : (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }

        static string Utf8(IntPtr text)
        {
            if (text == IntPtr.Zero) return "";
            var length = 0;
            while (Marshal.ReadByte(text, length) != 0) length++;
            var bytes = new byte[length];
            Marshal.Copy(text, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }

        string Data(ulong lobby, string key) => Utf8(_getData(_matchmaking, lobby, key));

        /// <summary>One lobby list request, as HD2LobbyBrowser makes it: worldwide, 100 results,
        /// with the filters Steam takes itself.</summary>
        internal List<ulong> Query(int planet, int difficulty, bool publicOnly, string extraKey, int extraValue, int extraComparison)
        {
            if (!Ok) return new List<ulong>();
            lock (Call)
            {
                _addDistance(_matchmaking, 3);
                _addCount(_matchmaking, ResultCount);
                if (planet > 0) _addNumber(_matchmaking, "12", planet, 0);
                if (difficulty > 0) _addNumber(_matchmaking, "17", difficulty, 0);
                if (publicOnly) _addNumber(_matchmaking, "19", 0, 0);
                if (extraKey != null) _addNumber(_matchmaking, extraKey, extraValue, extraComparison);

                var call = _requestList(_matchmaking);
                var count = -1;
                if (call != 0)
                {
                    var until = DateTime.UtcNow.AddSeconds(2.5);
                    while (DateTime.UtcNow < until)
                    {
                        byte failed;
                        if (_completed(_utils, call, out failed))
                        {
                            var buffer = Marshal.AllocHGlobal(8);
                            try
                            {
                                byte callFailed;
                                if (_result(_utils, call, buffer, 4, (int)LobbyMatchList, out callFailed) && callFailed == 0)
                                    count = Marshal.ReadInt32(buffer);
                            }
                            finally { Marshal.FreeHGlobal(buffer); }
                            break;
                        }
                        Thread.Sleep(20);
                    }
                }
                else Thread.Sleep(1200);

                var lobbies = new List<ulong>();
                for (var i = 0; i < MaxLobbies; i++)
                {
                    var id = _lobbyAt(_matchmaking, i);
                    if (id == 0) break;
                    lobbies.Add(id);
                    if (count >= 0 && lobbies.Count >= count) break;
                }
                return lobbies;
            }
        }

        static readonly object Call = new object();

        /// <summary>Asks Steam for every lobby's data, then waits for the answers to begin, as the
        /// game's own list does (Steam fills them in asynchronously). A few lobbies are looked at,
        /// not one: the first may publish nothing at all, and waiting the whole cap for it made
        /// every scan a second longer.</summary>
        internal void AskData(IList<ulong> lobbies)
        {
            if (!Ok || lobbies.Count == 0) return;
            foreach (var id in lobbies) _requestData(_matchmaking, id);
            var sample = Math.Min(8, lobbies.Count);
            var until = DateTime.UtcNow.AddSeconds(1.5);
            while (DateTime.UtcNow < until)
            {
                for (var i = 0; i < sample; i++)
                    if (Data(lobbies[i], "12") != "" || Data(lobbies[i], "1") != "") return;
                Thread.Sleep(20);
            }
        }

        internal Room Read(ulong lobby, string lang, int now)
        {
            var room = new Room { Lobby = lobby };
            var planet = Data(lobby, "12");
            int index;
            if (int.TryParse(planet, out index)) room.Planet = index;

            var packet = Data(lobby, "10");
            int front, difficulty;
            string hash;
            Packet.Parse(packet, out front, out difficulty, out hash);
            room.Session = !string.IsNullOrEmpty(packet);
            room.MissionHash = hash;
            room.Mission = Tables.Mission(hash, lang);
            room.MissionText = room.Mission != "" ? room.Mission : Words.Get(lang, "unknown_mission");

            var byKey = Data(lobby, "16");
            room.Front = Packet.Front(front) ?? Packet.Front(byKey == "2" ? 2 : byKey == "3" ? 3 : byKey == "4" ? 4 : 0)
                         ?? (Tables.PlanetFront.TryGetValue(room.Planet, out var f) ? f : null);
            var key17 = Data(lobby, "17");
            int diff17;
            room.Difficulty = difficulty > 0 ? difficulty : int.TryParse(key17, out diff17) && diff17 >= 1 && diff17 <= 10 ? diff17 : 0;
            room.DifficultyGuess = difficulty == 0 && room.Difficulty > 0;

            room.Region = Data(lobby, "21");
            // keys 22/23 are the host's published longitude/latitude in hundredths of a degree
            // (-12230/3754 is San Francisco, 11415/2224 Shenzhen). The region is only a continent;
            // these are what let rooms in one region get different latency estimates.
            int hundredths;
            if (int.TryParse(Data(lobby, "22"), out hundredths)) room.Lon = hundredths / 100.0;
            if (int.TryParse(Data(lobby, "23"), out hundredths)) room.Lat = hundredths / 100.0;
            room.Sos = Data(lobby, "8") == "1";
            var privacy = Data(lobby, "19");
            int p;
            room.Privacy = int.TryParse(privacy, out p) ? p : -1;
            room.Members = _members(_matchmaking, lobby);
            room.Limit = _limit(_matchmaking, lobby);
            var created = Data(lobby, "18");
            long at;
            if (long.TryParse(created, out at) && at > 1500000000 && at <= now + 60)
            {
                // keep the absolute timestamp: the manager recomputes the age against its own
                // clock, so the column keeps moving while the window stays open
                room.Created = at;
                room.Age = now - at;
            }
            // key 3 is the host's Steam ID only while the lobby is not crossplay: with crossplay
            // off it is a PlayFab entity id, which a join URL cannot name (JoinLink.Url falls back
            // to the player's own id then) — but it is exactly what PlayFab wants to say which
            // platform the host plays on (Platforms.cs)
            var crossplay = Data(lobby, "20");
            var host = Data(lobby, "3");
            if (crossplay == "1")
            {
                room.Platform = "steam";                  // crossplay off: a Steam lobby, no lookup needed
                room.HostId = JoinLink.IsSteamId(host) ? host : "";
            }
            else if (crossplay == "0")
            {
                ulong entity;
                if (ulong.TryParse(host, out entity) && host.Length >= 17)
                    room.HostEntity = entity.ToString("X16");
            }
            else
            {
                room.HostId = JoinLink.IsSteamId(host) ? host : "";
                if (room.HostId != "") room.Platform = "steam";
            }
            return room;
        }

        /// <summary>The host's name when Steam knows the host as a Steam user (the lobby is not
        /// crossplay, or the host is on Steam); empty otherwise. Asked for, then looked for until
        /// every name is there or a moment has passed — a fixed sleep waited for the slowest
        /// answer even when they were all in.</summary>
        internal void Names(IList<Room> rooms)
        {
            var pending = new List<Room>();
            foreach (var r in rooms)
                if (r.HostId.Length >= 17 && ulong.TryParse(r.HostId, out _)) pending.Add(r);
            if (pending.Count == 0) return;
            foreach (var r in pending)
                try { _requestInfo(_friends, ulong.Parse(r.HostId), false); } catch { }

            var until = DateTime.UtcNow.AddMilliseconds(600);
            while (true)
            {
                var known = 0;
                foreach (var r in pending)
                {
                    if (r.Host != "") { known++; continue; }
                    try
                    {
                        var name = Utf8(_persona(_friends, ulong.Parse(r.HostId)));
                        if (name != "" && name != "[unknown]") { r.Host = name; known++; }
                    }
                    catch { known++; }                       // a host Steam will never name
                }
                if (known >= pending.Count || DateTime.UtcNow >= until) return;
                Thread.Sleep(40);
            }
        }

        public void Dispose()
        {
            _stop = true;
        }

        /// <summary>The game's own steam_api64.dll: the environment variable first, then the
        /// Steam library folders and the usual drives.</summary>
        static string FindGameApi()
        {
            var over = Environment.GetEnvironmentVariable("HD2_STEAM_API");
            if (!string.IsNullOrEmpty(over) && File.Exists(over)) return over;

            var roots = new List<string>();
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    var steam = key != null ? key.GetValue("SteamPath") as string : null;
                    if (!string.IsNullOrEmpty(steam))
                    {
                        roots.Add(steam);
                        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                        if (File.Exists(vdf))
                            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s*\"([^\"]+)\""))
                                roots.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
                    }
                }
            }
            catch { }

            var candidates = new List<string>();
            foreach (var root in roots)
                candidates.Add(Path.Combine(root, "steamapps", "common", "Helldivers 2", "bin", "steam_api64.dll"));
            foreach (var drive in "CDEFGH")
            {
                candidates.Add(drive + @":\Steam\steamapps\common\Helldivers 2\bin\steam_api64.dll");
                candidates.Add(drive + @":\SteamLibrary\steamapps\common\Helldivers 2\bin\steam_api64.dll");
            }
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
            return null;
        }
    }
}
