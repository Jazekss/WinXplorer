using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using WinXplorer.Models;

namespace WinXplorer.Services
{
    public static class AppState
    {
        public static AppSettings Settings { get; private set; } = new();
        /// <summary>%LOCALAPPDATA%\JKapps\WinXplorer – settings, index, logs (moved here from the old %APPDATA%\WinXplorer on first start).</summary>
        public static readonly string DataDir = InitDataDir();

        private static string InitDataDir()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JKapps", "WinXplorer");
            try
            {
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                    foreach (var old in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinXplorer"),
                                                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinXplorer") })
                    {
                        if (!Directory.Exists(old)) continue;
                        foreach (var f in Directory.GetFiles(old, "*", SearchOption.AllDirectories))
                        {
                            var dest = Path.Combine(dir, Path.GetRelativePath(old, f));
                            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                            if (!File.Exists(dest)) File.Copy(f, dest);
                        }
                        try { Directory.Delete(old, true); } catch { }
                    }
                }
            }
            catch { }
            return dir;
        }
        private static string FilePath => Path.Combine(DataDir, "settings.json");

        public static double DpiScale { get; set; } = 1.0;
        /// <summary>Icon size of the folder being shown (folders remember their own); thumbnails are made at this size.</summary>
        public static int CurrentIconSize { get; set; }
        public static int ThumbnailRequestSize => (int)Math.Ceiling((CurrentIconSize > 0 ? CurrentIconSize : Settings.IconSize) * DpiScale);

        public static event Action SettingsChanged;
        public static void RaiseSettingsChanged() => SettingsChanged?.Invoke();

        public const string DonateUrl = "https://buymeacoffee.com/jazeknom";

        public static void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    Settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }
            catch { Settings = new AppSettings(); }
            Settings.Sidebar ??= DefaultSidebar();
            Settings.HiddenDrives ??= new();
            Settings.Tabs ??= new();
            MenuCatalog.Ensure(Settings);
        }

        /// <param name="keepSavedSession">Keep the tabs stored on disk (used by extra windows, so they don't replace the main window's session).</param>
        public static void Save(bool keepSavedSession = false)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                if (keepSavedSession && File.Exists(FilePath))
                {
                    try
                    {
                        var disk = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                        if (disk != null) { Settings.Tabs = disk.Tabs ?? new(); Settings.SelectedTab = disk.SelectedTab; }
                    }
                    catch { }
                }
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, FilePath, true);
            }
            catch { }
        }

        public static System.Collections.Generic.List<SidebarEntryData> DefaultSidebar()
        {
            var list = new System.Collections.Generic.List<SidebarEntryData>
            {
                new() { Kind = SidebarKind.Folder, Path = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) },
                new() { Kind = SidebarKind.Folder, Path = Interop.Native.GetKnownFolder(Interop.Native.FOLDERID_Downloads)
                                                          ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") },
                new() { Kind = SidebarKind.Drives },
                new() { Kind = SidebarKind.Folder, Path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) },
            };
            var shots = Interop.Native.GetKnownFolder(Interop.Native.FOLDERID_Screenshots)
                        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
            if (Directory.Exists(shots)) list.Add(new() { Kind = SidebarKind.Folder, Path = shots });
            return list.Where(e => e.Kind != SidebarKind.Folder || !string.IsNullOrEmpty(e.Path)).ToList();
        }
    }
}
