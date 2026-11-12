using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using WinXplorer.Interop;

namespace WinXplorer.Services
{
    /// <summary>Follows the Windows light/dark mode and accent color (live), or a forced theme from settings.</summary>
    public static class ThemeManager
    {
        /// <summary>Accent presets offered in Settings (besides "use Windows accent").</summary>
        public static readonly string[] AccentPresets = { "#0078D4", "#8764B8", "#E3008C", "#F7630C", "#10893E", "#038387" };

        public static bool IsDark { get; private set; }
        public static Color Accent { get; private set; } = Color.FromRgb(0x00, 0x78, 0xD4);
        public static event Action ThemeChanged;

        public static void Init()
        {
            Apply();
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color || e.Category == UserPreferenceCategory.VisualStyle)
                    Application.Current?.Dispatcher.BeginInvoke(Apply);
            };
        }

        private static bool SystemIsDark()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch { return false; }
        }

        private static Color SystemAccent()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
                if (k?.GetValue("AccentColor") is int abgr)
                    return Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
            }
            catch { }
            return Color.FromRgb(0x00, 0x78, 0xD4);
        }

        public static void Apply()
        {
            var mode = AppState.Settings.Theme;
            IsDark = mode == "dark" || (mode != "light" && SystemIsDark());
            Accent = SystemAccent();
            var custom = AppState.Settings.Accent;
            if (!string.IsNullOrEmpty(custom) && custom.StartsWith('#'))
                try { Accent = Parse(custom); } catch { }
            var r = Application.Current.Resources;

            void B(string key, string light, string dark) => r[key] = Freeze(new SolidColorBrush(Parse(IsDark ? dark : light)));

            // Surfaces (modelled after the Windows 11 File Explorer)
            B("ChromeBrush", "#EEF1F5", "#1C1C1C");
            B("TabActiveBrush", "#F9F9F9", "#2B2B2B");
            B("BarBrush", "#F9F9F9", "#2B2B2B");
            B("ContentBrush", "#FFFFFF", "#191919");
            B("ContentInactiveBrush", "#F4F5F7", "#141414");
            B("SidebarBrush", "#F9F9F9", "#202020");
            B("PopupBrush", "#F9F9F9", "#2C2C2C");
            B("InputBrush", "#FFFFFF", "#1F1F1F");
            B("InputBorderBrush", "#D6D6D6", "#3A3A3A");
            B("BorderBrush", "#E5E5E5", "#333333");
            B("FgBrush", "#1A1A1A", "#F2F2F2");
            B("FgDimBrush", "#616161", "#A8A8A8");
            B("FgFaintBrush", "#9A9A9A", "#6E6E6E");
            B("HoverBrush", "#0F000000", "#15FFFFFF");
            B("PressedBrush", "#1A000000", "#22FFFFFF");
            B("ScrollThumbBrush", "#40000000", "#55FFFFFF");
            B("ViewerBgBrush", "#F0F0F0", "#111111");

            var a = Accent;
            var accentText = IsDark ? Lighten(a, 0.35) : Darken(a, 0.1);
            r["AccentBrush"] = Freeze(new SolidColorBrush(a));
            r["AccentTextBrush"] = Freeze(new SolidColorBrush(accentText));
            r["AccentFgBrush"] = Freeze(new SolidColorBrush(Lum(a) > 0.55 ? Colors.Black : Colors.White));
            r["SelectedBrush"] = Freeze(new SolidColorBrush(Color.FromArgb((byte)(IsDark ? 0x55 : 0x33), a.R, a.G, a.B)));
            r["SelectedHoverBrush"] = Freeze(new SolidColorBrush(Color.FromArgb((byte)(IsDark ? 0x70 : 0x48), a.R, a.G, a.B)));
            r["SelectedBorderBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(0x90, a.R, a.G, a.B)));
            r["DropBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(0x60, a.R, a.G, a.B)));
            r["AccentColor"] = a;

            Native.SetNativeMenusDark(IsDark);
            ThemeChanged?.Invoke();
        }

        public static void ApplyWindowFrame(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            int dark = IsDark ? 1 : 0;
            Native.DwmSetWindowAttribute(hwnd, 20 /*DWMWA_USE_IMMERSIVE_DARK_MODE*/, ref dark, 4);
            int round = 2; // DWMWCP_ROUND
            Native.DwmSetWindowAttribute(hwnd, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/, ref round, 4);
        }

        private static Brush Freeze(Brush b) { b.Freeze(); return b; }
        private static Color Parse(string s) => (Color)ColorConverter.ConvertFromString(s);
        private static double Lum(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        private static Color Lighten(Color c, double t) => Color.FromRgb((byte)(c.R + (255 - c.R) * t), (byte)(c.G + (255 - c.G) * t), (byte)(c.B + (255 - c.B) * t));
        private static Color Darken(Color c, double t) => Color.FromRgb((byte)(c.R * (1 - t)), (byte)(c.G * (1 - t)), (byte)(c.B * (1 - t)));
    }
}
