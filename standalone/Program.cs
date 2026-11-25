using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Hd2Qd.LobbyBrowser
{
    /// <summary>The lobby browser on its own. It opens the same body the plugin opens, through the
    /// same public Plugin.Open contract, and supplies the one thing HD2QD would: a window. That
    /// window has no Windows title bar, border or status bar — the caption and edge are drawn here
    /// from the manager's own colours — so the program looks like the plugin, not like stock WPF.
    ///
    /// Steam still lives in the embedded helper: this process must not initialise Steamworks for
    /// the game, or Steam would end it along with Helldivers 2.</summary>
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            Theme.Adopt();

            // the same folder the plugin uses when HD2QD passes no home: presets, language choice,
            // planet cache and the released helper all live here, never beside the exe.
            var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HD2QD.LobbyBrowser");
            var host = new Dictionary<string, object>
            {
                ["api"] = 2L,
                ["lang"] = Language.Tag(CultureInfo.CurrentUICulture.Name),
                ["home"] = home,
                ["window"] = new Func<string, FrameworkElement, Window>((title, content) => Frame(title, content)),
            };

            Plugin.Open(null, host);

            var shot = Shot(args);
            if (shot != null)
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(double.Parse(shot, CultureInfo.InvariantCulture)) };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    try { Capture(app.MainWindow, "standalone.png"); } catch { }
                    app.Shutdown();
                };
                timer.Start();
            }
            app.Run();
            return 0;
        }

        /// <summary>The manager's ChromeWindow, rebuilt from the theme: a dark caption and a hairline
        /// edge around the very same content. No native frame, no system buttons.</summary>
        static Window Frame(string title, FrameworkElement content)
        {
            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = true,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Width = 1300, Height = 820, MinWidth = 1120, MinHeight = 560,
                Background = Theme.B("Ground900"), Foreground = Theme.B("Ink"),
                FontFamily = Theme.Font("Ui"), FontSize = 14,
                Icon = Mark(),
            };

            var caption = new DockPanel { Height = 48, Background = Theme.B("Ground700"), LastChildFill = true };

            // buttons docked apart from the drag strip: dragging the caption never eats their clicks
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            buttons.Children.Add(Ghost("−", () => window.WindowState = WindowState.Minimized, "Minimize"));
            buttons.Children.Add(Ghost("×", () => window.Close(), "Close"));
            DockPanel.SetDock(buttons, Dock.Right);
            caption.Children.Add(buttons);

            var drag = new Grid { Background = Brushes.Transparent };
            drag.Children.Add(new TextBlock
            {
                Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold,
                Foreground = Theme.B("Ink"), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 0, 0),
            });
            drag.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed) window.DragMove();
            };
            caption.Children.Add(drag);

            var dock = new DockPanel();
            DockPanel.SetDock(caption, Dock.Top);
            dock.Children.Add(caption);
            dock.Children.Add(content);
            window.Content = new Border
            {
                BorderBrush = Theme.B("Grid"), BorderThickness = new Thickness(1), Child = dock,
            };
            return window;
        }

        /// <summary>A caption button built from the theme, not the Windows one.</summary>
        static Button Ghost(string glyph, Action click, string tip)
        {
            var button = new Button
            {
                Width = 46, Height = 48, Content = glyph, FontSize = 18,
                Foreground = Theme.B("Muted"), Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), Focusable = false, ToolTip = tip,
                Cursor = Cursors.Hand,
            };
            button.Click += (s, e) => click();
            button.MouseEnter += (s, e) => button.Foreground = Theme.B("Ink");
            button.MouseLeave += (s, e) => button.Foreground = Theme.B("Muted");
            return button;
        }

        static ImageSource Mark()
        {
            try
            {
                using (var stream = typeof(Program).Assembly.GetManifestResourceStream("app.ico"))
                    return BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            }
            catch { return null; }
        }

        /// <summary>An opt-in self-check: render the window to a PNG in the current folder. Normal
        /// runs pass no arguments and ignore it.</summary>
        static string Shot(string[] args)
        {
            foreach (var arg in args)
                if (arg.StartsWith("--shot=", StringComparison.OrdinalIgnoreCase)) return arg.Substring(7);
            return null;
        }

        static void Capture(Window window, string file)
        {
            var content = window.Content as FrameworkElement;
            var width = (int)Math.Max(1, content.ActualWidth);
            var height = (int)Math.Max(1, content.ActualHeight);
            content.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(file)) png.Save(stream);
            Console.WriteLine("PASS  standalone window rendered to " + file + " (" + width + "x" + height + ")");
        }
    }
}
