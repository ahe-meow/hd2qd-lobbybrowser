using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Hd2Qd.ApiProbe
{
    /// <summary>Diagnoses every online API the lobby browser touches, with the same
    /// HttpWebRequest stack, headers and timeouts the plugin itself uses. Each target gets
    /// two attempts: the first includes DNS + TCP + TLS handshakes, the second reuses the
    /// connection, so a big gap between them points at handshaking, not at the server.
    /// Run it with no arguments and paste the whole output back.</summary>
    internal static class Program
    {
        // the exact endpoints the plugin hits, in its own order of preference
        static readonly (string Name, string Url, string[] Headers)[] Targets =
        {
            ("星球表(社区) planets", "https://api.helldivers2.dev/api/v1/planets",
                new[] { "X-Super-Client: HD2QD.LobbyBrowser", "X-Super-Contact: https://github.com/ahe-meow/hd2qd-lobbybrowser" }),
            ("变种(社区) WarSeason", "https://api.helldivers2.dev/raw/api/WarSeason/801/status",
                new[] { "X-Super-Client: HD2QD.LobbyBrowser", "X-Super-Contact: https://github.com/ahe-meow/hd2qd-lobbybrowser" }),
            ("星球表/变种(官方回退) WarSeason", "https://api.live.prod.thehelldiversgame.com/api/WarSeason/801/status",
                new[] { "Accept-Language: en-US" }),
            ("本机定位 ipinfo", "https://ipinfo.io/json", new string[0]),
            ("本机定位 ipwho", "https://ipwho.is/", new string[0]),
            ("平台(PlayFab) ping", "https://974a9.playfabapi.com/ping", new string[0]),
        };

        static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            var line = new string('=', 72);
            Console.WriteLine(line);
            Console.WriteLine("HD2QD.LobbyBrowser 在线接口诊断  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Console.WriteLine("运行时: " + Environment.Version + "  |  OS: " + Environment.OSVersion.VersionString);
            Console.WriteLine("TLS: " + ServicePointManager.SecurityProtocol);
            Console.WriteLine("系统代理: " + (WebRequest.DefaultWebProxy != null ? "已配置(如走代理会在耗时中体现)" : "无"));
            Console.WriteLine(line);

            var failures = 0;
            foreach (var (name, url, headers) in Targets)
            {
                Console.WriteLine();
                Console.WriteLine("── " + name);
                Console.WriteLine("   " + url);
                try
                {
                    var host = new Uri(url).Host;
                    var dns = Dns.GetHostEntry(host);
                    Console.WriteLine("   DNS: " + dns.AddressList[0] + "（共 " + dns.AddressList.Length + " 个地址）");
                }
                catch (Exception e)
                {
                    Console.WriteLine("   DNS: 失败 — " + e.Message);
                }

                for (var attempt = 1; attempt <= 2; attempt++)
                {
                    var clock = Stopwatch.StartNew();
                    try
                    {
                        var request = (HttpWebRequest)WebRequest.Create(url);
                        request.Timeout = 10000;
                        request.ReadWriteTimeout = 10000;
                        request.UserAgent = "HD2QD.LobbyBrowser";
                        request.AutomaticDecompression = DecompressionMethods.None;
                        foreach (var header in headers)
                        {
                            var split = header.IndexOf(':');
                            request.Headers[header.Substring(0, split).Trim()] = header.Substring(split + 1).Trim();
                        }
                        using (var response = (HttpWebResponse)request.GetResponse())
                        using (var stream = response.GetResponseStream())
                        {
                            var buffer = new byte[64 * 1024];
                            long size = 0;
                            int read;
                            while (stream != null && (read = stream.Read(buffer, 0, buffer.Length)) > 0) size += read;
                            clock.Stop();
                            var rate = "";
                            if (response.Headers["X-RateLimit-Remaining"] != null)
                                rate = "  限流: 剩余 " + response.Headers["X-RateLimit-Remaining"] + "/" + response.Headers["X-RateLimit-Limit"];
                            Console.WriteLine("   尝试" + attempt + ": " + (int)response.StatusCode + " " + response.StatusDescription
                                + "  " + size + " B  " + clock.ElapsedMilliseconds + " ms" + rate
                                + (attempt == 2 && clock.ElapsedMilliseconds < 300 ? "  (连接已复用)" : ""));
                        }
                    }
                    catch (WebException e)
                    {
                        clock.Stop();
                        var response = e.Response as HttpWebResponse;
                        var bad = false;
                        if (response != null)
                        {
                            var retry = response.Headers["Retry-After"];
                            var status = (int)response.StatusCode;
                            // 4xx (except 429) means the link is fine and the server answered;
                            // PlayFab answers 403 to an anonymous ping, which is expected.
                            bad = status == 429 || status >= 500;
                            Console.WriteLine("   尝试" + attempt + ": HTTP " + status + " " + response.StatusDescription
                                + "  " + clock.ElapsedMilliseconds + " ms"
                                + (retry != null ? "  Retry-After: " + retry + " s" : "")
                                + (!bad && status >= 400 ? "  (可达；服务端拒绝匿名请求，链路正常)" : ""));
                        }
                        else
                        {
                            bad = true;
                            Console.WriteLine("   尝试" + attempt + ": 网络错误 — " + e.Status + " — " + e.GetBaseException().Message
                                + "  " + clock.ElapsedMilliseconds + " ms");
                        }
                        if (attempt == 2 && bad) failures++;
                    }
                    catch (Exception e)
                    {
                        clock.Stop();
                        Console.WriteLine("   尝试" + attempt + ": 异常 — " + e.Message + "  " + clock.ElapsedMilliseconds + " ms");
                        if (attempt == 2) failures++;
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine(line);
            Console.WriteLine(failures == 0
                ? "结论: 全部目标可达。注意每个目标首次尝试的耗时——插件对该接口的超时是 8 秒，"
                  + "首次耗时接近或超过 8000 ms 时插件会报失败（首次包含 DNS+TLS 握手，二次明显更快属正常）。"
                  + "把这份完整输出贴回去即可定位。"
                : "结论: 有 " + failures + " 个目标链路级失败。判读：首次慢、二次快=握手/DNS 问题；"
                  + "两次都超时=到该服务的链路被墙或被安全软件拦截。");
            Console.WriteLine("(窗口将保持 15 秒后关闭，或按任意键退出)");
            Thread.Sleep(15000);
            return failures == 0 ? 0 : 2;
        }
    }
}
