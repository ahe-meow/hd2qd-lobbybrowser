using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Hd2Qd.LobbyBrowser
{
    /// <summary>What the ping column means: this PC's latency to the Steam Datagram Relay
    /// network of the lobby's region — the best node there, as HD2LobbyBrowser measures it
    /// (sdr_pops.json, ICMP through iphlpapi, no administrator rights needed). A lobby's own
    /// address is never published, so the region is the closest a client can get.
    ///
    /// Measured here, in the manager's process: ICMP needs no Steamworks, so no Steam session is
    /// opened for it, and the answers are kept five minutes across refreshes.
    ///
    /// A game accelerator (奇游 and other TUN/TAP ones) puts the game on a virtual adapter while
    /// a bare ICMP echo would leave by the default route. When such an adapter is up, its IPv4
    /// address becomes the source of the echo, so the probe uses the accelerated route too; a
    /// probe that gets no answer is retried on the default route rather than leaving the column
    /// empty.</summary>
    internal static class Ping
    {
        const int TimeoutMs = 900;
        static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
        static readonly object Gate = new object();
        static readonly Dictionary<string, KeyValuePair<DateTime, int?>> Cache = new Dictionary<string, KeyValuePair<DateTime, int?>>();
        static Dictionary<string, List<string>> _nodes;

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern IntPtr IcmpCreateFile();

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern bool IcmpCloseHandle(IntPtr handle);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint IcmpSendEcho(IntPtr handle, uint address, byte[] request, ushort requestSize,
            IntPtr options, byte[] reply, uint replySize, uint timeout);

        // the same echo, but out of a chosen local address: an accelerator's virtual adapter
        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint IcmpSendEcho2Ex(IntPtr handle, IntPtr evt, IntPtr apc, IntPtr context,
            uint source, uint address, byte[] request, ushort requestSize,
            IntPtr options, byte[] reply, uint replySize, uint timeout);

        static readonly Lazy<uint> Source = new Lazy<uint>(FindSource);

        /// <summary>A name that names the accelerator itself, before the generic TUN/TAP ones.</summary>
        static bool Vendor(string name)
        {
            var s = (name ?? "").ToLowerInvariant();
            return s.Contains("奇游") || s.Contains("qiyou") || s.Contains("加速器") || s.Contains("accelerator");
        }

        /// <summary>Also accepts the generic names accelerators give their virtual adapters.</summary>
        internal static bool Accelerator(string name)
        {
            var s = (name ?? "").ToLowerInvariant();
            return Vendor(s) || s.Contains("虚拟网卡") || s.Contains("tun") || s.Contains("tap");
        }

        static uint FindSource()
        {
            try
            {
                var up = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                                n.NetworkInterfaceType != NetworkInterfaceType.Loopback).ToList();
                // a named accelerator first; any TUN/TAP fallback second
                foreach (var pass in new[] { true, false })
                    foreach (var nic in up)
                    {
                        var name = nic.Name + " " + nic.Description;
                        if ((pass ? Vendor(name) : Accelerator(name)) == false) continue;
                        if (pass == false && Vendor(name)) continue;
                        foreach (var address in nic.GetIPProperties().UnicastAddresses)
                            if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                                return BitConverter.ToUInt32(address.Address.GetAddressBytes(), 0);
                    }
            }
            catch
            {
                // no adapter list: keep the default route
            }
            return 0;
        }

        static Dictionary<string, List<string>> Nodes()
        {
            if (_nodes != null) return _nodes;
            _nodes = new Dictionary<string, List<string>>();
            try
            {
                var asm = typeof(Ping).Assembly;
                using (var reader = new StreamReader(asm.GetManifestResourceStream("Data.sdr_pops.json")))
                {
                    var text = reader.ReadToEnd();
                    foreach (var pair in new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                                 .Deserialize<Dictionary<string, object>>(text))
                    {
                        var addresses = new List<string>();
                        if (pair.Value is System.Collections.IList list)
                            foreach (var node in list)
                                if (node is Dictionary<string, object> d && d.TryGetValue("ip", out var ip) && ip is string s)
                                    addresses.Add(s);
                        if (addresses.Count > 0) _nodes[pair.Key.ToUpperInvariant()] = addresses;
                    }
                }
            }
            catch
            {
                // no table: the column stays empty, nothing else changes
            }
            return _nodes;
        }

        /// <summary>The latency of a region, from the cache or measured now; null when unknown.</summary>
        internal static int? Region(string region)
        {
            if (string.IsNullOrEmpty(region)) return null;
            var key = region.Trim().ToUpperInvariant();
            lock (Gate)
                if (Cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.Key < Ttl) return hit.Value;
            var value = Measure(key);
            lock (Gate) Cache[key] = new KeyValuePair<DateTime, int?>(DateTime.UtcNow, value);
            return value;
        }

        /// <summary>Every region of the list at once: the regions side by side and, inside each,
        /// its nodes side by side (two probes a node, the closest kept, as the original does).
        /// A region measured in the last five minutes is answered from the cache instead.</summary>
        internal static Dictionary<string, int?> Regions(IEnumerable<string> regions, string home)
        {
            var wanted = (regions ?? new string[0]).Where(r => !string.IsNullOrEmpty(r))
                .Select(r => r.Trim().ToUpperInvariant()).Distinct().ToList();
            var result = new Dictionary<string, int?>();
            var missing = new List<string>();
            lock (Gate)
            {
                foreach (var region in wanted)
                {
                    if (Cache.TryGetValue(region, out var hit) && DateTime.UtcNow - hit.Key < Ttl) result[region] = hit.Value;
                    else missing.Add(region);
                }
            }
            // opt-in: a process-named Winsock probe, so a name-based accelerator sees the traffic.
            // It shares one short-lived helper across all regions; any region it cannot answer
            // falls back to the ordinary ICMP route below.
            var nodes = Nodes();
            var viaWinsock = Helper.Winsock(home,
                wanted.SelectMany(r => nodes.TryGetValue(r, out var list) ? list : new List<string>()));
            Parallel.ForEach(missing, region =>
            {
                int? value = null;
                if (viaWinsock.Count > 0 && nodes.TryGetValue(region, out var list))
                {
                    var answers = new List<int>();
                    foreach (var node in list)
                        if (viaWinsock.TryGetValue(node, out var answer) && answer.HasValue) answers.Add(answer.Value);
                    if (answers.Count > 0) value = answers.Min();
                }
                if (!value.HasValue) value = Measure(region);
                lock (Gate)
                {
                    Cache[region] = new KeyValuePair<DateTime, int?>(DateTime.UtcNow, value);
                    result[region] = value;
                }
            });
            return result;
        }

        static int? Measure(string region)
        {
            if (!Nodes().TryGetValue(region, out var addresses) || addresses.Count == 0) return null;
            var values = new List<int>();
            // the nodes side by side, two probes each: the closest one is the region's latency
            Parallel.ForEach(addresses, address =>
            {
                var best = -1;
                for (var i = 0; i < 2; i++)
                {
                    var ms = Echo(address);
                    if (ms >= 0 && (best < 0 || ms < best)) best = ms;
                }
                if (best >= 0) lock (values) values.Add(best);
            });
            return values.Count == 0 ? (int?)null : values.Min();
        }

        /// <summary>One ICMP echo; -1 when it did not answer.</summary>
        static int Echo(string address)
        {
            var parts = (address ?? "").Split('.');
            if (parts.Length != 4) return -1;
            uint target = 0;
            for (var i = 0; i < 4; i++)
            {
                int part;
                if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out part) || part < 0 || part > 255) return -1;
                target |= (uint)part << (8 * i);              // the first part is the low byte
            }
            var handle = IcmpCreateFile();
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return -1;
            try
            {
                var payload = System.Text.Encoding.ASCII.GetBytes("hd2qd-lobbybrowser");
                var reply = new byte[64];
                var source = Source.Value;
                uint count = 0;
                if (source != 0)
                    try
                    {
                        count = IcmpSendEcho2Ex(handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, source, target,
                            payload, (ushort)payload.Length, IntPtr.Zero, reply, (uint)reply.Length, TimeoutMs);
                    }
                    catch
                    {
                        // an older Windows without this entry point: use the default route
                    }
                if (count == 0)
                    count = IcmpSendEcho(handle, target, payload, (ushort)payload.Length, IntPtr.Zero,
                        reply, (uint)reply.Length, TimeoutMs);   // a tunnel need not carry ICMP
                if (count == 0) return -1;
                var status = BitConverter.ToUInt32(reply, 4);
                return status == 0 ? BitConverter.ToInt32(reply, 8) : -1;
            }
            catch
            {
                return -1;
            }
            finally
            {
                IcmpCloseHandle(handle);
            }
        }
    }
}
