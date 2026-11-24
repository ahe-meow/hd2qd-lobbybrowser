using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Hd2Qd.LobbyBrowser
{
    /// <summary>A list to pick several things from, in the manager's own parts: a card of
    /// Ground700 with a grid line, an Input for the search, Chips for the fronts, Tick rows —
    /// the shape its own dialogs take, not a Windows list box.</summary>
    internal static class Picker
    {
        internal sealed class Item
        {
            internal string Key;
            internal string Text;
            internal string Note;
            internal string Group;      // the front's name: the chips over the list
            internal bool On;
        }

        internal static void Show(FrameworkElement anchor, string title, string hint,
            string clear, string empty, List<Item> items, Action changed)
        {
            var popup = new Popup
            {
                PlacementTarget = anchor,
                Placement = PlacementMode.Bottom,
                VerticalOffset = 6,
                StaysOpen = false,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.None,
            };
            var list = new StackPanel();
            var search = new TextBox { Style = Theme.S("Input"), Tag = hint, Margin = new Thickness(0, 10, 0, 8) };
            var chips = new WrapPanel();
            var groups = items.Select(i => i.Group).Where(g => !string.IsNullOrEmpty(g)).Distinct().ToList();

            void Paint()
            {
                list.Children.Clear();
                var needle = (search.Text ?? "").Trim();
                var shown = 0;
                foreach (var item in items)
                {
                    if (needle.Length > 0 &&
                        item.Text.IndexOf(needle, StringComparison.CurrentCultureIgnoreCase) < 0 &&
                        (item.Note ?? "").IndexOf(needle, StringComparison.CurrentCultureIgnoreCase) < 0) continue;
                    shown++;
                    var row = new Grid();
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var box = new CheckBox
                    {
                        Style = Theme.S("Tick"),
                        IsChecked = item.On,
                        Content = new TextBlock
                        {
                            Text = item.Text, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                        Margin = new Thickness(0, 0, 8, 0),
                    };
                    box.Click += (s, e) => { item.On = box.IsChecked == true; changed(); };
                    row.Children.Add(box);
                    if (!string.IsNullOrEmpty(item.Note))
                    {
                        var note = new TextBlock
                        {
                            Text = item.Note, FontSize = 11, Foreground = Theme.B("Muted"),
                            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0),
                        };
                        Grid.SetColumn(note, 1);
                        row.Children.Add(note);
                    }
                    list.Children.Add(new Border { Padding = new Thickness(2, 3, 2, 3), Child = row });
                }
                if (shown == 0)
                    list.Children.Add(new TextBlock
                    {
                        Text = empty, FontSize = 12, Foreground = Theme.B("Muted"), Margin = new Thickness(2, 8, 2, 8),
                        TextWrapping = TextWrapping.Wrap,
                    });
            }

            foreach (var group in groups)
            {
                var chip = new CheckBox
                {
                    Content = group, Style = Theme.S("Chip"), Margin = new Thickness(0, 0, 6, 0),
                    IsChecked = items.Where(i => i.Group == group).All(i => i.On),
                };
                chip.Click += (s, e) =>
                {
                    var on = chip.IsChecked == true;
                    foreach (var item in items.Where(i => i.Group == group)) item.On = on;
                    changed();
                    Paint();
                };
                chips.Children.Add(chip);
            }

            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 0) };
            var clearButton = new Button
            {
                Content = clear, Style = Theme.S("Ghost"), MinHeight = 28, Padding = new Thickness(12, 0, 12, 0),
                FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            };
            clearButton.Click += (s, e) =>
            {
                foreach (var item in items) item.On = false;
                foreach (var chip in chips.Children.OfType<CheckBox>()) chip.IsChecked = false;
                changed();
                Paint();
            };
            DockPanel.SetDock(clearButton, Dock.Right);
            head.Children.Add(clearButton);
            head.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });

            var body = new StackPanel { Width = 330 };
            body.Children.Add(head);
            body.Children.Add(search);
            if (groups.Count > 0) body.Children.Add(new WrapPanel { Margin = new Thickness(0, 0, 0, 8), Children = { chips } });
            body.Children.Add(new ScrollViewer
            {
                MaxHeight = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = list,
            });

            popup.Child = new Border
            {
                Background = Theme.B("Ground700"), BorderBrush = Theme.B("Grid"), BorderThickness = new Thickness(1),
                Padding = new Thickness(12), Child = body,
            };
            search.TextChanged += (s, e) => Paint();
            popup.Opened += (s, e) => { search.Focus(); Paint(); };
            Paint();
            popup.IsOpen = true;
        }
    }
}
