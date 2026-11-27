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
    /// Ground700 with a grid line, an Input for the search and Tick rows. With grouped=true the
    /// groups become expandable rows with their own tick box, so a planet picker reads
    /// sector → planet instead of filling the popup with sector chips.</summary>
    internal static class Picker
    {
        internal sealed class Item
        {
            internal string Key;
            internal string Text;
            internal string Note;
            internal long Count;        // live players, as upstream's planet menu shows
            internal bool Hot;          // count > 0: the note is painted yellow, as upstream's .warm
            internal string Group;      // sector for planets; a group header/chip when present
            internal bool On;
        }

        /// <summary>The search narrows what the bulk actions touch: a row hidden by the search
        /// keeps its state, so "invert" after searching one front does not flip every planet.</summary>
        internal static bool Matches(Item item, string needle) =>
            string.IsNullOrEmpty(needle) ||
            (item.Text ?? "").IndexOf(needle, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
            (item.Note ?? "").IndexOf(needle, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
            (item.Group ?? "").IndexOf(needle, StringComparison.CurrentCultureIgnoreCase) >= 0;

        /// <summary>Select all matching rows, or flip them — the two buttons above the list.</summary>
        internal static void Choose(List<Item> items, string needle, bool select)
        {
            foreach (var item in items)
                if (Matches(item, needle)) item.On = select || !item.On;
        }

        internal static Popup Show(FrameworkElement anchor, string title, string hint,
            string clear, string all, string invert, string empty, List<Item> items, Action changed,
            bool grouped = false)
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
            var chips = new StackPanel { Orientation = Orientation.Horizontal };
            var groups = items.Select(i => i.Group).Where(g => !string.IsNullOrEmpty(g)).Distinct().ToList();
            var expanded = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

            Border Row(Item item)
            {
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
                box.Click += (s, e) => { item.On = box.IsChecked == true; changed(); if (grouped) Paint(); };
                row.Children.Add(box);
                if (!string.IsNullOrEmpty(item.Note))
                {
                    var note = new TextBlock
                    {
                        Text = item.Note, FontSize = 11,
                        Foreground = item.Hot ? Theme.B("Yellow") : Theme.B("Muted"),
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0),
                    };
                    Grid.SetColumn(note, 1);
                    row.Children.Add(note);
                }
                return new Border { Padding = new Thickness(2, 3, 2, 3), Child = row };
            }

            void Paint()
            {
                list.Children.Clear();
                var needle = (search.Text ?? "").Trim();
                var shown = 0;
                if (grouped && groups.Count > 0)
                {
                    // groups keep the caller's order (the planet menu sorts sectors by live
                    // players, descending), not an alphabetical one
                    foreach (var group in items.Where(i => Matches(i, needle)).GroupBy(i => i.Group ?? ""))
                    {
                        shown++;
                        var rows = group.ToList();
                        var on = rows.Count(i => i.On);
                        var open = needle != "" || expanded.Contains(group.Key);
                        var header = new Grid();
                        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        var arrow = new Button
                        {
                            Content = open ? "▾" : "▸", Style = Theme.S("Ghost"), Width = 22, MinHeight = 22,
                            Padding = new Thickness(0), Margin = new Thickness(0), FontSize = 12,
                            VerticalAlignment = VerticalAlignment.Center,
                        };
                        arrow.Click += (s, e) =>
                        {
                            if (!expanded.Add(group.Key)) expanded.Remove(group.Key);
                            Paint();
                        };
                        header.Children.Add(arrow);
                        var headBox = new CheckBox
                        {
                            Style = Theme.S("Tick"), IsThreeState = true,
                            IsChecked = on == rows.Count ? true : on > 0 ? (bool?)null : false,
                            Content = new TextBlock
                            {
                                Text = group.Key + "  " + on + "/" + rows.Count, FontSize = 13,
                                FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
                            },
                        };
                        headBox.Click += (s, e) =>
                        {
                            var set = headBox.IsChecked == true;
                            foreach (var item in rows) item.On = set;
                            changed();
                            Paint();
                        };
                        Grid.SetColumn(headBox, 1);
                        header.Children.Add(headBox);
                        // upstream's sector rows carry the sector's live-player total, warm yellow
                        // when anyone is there
                        var total = rows.Sum(i => i.Count);
                        var headCount = new TextBlock
                        {
                            Text = total.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
                            FontSize = 12,
                            Foreground = total > 0 ? Theme.B("Yellow") : Theme.B("Muted"),
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(6, 0, 2, 0),
                        };
                        Grid.SetColumn(headCount, 2);
                        header.Children.Add(headCount);
                        list.Children.Add(new Border { Padding = new Thickness(0, 2, 0, 2), Child = header });
                        if (!open) continue;
                        foreach (var item in rows)
                        {
                            var child = Row(item);
                            child.Margin = new Thickness(18, 0, 0, 0);
                            list.Children.Add(child);
                        }
                    }
                }
                else
                    foreach (var item in items)
                    {
                        if (!Matches(item, needle)) continue;
                        shown++;
                        list.Children.Add(Row(item));
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
                    Content = group, Tag = group, Style = Theme.S("Chip"), Margin = new Thickness(0, 0, 6, 0),
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

            void SyncChips()
            {
                foreach (var chip in chips.Children.OfType<CheckBox>())
                    chip.IsChecked = items.Where(i => i.Group == (string)chip.Tag).All(i => i.On);
            }

            var head = new DockPanel();
            void AddAction(string label, Action act)
            {
                var button = new Button
                {
                    Content = label, Style = Theme.S("Ghost"), MinHeight = 26,
                    Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(4, 0, 0, 0),
                    FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                };
                button.Click += (s, e) => { act(); SyncChips(); changed(); Paint(); };
                DockPanel.SetDock(button, Dock.Right);
                head.Children.Add(button);
            }
            // docked right, so added right to left: 全选 反选 清空 read left to right
            AddAction(clear, () => { foreach (var item in items) item.On = false; });
            AddAction(invert, () => Choose(items, (search.Text ?? "").Trim(), false));
            AddAction(all, () => Choose(items, (search.Text ?? "").Trim(), true));
            head.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });

            var body = new StackPanel { Width = 330 };
            body.Children.Add(head);
            body.Children.Add(search);
            // one scrollable row, not a wrapping block: dozens of sectors used to eat the popup
            // and leave only two planet rows visible
            if (!grouped && groups.Count > 0)
                body.Children.Add(new ScrollViewer
                {
                    Height = 38, Margin = new Thickness(0, 0, 0, 8), Content = chips,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                });
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
            return popup;
        }
    }
}
