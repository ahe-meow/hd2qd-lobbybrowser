using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
    /// opened for it, and the answers are kept five minutes across refreshes.</summary>
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
        internal static Dictionary<string, int?> Regions(IEnumerable<string> regions)
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
            Parallel.ForEach(missing, region =>
            {
                var value = Measure(region);
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
                var count = IcmpSendEcho(handle, target, payload, (ushort)payload.Length, IntPtr.Zero, reply, (uint)reply.Length, TimeoutMs);
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
