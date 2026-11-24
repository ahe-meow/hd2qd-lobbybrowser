using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace Hd2Qd.LobbyBrowser
{
    /// <summary>The manager's colours, fonts and control styles: its own resources when the
    /// manager is drawing the plugin, and the same values copied in Ui.xaml when it is not
    /// (a test window, an older manager). Nothing here is a Windows default: a plugin's window
    /// must look like the manager's, not like a stock WPF window dropped on top of it.</summary>
    static class Theme
    {
        static ResourceDictionary _fallback;

        static ResourceDictionary Fallback
        {
            get
            {
                if (_fallback != null) return _fallback;
                var asm = typeof(Theme).Assembly;
                using (var r = new StreamReader(asm.GetManifestResourceStream("Ui.xaml")))
                    _fallback = (ResourceDictionary)XamlReader.Parse(r.ReadToEnd());
                return _fallback;
            }
        }

        static object Get(string key) =>
            (Application.Current != null ? Application.Current.TryFindResource(key) : null) ?? Fallback[key];

        /// <summary>When the drawing application is not the manager (a test window, an older
        /// manager), the copied dictionary goes into its resources too: the implicit styles of
        /// a scrollbar and a menu are found there and nowhere else. With the manager's resources
        /// present nothing is added, so its theme is never overridden.</summary>
        internal static void Adopt()
        {
            var app = Application.Current;
            if (app == null) return;
            if (app.TryFindResource("Ink") != null) return;
            foreach (var dictionary in app.Resources.MergedDictionaries)
                if (ReferenceEquals(dictionary, Fallback)) return;
            app.Resources.MergedDictionaries.Add(Fallback);
        }

        internal static Brush B(string key)
        {
            Adopt();
            return Get(key) as Brush ?? Brushes.Transparent;
        }

        internal static Style S(string key) => Get(key) as Style;

        internal static FontFamily Font(string key) => Get(key) as FontFamily ?? new FontFamily("Microsoft YaHei UI");

        /// <summary>A colour of the theme as #AARRGGBB, for the templates a window builds from
        /// text: those cannot reach the manager's dictionary, so the values go in.</summary>
        internal static string Hex(string key) =>
            (B(key) as SolidColorBrush)?.Color.ToString() ?? "#FF000000";

        /// <summary>A template of this window, its $Key$ placeholders being the theme's colours
        /// and fonts, so a copied template cannot drift from the manager's theme.</summary>
        internal static T Parse<T>(string xaml) where T : class
        {
            var text = xaml
                .Replace("$Ground900$", Hex("Ground900"))
                .Replace("$Ground700$", Hex("Ground700"))
                .Replace("$Grid$", Hex("Grid"))
                .Replace("$Ink$", Hex("Ink"))
                .Replace("$Muted$", Hex("Muted"))
                .Replace("$Yellow$", Hex("Yellow"))
                .Replace("$Blue$", Hex("Blue"))
                .Replace("$TapeInk$", Hex("TapeInk"))
                .Replace("$Band$", Hex("Band"))
                .Replace("$Ui$", Font("Ui").Source)
                .Replace("$Mono$", Font("Mono").Source);
            return (T)XamlReader.Parse(text);
        }

        /// <summary>A panel of the manager: the ground one step up, a grid line round it.</summary>
        internal static Border Card(UIElement child, double padding) => new Border
        {
            Background = B("Ground700"),
            BorderBrush = B("Grid"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(padding),
            Child = child,
        };

        internal static TextBlock Words(string text, double size, Brush colour, bool bold = false) => new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = colour,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }
}
