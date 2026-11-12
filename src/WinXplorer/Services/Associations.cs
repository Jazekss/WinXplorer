using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32;

namespace WinXplorer.Services
{
    /// <summary>
    /// Puts WinXplorer into Windows' menus for archives (zip, 7z, rar...): "Open in WinXplorer" with the archive
    /// icon (dark or light, following Windows) and right-click › Open with › WinXplorer. Only the current user's
    /// registry (HKCU) is touched, no admin rights, and the user's default app for those files is not changed.
    /// Windows 11 shows the "Open in WinXplorer" item under "Show more options" (Shift+F10 / Shift+right-click).
    /// </summary>
    public static class Associations
    {
        private const string ProgId = "WinXplorer.Archive";
        private const string Verb = "WinXplorer.Open";
        private static readonly string[] Exts = { ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".txz", ".cbz", ".cbr" };

        private static string IconDir => Path.Combine(AppState.DataDir, "Shell");

        public static void Apply(bool on)
        {
            try { if (on) Register(); else Unregister(); } catch { }
        }

        /// <summary>Windows itself (Explorer's menus) in dark mode?</summary>
        private static bool WindowsIsDark()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch { return false; }
        }

        /// <summary>archive-dark.ico / archive-light.ico rendered from the SVGs (once; WPF drawing needs an STA thread).</summary>
        private static string EnsureIcon(bool dark)
        {
            var path = Path.Combine(IconDir, dark ? "archive-dark.ico" : "archive-light.ico");
            if (File.Exists(path)) return path;
            Directory.CreateDirectory(IconDir);
            var t = new Thread(() =>
            {
                try { App.WriteIco(App.LoadSvgResource(dark ? "archive_dark.svg" : "archive_light.svg"), path); } catch { }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start(); t.Join();
            return File.Exists(path) ? path : null;
        }

        private static void Register()
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || exe.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase)) return;
            var cmd = $"\"{exe}\" \"%1\"";
            bool dark = WindowsIsDark();
            var verbText = Loc.T("OpenInWinXplorer");
            // runs at every start and on theme changes: skip when nothing changed
            var sig = string.Join("|", exe, dark ? "dark" : "light", verbText, "2");
            using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
            using (var existing = classes.OpenSubKey(ProgId))
                if (existing?.GetValue("WinXplorerSig") as string == sig) return;

            var icon = EnsureIcon(dark);
            var iconValue = icon != null ? $"\"{icon}\"" : $"\"{exe}\",0";
            using (var p = classes.CreateSubKey(ProgId))
            {
                p.SetValue(null, Loc.T("ArchiveTypeName"));
                using (var di = p.CreateSubKey("DefaultIcon")) di.SetValue(null, iconValue);
                using (var c = p.CreateSubKey(@"shell\open\command")) c.SetValue(null, cmd);
                using (var f = p.CreateSubKey(@"shell\open")) f.SetValue("FriendlyAppName", "WinXplorer");
            }
            using (var app = classes.CreateSubKey(@"Applications\WinXplorer.exe"))
            {
                app.SetValue("FriendlyAppName", "WinXplorer");
                using (var c = app.CreateSubKey(@"shell\open\command")) c.SetValue(null, cmd);
                using var st = app.CreateSubKey("SupportedTypes");
                foreach (var e in Exts) st.SetValue(e, "");
            }
            foreach (var e in Exts)
            {
                using (var k = classes.CreateSubKey(e + @"\OpenWithProgids"))
                    k.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
                // "Open in WinXplorer" right in the menu (whatever app the type belongs to)
                using var v = classes.CreateSubKey($@"SystemFileAssociations\{e}\shell\{Verb}");
                v.SetValue("MUIVerb", verbText);
                v.SetValue("Icon", iconValue);
                using var c = v.CreateSubKey("command");
                c.SetValue(null, cmd);
            }
            using (var p = classes.CreateSubKey(ProgId)) p.SetValue("WinXplorerSig", sig);
            Native.SHChangeNotify(0x08000000 /*SHCNE_ASSOCCHANGED*/, 0, IntPtr.Zero, IntPtr.Zero);
        }

        private static void Unregister()
        {
            using var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", true);
            if (classes == null) return;
            if (classes.OpenSubKey(ProgId) == null) return;
            foreach (var e in Exts)
            {
                using (var k = classes.OpenSubKey(e + @"\OpenWithProgids", true))
                    if (k?.GetValueNames().Contains(ProgId) == true) k.DeleteValue(ProgId, false);
                try { classes.DeleteSubKeyTree($@"SystemFileAssociations\{e}\shell\{Verb}", false); } catch { }
            }
            try { classes.DeleteSubKeyTree(ProgId, false); } catch { }
            try { classes.DeleteSubKeyTree(@"Applications\WinXplorer.exe", false); } catch { }
            Native.SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }

        private static class Native
        {
            [System.Runtime.InteropServices.DllImport("shell32.dll")]
            public static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
        }
    }
}
