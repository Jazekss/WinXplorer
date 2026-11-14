using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace WinXplorer.Services
{
    /// <summary>
    /// WinXplorer's own vector icons for file types: a small coloured tile with the extension (lists) and a page with a
    /// coloured label (icon view). Used for code files and for any type Windows only shows a blank page for.
    /// Drop &lt;ext&gt;.svg (and optionally &lt;ext&gt;-small.svg) into %APPDATA%\WinXplorer\Icons to use your own drawing instead.
    /// </summary>
    public static class FileIcons
    {
        private static readonly ConcurrentDictionary<string, ImageSource> _small = new(), _large = new();
        public static string CustomDir => Path.Combine(AppState.DataDir, "Icons");

        private static readonly Dictionary<string, string> Colors = new(StringComparer.OrdinalIgnoreCase)
        {
            [".html"] = "#E44D26", [".htm"] = "#E44D26", [".xhtml"] = "#E44D26",
            [".css"] = "#2965F1", [".scss"] = "#CD6799", [".sass"] = "#CD6799", [".less"] = "#1D365D",
            [".js"] = "#F0DB4F", [".mjs"] = "#F0DB4F", [".cjs"] = "#F0DB4F", [".jsx"] = "#61DAFB",
            [".ts"] = "#3178C6", [".tsx"] = "#3178C6", [".vue"] = "#41B883", [".svelte"] = "#FF3E00",
            [".json"] = "#5B5B5B", [".jsonc"] = "#5B5B5B", [".xml"] = "#F16529", [".xaml"] = "#512BD4",
            [".yaml"] = "#CB171E", [".yml"] = "#CB171E", [".toml"] = "#9C4121",
            [".php"] = "#777BB3", [".py"] = "#3776AB", [".pyw"] = "#3776AB", [".rb"] = "#CC342D",
            [".cs"] = "#68217A", [".vb"] = "#00539C", [".fs"] = "#378BBA", [".java"] = "#E76F00", [".kt"] = "#7F52FF",
            [".c"] = "#555555", [".h"] = "#A8B9CC", [".cpp"] = "#00599C", [".hpp"] = "#00599C", [".cc"] = "#00599C",
            [".go"] = "#00ADD8", [".rs"] = "#DEA584", [".swift"] = "#F05138", [".dart"] = "#0175C2", [".lua"] = "#000080",
            [".sql"] = "#E38C00", [".sh"] = "#4EAA25", [".bash"] = "#4EAA25", [".ps1"] = "#2671BE", [".bat"] = "#C1F12E", [".cmd"] = "#C1F12E",
            [".md"] = "#083FA1", [".markdown"] = "#083FA1", [".ini"] = "#7A7A7A", [".cfg"] = "#7A7A7A", [".conf"] = "#7A7A7A", [".env"] = "#ECD53F",
            [".log"] = "#8A8A8A", [".svg"] = "#FFB13B", [".csv"] = "#217346", [".sln"] = "#5C2D91", [".csproj"] = "#5C2D91",
            [".gitignore"] = "#F05032", [".lock"] = "#7A7A7A", [".graphql"] = "#E10098", [".proto"] = "#4285F4",
        };

        private static readonly HashSet<string> KeepWindowsIcon = new(StringComparer.OrdinalIgnoreCase)
            { ".txt", ".csv", ".reg", ".inf", ".sln", ".csproj", ".vbproj", ".vcxproj", ".bat", ".cmd", ".vbs", ".manifest" };

        /// <summary>Code/dev types that get WinXplorer's icon when "own icons for code files" is on.</summary>
        public static bool IsCodeType(string ext) => !string.IsNullOrEmpty(ext) && FileTypes.Text.Contains(ext) && !KeepWindowsIcon.Contains(ext);

        private static Color ColorFor(string ext)
        {
            if (Colors.TryGetValue(ext, out var hex)) return (Color)ColorConverter.ConvertFromString(hex);
            // stable colour per unknown extension
            uint h = 2166136261;
            foreach (var ch in ext.ToLowerInvariant()) h = (h ^ ch) * 16777619;
            return HsvToRgb(h % 360, 0.55, 0.62);
        }

        private static Color HsvToRgb(double hue, double s, double v)
        {
            double c = v * s, x = c * (1 - Math.Abs(hue / 60 % 2 - 1)), m = v - c;
            (double r, double g, double b) = hue switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
            return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
        }

        private static double Lum(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

        private static string Label(string ext)
        {
            var t = ext.TrimStart('.').ToUpperInvariant();
            return t.Length > 4 ? t[..4] : t;
        }

        private static Geometry Text(string text, double size, Point center, FontWeight weight)
        {
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.SemiCondensed), size, Brushes.Black, 1.0);
            return ft.BuildGeometry(new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));
        }

        private static ImageSource Custom(string ext, bool small)
        {
            try
            {
                var name = ext.TrimStart('.').ToLowerInvariant();
                var f = Path.Combine(CustomDir, name + (small ? "-small" : "") + ".svg");
                if (!File.Exists(f) && small) f = Path.Combine(CustomDir, name + ".svg");
                return File.Exists(f) ? ImageLoader.LoadSvg(f) : null;
            }
            catch { return null; }
        }

        /// <summary>16×16 tile: coloured rounded square with the extension.</summary>
        public static ImageSource Small(string ext) => _small.GetOrAdd(ext, e => Custom(e, true) ?? (FileTypes.Archive.Contains(e) ? Archive() : null) ?? BuildSmall(e));

        /// <summary>Page with a folded corner and a coloured label (scales to any icon size).</summary>
        public static ImageSource Large(string ext) => _large.GetOrAdd(ext, e => Custom(e, false) ?? (FileTypes.Archive.Contains(e) ? Archive() : null) ?? BuildLarge(e));

        private static ImageSource _archive;
        private static readonly object _archiveLock = new();

        /// <summary>WinXplorer's archive icon (a folder with a zipper) for zip, 7z, rar… files – they open like folders here.</summary>
        public static ImageSource Archive()
        {
            lock (_archiveLock) return _archive ??= App.LoadSvgResource("archive_file.svg");
        }

        private static ImageSource BuildSmall(string ext)
        {
            var c = ColorFor(ext);
            var fg = Lum(c) > 0.6 ? Color.FromRgb(0x20, 0x20, 0x20) : System.Windows.Media.Colors.White;
            var label = Label(ext);
            var g = new DrawingGroup();
            g.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
            g.Children.Add(new GeometryDrawing(new SolidColorBrush(c), null, new RectangleGeometry(new Rect(0.5, 1.5, 15, 13), 2.5, 2.5)));
            double size = label.Length <= 2 ? 7.5 : label.Length == 3 ? 6.2 : 5.0;
            g.Children.Add(new GeometryDrawing(new SolidColorBrush(fg), null, Text(label, size, new Point(8, 8.1), FontWeights.Bold)));
            var img = new DrawingImage(g);
            img.Freeze();
            return img;
        }

        private static ImageSource BuildLarge(string ext)
        {
            var c = ColorFor(ext);
            var fg = Lum(c) > 0.6 ? Color.FromRgb(0x20, 0x20, 0x20) : System.Windows.Media.Colors.White;
            var label = Label(ext);
            const double L = 44, T = 14, R = 212, B = 242, Fold = 50;
            var page = Geometry.Parse($"M{L},{T + 10} Q{L},{T} {L + 10},{T} L{R - Fold},{T} L{R},{T + Fold} L{R},{B - 10} Q{R},{B} {R - 10},{B} L{L + 10},{B} Q{L},{B} {L},{B - 10} Z");
            var fold = Geometry.Parse($"M{R - Fold},{T} L{R - Fold},{T + Fold - 10} Q{R - Fold},{T + Fold} {R - Fold + 10},{T + Fold} L{R},{T + Fold} Z");
            var g = new DrawingGroup();
            g.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 256, 256))));
            g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA)), new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)), 3), page));
            g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)), null, fold));
            // a few "text lines" to read as a document
            var lines = new SolidColorBrush(Color.FromArgb(0x30, c.R, c.G, c.B));
            for (int i = 0; i < 4; i++)
                g.Children.Add(new GeometryDrawing(lines, null, new RectangleGeometry(new Rect(L + 22, T + 66 + i * 18, (i % 2 == 0 ? 112 : 84) - (i == 0 ? 30 : 0), 8), 4, 4)));
            // label band
            var band = new Rect(L - 14, 150, R - L + 28, 62);
            g.Children.Add(new GeometryDrawing(new SolidColorBrush(c), null, new RectangleGeometry(band, 8, 8)));
            double size = label.Length <= 2 ? 44 : label.Length == 3 ? 40 : 33;
            g.Children.Add(new GeometryDrawing(new SolidColorBrush(fg), null, Text(label, size, new Point(128, band.Top + band.Height / 2), FontWeights.Bold)));
            var img = new DrawingImage(g);
            img.Freeze();
            return img;
        }
    }
}
