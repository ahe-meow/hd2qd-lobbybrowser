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
    /// <summary>The plugin's body — ghost buttons, chips, ticks, a dark card, banded rows, the
    /// count and Join in a bar at the bottom right — shown as a tab of the manager's window when
    /// Quick Deploy 0.25.0 passes host["tab"], or inside host["window"]'s frame on older managers
    /// and in the standalone build.</summary>
    internal static class BrowserWindow
    {
        /// <summary>Returns the tab's "show" action, or null when opened as a window.</summary>
        internal static Action Show(Window owner, Dictionary<string, object> host, Action closed)
        {
            var shell = new LobbyShell(host, closed);
            if (host.TryGetValue("tab", out var t) && t is Func<string, FrameworkElement, Action, Dictionary<string, object>> tab)
            {
                // The tab has no window of its own. Shell stops its timers when the manager
                // closes the tab, and tells Plugin so a later Open shows this tab again.
                var handle = tab(Words.Get(shell.Lang, "title"), shell.Root, shell.Close);
                return handle.TryGetValue("show", out var show) ? show as Action : null;
            }
            var make = (Func<string, FrameworkElement, Window>)host["window"];
            var window = make(Words.Get(shell.Lang, "title"), shell.Root);
            shell.Host = window;
            window.Show();
            return null;
        }
    }

    /// <summary>The window body, and the one thing that survives a language switch: the element
    /// the manager's frame holds. A switch builds that body again in the other language, carrying
    /// the filters, the rows already scanned and the pings measured so far — the frame itself is
    /// the manager's, and is not touched.</summary>
    internal sealed class LobbyShell
    {
        readonly Grid _root = new Grid();
        readonly Dictionary<string, object> _host;
        readonly string _home;
        readonly string _handed;
        readonly Action _closed;
        LobbyPage _page;

        internal LobbyShell(Dictionary<string, object> host, Action closed = null)
        {
            _host = host;
            _closed = closed;
            _home = host != null && host.TryGetValue("home", out var home) ? home as string : null;
            War.Warm(_home);                   // upstream WAR.warm(): variants ready before the first scan
            _handed = Words.Lang(host);
            Lang = Language.Chosen(_home) ?? Language.Manager(_home, _handed);
            Show(null);
        }

        /// <summary>The manager calls this once when it closes our tab: stop the timers and let
        /// Plugin forget the tab handle. A window's own Closed does the same through Host.</summary>
        internal void Close() { _page?.Stop(); _closed?.Invoke(); }

        /// <summary>The language this body is drawn in.</summary>
        internal string Lang { get; private set; }

        internal FrameworkElement Root => _root;

        Window _window;
        internal Window Host
        {
            get => _window;
            set
            {
                _window = value;
                if (value != null) value.Closed += (s, e) => _page?.Stop();
            }
        }

        internal void Refresh() => _page.Refresh();

        /// <summary>Another language for this window, or (null) HD2QD's own again. The choice is
        /// kept beside the presets; the body is drawn once more in it with what it already knew.</summary>
        internal void Switch(string tag)
        {
            var state = _page.Save();
            Language.Choose(_home, tag);
            Lang = tag ?? Language.Manager(_home, _handed);
            Show(state);
        }

        void Show(LobbyPage.State state)
        {
            _page?.Stop();
            _root.Children.Clear();
            _page = new LobbyPage(Lang, _host, this, state);
            _root.Children.Add(_page.Root);
        }
    }

    internal sealed class LobbyPage
    {
        enum Sort { Planet, Front, Diff, Members, Mission, Privacy, Platform, Region, Ping, Host, Age }

        // the table's columns, in the same stars in the header and in every row
        // the front column carries the variant names under the front, so it needs room
        static readonly double[] Columns = { 1.25, 1.3, 0.6, 0.58, 1.25, 0.55, 0.5, 0.55, 0.36, 0.62, 0.85, 0.48 };
        static readonly string[] Heads = { "col_planet", "col_front", "col_diff", "col_members", "col_mission", "col_priv", "col_platform", "col_region", "col_sos", "col_ping", "col_host", "col_age" };
        static readonly bool[] AlignRight = { false, false, false, true, false, false, false, false, false, true, false, true };
        static readonly bool[] Numeric = { false, false, false, true, false, false, false, false, false, true, false, true };
        static readonly Sort?[] SortOf = { Sort.Planet, Sort.Front, Sort.Diff, Sort.Members, Sort.Mission, Sort.Privacy, Sort.Platform, Sort.Region, null, Sort.Ping, Sort.Host, Sort.Age };

        readonly string _lang;
        readonly string _home;
        readonly Action<string, string> _foot;
        readonly ListBox _list = new ListBox();
        readonly Grid _head = new Grid { Height = 34, Margin = new Thickness(16, 0, 26, 0) };
        readonly TextBlock _status = new TextBlock { FontSize = 12, Foreground = Theme.B("Muted"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly TextBlock _progressDetail = new TextBlock
        {
            FontSize = 12, Foreground = Theme.B("Muted"), TextTrimming = TextTrimming.CharacterEllipsis,
            Visibility = Visibility.Collapsed,
        };
        readonly Border _progressTrack;
        readonly Border _progressFill = new Border { Background = Theme.B("Yellow") };
        readonly ColumnDefinition _progressBefore = new ColumnDefinition();
        readonly ColumnDefinition _progressBar = new ColumnDefinition();
        readonly ColumnDefinition _progressAfter = new ColumnDefinition();
        readonly DispatcherTimer _progressTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        readonly DispatcherTimer _ageTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5),
        };
        double _progressPhase;
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
        readonly Button _mapButton;
        readonly Button _variantButton;
        readonly Button _presetButton;
        readonly Button _join;
        Popup _openPopup;
        ContextMenu _openMenu;
        readonly Func<string, string, string, string> _askText;
        readonly HashSet<int> _planets = new HashSet<int>();
        readonly HashSet<string> _missions = new HashSet<string>();     // by mission hash
        readonly HashSet<int> _mapSel = new HashSet<int>();             // V26/V27: contains-any
        readonly HashSet<string> _varSel = new HashSet<string>();       // V27: contains-any
        readonly HashSet<int> _difficulties = new HashSet<int>();       // V28: 0 = any
        string _selfLobby = "";                                          // V28: my current lobby
        Room _selfRoom;                                                  // kept across refreshes
        List<Room> _rooms = new List<Room>();
        Dictionary<int, List<string>> _effects = new Dictionary<int, List<string>>();  // V27: planet -> variants
        Dictionary<string, int> _steamPings = new Dictionary<string, int>();
        int _difficultyValue;
        string _self = "0";
        Sort _sort = Sort.Members;
        bool _sortDesc = true;
        bool _busy;
        bool _showHint = true;                 // first open: wait for a manual Refresh
        string _note;
        int _pingRound;

        LobbyShell _shell;
        int _scannedPlanets;

        internal LobbyPage(string lang, Dictionary<string, object> host, LobbyShell shell, State state)
        {
            _lang = lang;
            _shell = shell;
            var meter = new Grid();
            meter.ColumnDefinitions.Add(_progressBefore);
            meter.ColumnDefinitions.Add(_progressBar);
            meter.ColumnDefinitions.Add(_progressAfter);
            Grid.SetColumn(_progressFill, 1);
            meter.Children.Add(_progressFill);
            // a full-width hairline stuck to the window's bottom edge — the negative side margins
            // cross the root's gutters, so the fill starts at the frame, not inside it
            _progressTrack = new Border
            {
                Height = 6, Margin = new Thickness(-20, 10, -20, 0),
                Background = Theme.B("Grid"), ClipToBounds = true, Child = meter,
                VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed,
            };
            _progressTimer.Tick += (s, e) => { _progressPhase = (_progressPhase + 0.7) % 70; Bar(-1); };
            _ageTimer.Tick += (s, e) => { foreach (var room in _rooms) room.RefreshAge(); };
            _ageTimer.Start();
            _home = host != null && host.TryGetValue("home", out var home) ? home as string : null;
            _foot = host != null && host.TryGetValue("foot", out var foot) ? foot as Action<string, string> : null;
            _askText = host != null && host.TryGetValue("ask_text", out var ask) ? ask as Func<string, string, string, string> : null;

            _refresh = Farm(Words.Get(lang, "refresh"), "Ghost");
            _refresh.ToolTip = Words.Get(lang, "refresh_tip");
            _refresh.Click += (s, e) => Refresh();

            _planetButton = Farm(Words.Get(lang, "filter_planet_all"), "Ghost");
            _planetButton.Click += (s, e) => PickPlanets();
            _difficulty = Farm(Words.Get(lang, "diff_any"), "Ghost");
            _difficulty.Click += (s, e) => PickDifficulty();
            _missionButton = Farm(Words.Get(lang, "filter_mission_all"), "Ghost");
            _missionButton.Click += (s, e) => PickMissions();
            _mapButton = Farm(Words.Get(lang, "filter_map_all"), "Ghost");
            _mapButton.ToolTip = Words.Get(lang, "map_tip");
            _mapButton.Click += (s, e) => PickMaps();
            _variantButton = Farm(Words.Get(lang, "filter_variant_all"), "Ghost");
            _variantButton.ToolTip = Words.Get(lang, "variant_tip");
            _variantButton.Click += (s, e) => PickVariants();
            _presetButton = Farm(Words.Get(lang, "preset"), "Ghost");
            _presetButton.Click += (s, e) => PickPresets();

            _join = new Button
            {
                Content = Words.Get(lang, "join"), Style = Theme.S("Primary"), MinHeight = 40, MinWidth = 116,
                Padding = new Thickness(22, 0, 22, 0), IsEnabled = false, ToolTip = Words.Get(lang, "hint_wait"),
            };
            _join.Click += (s, e) => Join();

            // three groups on one line — the filters that open something, the fronts, the
            // switches — with a hairline between them, so a wrapped line never reads as one run
            var bar = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            bar.Children.Add(_refresh);
            bar.Children.Add(_planetButton);
            bar.Children.Add(_difficulty);
            bar.Children.Add(_missionButton);
            bar.Children.Add(_mapButton);
            bar.Children.Add(_variantButton);
            bar.Children.Add(_presetButton);
            bar.Children.Add(Rule());
            foreach (var front in new[] { "Terminids", "Automatons", "Illuminate" })
            {
                var chip = new CheckBox
                {
                    Content = Tables.FrontShort(front, lang), Tag = front, IsChecked = true, Style = Theme.S("Chip"),
                    ToolTip = Tables.FrontName(front, lang),
                };
                _fronts.Add(chip);
                bar.Children.Add(chip);
            }
            bar.Children.Add(Rule());
            _publicOnly = Tick(lang, "tick_public", "public_only", true);
            _hideNoSession = Tick(lang, "tick_nosession", "hide_nosession", true);
            _hideFull = Tick(lang, "tick_full", "hide_full", false);
            _sosOnly = Tick(lang, "tick_sos", "sos_only", false);
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
            _list.SelectionChanged += (s, e) => SyncJoin(_list.SelectedItem as Room);
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

            var line = new DockPanel();
            DockPanel.SetDock(_join, Dock.Right);
            line.Children.Add(_join);
            line.Children.Add(_status);

            var bottom = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            bottom.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            bottom.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            bottom.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(line, 0);
            _progressDetail.Margin = new Thickness(0, 5, 0, 0);
            Grid.SetRow(_progressDetail, 1);
            Grid.SetRow(_progressTrack, 2);
            bottom.Children.Add(line);
            bottom.Children.Add(_progressDetail);
            bottom.Children.Add(_progressTrack);

            var root = new Grid { Margin = new Thickness(20, 18, 20, 0), MinWidth = 1120, MinHeight = 560, Background = Theme.B("Ground900") };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            // the filters left, the language of this window at the right edge of the same row
            var language = new Button
            {
                Content = _lang == Language.Zh ? "中文" : "EN",
                Style = Theme.S("Ghost"), MinHeight = 36, Padding = new Thickness(14, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Center, ToolTip = Words.Get(lang, "language"),
            };
            language.Click += (s, e) => PickLanguage(language);
            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
            DockPanel.SetDock(language, Dock.Right);
            top.Children.Add(language);
            top.Children.Add(bar);
            Grid.SetRow(top, 0);
            Grid.SetRow(card, 1);
            Grid.SetRow(bottom, 2);
            root.Children.Add(top);
            root.Children.Add(card);
            root.Children.Add(bottom);
            Root = root;

            BuildHeader();
            if (state != null)
            {
                Restore(state);
                foreach (var room in _rooms) room.Dress(lang);   // a language switch rebuilds the body
            }
            _showHint = state == null || state.Rooms.Count == 0;
            ShowRows();
            if (state != null && state.Rooms.Count > 0) SetFoundText(state.Rooms.Count);
            else SetStatus(Words.Get(lang, "ready"));
        }

        internal FrameworkElement Root { get; }

        /// <summary>What a language switch carries over: the filters in hand, the rows already
        /// scanned (with their pings), and how many planets the scan asked about.</summary>
        internal sealed class State
        {
            internal List<Room> Rooms = new List<Room>();
            internal HashSet<int> Planets = new HashSet<int>();
            internal HashSet<string> Missions = new HashSet<string>();
            internal HashSet<int> Maps = new HashSet<int>();
            internal HashSet<string> Vars = new HashSet<string>();
            internal HashSet<int> Difficulties = new HashSet<int>();
            internal string SelfLobby = "";
            internal Room SelfRoom;
            internal List<string> Fronts = new List<string>();
            internal int Difficulty;
            internal bool PublicOn = true;
            internal bool NoSessionOn = true;
            internal bool FullOn;
            internal bool SosOn;
            internal int Sort;
            internal bool SortDesc;
            internal string Self = "0";
            internal int PlanetsScanned;
        }

        internal State Save() => new State
        {
            Rooms = _rooms,
            Planets = new HashSet<int>(_planets),
            Missions = new HashSet<string>(_missions),
            Maps = new HashSet<int>(_mapSel),
            Vars = new HashSet<string>(_varSel),
            Difficulties = new HashSet<int>(_difficulties),
            SelfLobby = _selfLobby,
            SelfRoom = _selfRoom,
            Fronts = _fronts.Where(box => box.IsChecked == true).Select(box => (string)box.Tag).ToList(),
            Difficulty = _difficultyValue,
            PublicOn = _publicOnly.IsChecked == true,
            NoSessionOn = _hideNoSession.IsChecked == true,
            FullOn = _hideFull.IsChecked == true,
            SosOn = _sosOnly.IsChecked == true,
            Sort = (int)_sort,
            SortDesc = _sortDesc,
            Self = _self,
            PlanetsScanned = _scannedPlanets,
        };

        void Restore(State state)
        {
            _rooms = state.Rooms;
            _self = state.Self;
            _scannedPlanets = state.PlanetsScanned;
            _planets.Clear();
            foreach (var planet in state.Planets) _planets.Add(planet);
            _missions.Clear();
            foreach (var hash in state.Missions) _missions.Add(hash);
            _mapSel.Clear();
            foreach (var map in state.Maps) _mapSel.Add(map);
            _varSel.Clear();
            foreach (var variant in state.Vars) _varSel.Add(variant);
            _difficulties.Clear();
            foreach (var diff in state.Difficulties) _difficulties.Add(diff);
            _selfLobby = state.SelfLobby ?? "";
            _selfRoom = state.SelfRoom;
            _difficultyValue = state.Difficulty;
            if (_difficultyValue > 0) _difficulty.Content = Words.F(_lang, "diff_n", _difficultyValue);
            _publicOnly.IsChecked = state.PublicOn;
            _hideNoSession.IsChecked = state.NoSessionOn;
            _hideFull.IsChecked = state.FullOn;
            _sosOnly.IsChecked = state.SosOn;
            foreach (var box in _fronts) box.IsChecked = state.Fronts.Contains((string)box.Tag);
            _sort = (Sort)state.Sort;
            _sortDesc = state.SortDesc;
            BuildHeader();
            UpdateFilterLabels();
        }

        /// <summary>The two languages of this plugin, and the manager's own: the choice is the
        /// window's (kept beside the presets), and following HD2QD is what it starts with.</summary>
        void PickLanguage(Button anchor)
        {
            CloseFloats();
            var chosen = Language.Chosen(_home);
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
            WatchMenu(menu);
            var zh = new MenuItem { Header = "中文", IsCheckable = true, IsChecked = _lang == Language.Zh };
            zh.Click += (s, e) => _shell.Switch(Language.Zh);
            var en = new MenuItem { Header = "English", IsCheckable = true, IsChecked = _lang == Language.En };
            en.Click += (s, e) => _shell.Switch(Language.En);
            var follow = new MenuItem { Header = Words.Get(_lang, "lang_follow"), IsCheckable = true, IsChecked = chosen == null };
            follow.Click += (s, e) => _shell.Switch(null);
            menu.Items.Add(zh);
            menu.Items.Add(en);
            menu.Items.Add(new Separator());
            menu.Items.Add(follow);
            menu.IsOpen = true;
        }

        Button Farm(string text, string style) => new Button
        {
            Content = text, Style = Theme.S(style), MinHeight = 34,
            Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 6, 0),
        };

        /// <summary>A hairline between two groups of the filter row: the manager's grid colour,
        /// as tall as a button's words.</summary>
        static Border Rule() => new Border
        {
            Width = 1, Height = 18, Background = Theme.B("Grid"),
            Margin = new Thickness(8, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
        };

        /// <summary>A switch: a short word on the row, the whole sentence in its tip.</summary>
        CheckBox Tick(string lang, string key, string tip, bool on) => new CheckBox
        {
            Content = Words.Get(lang, key), IsChecked = on, Style = Theme.S("Tick"),
            ToolTip = Words.Get(lang, tip), FontSize = 12,
            Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
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
            "<Border x:Name='b' Background='Transparent' BorderBrush='Transparent' BorderThickness='3,0,0,0' Padding='13,0' Cursor='Hand'>" +
            "<ContentPresenter HorizontalAlignment='Stretch' VerticalAlignment='Center'/></Border>" +
            "<ControlTemplate.Triggers>" +
            "<Trigger Property='ItemsControl.AlternationIndex' Value='1'><Setter TargetName='b' Property='Background' Value='$Band$'/></Trigger>" +
            "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Background' Value='$Grid$'/></Trigger>" +
            "<DataTrigger Binding='{Binding IsMine}' Value='True'><Setter TargetName='b' Property='Background' Value='#33C8A13A'/>" +
            "<Setter TargetName='b' Property='BorderBrush' Value='$Yellow$'/></DataTrigger>" +
            "<Trigger Property='IsSelected' Value='True'><Setter TargetName='b' Property='Background' Value='$Yellow$'/>" +
            "<Setter Property='Foreground' Value='$TapeInk$'/></Trigger>" +
            "</ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>";

        static string Row
        {
            get
            {
                var xaml =
                    "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
                    "<Grid Height='44'><Grid.ColumnDefinitions>";
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
                    new[] { "PrivacyText", "Normal", "" },
                    new[] { "PlatformText", "Normal", "" },
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
                    if (i == 0)
                    {
                        // the planet cell carries the sector and map type under its name
                        xaml += "<StackPanel Grid.Column='0' Margin='0,0,10,0' VerticalAlignment='Center'>" +
                                "<TextBlock Text='{Binding PlanetText}' FontSize='13' FontWeight='SemiBold'/>" +
                                "<TextBlock Text='{Binding SubText}' FontSize='11' Opacity='0.78'/>" +
                                "</StackPanel>";
                        continue;
                    }
                    if (i == 1)
                    {
                        // variants are planet-level; they read under the front, in the same small type
                        xaml += "<StackPanel Grid.Column='1' Margin='0,0,10,0' VerticalAlignment='Center'>" +
                                "<TextBlock Text='{Binding FrontText}' FontSize='13'/>" +
                                "<TextBlock Text='{Binding VariantText}' FontSize='11' Opacity='0.78' TextWrapping='Wrap' TextTrimming='None'/>" +
                                "</StackPanel>";
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

        /// <summary>V28: the difficulties are ticks, not one menu choice. An empty set is "any";
        /// the old single value stays for old presets and for the Steam query split.</summary>
        void PickDifficulty()
        {
            CloseFloats();
            var items = Enumerable.Range(0, 11).Select(i => new Picker.Item
            {
                Key = i.ToString(CultureInfo.InvariantCulture),
                Text = i == 0 ? Words.Get(_lang, "diff_any") : Words.F(_lang, "diff_n", i),
                Note = Words.F(_lang, "map_count", _rooms.Count(r => i == 0 || r.Difficulty == i)),
                On = i == 0 ? _difficulties.Count == 0 : _difficulties.Contains(i),
            }).ToList();
            _openPopup = Picker.Show(_difficulty, Words.Get(_lang, "difficulty_pick"), Words.Get(_lang, "filter_search"),
                Words.Get(_lang, "filter_clear"), Words.Get(_lang, "filter_all"), Words.Get(_lang, "filter_invert"),
                Words.Get(_lang, "filter_empty"), items, () =>
                {
                    _difficulties.Clear();
                    foreach (var item in items.Where(i => i.On))
                    {
                        int value;
                        if (int.TryParse(item.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > 0)
                            _difficulties.Add(value);
                    }
                    UpdateFilterLabels();
                    ShowRows();
                });
            WatchPopup(_openPopup);
        }

        /// <summary>V26/V27: map types come from the mission hash in the current results; a room
        /// with several scenes matches any selected one.</summary>
        void PickMaps()
        {
            CloseFloats();
            var counts = new Dictionary<int, int>();
            foreach (var room in _rooms) foreach (var type in room.MapTypes)
                if (!counts.ContainsKey(type)) counts[type] = 1; else counts[type]++;
            if (counts.Count == 0)
            {
                SetStatus(Words.Get(_lang, "map_empty"));
                return;
            }
            var items = counts.Keys.OrderBy(k => k).Select(k => new Picker.Item
            {
                Key = k.ToString(CultureInfo.InvariantCulture),
                Text = Tables.MapTypeName(k, _lang),
                Note = Words.F(_lang, "map_count", counts[k]),
                On = _mapSel.Contains(k),
            }).ToList();
            _openPopup = Picker.Show(_mapButton, Words.Get(_lang, "map_pick"), Words.Get(_lang, "filter_search"),
                Words.Get(_lang, "filter_clear"), Words.Get(_lang, "filter_all"), Words.Get(_lang, "filter_invert"),
                Words.Get(_lang, "filter_empty"), items, () =>
                {
                    _mapSel.Clear();
                    foreach (var item in items.Where(i => i.On))
                    {
                        int value;
                        if (int.TryParse(item.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) _mapSel.Add(value);
                    }
                    UpdateFilterLabels();
                    ShowRows();
                });
            WatchPopup(_openPopup);
        }

        /// <summary>V27/V28: variants are planet-level; the list is every known type, with the
        /// count in the current results so an absent one reads as 0.</summary>
        void PickVariants()
        {
            CloseFloats();
            var counts = new Dictionary<string, int>();
            foreach (var room in _rooms) foreach (var variant in room.Variants)
                if (!counts.ContainsKey(variant)) counts[variant] = 1; else counts[variant]++;
            // Upstream's warReady is bool(WAR and WAR.planets); when true, buildVarPanel lists
            // every VARLABELS key with a count from the current rooms. Match that exactly.
            _effects = War.Effects(_home);
            if (_effects.Count == 0)
            {
                SetStatus(Words.Get(_lang, "variant_empty"));
                return;
            }
            var items = Tables.VariantKeys().OrderByDescending(k => counts.ContainsKey(k) ? counts[k] : 0)
                .ThenBy(k => Tables.VariantName(k, _lang)).Select(k => new Picker.Item
                {
                    Key = k,
                    Text = Tables.VariantName(k, _lang),
                    Note = Words.F(_lang, "variant_count", counts.ContainsKey(k) ? counts[k] : 0),
                    On = _varSel.Contains(k),
                }).ToList();
            _openPopup = Picker.Show(_variantButton, Words.Get(_lang, "variant_pick"), Words.Get(_lang, "filter_search"),
                Words.Get(_lang, "filter_clear"), Words.Get(_lang, "filter_all"), Words.Get(_lang, "filter_invert"),
                Words.Get(_lang, "variant_empty"), items, () =>
                {
                    _varSel.Clear();
                    foreach (var item in items.Where(i => i.On)) _varSel.Add(item.Key);
                    UpdateFilterLabels();
                    ShowRows();
                });
            WatchPopup(_openPopup);
        }

        /// <summary>V28: only one filter panel at a time. The manager's own menus obey the same
        /// rule, so a Popup or a ContextMenu never stacks over another.</summary>
        void CloseFloats()
        {
            if (_openPopup != null) { _openPopup.IsOpen = false; _openPopup = null; }
            if (_openMenu != null) { _openMenu.IsOpen = false; _openMenu = null; }
        }

        void WatchPopup(Popup popup)
        {
            if (popup == null) return;
            popup.Closed += (s, e) => { if (ReferenceEquals(_openPopup, popup)) _openPopup = null; };
        }

        void WatchMenu(ContextMenu menu)
        {
            _openMenu = menu;
            menu.Closed += (s, e) => { if (ReferenceEquals(_openMenu, menu)) _openMenu = null; };
        }

        /// <summary>The planets the scan should ask for, by name in the manager's language, in a
        /// sector → planet menu: a sector's own tick box takes or leaves all its planets.</summary>
        void PickPlanets()
        {
            CloseFloats();
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
                // V28: group by sector so ticking a group takes a whole sector; the front is
                // already a chip on the filter row, so it moves to the note.
                Note = Tables.FrontName(p.Front, _lang),
                Group = Tables.Sector(p.Index, _lang),
                On = _planets.Contains(p.Index),
            }).ToList();
            _openPopup = Picker.Show(_planetButton, Words.Get(_lang, "planet_pick"), Words.Get(_lang, "filter_search"),
                Words.Get(_lang, "filter_clear"), Words.Get(_lang, "filter_all"), Words.Get(_lang, "filter_invert"),
                Words.Get(_lang, "filter_empty"), items, () =>
                {
                    _planets.Clear();
                    foreach (var item in items.Where(i => i.On))
                    {
                        int index;
                        if (int.TryParse(item.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out index)) _planets.Add(index);
                    }
                    UpdateFilterLabels();
                }, grouped: true);
            WatchPopup(_openPopup);
        }

        /// <summary>The missions the current results hold, filtered here: the scan is not asked
        /// again for them, so picking one costs nothing and the list keeps its age.</summary>
        void PickMissions()
        {
            CloseFloats();
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
            _openPopup = Picker.Show(_missionButton, Words.Get(_lang, "mission_pick"), Words.Get(_lang, "filter_search"),
                Words.Get(_lang, "filter_clear"), Words.Get(_lang, "filter_all"), Words.Get(_lang, "filter_invert"),
                Words.Get(_lang, "filter_empty"), items, () =>
                {
                    _missions.Clear();
                    foreach (var item in items.Where(i => i.On)) _missions.Add(item.Key);
                    UpdateFilterLabels();
                    ShowRows();
                });
            WatchPopup(_openPopup);
        }

        /// <summary>The presets: the filters in hand saved under a name, and put back with one
        /// click. Each name has its own line, with Load and Delete beside it — a list that grows
        /// costs one line a preset, not two (the user, 2026-11-25).</summary>
        void PickPresets()
        {
            CloseFloats();
            var popup = new Popup
            {
                PlacementTarget = _presetButton, Placement = PlacementMode.Bottom, VerticalOffset = 6,
                StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.None,
            };
            void Paint()
            {
                var presets = Presets.Load(_home);
                popup.Child = PresetView.Card(presets, _lang,
                    name =>
                    {
                        popup.IsOpen = false;
                        var preset = presets.Find(p => string.Equals(p.Name, name, StringComparison.CurrentCulture));
                        if (preset != null) ApplyPreset(preset);
                    },
                    name =>
                    {
                        RemovePreset(name);
                        Paint();                       // the list is drawn again, one line shorter
                    },
                    () =>
                    {
                        popup.IsOpen = false;
                        SavePreset(presets);
                    });
            }
            Paint();
            popup.IsOpen = true;
            _openPopup = popup;
            WatchPopup(popup);
        }

        /// <summary>The filters in hand, named: a name already in the list is replaced by what is
        /// in hand now, which is how a preset is changed.</summary>
        void SavePreset(List<Preset> presets)
        {
            var name = AskName(presets);
            if (string.IsNullOrEmpty(name)) return;
            var preset = CollectPreset(name);
            var same = presets.FindIndex(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
            if (same >= 0) presets[same] = preset;
            else presets.Add(preset);
            var error = Presets.Save(_home, presets);
            SetStatus(error == null ? Words.F(_lang, "preset_saved", name) : Words.F(_lang, "preset_failed", error));
            UpdateFilterLabels();
        }

        /// <summary>The manager's own "type a line in" dialog when it is there (it hands one in
        /// host["ask_text"]); otherwise the next free default name, so no click is lost.</summary>
        string AskName(List<Preset> presets)
        {
            var start = Words.F(_lang, "preset_name_default", presets.Count + 1);
            if (_askText != null)
            {
                try
                {
                    var typed = _askText(Words.Get(_lang, "preset_name"), Words.Get(_lang, "preset_save_action"), start);
                    return typed == null ? null : typed.Trim();
                }
                catch
                {
                    // an older manager without the dialog: fall through to the default
                }
            }
            var index = presets.Count + 1;
            while (presets.Exists(p => string.Equals(p.Name, Words.F(_lang, "preset_name_default", index), StringComparison.CurrentCultureIgnoreCase)))
                index++;
            return Words.F(_lang, "preset_name_default", index);
        }

        Preset CollectPreset(string name) => new Preset
        {
            Name = name,
            Planets = _planets.ToList(),
            Fronts = _fronts.Where(box => box.IsChecked == true).Select(box => (string)box.Tag).ToList(),
            Difficulty = _difficultyValue,
            Difficulties = _difficulties.ToList(),
            Maps = _mapSel.ToList(),
            Vars = _varSel.ToList(),
            PublicOnly = _publicOnly.IsChecked == true,
            HideNoSession = _hideNoSession.IsChecked == true,
            HideFull = _hideFull.IsChecked == true,
            SosOnly = _sosOnly.IsChecked == true,
            Missions = _missions.ToList(),
            Sort = (int)_sort,
            SortDesc = _sortDesc,
        };

        void ApplyPreset(Preset preset)
        {
            _planets.Clear();
            foreach (var planet in preset.Planets) _planets.Add(planet);
            foreach (var box in _fronts) box.IsChecked = preset.Fronts.Contains((string)box.Tag);
            _difficultyValue = preset.Difficulty;
            _difficulty.Content = preset.Difficulty <= 0 ? Words.Get(_lang, "diff_any") : Words.F(_lang, "diff_n", preset.Difficulty);
            _difficulties.Clear();
            foreach (var diff in preset.Difficulties) _difficulties.Add(diff);
            _mapSel.Clear();
            foreach (var map in preset.Maps) _mapSel.Add(map);
            _varSel.Clear();
            foreach (var variant in preset.Vars) _varSel.Add(variant);
            _publicOnly.IsChecked = preset.PublicOnly;
            _hideNoSession.IsChecked = preset.HideNoSession;
            _hideFull.IsChecked = preset.HideFull;
            _sosOnly.IsChecked = preset.SosOnly;
            _missions.Clear();
            foreach (var hash in preset.Missions) _missions.Add(hash);
            _sort = (Sort)preset.Sort;
            _sortDesc = preset.SortDesc;
            BuildHeader();
            UpdateFilterLabels();
            SetStatus(Words.F(_lang, "preset_applied", preset.Name));
            Refresh();
        }

        void RemovePreset(string name)
        {
            var presets = Presets.Load(_home);
            presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
            var error = Presets.Save(_home, presets);
            SetStatus(error == null ? Words.F(_lang, "preset_removed", name) : Words.F(_lang, "preset_failed", error));
        }

        void UpdateFilterLabels()
        {
            _planetButton.Content = _planets.Count == 0
                ? Words.Get(_lang, "filter_planet_all")
                : Words.F(_lang, "filter_planet_n", _planets.Count);
            _missionButton.Content = _missions.Count == 0
                ? Words.Get(_lang, "filter_mission_all")
                : Words.F(_lang, "filter_mission_n", _missions.Count);
            _mapButton.Content = _mapSel.Count == 0
                ? Words.Get(_lang, "filter_map_all")
                : Words.F(_lang, "filter_map_n", _mapSel.Count);
            _variantButton.Content = _varSel.Count == 0
                ? Words.Get(_lang, "filter_variant_all")
                : Words.F(_lang, "filter_variant_n", _varSel.Count);
            _difficulty.Content = _difficulties.Count == 0
                ? Words.Get(_lang, "diff_any")
                : Words.F(_lang, "filter_difficulty_n", _difficulties.Count);
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
                case Sort.Privacy: result = a.Privacy.CompareTo(b.Privacy); break;
                case Sort.Platform: result = string.Compare(a.PlatformText, b.PlatformText, StringComparison.CurrentCulture); break;
                case Sort.Region: result = string.Compare(a.RegionText, b.RegionText, StringComparison.CurrentCulture); break;
                case Sort.Ping: result = (a.Ping ?? int.MaxValue).CompareTo(b.Ping ?? int.MaxValue); break;
                case Sort.Host: result = string.Compare(a.HostText, b.HostText, StringComparison.CurrentCulture); break;
                default: result = (a.Age < 0 ? long.MaxValue : a.Age).CompareTo(b.Age < 0 ? long.MaxValue : b.Age); break;
            }
            if (result == 0) result = string.Compare(a.PlanetText, b.PlanetText, StringComparison.CurrentCulture);
            return _sortDesc ? -result : result;
        }

        bool MatchesMission(Room room) => _missions.Count == 0 || _missions.Contains(room.MissionHash ?? "");
        bool MatchesMap(Room room) => _mapSel.Count == 0 || room.MapTypes.Any(t => _mapSel.Contains(t));
        bool MatchesVariant(Room room) => _varSel.Count == 0 || room.Variants.Any(v => _varSel.Contains(v));
        bool MatchesDifficulty(Room room) => _difficulties.Count == 0 || _difficulties.Contains(room.Difficulty);

        /// <summary>The rows as the filters leave them, sorted, with this player's room pinned:
        /// what the list shows and what the count in the status bar speaks of.</summary>
        void ShowRows()
        {
            var rooms = _rooms.Where(r => MatchesMission(r) && MatchesMap(r) && MatchesVariant(r) && MatchesDifficulty(r)).ToList();
            rooms.Sort(Compare);
            // V28: the room you joined stays at the top even after a refresh, and even when a
            // filter would otherwise hide it — the cached room is used when it is not in the list.
            var mine = rooms.FirstOrDefault(r => r.Lobby.ToString(CultureInfo.InvariantCulture) == _selfLobby) ?? _selfRoom;
            if (mine != null)
            {
                rooms.Remove(mine);
                rooms.Insert(0, mine);
            }
            foreach (var room in _rooms) room.IsMine = room.Lobby == mine?.Lobby;
            _list.ItemsSource = rooms;
            _empty.Text = Words.Get(_lang, rooms.Count == 0 && _showHint ? "start_hint" : "empty");
            _empty.Visibility = rooms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SyncJoin(null);
            SetFoundText(rooms.Count);
        }

        /// <summary>V28: the room you are already in has no Join button — it says so instead.</summary>
        void SyncJoin(Room room)
        {
            var mine = room != null && room.Lobby.ToString(CultureInfo.InvariantCulture) == _selfLobby;
            _join.Content = Words.Get(_lang, mine ? "you_here" : "join");
            _join.IsEnabled = room != null && !mine;
            _join.ToolTip = Words.Get(_lang, mine ? "state_joined" : "hint_wait");
        }

        /// <summary>V26: the number beside the table is the number of rows on screen, not the
        /// number Steam returned before the local filters ran.</summary>
        void SetFoundText(int count)
        {
            var text = _scannedPlanets == 0
                ? Words.F(_lang, "found_world", count)
                : Words.F(_lang, "found", count, _scannedPlanets);
            SetStatus(_note == null ? text : text + " · " + _note);
        }

        /// <summary>Stop both timers when the window closes or the body is rebuilt.</summary>
        internal void Stop()
        {
            _progressTimer.Stop();
            _ageTimer.Stop();
        }

        void SetStatus(string text)
        {
            _progressTimer.Stop();
            _progressTrack.Visibility = Visibility.Collapsed;
            _progressDetail.Visibility = Visibility.Collapsed;
            _status.Text = text;
            try { _foot?.Invoke(text, "Muted"); } catch { }   // the manager's own status bar
        }

        /// <summary>A determinate slice, or a moving one when the stage has no real counter. The
        /// three star columns keep the bar correct at any window width.</summary>
        void Bar(double percent)
        {
            if (percent >= 0)
            {
                _progressBefore.Width = new GridLength(0, GridUnitType.Star);
                _progressBar.Width = new GridLength(Math.Max(1, percent), GridUnitType.Star);
                _progressAfter.Width = new GridLength(Math.Max(0, 100 - Math.Max(1, percent)), GridUnitType.Star);
                _progressFill.Opacity = 1;
            }
            else
            {
                _progressBefore.Width = new GridLength(_progressPhase, GridUnitType.Star);
                _progressBar.Width = new GridLength(30, GridUnitType.Star);
                _progressAfter.Width = new GridLength(70 - _progressPhase, GridUnitType.Star);
                _progressFill.Opacity = 0.75;
            }
        }

        /// <summary>One line of stage, one line of what is actually happening. Only the planet
        /// stage has a real counter; every other stage uses a moving bar rather than inventing a
        /// percentage the scan cannot know.</summary>
        void SetProgress(string stage, int done, int total, List<int> planets, string lang)
        {
            _progressTrack.Visibility = Visibility.Visible;
            _progressDetail.Visibility = Visibility.Visible;
            var percent = -1.0;
            string detail;
            switch (stage)
            {
                case "steam":
                    _status.Text = Words.Get(lang, "stage_steam");
                    detail = Words.Get(lang, "detail_steam");
                    break;
                case "scan":
                    _status.Text = Words.F(lang, "stage_scan", done, total);
                    detail = planets != null && done > 0 && done <= planets.Count && planets[done - 1] > 0
                        ? Words.F(lang, "detail_scan", Tables.Planet(planets[done - 1], lang))
                        : Words.Get(lang, "detail_scan_more");
                    if (total > 0) percent = 100.0 * done / total;
                    break;
                case "rooms":
                    _status.Text = Words.Get(lang, "stage_rooms");
                    detail = Words.Get(lang, "detail_rooms");
                    break;
                case "pings":
                    _status.Text = Words.Get(lang, "stage_pings");
                    detail = Words.Get(lang, "detail_pings");
                    break;
                case "platforms":
                    _status.Text = Words.Get(lang, "stage_platforms");
                    detail = Words.Get(lang, "detail_platforms");
                    break;
                default:
                    _status.Text = Words.Get(lang, "stage_planets");
                    detail = Words.Get(lang, "detail_planets");
                    break;
            }
            _progressDetail.Text = detail;
            if (percent >= 0) { _progressTimer.Stop(); Bar(percent); }
            else { Bar(-1); _progressTimer.Start(); }
            try { _foot?.Invoke(_status.Text, "Muted"); } catch { }
        }

        Filters Read()
        {
            var filters = new Filters
            {
                Difficulty = _difficultyValue,
                Difficulties = _difficulties.ToList(),
                MapTypes = _mapSel.ToList(),
                Variants = _varSel.ToList(),
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

        static bool SteamRunning()
        {
            try { return Process.GetProcessesByName("steam").Length > 0; }
            catch { return false; }
        }

        internal void Refresh()
        {
            if (_busy) return;
            var lang = _lang;
            if (!SteamRunning())
            {
                _showHint = false;
                SetStatus(Words.Get(lang, "steam_not_running"));
                _empty.Text = Words.Get(lang, "steam_not_running");
                _empty.Visibility = Visibility.Visible;
                return;
            }
            _busy = true;
            _refresh.IsEnabled = false;
            SetProgress("planets", 0, 0, null, lang);
            _showHint = false;
            _empty.Visibility = Visibility.Collapsed;
            var filters = Read();
            var manual = _planets.Count > 0;
            var fronts = filters.Fronts.ToList();
            // V28: with several difficulties ticked, Live only needs to know "a filter is on"
            // to pick the deeper set of planets; the exact values are applied to the rows here.
            var difficulty = _difficulties.Count > 0 ? 1 : _difficultyValue;
            var variants = _varSel.ToList();
            var dispatcher = _shell.Host != null ? _shell.Host.Dispatcher : Dispatcher.CurrentDispatcher;
            Task.Run(() =>
            {
                string note = null;
                if (!manual)
                {
                    // the game's planet table is read here, once per five minutes at most, and
                    // never in the helper: a scan then asks the API for nothing at all
                    var targets = Live.Pick(fronts, difficulty, _home, lang, out note);
                    filters.Planets.AddRange(targets);
                }
                // A variant lives on planets, not in lobby data. Include those planets even when
                // they are not among the busiest ones picked above, or the filter finds nothing.
                filters.Planets.AddRange(War.PlanetsFor(War.Effects(_home), variants));
                var planets = filters.Planets.Distinct().Take(20).ToList();
                dispatcher.BeginInvoke((Action)(() => SetProgress("steam", 0, 0, planets, lang)));
                var result = Helper.Scan(_home, filters, lang,
                    stage => dispatcher.BeginInvoke((Action)(() => SetProgress(stage, 0, 0, planets, lang))),
                    (done, total) => dispatcher.BeginInvoke((Action)(() => SetProgress("scan", done, total, planets, lang))));
                return Tuple.Create(result, note);
            }).ContinueWith(task => dispatcher.BeginInvoke((Action)(() => Finish(task, lang))));
        }

        void Finish(Task<Tuple<ScanResult, string>> task, string lang)
        {
            _busy = false;
            _refresh.IsEnabled = true;
            if (task.IsFaulted)
            {
                SetStatus(task.Exception != null ? task.Exception.GetBaseException().Message : "");
                return;
            }
            var result = task.Result.Item1;
            var note = task.Result.Item2;
            if (result.Error != null)
            {
                SetStatus(note == null ? result.Error : result.Error + " · " + note);
                _rooms = new List<Room>();
                _list.ItemsSource = null;
                _empty.Text = result.Error;
                _empty.Visibility = Visibility.Visible;
                return;
            }
            _rooms = result.Rooms;
            _self = result.Self;
            _steamPings = result.SteamPings;
            _scannedPlanets = result.Planets;
            _note = note;
            // V26/V27: map types come from the mission hash; variants are planet-level war
            // effects. Both are computed here, in the manager, so the helper's Steam session
            // stays short and the HTTP stays out of it (Live.cs's design constraint).
            _effects = War.Effects(_home);
            var effects = _effects;
            foreach (var room in _rooms)
            {
                if (room.MapTypes.Count == 0) room.MapTypes = Tables.MapTypes(room.MissionHash);
                if (room.Variants.Count == 0 && effects.TryGetValue(room.Planet, out var variants))
                    room.Variants = new List<string>(variants);
                room.Dress(lang);
            }
            // V28: an empty reading is not a reason to forget the room we are in.
            if (!string.IsNullOrEmpty(result.SelfLobby))
            {
                _selfLobby = result.SelfLobby;
                var mine = _rooms.FirstOrDefault(r => r.Lobby.ToString(CultureInfo.InvariantCulture) == _selfLobby);
                if (mine != null) _selfRoom = mine;
            }
            _missions.RemoveWhere(hash => _rooms.All(r => (r.MissionHash ?? "") != hash));
            UpdateFilterLabels();
            ShowRows();
            MeasurePings(lang);
        }

        /// <summary>The ping column: each room gets an estimate from the host coordinates the
        /// lobby publishes, anchored on Steam's measured latency to that region's relay. Rooms
        /// without coordinates keep the measured regional value; regions Steam missed are probed
        /// by ICMP. The rows are sorted again after.</summary>
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
            var dispatcher = _shell.Host != null ? _shell.Host.Dispatcher : Dispatcher.CurrentDispatcher;
            Task.Run(() => Tuple.Create(Ping.Regions(regions, _steamPings), Live.Where(_home)))
                .ContinueWith(task => dispatcher.BeginInvoke((Action)(() =>
            {
                if (round != _pingRound || task.IsFaulted) return;
                var byRegion = task.Result.Item1;
                var here = task.Result.Item2;
                foreach (var room in _rooms)
                {
                    int? value;
                    var estimate = Ping.Estimate(room, _steamPings, here);
                    room.PingEstimated = estimate.HasValue;
                    room.Ping = estimate ?? (room.Region != null && byRegion.TryGetValue(room.Region.ToUpperInvariant(), out value)
                        ? value : (int?)null);
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

        /// <summary>Join: one steam://joinlobby, which Steam itself turns into a launch when the
        /// game is closed — the client starts it with "+connect_lobby &lt;lobby&gt;" and the game
        /// joins as it boots, the same mechanism its own launch options use (a lobby that does
        /// not exist leaves the game at its menu, which is how players skip the intro).
        ///
        /// Starting the game first, as 1.3.0 did, makes Steam believe a game is already running:
        /// it then hands the lobby to the running-game callback, in a process that has not started
        /// its Steamworks yet, and the request is dropped. Valve's own note on the invitation flow
        /// says it plainly — closed game: "+connect_lobby" on the command line; running game:
        /// GameLobbyJoinRequested_t.</summary>
        void Join()
        {
            var room = _list.SelectedItem as Room;
            if (room == null)
            {
                SetStatus(Words.Get(_lang, "no_selection"));
                return;
            }
            var url = JoinLink.Url(room.Lobby, room.HostId, _self);
            var where = room.PlanetText + " · " + room.DifficultyText;
            try
            {
                var running = GameRunning();
                SetStatus(Words.F(_lang, running ? "join_sent" : "join_launching", where));
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                if (!running) WatchLaunch();
            }
            catch (Exception e)
            {
                SetStatus(e.Message);
            }
        }

        /// <summary>Steam was asked to start the game and enter the lobby: say so if it never
        /// appears (not installed, Steam offline, the client refused), rather than leaving the
        /// sentence hanging.</summary>
        async void WatchLaunch()
        {
            var until = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < until)
            {
                await Task.Delay(1000);
                if (GameRunning()) return;
            }
            SetStatus(Words.Get(_lang, "launch_timeout"));
        }
    }
}
