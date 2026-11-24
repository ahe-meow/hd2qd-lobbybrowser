using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace Hd2Qd.LobbyBrowswer
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
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)] delegate bool CompletedFn(IntPtr self, ulong call, out byte failed);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)] delegate bool ResultFn(IntPtr self, ulong call, IntPtr buffer, int size, int expected, out byte failed);

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

        readonly ManualResetEventSlim _started = new ManualResetEventSlim();
        Thread _pump;
        volatile bool _stop;

        IntPtr _module, _matchmaking, _friends, _user, _utils;
        InitFlatFn _init;
        VoidFn _runCallbacks, _shutdown;
        InterfaceFn _getMatchmaking, _getFriends, _getUser, _getUtils;
        AddDistanceFn _addDistance;
        AddCountFn _addCount;
        AddNumberFn _addNumber;
        RequestListFn _requestList;
        LobbyAtFn _lobbyAt;
        MembersFn _members, _limit;
        RequestDataFn _requestData;
        DataFn _getData;
        SteamIdFn _getSteamId;
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
            _pump = new Thread(Pump) { IsBackground = true, Name = "HD2QD.LobbyBrowswer.Steam" };
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
            _persona = Fn<PersonaFn>("SteamAPI_ISteamFriends_GetFriendPersonaName");
            _requestInfo = Fn<RequestInfoFn>("SteamAPI_ISteamFriends_RequestUserInformation");
            _completed = Fn<CompletedFn>("SteamAPI_ISteamUtils_IsAPICallCompleted");
            _result = Fn<ResultFn>("SteamAPI_ISteamUtils_GetAPICallResult");

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
                    var until = DateTime.UtcNow.AddSeconds(4);
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

        /// <summary>Asks Steam for every lobby's data, then waits for the first answer, as the
        /// game's own list does (Steam fills them in asynchronously).</summary>
        internal void AskData(IList<ulong> lobbies)
        {
            if (!Ok || lobbies.Count == 0) return;
            foreach (var id in lobbies) _requestData(_matchmaking, id);
            var until = DateTime.UtcNow.AddSeconds(2.5);
            while (DateTime.UtcNow < until)
            {
                if (Data(lobbies[0], "12") != "" || Data(lobbies[0], "1") != "") return;
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
            room.Sos = Data(lobby, "8") == "1";
            var privacy = Data(lobby, "19");
            int p;
            room.Privacy = int.TryParse(privacy, out p) ? p : -1;
            room.Members = _members(_matchmaking, lobby);
            room.Limit = _limit(_matchmaking, lobby);
            var created = Data(lobby, "18");
            long at;
            if (long.TryParse(created, out at) && at > 1500000000)
            {
                var age = now - at;
                if (age >= 0 && age <= 172800) room.Age = age;
            }
            room.HostId = Data(lobby, "3");
            return room;
        }

        /// <summary>The host's name when Steam knows the host as a Steam user (the lobby is not
        /// crossplay, or the host is on Steam); empty otherwise.</summary>
        internal void Names(IList<Room> rooms)
        {
            var pending = new List<Room>();
            foreach (var r in rooms)
                if (r.HostId.Length >= 17 && ulong.TryParse(r.HostId, out _)) pending.Add(r);
            if (pending.Count == 0) return;
            foreach (var r in pending)
                try { _requestInfo(_friends, ulong.Parse(r.HostId), false); } catch { }
            Thread.Sleep(800);
            foreach (var r in pending)
            {
                try
                {
                    var name = Utf8(_persona(_friends, ulong.Parse(r.HostId)));
                    if (name != "" && name != "[unknown]") r.Host = name;
                }
                catch { }
            }
        }

        internal static string JoinUrl(ulong lobby, string host)
        {
            var who = host;
            if (string.IsNullOrEmpty(who))
            {
                var steam = _instance;
                who = steam != null ? steam.SelfId.ToString() : "0";
            }
            return "steam://joinlobby/" + AppId + "/" + lobby + "/" + who;
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
