using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Hd2Qd.Verify
{
    /// <summary>Checks a built plugin against the contract in docs/plugin-authors.md of
    /// hd2qd-plugins, then runs one live scan through its own Run command. Usage:
    /// Verify.exe &lt;HD2QD.*.dll&gt; [--offline]</summary>
    static class Verify
    {
        static int _failed;
        static readonly string[] Framework =
        {
            "mscorlib", "System", "System.Core", "System.Runtime", "System.Web.Extensions",
            "PresentationCore", "PresentationFramework", "WindowsBase", "System.Xaml", "System.Xml",
            "System.Drawing", "System.Data", "System.Configuration", "System.Net.Http", "System.Web",
        };

        static void Check(string what, bool ok, string detail = "")
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what + (detail != "" ? "  [" + detail + "]" : ""));
            if (!ok) _failed++;
        }

        /// <summary>Starts the extracted helper the way the plugin does and prints what comes
        /// back, raw: a line's bytes, the exit code, stderr. For when a scan answers nothing.
        /// Usage: Verify.exe &lt;dll&gt; --helper &lt;path to the helper exe&gt;</summary>
        static int ProbeHelper(string dll, string exe)
        {
            Console.WriteLine("helper: " + exe);
            var request = "{\"lang\":\"zh-CN\",\"filters\":{\"fronts\":[],\"difficulty\":0,\"publicOnly\":true,\"hideNoSession\":true,\"hideFull\":false,\"sosOnly\":false}}";
            var info = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe, WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            using (var process = System.Diagnostics.Process.Start(info))
            {
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) Console.WriteLine("      err: " + e.Data); };
                process.BeginErrorReadLine();
                process.StandardInput.Write(request);
                process.StandardInput.Close();
                string line;
                var n = 0;
                string last = null;
                while ((line = process.StandardOutput.ReadLine()) != null)
                {
                    last = line;
                    Console.WriteLine("      out[" + n++ + "] len=" + line.Length + " " + line.Substring(0, Math.Min(80, line.Length)));
                }
                process.WaitForExit(150000);
                Console.WriteLine("      lines=" + n + " exit=" + process.ExitCode);
                try
                {
                    var json = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                    var message = json.Deserialize<Dictionary<string, object>>(last);
                    var rooms = message.TryGetValue("rooms", out var r) ? (r as System.Collections.IList)?.Count ?? -1 : -1;
                    Console.WriteLine("      parsed: rooms=" + rooms + " keys=" + string.Join(",", message.Keys));
                }
                catch (Exception e)
                {
                    Console.WriteLine("      parse failed: " + e.GetBaseException().Message);
                }
            }
            return 0;
        }

        /// <summary>A preset written and read back: the file the window keeps its filters in.
        /// Two of them, one with planets, a mission hash and a sort, so the Json round trip is
        /// what is checked — not just that the type exists.</summary>
        static void CheckPresets(Assembly asm)
        {
            try
            {
                var type = asm.GetType("Hd2Qd.LobbyBrowser.Preset");
                var store = asm.GetType("Hd2Qd.LobbyBrowser.Presets");
                if (type == null || store == null) { Check("presets round trip", false, "Preset/Presets missing"); return; }
                var listType = typeof(List<>).MakeGenericType(type);
                var list = (System.Collections.IList)Activator.CreateInstance(listType);

                var one = Activator.CreateInstance(type, true);
                type.GetField("Name", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(one, "深夜刷样本");
                ((System.Collections.IList)type.GetField("Planets", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(one)).Add(92);
                ((System.Collections.IList)type.GetField("Missions", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(one)).Add("1bc57256");
                type.GetField("Difficulty", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(one, 7);
                type.GetField("SortDesc", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(one, true);
                list.Add(one);

                var two = Activator.CreateInstance(type, true);
                type.GetField("Name", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(two, "East only");
                type.GetField("SosOnly", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(two, true);
                list.Add(two);

                var home = Path.Combine(Home(), "presets-" + Guid.NewGuid().ToString("N"));
                var flags = BindingFlags.NonPublic | BindingFlags.Static;
                var saved = store.GetMethod("Save", flags).Invoke(null, new object[] { home, list }) as string;
                var back = store.GetMethod("Load", flags).Invoke(null, new object[] { home }) as System.Collections.IList;
                var first = back != null && back.Count == 2 ? back[0] : null;
                var name = first != null ? (string)type.GetField("Name", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(first) : null;
                var difficulty = first != null ? (int)type.GetField("Difficulty", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(first) : -1;
                var planets = first != null ? ((System.Collections.IList)type.GetField("Planets", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(first)).Count : -1;
                var sos = back != null && back.Count == 2 ? (bool)type.GetField("SosOnly", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(back[1]) : false;
                Check("presets round trip", saved == null && back != null && back.Count == 2 && name == "深夜刷样本"
                    && difficulty == 7 && planets == 1 && sos, "saved=" + (saved ?? "ok") + " count=" + (back == null ? -1 : back.Count));
            }
            catch (Exception e)
            {
                Check("presets round trip", false, (e.InnerException ?? e).Message);
            }
        }

        /// <summary>Select all and invert touch only the rows the search shows — inverting a
        /// search for one item must not flip the rest. Both languages name the two buttons.</summary>
        static void CheckPicker(Assembly asm)
        {
            try
            {
                var type = asm.GetType("Hd2Qd.LobbyBrowser.Picker");
                var itemType = asm.GetType("Hd2Qd.LobbyBrowser.Picker+Item");
                var words = asm.GetType("Hd2Qd.LobbyBrowser.Words");
                if (type == null || itemType == null || words == null)
                { Check("picker all and invert", false, "Picker/Item/Words missing"); return; }
                var choose = type.GetMethod("Choose", BindingFlags.NonPublic | BindingFlags.Static);
                var list = (System.Collections.IList)Activator.CreateInstance(
                    typeof(List<>).MakeGenericType(itemType));
                var on = itemType.GetField("On", BindingFlags.NonPublic | BindingFlags.Instance);
                var text = itemType.GetField("Text", BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (var name in new[] { "West", "East", "North" })
                {
                    var item = Activator.CreateInstance(itemType, true);
                    text.SetValue(item, name);
                    list.Add(item);
                }
                choose.Invoke(null, new object[] { list, "", true });
                var allOn = list.Cast<object>().All(i => (bool)on.GetValue(i));
                choose.Invoke(null, new object[] { list, "east", false });
                var values = list.Cast<object>().Select(i => (bool)on.GetValue(i)).ToArray();
                var invert = values.SequenceEqual(new[] { true, false, true });
                var get = words.GetMethod("Get", BindingFlags.Public | BindingFlags.Static);
                var zh = (string)get.Invoke(null, new object[] { "zh-CN", "filter_all" });
                var en = (string)get.Invoke(null, new object[] { "en", "filter_invert" });
                Check("picker all and invert", allOn && invert && zh == "全选" && en == "Invert",
                    "all=" + allOn + " invert=" + invert + " zh=" + zh + " en=" + en);
            }
            catch (Exception e)
            {
                Check("picker all and invert", false, (e.InnerException ?? e).Message);
            }
        }

        /// <summary>The accelerator adapter is found by its name: the vendor's own (奇游) or a
        /// generic TUN/TAP name, never an ordinary Ethernet/Wi-Fi adapter.</summary>
        static void CheckPingRoute(Assembly asm)
        {
            var ping = asm.GetType("Hd2Qd.LobbyBrowser.Ping");
            var match = ping != null ? ping.GetMethod("Accelerator", BindingFlags.NonPublic | BindingFlags.Static) : null;
            if (match == null) { Check("accelerator adapter rule", false, "Ping.Accelerator missing"); return; }
            var qiyou = (bool)match.Invoke(null, new object[] { "奇游加速器虚拟网卡" });
            var tap = (bool)match.Invoke(null, new object[] { "TAP-Windows Adapter V9" });
            var lan = (bool)match.Invoke(null, new object[] { "以太网" });
            Check("accelerator adapter rule", qiyou && tap && !lan,
                "qiyou=" + qiyou + " tap=" + tap + " lan=" + lan);
        }

        /// <summary>The disguised Winsock probe is opt-in: without winsock.txt it must not even
        /// start a process; with it, the helper answers a local address over the JSON protocol.</summary>
        static void CheckWinsock(Assembly asm)
        {
            try
            {
                var helper = asm.GetType("Hd2Qd.LobbyBrowser.Helper");
                var winsock = helper != null
                    ? helper.GetMethod("Winsock", BindingFlags.NonPublic | BindingFlags.Static) : null;
                if (winsock == null) { Check("winsock probe is opt-in", false, "Helper.Winsock missing"); return; }
                var home = Path.Combine(Home(), "winsock-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(home);
                var args = new object[] { home, new[] { "127.0.0.1" } };
                var off = winsock.Invoke(null, args) as System.Collections.IDictionary;
                Directory.CreateDirectory(Path.Combine(home, "lobbybrowser"));
                File.WriteAllText(Path.Combine(home, "lobbybrowser", "winsock.txt"), "on");
                var on = winsock.Invoke(null, args) as System.Collections.IDictionary;
                Check("winsock probe is opt-in", off != null && off.Count == 0 && on != null && on.Contains("127.0.0.1"),
                    "off=" + (off == null ? -1 : off.Count) + " on=" + (on == null ? -1 : on.Count));
            }
            catch (Exception e)
            {
                Check("winsock probe is opt-in", false, (e.InnerException ?? e).Message);
            }
        }

        /// <summary>The language rule: a manager tag maps onto the two this plugin has, the
        /// player's choice is read from its own file and beats the manager's, and following
        /// reads the manager's gui.json as it stands.</summary>
        static void CheckLanguage(Assembly asm)
        {
            try
            {
                var type = asm.GetType("Hd2Qd.LobbyBrowser.Language");
                if (type == null) { Check("language follows and overrides", false, "Language missing"); return; }
                var flags = BindingFlags.NonPublic | BindingFlags.Static;
                var tagOf = type.GetMethod("Tag", flags);
                var choose = type.GetMethod("Choose", flags);
                var chosen = type.GetMethod("Chosen", flags);
                var manager = type.GetMethod("Manager", flags);

                var zhTw = (string)tagOf.Invoke(null, new object[] { "zh-TW" });
                var fr = (string)tagOf.Invoke(null, new object[] { "fr-FR" });

                var home = Path.Combine(Home(), "lang-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(home);
                File.WriteAllText(Path.Combine(home, "gui.json"), "{\"lang\":\"en\"}");
                var fromFile = (string)manager.Invoke(null, new object[] { home, "zh-CN" });

                choose.Invoke(null, new object[] { home, "zh-CN" });
                var kept = (string)chosen.Invoke(null, new object[] { home });

                choose.Invoke(null, new object[] { home, null });
                var cleared = chosen.Invoke(null, new object[] { home });

                Check("language follows and overrides",
                    zhTw == "zh-CN" && fr == "en" && fromFile == "en" && kept == "zh-CN" && cleared == null,
                    "zh-TW=" + zhTw + " fr=" + fr + " gui=" + fromFile + " chosen=" + (kept ?? "null") + " cleared=" + (cleared == null ? "null" : cleared));
            }
            catch (Exception e)
            {
                Check("language follows and overrides", false, (e.InnerException ?? e).Message);
            }
        }

        /// <summary>A settings folder of the test's own: the manager's own is not written to.</summary>
        static string Home()
        {
            var home = Path.Combine(Path.GetTempPath(), "hd2qd-lobbybrowser-verify");
            Directory.CreateDirectory(home);
            return home;
        }

        [STAThread]
        static int Main(string[] args)
        {
            var path = Path.GetFullPath(args.Length > 0 ? args[0] : "release/HD2QD.LobbyBrowser.dll");
            var offline = args.Contains("--offline");
            if (args.Contains("--window")) return ShowWindow(path);
            if (args.Contains("--api")) return CheckApi(path);
            if (args.Contains("--helper")) return ProbeHelper(path, args[args.Length - 1]);
            if (args.Contains("--presets")) return ShowPresets(path);
            Console.WriteLine("Plugin: " + path);
            if (!File.Exists(path)) { Console.WriteLine("FAIL  file not found"); return 2; }

            var asm = Assembly.LoadFrom(path);
            var name = asm.GetName();

            // ---- naming and target ----
            Check("file name is HD2QD.<name>.dll", Path.GetFileName(path).StartsWith("HD2QD.") && path.EndsWith(".dll"));
            Check("assembly name matches the file", name.Name + ".dll" == Path.GetFileName(path), name.Name);
            var tfm = asm.GetCustomAttribute<TargetFrameworkAttribute>();
            Check("target framework is .NET Framework 4.8", tfm != null && tfm.FrameworkName == ".NETFramework,Version=v4.8",
                tfm != null ? tfm.FrameworkName : "none");

            // ---- the contract's class ----
            var exported = asm.GetExportedTypes();
            Check("exactly one public type", exported.Length == 1, string.Join(", ", exported.Select(t => t.FullName)));
            var plugin = exported.FirstOrDefault(t => t.Name == "Plugin");
            Check("public class is named Plugin", plugin != null);
            if (plugin == null) return 1;
            Check("Plugin is static", plugin.IsAbstract && plugin.IsSealed);

            var api = plugin.GetField("Api", BindingFlags.Public | BindingFlags.Static);
            Check("Api is const int 2", api != null && api.IsLiteral && api.FieldType == typeof(int) && (int)api.GetRawConstantValue() == 2);
            var kind = plugin.GetField("Kind", BindingFlags.Public | BindingFlags.Static);
            Check("Kind is const \"window\"", kind != null && kind.IsLiteral && Equals(kind.GetRawConstantValue(), "window"));

            Check("Title/Summary answer in en and zh-CN", Answers(plugin, "Title", "en", "zh-CN") && Answers(plugin, "Summary", "en", "zh-CN"));
            var open = plugin.GetMethod("Open", BindingFlags.Public | BindingFlags.Static);
            Check("Open(Window, Dictionary<string, object>)", open != null && open.ReturnType == typeof(void) &&
                open.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(Window), typeof(Dictionary<string, object>) }));
            var run = plugin.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
            Check("Run(command, args, output, host)", run != null && run.GetParameters().Length == 4);

            // ---- what the plugin carries ----
            var resources = asm.GetManifestResourceNames();
            foreach (var r in new[] { "Lang.en.json", "Lang.zh-CN.json", "Data.planets.json", "Data.mission_names.json" })
                Check("resource " + r, resources.Contains(r), string.Join(", ", resources));

            // the manager reads a manifest resource named exactly "app.ico" for the card's icon
            var icon = resources.Contains("app.ico") ? asm.GetManifestResourceStream("app.ico") : null;
            var header = new byte[4];
            var read = icon != null ? icon.Read(header, 0, 4) : 0;
            Check("icon app.ico is an ICO", read == 4 && header[0] == 0 && header[1] == 0 && header[2] == 1 && header[3] == 0,
                read == 4 ? string.Join(" ", header) : "missing");

            // ---- dependencies: the framework and nothing else ----
            var outside = asm.GetReferencedAssemblies().Select(a => a.Name).Where(n => !Framework.Contains(n)).ToArray();
            Check("references the framework only", outside.Length == 0, string.Join(", ", outside));
            var beside = Directory.GetFiles(Path.GetDirectoryName(path), "*.dll")
                .Where(f => !Path.GetFileName(f).Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)).ToArray();
            Check("no other file needed beside it", beside.Length == 0, string.Join(", ", beside.Select(Path.GetFileName)));

            CheckPresets(asm);
            CheckPicker(asm);
            CheckPingRoute(asm);
            CheckWinsock(asm);
            CheckLanguage(asm);

            // ---- a live scan through the plugin's own command ----
            if (!offline)
            {
                var host = new Dictionary<string, object> { ["lang"] = "zh-CN", ["api"] = 2L, ["home"] = Home() };
                var output = new StringWriter();
                var code = (int)run.Invoke(null, new object[] { "lobbies", new string[0], output, host });
                var text = output.ToString();
                Check("lobbies command returns 0", code == 0, text.Split('\n').FirstOrDefault());
                var rows = text.Split('\n').Count(l => l.Length > 0 && char.IsDigit(l[0]));
                Check("lobbies command lists lobbies", rows > 1, rows + " rows");
                Check("lobbies carry a planet and a difficulty", text.Contains("难度") || text.Contains("Difficulty"));
                foreach (var line in text.Split('\n').Take(4)) Console.WriteLine("      " + line);
            }

            Console.WriteLine(_failed == 0 ? "OK: every check passed." : _failed + " check(s) failed.");
            return _failed == 0 ? 0 : 1;
        }

        static bool Answers(Type plugin, string member, params string[] langs)
        {
            var method = plugin.GetMethod(member, BindingFlags.Public | BindingFlags.Static);
            if (method == null) return false;
            var texts = langs.Select(l => (string)method.Invoke(null, new object[] { l })).ToArray();
            return texts.All(t => !string.IsNullOrWhiteSpace(t) && t != member) && texts.Distinct().Count() > 1;
        }

        /// <summary>The planet table the scan picks its targets from, asked the way the plugin
        /// asks it: a plain HttpWebRequest with the API's two headers.</summary>
        static int CheckApi(string path)
        {
            var asmForApi = Assembly.LoadFrom(path);
            try
            {
                var request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create("https://api.helldivers2.dev/api/v1/planets");
                request.Timeout = 8000;
                request.UserAgent = "hd2qd-lobbybrowser";
                request.Headers["X-Super-Client"] = "hd2qd-lobbybrowser";
                request.Headers["X-Super-Contact"] = "local";
                using (var response = request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    var text = reader.ReadToEnd();
                    Check("planet table answers", text.Length > 1000, text.Length + " bytes");
                    var live = asmForApi.GetType("Hd2Qd.LobbyBrowser.Live");
                    var picks = (System.Collections.IEnumerable)live.GetMethod("Pick", BindingFlags.NonPublic | BindingFlags.Static)
                        .Invoke(null, new object[] { new List<string>(), 0 });
                    var list = picks.Cast<int>().ToList();
                    var counts = (System.Collections.IDictionary)live.GetField("_counts", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                    Check("planet table is read", counts.Count > 100, counts.Count + " planets");
                    Check("active planets chosen", list.Count > 5 && !list.Contains(0), string.Join(",", list));
                    var error = live.GetField("LastError", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null) as string;
                    if (!string.IsNullOrEmpty(error)) Console.WriteLine("      last error: " + error);
                }
            }
            catch (Exception e)
            {
                Check("planet table answers", false, e.GetBaseException().Message);
            }
            return _failed == 0 ? 0 : 1;
        }

        static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                yield return child;
                foreach (var deeper in Descendants(child)) yield return deeper;
            }
        }

        /// <summary>Opens the plugin's window with the manager's own host dictionary shape, lets
        /// its scan run, and writes what a player sees to tests/window.png: the only check of the
        /// drawing itself, which needs no manager.</summary>
        /// <summary>The presets card alone: six names, each with Load and Delete on its own line.
        /// A picture of that part, for the README and for the person who asked for it
        /// (2026-11-25). The card is the plugin's own (PresetView.Card); only the window around it
        /// is the test's, and no scan is started for a shot.</summary>
        static int ShowPresets(string path)
        {
            var asm = Assembly.LoadFrom(path);
            var preset = asm.GetType("Hd2Qd.LobbyBrowser.Preset");
            var view = asm.GetType("Hd2Qd.LobbyBrowser.PresetView");
            var theme = asm.GetType("Hd2Qd.LobbyBrowser.Theme");
            var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(preset));
            foreach (var name in new[] { "深夜刷样本 · 难度 10", "东线 快速刷样本", "西线 机器人 7 级", "光能 紧急撤离", "萌新友好（3–5 级）", "SOS 救援" })
            {
                var item = Activator.CreateInstance(preset, true);
                preset.GetField("Name", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(item, name);
                list.Add(item);
            }

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var ground = (System.Windows.Media.Brush)theme.GetMethod("B", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { "Ground900" });
            var ink = (System.Windows.Media.Brush)theme.GetMethod("B", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { "Ink" });

            var frame = new System.Windows.Controls.Border { Padding = new Thickness(16) };
            var window = new Window
            {
                Title = "presets", Width = 420, Height = 380, Background = ground, Foreground = ink,
                FontFamily = new FontFamily("Bahnschrift, Microsoft YaHei UI"), FontSize = 14,
                Content = frame,
            };
            // the card is built once the window's message loop is up: WPF elements made before
            // Application.Run have to reach their Dispatcher afterwards, and this shot is not
            // worth that risk
            window.Loaded += (s, e) =>
            {
                frame.Child = (FrameworkElement)view.GetMethod("Card", BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, new object[] { list, "zh-CN", new Action<string>(name => { }), new Action<string>(name => { }), new Action(() => { }) });
                window.UpdateLayout();
            };
            window.Show();

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                var content = window.Content as FrameworkElement;
                content.UpdateLayout();
                var width = (int)Math.Max(1, content.ActualWidth);
                var height = (int)Math.Max(1, content.ActualHeight);
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var file = Path.Combine(Directory.GetCurrentDirectory(), "presets.png");
                using (var stream = File.Create(file)) encoder.Save(stream);
                Console.WriteLine("PASS  presets card rendered to " + file + " (" + width + "x" + height + ")");
                app.Shutdown();
            };
            timer.Start();
            app.Run();
            return 0;
        }

        static int ShowWindow(string path)
        {
            var asm = Assembly.LoadFrom(path);
            var plugin = asm.GetType("Hd2Qd.LobbyBrowser.Plugin");
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Window shown = null;
            var host = new Dictionary<string, object>
            {
                ["api"] = 2L,
                ["lang"] = "zh-CN",
                ["home"] = Home(),
                ["window"] = new Func<string, FrameworkElement, Window>((title, content) =>
                {
                    // the manager's ChromeWindow, as near as a test needs it: Ground900 ground,
                    // a 48 px Ground700 caption, a grid line round both
                    var ground = new SolidColorBrush(Color.FromRgb(0x0C, 0x0E, 0x11));
                    var bar = new SolidColorBrush(Color.FromRgb(0x1A, 0x1D, 0x21));
                    var grid = new SolidColorBrush(Color.FromRgb(0x30, 0x35, 0x3C));
                    var ink = new SolidColorBrush(Color.FromRgb(0xF2, 0xEF, 0xE4));
                    var head = new System.Windows.Controls.Border
                    {
                        Background = bar, BorderBrush = grid, BorderThickness = new Thickness(0, 0, 0, 1), Height = 48,
                        Child = new System.Windows.Controls.TextBlock
                        {
                            Text = title, FontWeight = FontWeights.SemiBold, FontSize = 15,
                            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0), Foreground = ink,
                        },
                    };
                    var dock = new System.Windows.Controls.DockPanel();
                    System.Windows.Controls.DockPanel.SetDock(head, System.Windows.Controls.Dock.Top);
                    dock.Children.Add(head);
                    dock.Children.Add(content);
                    shown = new Window
                    {
                        Title = title, Width = 1300, Height = 820, Background = ground, Foreground = ink,
                        FontFamily = new FontFamily("Bahnschrift, Microsoft YaHei UI"), FontSize = 14,
                        Content = new System.Windows.Controls.Border { BorderBrush = grid, BorderThickness = new Thickness(1), Child = dock },
                    };
                    return shown;
                }),
            };
            try
            {
                plugin.GetMethod("Open").Invoke(null, new object[] { null, host });
                Console.WriteLine("PASS  Open() drew the window: " + shown.Title);
            }
            catch (Exception e)
            {
                Console.WriteLine("FAIL  Open() threw: " + (e.InnerException ?? e).Message);
                return 1;
            }
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(55) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                var content = shown.Content as FrameworkElement;
                var width = (int)Math.Max(1, content.ActualWidth);
                var height = (int)Math.Max(1, content.ActualHeight);
                foreach (var block in Descendants(content).OfType<System.Windows.Controls.TextBlock>().Take(3))
                {
                    var at = block.TranslatePoint(new Point(0, 0), content);
                    Console.WriteLine("      text at " + (int)at.X + "," + (int)at.Y + ": " + block.Text);
                }
                foreach (var list in Descendants(content).OfType<System.Windows.Controls.ListBox>())
                {
                    Console.WriteLine("      list width " + (int)list.ActualWidth + " of " + (int)content.ActualWidth);
                    var item = list.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
                    if (item != null) Console.WriteLine("      row width " + (int)item.ActualWidth);
                    if (list.SelectedIndex < 0 && list.Items.Count > 0) list.SelectedIndex = 0;   // the chosen row, as a player sees it
                }
                content.UpdateLayout();
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                var ground = new DrawingVisual();
                using (var dc = ground.RenderOpen())
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0C, 0x0E, 0x11)), null, new Rect(0, 0, width, height));
                bitmap.Render(ground);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var file = Path.Combine(Directory.GetCurrentDirectory(), "window.png");
                using (var stream = File.Create(file)) encoder.Save(stream);
                Console.WriteLine("PASS  window rendered to " + file + " (" + width + "x" + height + ")");
                app.Shutdown();
            };
            timer.Start();
            app.Run();
            return 0;
        }
    }
}
