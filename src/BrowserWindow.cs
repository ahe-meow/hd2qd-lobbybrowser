using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// of the manager's own parts — ghost buttons, chips, ticks, a dark card, banded rows — so a
    /// player cannot tell it from the manager's pages. The filters and Join above, the count
    /// below, the lobbies between them.</summary>
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
        // the table's columns, in the same stars in the header and in every row
        static readonly double[] Columns = { 1.8, 1.2, 0.95, 0.8, 2.05, 0.85, 0.55, 1.4, 0.75 };
        static readonly string[] Heads = { "col_planet", "col_front", "col_diff", "col_members", "col_mission", "col_region", "col_sos", "col_host", "col_age" };
        static readonly bool[] AlignRight = { false, false, false, true, false, false, false, false, true };
        static readonly bool[] Numeric = { false, false, false, true, false, false, false, false, true };

        readonly string _lang;
        readonly string _home;
        readonly Action<string, string> _foot;
        readonly ListBox _list = new ListBox();
        readonly TextBlock _status = new TextBlock { FontSize = 12, Foreground = Theme.B("Muted"), VerticalAlignment = VerticalAlignment.Center };
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
        readonly Button _join;
        readonly Button _difficulty;
        int _difficultyValue;
        string _self = "0";
        bool _busy;

        internal Window Host;

        internal LobbyPage(string lang, Dictionary<string, object> host)
        {
            _lang = lang;
            _home = host != null && host.TryGetValue("home", out var home) ? home as string : null;
            _foot = host != null && host.TryGetValue("foot", out var foot) ? foot as Action<string, string> : null;

            _refresh = new Button { Content = Words.Get(lang, "refresh"), Style = Theme.S("Ghost"), MinHeight = 36, Padding = new Thickness(16, 0, 16, 0), Margin = new Thickness(0, 0, 8, 0), ToolTip = Words.Get(lang, "refresh_tip") };
            _refresh.Click += (s, e) => Refresh();

            _difficulty = new Button { Content = Words.Get(lang, "diff_any"), Style = Theme.S("Ghost"), MinHeight = 36, Padding = new Thickness(16, 0, 16, 0), Margin = new Thickness(0, 0, 12, 0) };
            _difficulty.Click += (s, e) => PickDifficulty();

            _join = new Button { Content = Words.Get(lang, "join"), Style = Theme.S("Primary"), MinHeight = 36, MinWidth = 96, Padding = new Thickness(20, 0, 20, 0), IsEnabled = false, VerticalAlignment = VerticalAlignment.Center };
            _join.Click += (s, e) => Join();

            var bar = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            bar.Children.Add(_refresh);
            bar.Children.Add(_difficulty);
            foreach (var front in new[] { "Terminids", "Automatons", "Illuminate" })
            {
                var chip = new CheckBox
                {
                    Content = Tables.FrontName(front, lang), Tag = front,
                    IsChecked = true, Style = Theme.S("Chip"),
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

            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
            DockPanel.SetDock(_join, Dock.Right);
            top.Children.Add(_join);
            top.Children.Add(bar);

            // the table: a card of the manager's, its words over the columns, a grid line, the rows
            var table = new Grid();
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var head = Header();
            Grid.SetRow(head, 0);
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
            table.Children.Add(head);
            table.Children.Add(rule);
            table.Children.Add(body);
            var card = Theme.Card(table, 0);
            card.Margin = new Thickness(0, 0, 0, 12);

            var bottom = new DockPanel();
            var hint = new TextBlock
            {
                Text = Words.Get(lang, "hint_wait"), FontSize = 11, Foreground = Theme.B("Muted"),
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 620,
            };
            DockPanel.SetDock(hint, Dock.Right);
            bottom.Children.Add(hint);
            bottom.Children.Add(_status);

            var root = new Grid { Margin = new Thickness(20, 18, 20, 16), MinWidth = 1040, MinHeight = 520, Background = Theme.B("Ground900") };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(top, 0);
            Grid.SetRow(card, 1);
            Grid.SetRow(bottom, 2);
            root.Children.Add(top);
            root.Children.Add(card);
            root.Children.Add(bottom);
            Root = root;

            _empty.Text = Words.Get(lang, "empty");
            _empty.Visibility = Visibility.Collapsed;
            SetStatus(Words.Get(lang, "ready"));
        }

        internal FrameworkElement Root { get; }

        CheckBox Tick(string lang, string key, bool on) => new CheckBox
        {
            Content = Words.Get(lang, key), IsChecked = on, Style = Theme.S("Tick"),
            Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center,
        };

        /// <summary>The table's words, over the same columns as the rows, in the manager's label
        /// style: 12, semi-bold, muted.</summary>
        Grid Header()
        {
            // 26 on the right: 16 of the card's padding and the 10 the list keeps for its
            // scrollbar, so a column's words stand over that column's values
            var grid = new Grid { Margin = new Thickness(16, 0, 26, 0), Height = 34 };
            AddColumns(grid);
            for (var i = 0; i < Heads.Length; i++)
            {
                var text = new TextBlock
                {
                    Text = Words.Get(_lang, Heads[i]), FontSize = 12, FontWeight = FontWeights.SemiBold,
                    Foreground = Theme.B("Muted"), VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = AlignRight[i] ? TextAlignment.Right : TextAlignment.Left,
                    HorizontalAlignment = HorizontalAlignment.Stretch, TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 0, AlignRight[i] ? 16 : 10, 0),
                };
                if (Numeric[i]) text.FontFamily = Theme.Font("Mono");
                Grid.SetColumn(text, i);
                grid.Children.Add(text);
            }
            return grid;
        }

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
                    xaml += "<ColumnDefinition Width='" + star.ToString(System.Globalization.CultureInfo.InvariantCulture) + "*'/>";
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
                _list.ItemsSource = null;
                _empty.Text = result.Error;
                _empty.Visibility = Visibility.Visible;
                return;
            }
            _list.ItemsSource = result.Rooms;
            _self = result.Self;
            _empty.Text = Words.Get(lang, "empty");
            _empty.Visibility = result.Rooms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _join.IsEnabled = false;
            SetStatus(result.Rooms.Count == 0
                ? Words.Get(lang, "empty")
                : result.Planets == 0
                    ? Words.F(lang, "found_world", result.Rooms.Count)
                    : Words.F(lang, "found", result.Rooms.Count, result.Planets));
        }

        void Join()
        {
            var room = _list.SelectedItem as Room;
            if (room == null)
            {
                SetStatus(Words.Get(_lang, "no_selection"));
                return;
            }
            try
            {
                SetStatus(Words.F(_lang, "joining", room.PlanetText + " · " + room.DifficultyText));
                Process.Start(new ProcessStartInfo { FileName = JoinLink.Url(room.Lobby, room.HostId, _self), UseShellExecute = true });
            }
            catch (Exception e)
            {
                SetStatus(e.Message);
            }
        }
    }
}
