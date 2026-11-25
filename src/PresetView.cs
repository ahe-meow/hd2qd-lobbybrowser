using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace Hd2Qd.LobbyBrowser
{
    /// <summary>The presets, as a card: a save line over a list of names, and on each name's own
    /// line the two things done to it — load and delete. HD2LobbyBrowser kept a single set; this
    /// keeps several, and a list that grows must not make the menu twice as tall (the user,
    /// 2026-11-25), so the actions sit beside the name rather than under it.</summary>
    internal static class PresetView
    {
        /// <summary>The card. load(name), remove(name) and save are the window's; with null they
        /// draw the same card and do nothing (the test window's shot).</summary>
        internal static FrameworkElement Card(IList<Preset> presets, string lang, Action<string> load, Action<string> remove, Action save)
        {
            var body = new StackPanel { Width = 336 };

            var saveButton = new Button
            {
                Content = Words.Get(lang, "preset_save"), Style = Theme.S("Ghost"), MinHeight = 32,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            saveButton.Click += (s, e) => save?.Invoke();
            body.Children.Add(saveButton);
            body.Children.Add(new Border { Height = 1, Background = Theme.B("Grid"), Margin = new Thickness(0, 10, 0, 6) });

            if (presets == null || presets.Count == 0)
            {
                body.Children.Add(new TextBlock
                {
                    Text = Words.Get(lang, "preset_none"), FontSize = 12, Foreground = Theme.B("Muted"),
                    Margin = new Thickness(2, 6, 2, 4),
                });
            }
            else
            {
                var rows = new StackPanel();
                foreach (var preset in presets)
                {
                    var name = preset.Name;
                    var row = new DockPanel { Height = 32, Margin = new Thickness(0, 1, 0, 1) };

                    var delete = Small(lang, "preset_delete", () => remove?.Invoke(name));
                    DockPanel.SetDock(delete, Dock.Right);
                    row.Children.Add(delete);

                    var loadButton = Small(lang, "preset_load", () => load?.Invoke(name));
                    DockPanel.SetDock(loadButton, Dock.Right);
                    row.Children.Add(loadButton);

                    row.Children.Add(new TextBlock
                    {
                        Text = name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 0, 8, 0),
                        ToolTip = name,
                    });
                    rows.Children.Add(row);
                }
                body.Children.Add(new ScrollViewer
                {
                    MaxHeight = 268, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = rows,
                });
            }

            return new Border
            {
                Background = Theme.B("Ground700"), BorderBrush = Theme.B("Grid"), BorderThickness = new Thickness(1),
                Padding = new Thickness(10), Child = body,
            };
        }

        static Button Small(string lang, string key, Action click)
        {
            var button = new Button
            {
                Content = Words.Get(lang, key), Style = Theme.S("Ghost"), MinHeight = 26, MinWidth = 50,
                Padding = new Thickness(8, 0, 8, 0), FontSize = 11, Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            button.Click += (s, e) => click();
            return button;
        }
    }
}
