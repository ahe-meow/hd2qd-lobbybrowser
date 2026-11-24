using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Hd2Qd.LobbyBrowswer
{
    /// <summary>The plugin's window: the filters above, the lobbies in a grid, the state of the
    /// scan below. A scan runs off the UI thread; the grid is filled when it returns.</summary>
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
        readonly string _lang;
        readonly Action<string, string> _foot;
        readonly DataGrid _grid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, IsReadOnly = true };
        readonly TextBlock _status = new TextBlock { Margin = new Thickness(0, 6, 0, 0) };
        readonly ComboBox _difficulty = new ComboBox { MinWidth = 130, Margin = new Thickness(0, 0, 12, 0) };
        readonly List<CheckBox> _fronts = new List<CheckBox>();
        readonly CheckBox _publicOnly;
        readonly CheckBox _hideNoSession;
        readonly CheckBox _hideFull;
        readonly CheckBox _sosOnly;
        readonly Button _refresh;
        readonly Button _join;
        bool _busy;

        internal Window Host;

        internal LobbyPage(string lang, Dictionary<string, object> host)
        {
            _lang = lang;
            _foot = host != null && host.TryGetValue("foot", out var foot) ? foot as Action<string, string> : null;
            var ink = FindBrush("Ink") ?? SystemColors.WindowTextBrush;
            var muted = FindBrush("Muted") ?? SystemColors.GrayTextBrush;
            var ground = FindBrush("Ground900");

            _refresh = new Button { Content = Words.Get(lang, "refresh"), Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 12, 0) };
            _refresh.Click += (s, e) => Refresh();
            _join = new Button { Content = Words.Get(lang, "join"), Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(12, 0, 0, 0) };
            _join.Click += (s, e) => Join();

            _difficulty.Items.Add(new ComboBoxItem { Content = Words.Get(lang, "diff_any"), Tag = 0 });
            for (var i = 1; i <= 10; i++)
                _difficulty.Items.Add(new ComboBoxItem { Content = Words.F(lang, "diff_n", i), Tag = i });
            _difficulty.SelectedIndex = 0;

            foreach (var front in new[] { "Terminids", "Automatons", "Illuminate" })
            {
                var box = new CheckBox
                {
                    Content = Tables.FrontName(front, lang), Tag = front, IsChecked = true,
                    Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
                };
                _fronts.Add(box);
            }
            _publicOnly = Box(lang, "public_only", true);
            _hideNoSession = Box(lang, "hide_nosession", true);
            _hideFull = Box(lang, "hide_full", false);
            _sosOnly = Box(lang, "sos_only", false);

            var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            bar.Children.Add(_refresh);
            bar.Children.Add(_difficulty);
            foreach (var box in _fronts) bar.Children.Add(box);
            bar.Children.Add(_publicOnly);
            bar.Children.Add(_hideNoSession);
            bar.Children.Add(_hideFull);
            bar.Children.Add(_sosOnly);
            bar.Children.Add(_join);

            _grid.AutoGeneratingColumn += (s, e) => e.Cancel = true;
            _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
            _grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
            _grid.SelectionMode = DataGridSelectionMode.Single;
            _grid.MouseDoubleClick += (s, e) => Join();
            _grid.Columns.Add(Text("col_planet", "PlanetText", 160));
            _grid.Columns.Add(Text("col_front", "FrontText", 120));
            _grid.Columns.Add(Text("col_diff", "DifficultyText", 90));
            _grid.Columns.Add(Text("col_members", "MembersText", 60));
            _grid.Columns.Add(Text("col_mission", "MissionText", 200));
            _grid.Columns.Add(Text("col_region", "RegionText", 90));
            _grid.Columns.Add(Text("col_sos", "SosText", 50));
            _grid.Columns.Add(Text("col_host", "HostText", 140));
            _grid.Columns.Add(Text("col_age", "AgeText", 90));
            _grid.Foreground = ink;
            _grid.Background = Brushes.Transparent;
            _grid.RowBackground = Brushes.Transparent;
            _grid.BorderThickness = new Thickness(0);

            var hint = new TextBlock
            {
                Text = Words.Get(lang, "hint_wait"), Foreground = muted,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
            };
            _status.Foreground = ink;
            _status.Margin = new Thickness(0, 6, 0, 10);
            _status.TextWrapping = TextWrapping.Wrap;
            var head = new StackPanel();
            head.Children.Add(bar);
            head.Children.Add(hint);
            head.Children.Add(_status);

            var root = new Grid { Margin = new Thickness(16) };
            if (ground != null) root.Background = ground;
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(head, 0);
            Grid.SetRow(_grid, 1);
            root.Children.Add(head);
            root.Children.Add(_grid);
            Root = root;
            SetStatus(Words.Get(lang, "ready"));
        }

        internal FrameworkElement Root { get; }

        CheckBox Box(string lang, string key, bool on) => new CheckBox
        {
            Content = Words.Get(lang, key), IsChecked = on,
            Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
        };

        DataGridTextColumn Text(string header, string path, double width) => new DataGridTextColumn
        {
            Header = Words.Get(_lang, header),
            Binding = new Binding(path),
            Width = width,
            IsReadOnly = true,
        };

        static Brush FindBrush(string key)
        {
            var app = Application.Current;
            return app != null ? app.TryFindResource(key) as Brush : null;
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
                Difficulty = _difficulty.SelectedItem is ComboBoxItem item && item.Tag is int d ? d : 0,
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
            var dispatcher = Host != null ? Host.Dispatcher : System.Windows.Threading.Dispatcher.CurrentDispatcher;
            Task.Run(() => Scanner.Scan(filters, lang, (done, total) =>
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
                _grid.ItemsSource = null;
                return;
            }
            _grid.ItemsSource = result.Rooms;
            SetStatus(result.Rooms.Count == 0
                ? Words.Get(lang, "empty")
                : result.Planets == 0
                    ? Words.F(lang, "found_world", result.Rooms.Count)
                    : Words.F(lang, "found", result.Rooms.Count, result.Planets));
        }

        void Join()
        {
            var room = _grid.SelectedItem as Room;
            if (room == null)
            {
                SetStatus(Words.Get(_lang, "no_selection"));
                return;
            }
            try
            {
                SetStatus(Words.F(_lang, "joining", room.PlanetText + " · " + room.DifficultyText));
                Process.Start(new ProcessStartInfo { FileName = Steam.JoinUrl(room.Lobby, room.HostId), UseShellExecute = true });
            }
            catch (Exception e)
            {
                SetStatus(e.Message);
            }
        }
    }
}
