using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Hd2Qd.LobbyBrowser
{
    /// <summary>The plugin's window: the manager's own frame (host["window"]) drawn around a body
    /// of the manager's own parts — ghost buttons, chips, ticks, a dark card, banded rows. The
    /// filters above, the table between them, the count and Join in a bar at the bottom right.</summary>
    internal static class BrowserWindow
    {
        internal static void Show(Window owner, Dictionary<string, object> host)
        {
            var lang = Words.Lang(host);
            var page = new LobbyPage(lang, host);
            var make = (Func<string, FrameworkElement, Window>)host["window"];
            var window = make(Words.Get(lang, "title"), page.Root);
            page.Host = window;
            window.Show();
            page.Refresh();
        }
    }

    internal sealed class LobbyPage
    {
        enum Sort { Planet, Front, Diff, Members, Mission, Region, Ping, Host, Age }

        // the table's columns, in the same stars in the header and in every row
        static readonly double[] Columns = { 1.6, 1.05, 0.85, 0.65, 1.85, 0.75, 0.5, 0.65, 1.25, 0.65 };
        static readonly string[] Heads = { "col_planet", "col_front", "col_diff", "col_members", "col_mission", "col_region", "col_sos", "col_ping", "col_host", "col_age" };
        static readonly bool[] AlignRight = { false, false, false, true, false, false, false, true, false, true };
        static readonly bool[] Numeric = { false, false, false, true, false, false, false, true, false, true };
        static readonly Sort?[] SortOf = { Sort.Planet, Sort.Front, Sort.Diff, Sort.Members, Sort.Mission, Sort.Region, null, Sort.Ping, Sort.Host, Sort.Age };

        readonly string _lang;
        readonly string _home;
        readonly Action<string, string> _foot;
        readonly ListBox _list = new ListBox();
        readonly Grid _head = new Grid { Height = 34, Margin = new Thickness(16, 0, 26, 0) };
        readonly TextBlock _status = new TextBlock { FontSize = 12, Foreground = Theme.B("Muted"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly TextBlock _empty = new TextBlock
        {
            FontSize = 13, Foreground = Theme.B("Muted"), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(40),
        };
        readonly List<CheckBox> _fronts = new List<CheckBox>();
        readonly CheckBox _publicOnly;
        readonly CheckBox _hideNoSession;
        readonly CheckBox _hideFull;
        readonly CheckBox _sosOnly;
        readonly Button _refresh;
        readonly Button _planetButton;
        readonly Button _difficulty;
        readonly Button _missionButton;
        readonly Button _join;
        readonly HashSet<int> _planets = new HashSet<int>();
        readonly HashSet<string> _missions = new HashSet<string>();     // by mission hash
        List<Room> _rooms = new List<Room>();
        int _difficultyValue;
        string _self = "0";
        Sort _sort = Sort.Members;
        bool _sortDesc = true;
        bool _busy;
        int _pingRound;

        internal Window Host;

        internal LobbyPage(string lang, Dictionary<string, object> host)
        {
            _lang = lang;
            _home = host != null && host.TryGetValue("home", out var home) ? home as string : null;
            _foot = host != null && host.TryGetValue("foot", out var foot) ? foot as Action<string, string> : null;

            _refresh = Farm(Words.Get(lang, "refresh"), "Ghost");
            _refresh.ToolTip = Words.Get(lang, "refresh_tip");
            _refresh.Click += (s, e) => Refresh();

            _planetButton = Farm(Words.Get(lang, "filter_planet_all"), "Ghost");
            _planetButton.Click += (s, e) => PickPlanets();
            _difficulty = Farm(Words.Get(lang, "diff_any"), "Ghost");
            _difficulty.Click += (s, e) => PickDifficulty();
            _missionButton = Farm(Words.Get(lang, "filter_mission_all"), "Ghost");
            _missionButton.Click += (s, e) => PickMissions();

            _join = new Button
            {
                Content = Words.Get(lang, "join"), Style = Theme.S("Primary"), MinHeight = 40, MinWidth = 116,
                Padding = new Thickness(22, 0, 22, 0), IsEnabled = false, ToolTip = Words.Get(lang, "hint_wait"),
            };
            _join.Click += (s, e) => Join();

            var bar = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 14) };
            bar.Children.Add(_refresh);
            bar.Children.Add(_planetButton);
            bar.Children.Add(_difficulty);
            bar.Children.Add(_missionButton);
            foreach (var front in new[] { "Terminids", "Automatons", "Illuminate" })
            {
                var chip = new CheckBox
                {
                    Content = Tables.FrontName(front, lang), Tag = front, IsChecked = true, Style = Theme.S("Chip"),
                };
                _fronts.Add(chip);
                bar.Children.Add(chip);
            }
            _publicOnly = Tick(lang, "public_only", true);
            _hideNoSession = Tick(lang, "hide_nosession", true);
            _hideFull = Tick(lang, "hide_full", false);
            _sosOnly = Tick(lang, "sos_only", false);
            _publicOnly.Margin = new Thickness(6, 0, 18, 0);      // a breath after the chips
            bar.Children.Add(_publicOnly);
            bar.Children.Add(_hideNoSession);
            bar.Children.Add(_hideFull);
            bar.Children.Add(_sosOnly);

            // the table: a card of the manager's, its words over the columns, a grid line, the rows
            var table = new Grid();
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(_head, 0);
            var rule = new Border { Height = 1, Background = Theme.B("Grid"), Margin = new Thickness(16, 0, 16, 0) };
            Grid.SetRow(rule, 1);
            Dress();
            _list.SelectionChanged += (s, e) => _join.IsEnabled = _list.SelectedItem is Room;
            _list.MouseDoubleClick += (s, e) => { if (_list.SelectedItem is Room) Join(); };
            _list.KeyDown += (s, e) => { if (e.Key == Key.Enter) Join(); };
            var body = new Grid();
            Grid.SetRow(body, 2);
            body.Children.Add(_list);
            body.Children.Add(_empty);
            table.Children.Add(_head);
            table.Children.Add(rule);
            table.Children.Add(body);
            var card = Theme.Card(table, 0);

            var bottom = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            DockPanel.SetDock(_join, Dock.Right);
            bottom.Children.Add(_join);
            bottom.Children.Add(_status);

            var root = new Grid { Margin = new Thickness(20, 18, 20, 16), MinWidth = 1120, MinHeight = 560, Background = Theme.B("Ground900") };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(bar, 0);
            Grid.SetRow(card, 1);
            Grid.SetRow(bottom, 2);
            root.Children.Add(bar);
            root.Children.Add(card);
            root.Children.Add(bottom);
            Root = root;

            BuildHeader();
            _empty.Text = Words.Get(lang, "empty");
            _empty.Visibility = Visibility.Collapsed;
            SetStatus(Words.Get(lang, "ready"));
        }

        internal FrameworkElement Root { get; }

        Button Farm(string text, string style) => new Button
        {
            Content = text, Style = Theme.S(style), MinHeight = 36,
            Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(0, 0, 8, 0),
        };

        CheckBox Tick(string lang, string key, bool on) => new CheckBox
        {
            Content = Words.Get(lang, key), IsChecked = on, Style = Theme.S("Tick"),
            Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center,
        };

        /// <summary>The table's words, over the same columns as the rows, in the manager's label
        /// style: 12, semi-bold, muted; a column that sorts says so under the pointer, and the one
        /// sorting now carries its arrow.</summary>
        void BuildHeader()
        {
            _head.Children.Clear();
            _head.ColumnDefinitions.Clear();
            AddColumns(_head);
            for (var i = 0; i < Heads.Length; i++)
            {
                var text = new TextBlock
                {
                    Text = Words.Get(_lang, Heads[i]) + Arrow(i), FontSize = 12, FontWeight = FontWeights.SemiBold,
                    Foreground = Theme.B(SortOf[i].HasValue && SortOf[i].Value == _sort ? "Ink" : "Muted"),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = AlignRight[i] ? TextAlignment.Right : TextAlignment.Left,
                    HorizontalAlignment = HorizontalAlignment.Stretch, TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 0, AlignRight[i] ? 16 : 10, 0),
                };
                if (Numeric[i]) text.FontFamily = Theme.Font("Mono");
                if (SortOf[i].HasValue)
                {
                    var key = SortOf[i].Value;
                    text.Cursor = Cursors.Hand;
                    text.MouseLeftButtonUp += (s, e) => SortBy(key);
                }
                Grid.SetColumn(text, i);
                _head.Children.Add(text);
            }
        }

        string Arrow(int column) =>
            SortOf[column].HasValue && SortOf[column].Value == _sort ? (_sortDesc ? " ▼" : " ▲") : "";

        static void AddColumns(Grid grid)
        {
            foreach (var star in Columns)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(star, GridUnitType.Star) });
        }

        /// <summary>The list: no frame, no ground of its own, a row per lobby on the band of every
        /// other row, lit yellow when chosen — the manager's list, not the Windows one.</summary>
        void Dress()
        {
            _list.Background = Brushes.Transparent;
            _list.BorderThickness = new Thickness(0);
            _list.Padding = new Thickness(0);
            _list.Margin = new Thickness(0);
            _list.AlternationCount = 2;
            _list.ItemContainerStyle = Theme.Parse<Style>(Container);
            _list.ItemTemplate = Theme.Parse<DataTemplate>(Row);
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Visible);
            VirtualizingPanel.SetIsVirtualizing(_list, true);
            VirtualizingPanel.SetScrollUnit(_list, ScrollUnit.Pixel);
        }

        static string Container =>
            "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'" +
            " xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ListBoxItem'>" +
            "<Setter Property='Foreground' Value='$Ink$'/>" +
            "<Setter Property='HorizontalContentAlignment' Value='Stretch'/>" +
            "<Setter Property='FocusVisualStyle' Value='{x:Null}'/>" +
            "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ListBoxItem'>" +
            "<Border x:Name='b' Background='Transparent' Padding='16,0' Cursor='Hand'>" +
            "<ContentPresenter HorizontalAlignment='Stretch' VerticalAlignment='Center'/></Border>" +
            "<ControlTemplate.Triggers>" +
            "<Trigger Property='ItemsControl.AlternationIndex' Value='1'><Setter TargetName='b' Property='Background' Value='$Band$'/></Trigger>" +
            "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Background' Value='$Grid$'/></Trigger>" +
            "<Trigger Property='IsSelected' Value='True'><Setter TargetName='b' Property='Background' Value='$Yellow$'/>" +
            "<Setter Property='Foreground' Value='$TapeInk$'/></Trigger>" +
            "</ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>";

        static string Row
        {
            get
            {
                var xaml =
                    "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
                    "<Grid Height='34'><Grid.ColumnDefinitions>";
                foreach (var star in Columns)
                    xaml += "<ColumnDefinition Width='" + star.ToString(CultureInfo.InvariantCulture) + "*'/>";
                xaml +=
                    "</Grid.ColumnDefinitions><Grid.Resources>" +
                    "<Style TargetType='TextBlock'>" +
                    "<Setter Property='VerticalAlignment' Value='Center'/>" +
                    "<Setter Property='TextTrimming' Value='CharacterEllipsis'/>" +
                    "<Setter Property='Margin' Value='0,0,10,0'/>" +
                    "<Setter Property='Foreground' Value='{Binding Foreground, RelativeSource={RelativeSource AncestorType=ListBoxItem}}'/>" +
                    "</Style></Grid.Resources>";
                var cells = new[]
                {
                    new[] { "PlanetText", "SemiBold", "" },
                    new[] { "FrontText", "Normal", "" },
                    new[] { "DifficultyText", "Normal", "" },
                    new[] { "MembersText", "Normal", "mono" },
                    new[] { "MissionText", "Normal", "" },
                    new[] { "RegionText", "Normal", "" },
                    null,                                             // the SOS tag
                    new[] { "PingText", "Normal", "mono" },
                    new[] { "HostText", "Normal", "" },
                    new[] { "AgeText", "Normal", "mono" },
                };
                for (var i = 0; i < cells.Length; i++)
                {
                    if (cells[i] == null)
                    {
                        // the manager's own tag (a mod's LUA tag): blue box, dark words — and blue,
                        // not yellow, so it still reads on a chosen row's yellow
                        xaml += "<Border Grid.Column='" + i + "' Background='$Blue$' Padding='6,1' HorizontalAlignment='Left' VerticalAlignment='Center'>" +
                                "<Border.Style><Style TargetType='Border'><Style.Triggers>" +
                                "<DataTrigger Binding='{Binding SosText}' Value=''><Setter Property='Visibility' Value='Collapsed'/></DataTrigger>" +
                                "</Style.Triggers></Style></Border.Style>" +
                                "<TextBlock Text='SOS' FontSize='10' FontWeight='Bold' Foreground='$TapeInk$' Margin='0'/></Border>";
                        continue;
                    }
                    var extra = AlignRight[i] ? " TextAlignment='Right' Margin='0,0,16,0'" : "";
                    if (cells[i][2] == "mono") extra += " FontFamily='$Mono$' FontSize='12'";
                    if (cells[i][1] == "SemiBold") extra += " FontWeight='SemiBold'";
                    xaml += "<TextBlock Grid.Column='" + i + "' Text='{Binding " + cells[i][0] + "}'" + extra + "/>";
                }
                xaml += "</Grid></DataTemplate>";
                return xaml;
            }
        }

        void PickDifficulty()
        {
            var menu = new ContextMenu { PlacementTarget = _difficulty, Placement = PlacementMode.Bottom };
            menu.Items.Add(Choice(Words.Get(_lang, "diff_any"), 0));
            for (var i = 1; i <= 10; i++) menu.Items.Add(Choice(Words.F(_lang, "diff_n", i), i));
            menu.IsOpen = true;
        }

        MenuItem Choice(string text, int value)
        {
            var item = new MenuItem { Header = text, IsCheckable = true, IsChecked = value == _difficultyValue };
            item.Click += (s, e) =>
            {
                _difficultyValue = value;
                _difficulty.Content = text;
            };
            return item;
        }

        /// <summary>The planets the scan should ask for, by name in the manager's language; a
        /// front's chip over the list takes or leaves all of its planets at once.</summary>
        void PickPlanets()
        {
            var rows = Tables.Planets(_lang);
            if (rows.Count == 0)
            {
                SetStatus(Words.Get(_lang, "planet_none"));
                return;
            }
            var items = rows.Select(p => new Picker.Item
            {
                Key = p.Index.ToString(CultureInfo.InvariantCulture),
                Text = p.Name,
                Note = p.Sector,
                Group = Tables.FrontName(p.Front, _lang),
                On = _planets.Contains(p.Index),
            }).ToList();
            Picker.Show(_planetButton, Words.Get(_lang, "planet_pick"), Words.Get(_lang, "filter_search"),
                Words.Get(_lang, "filter_clear"), Words.Get(_lang, "filter_empty"), items, () =>
                {
                    _planets.Clear();
                    foreach (var item in items.Where(i => i.On))
                    {
                        int index;
                        if (int.TryParse(item.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out index)) _planets.Add(index);
                    }
                    UpdateFilterLabels();
                });
        }

        /// <summary>The missions the current results hold, filtered here: the scan is not asked
        /// again for them, so picking one costs nothing and the list keeps its age.</summary>
        void PickMissions()
        {
            if (_rooms.Count == 0)
            {
                SetStatus(Words.Get(_lang, "mission_none"));
                return;
            }
            var groups = _rooms.GroupBy(r => r.MissionHash ?? "").OrderByDescending(g => g.Count()).ToList();
            var items = groups.Select(g => new Picker.Item
            {
                Key = g.Key,
                Text = string.IsNullOrEmpty(g.First().Mission) ? Words.Get(_lang, "unknown_mission") : g.First().Mission,
                Note = Words.F(_lang, "mission_count", g.Count()),
                On = _missions.Contains(g.Key),
            }).ToList();
            Picker.Show(_missionButton, Words.Get(_lang, "mission_pick"), Words.Get(_lang, "filter_search"),
                Words.Get(_lang, "filter_clear"), Words.Get(_lang, "filter_empty"), items, () =>
                {
                    _missions.Clear();
                    foreach (var item in items.Where(i => i.On)) _missions.Add(item.Key);
                    UpdateFilterLabels();
                    ShowRows();
                });
        }

        void UpdateFilterLabels()
        {
            _planetButton.Content = _planets.Count == 0
                ? Words.Get(_lang, "filter_planet_all")
                : Words.F(_lang, "filter_planet_n", _planets.Count);
            _missionButton.Content = _missions.Count == 0
                ? Words.Get(_lang, "filter_mission_all")
                : Words.F(_lang, "filter_mission_n", _missions.Count);
        }

        void SortBy(Sort key)
        {
            if (_sort == key) _sortDesc = !_sortDesc;
            else
            {
                _sort = key;
                _sortDesc = key == Sort.Members;      // the fullest first, everything else as it reads
            }
            BuildHeader();
            ShowRows();
        }

        int Compare(Room a, Room b)
        {
            int result;
            switch (_sort)
            {
                case Sort.Planet: result = string.Compare(a.PlanetText, b.PlanetText, StringComparison.CurrentCulture); break;
                case Sort.Front: result = string.Compare(a.FrontText, b.FrontText, StringComparison.CurrentCulture); break;
                case Sort.Diff: result = a.Difficulty.CompareTo(b.Difficulty); break;
                case Sort.Members: result = a.Members.CompareTo(b.Members); break;
                case Sort.Mission: result = string.Compare(a.MissionText, b.MissionText, StringComparison.CurrentCulture); break;
                case Sort.Region: result = string.Compare(a.RegionText, b.RegionText, StringComparison.CurrentCulture); break;
                case Sort.Ping: result = (a.Ping ?? int.MaxValue).CompareTo(b.Ping ?? int.MaxValue); break;
                case Sort.Host: result = string.Compare(a.HostText, b.HostText, StringComparison.CurrentCulture); break;
                default: result = (a.Age < 0 ? long.MaxValue : a.Age).CompareTo(b.Age < 0 ? long.MaxValue : b.Age); break;
            }
            if (result == 0) result = string.Compare(a.PlanetText, b.PlanetText, StringComparison.CurrentCulture);
            return _sortDesc ? -result : result;
        }

        bool MatchesMission(Room room) => _missions.Count == 0 || _missions.Contains(room.MissionHash ?? "");

        /// <summary>The rows as the filters leave them, sorted: what the list shows.</summary>
        void ShowRows()
        {
            var rooms = _rooms.Where(MatchesMission).ToList();
            rooms.Sort(Compare);
            _list.ItemsSource = rooms;
            _empty.Text = Words.Get(_lang, "empty");
            _empty.Visibility = rooms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _join.IsEnabled = false;
        }

        void SetStatus(string text)
        {
            _status.Text = text;
            try { _foot?.Invoke(text, "Muted"); } catch { }   // the manager's own status bar
        }

        Filters Read()
        {
            var filters = new Filters
            {
                Difficulty = _difficultyValue,
                PublicOnly = _publicOnly.IsChecked == true,
                HideNoSession = _hideNoSession.IsChecked == true,
                HideFull = _hideFull.IsChecked == true,
                SosOnly = _sosOnly.IsChecked == true,
            };
            // a planet named means exactly that planet: the fronts only decide what the scan picks
            filters.Planets.AddRange(_planets);
            if (_planets.Count == 0)
                foreach (var box in _fronts)
                    if (box.IsChecked == true) filters.Fronts.Add((string)box.Tag);
            return filters;
        }

        internal void Refresh()
        {
            if (_busy) return;
            _busy = true;
            _refresh.IsEnabled = false;
            SetStatus(Words.Get(_lang, "scanning"));
            var filters = Read();
            var lang = _lang;
            var dispatcher = Host != null ? Host.Dispatcher : Dispatcher.CurrentDispatcher;
            Task.Run(() => Helper.Scan(_home, filters, lang, (done, total) =>
                    dispatcher.BeginInvoke((Action)(() => SetStatus(Words.F(lang, "scanning_planets", total))))))
                .ContinueWith(task => dispatcher.BeginInvoke((Action)(() => Finish(task, lang))));
        }

        void Finish(Task<ScanResult> task, string lang)
        {
            _busy = false;
            _refresh.IsEnabled = true;
            if (task.IsFaulted)
            {
                SetStatus(task.Exception != null ? task.Exception.GetBaseException().Message : "");
                return;
            }
            var result = task.Result;
            if (result.Error != null)
            {
                SetStatus(result.Error);
                _rooms = new List<Room>();
                _list.ItemsSource = null;
                _empty.Text = result.Error;
                _empty.Visibility = Visibility.Visible;
                return;
            }
            _rooms = result.Rooms;
            _self = result.Self;
            _missions.RemoveWhere(hash => _rooms.All(r => (r.MissionHash ?? "") != hash));
            UpdateFilterLabels();
            ShowRows();
            SetStatus(result.Rooms.Count == 0
                ? Words.Get(lang, "empty")
                : result.Planets == 0
                    ? Words.F(lang, "found_world", result.Rooms.Count)
                    : Words.F(lang, "found", result.Rooms.Count, result.Planets));
            MeasurePings(lang);
        }

        /// <summary>The ping column: each region's relay latency, measured beside the scan rather
        /// than in it (ICMP needs no Steam session), then the rows sorted again.</summary>
        void MeasurePings(string lang)
        {
            var regions = _rooms.Select(r => r.Region).Where(r => !string.IsNullOrEmpty(r)).Distinct().ToList();
            if (regions.Count == 0)
            {
                foreach (var room in _rooms) room.ShowPing();
                ShowRows();
                return;
            }
            var round = ++_pingRound;
            var dispatcher = Host != null ? Host.Dispatcher : Dispatcher.CurrentDispatcher;
            Task.Run(() => Ping.Regions(regions)).ContinueWith(task => dispatcher.BeginInvoke((Action)(() =>
            {
                if (round != _pingRound || task.IsFaulted) return;
                foreach (var room in _rooms)
                {
                    int? value;
                    room.Ping = room.Region != null && task.Result.TryGetValue(room.Region.ToUpperInvariant(), out value) ? value : null;
                    room.ShowPing();
                }
                ShowRows();
            })));
        }

        static bool GameRunning()
        {
            try { return Process.GetProcessesByName("helldivers2").Length > 0; }
            catch { return false; }
        }

        /// <summary>Join, and if the game is not running, start it first and enter the lobby once
        /// it is up: a join URL alone reaches a running game, and Steam will not hold one for a
        /// game that is still loading.</summary>
        async void Join()
        {
            var room = _list.SelectedItem as Room;
            if (room == null)
            {
                SetStatus(Words.Get(_lang, "no_selection"));
                return;
            }
            var url = JoinLink.Url(room.Lobby, room.HostId, _self);
            try
            {
                if (!GameRunning())
                {
                    SetStatus(Words.Get(_lang, "launching"));
                    Process.Start(new ProcessStartInfo { FileName = "steam://rungameid/" + JoinLink.AppId, UseShellExecute = true });
                    var until = DateTime.UtcNow.AddSeconds(150);
                    while (DateTime.UtcNow < until && !GameRunning()) await Task.Delay(1000);
                    if (!GameRunning())
                    {
                        SetStatus(Words.Get(_lang, "launch_timeout"));
                        return;
                    }
                    SetStatus(Words.Get(_lang, "game_started"));
                    await Task.Delay(4000);                 // its window, then the lobby
                }
                SetStatus(Words.F(_lang, "join_sent", room.PlanetText + " · " + room.DifficultyText));
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception e)
            {
                SetStatus(e.Message);
            }
        }
    }
}
