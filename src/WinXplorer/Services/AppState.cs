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
            try { _base = File.Exists(FilePath) ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(FilePath))?.AsObject() : null; } catch { _base = null; }
            try { if (File.Exists(FilePath)) _ownWrite = File.GetLastWriteTimeUtc(FilePath); } catch { }
            Fix(Settings);
            WatchOtherWindows();
        }

        private static void Fix(AppSettings s)
        {
            s.Sidebar ??= DefaultSidebar();
            s.HiddenDrives ??= new();
            s.Tabs ??= new();
            if (s.ContextMenuMode == "full") s.ContextMenuMode = "custom";   // 0.1.10 had a separate style for built menus
            MenuCatalog.Ensure(s);
        }

        // ---- every WinXplorer window is its own process: each one follows what the others save ----
        private static FileSystemWatcher _watch;
        private static System.Windows.Threading.DispatcherTimer _reloadTimer;
        private static DateTime _ownWrite;

        /// <summary>
        /// Without this, a window that was open while you changed a setting in another one kept the old settings
        /// in memory and wrote them back on its next save – the change seemed to "fall off" after a restart.
        /// </summary>
        private static void WatchOtherWindows()
        {
            var app = System.Windows.Application.Current;
            if (_watch != null || app == null) return;
            try
            {
                Directory.CreateDirectory(DataDir);
                _reloadTimer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(300), System.Windows.Threading.DispatcherPriority.Background,
                    (_, _) => { _reloadTimer.Stop(); ReloadFromDisk(); }, app.Dispatcher);
                _reloadTimer.Stop();
                _watch = new FileSystemWatcher(DataDir, "settings.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
                FileSystemEventHandler h = (_, _) => app.Dispatcher.BeginInvoke(() => { _reloadTimer.Stop(); _reloadTimer.Start(); });
                _watch.Changed += h; _watch.Created += h;
                _watch.Renamed += (_, _) => app.Dispatcher.BeginInvoke(() => { _reloadTimer.Stop(); _reloadTimer.Start(); });
                _watch.EnableRaisingEvents = true;
            }
            catch { }
        }

        // ---- three-way merge: every window writes only what it changed itself, everything else comes from the file ----
        /// <summary>The settings as they were last read from / written to the file (what "unchanged here" is measured against).</summary>
        private static System.Text.Json.Nodes.JsonObject _base;
        /// <summary>This window's own: never taken over from another window on reload.</summary>
        private static readonly string[] LocalOnly = { "Tabs", "SelectedTab", "WinLeft", "WinTop", "WinWidth", "WinHeight", "WinMaximized" };
        private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

        private static System.Text.Json.Nodes.JsonObject ToNode(AppSettings s) =>
            System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(s, Indented))!.AsObject();

        private static string J(System.Text.Json.Nodes.JsonNode n) => n?.ToJsonString() ?? "null";

        /// <summary>
        /// Takes into <see cref="Settings"/> every value another window saved, unless this window changed that same
        /// value since it last read the file. Per-folder views are merged folder by folder.
        /// </summary>
        private static void MergeFrom(System.Text.Json.Nodes.JsonObject disk, bool takeLocalOnly)
        {
            var cur = ToNode(Settings);
            var bas = _base ?? cur;
            foreach (var prop in typeof(AppSettings).GetProperties())
            {
                if (!prop.CanWrite || !disk.ContainsKey(prop.Name)) continue;
                if (LocalOnly.Contains(prop.Name) && !takeLocalOnly) continue;
                var d = disk[prop.Name]; var c = cur[prop.Name]; var b = bas[prop.Name];
                string dj = J(d), cj = J(c), bj = J(b);
                if (dj == cj) continue;
                object value;
                if (prop.Name == nameof(AppSettings.FolderViews) && d is System.Text.Json.Nodes.JsonObject dm && c is System.Text.Json.Nodes.JsonObject cm)
                {
                    // folder by folder: the ones changed here stay, the rest follow the file
                    var bm = b as System.Text.Json.Nodes.JsonObject;
                    var merged = new System.Text.Json.Nodes.JsonObject();
                    foreach (var key in dm.Select(k => k.Key).Union(cm.Select(k => k.Key)).Distinct().ToList())
                    {
                        var cv = cm.ContainsKey(key) ? cm[key] : null;
                        var bv = bm != null && bm.ContainsKey(key) ? bm[key] : null;
                        bool changedHere = J(cv) != J(bv);
                        var pick = changedHere ? cv : (dm.ContainsKey(key) ? dm[key] : null);
                        if (pick != null) merged[key] = pick.DeepClone();
                    }
                    value = JsonSerializer.Deserialize(merged.ToJsonString(), prop.PropertyType, Indented);
                }
                else
                {
                    if (cj != bj && !(LocalOnly.Contains(prop.Name) && takeLocalOnly)) continue;   // changed in this window: ours wins
                    value = d == null ? null : JsonSerializer.Deserialize(d.ToJsonString(), prop.PropertyType, Indented);
                }
                prop.SetValue(Settings, value);
            }
            Fix(Settings);
        }

        private static void ReloadFromDisk()
        {
            try
            {
                if (!File.Exists(FilePath) || File.GetLastWriteTimeUtc(FilePath) == _ownWrite) return;   // our own save
                var disk = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(FilePath))?.AsObject();
                if (disk == null) return;
                // this window's own tabs and place on screen stay as they are
                MergeFrom(disk, false);
                _base = disk;
                _ownWrite = File.GetLastWriteTimeUtc(FilePath);
            }
            catch { }   // being written right now: the next change event reads it again
        }

        private static System.Windows.Threading.DispatcherTimer _saveTimer;

        /// <summary>Saves a moment later (several quick changes – e.g. Ctrl+wheel on the icon size – become one write).</summary>
        public static void SaveSoon()
        {
            var app = System.Windows.Application.Current;
            if (app == null) { Save(); return; }
            _saveTimer ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(800), System.Windows.Threading.DispatcherPriority.Background,
                (_, _) => { _saveTimer.Stop(); Save(); }, app.Dispatcher);
            _saveTimer.Stop(); _saveTimer.Start();
        }

        /// <summary>Set by windows that aren't the main one: their saves keep the main window's tabs on disk.</summary>
        public static bool ExtraWindowSaves { get => _extraWindow; set => _extraWindow = value; }
        private static bool _extraWindow;

        /// <param name="keepSavedSession">Keep the tabs stored on disk (used by extra windows, so they don't replace the main window's session).</param>
        public static void Save(bool? keepSavedSessionOrDefault = null)
        {
            bool keepSavedSession = keepSavedSessionOrDefault ?? _extraWindow;
            try
            {
                Directory.CreateDirectory(DataDir);
                _saveTimer?.Stop();
                if (File.Exists(FilePath) && (File.GetLastWriteTimeUtc(FilePath) != _ownWrite || keepSavedSession))
                {
                    try
                    {
                        // another window saved since we last read the file: keep its changes, add ours
                        var disk = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(FilePath))?.AsObject();
                        if (disk != null)
                        {
                            var tabs = Settings.Tabs; var sel = Settings.SelectedTab;
                            double l = Settings.WinLeft, t = Settings.WinTop, w = Settings.WinWidth, h = Settings.WinHeight; bool max = Settings.WinMaximized;
                            // the main window's tabs and its place on screen stay as the main window left them
                            MergeFrom(disk, keepSavedSession);
                            if (!keepSavedSession)
                            {
                                Settings.Tabs = tabs; Settings.SelectedTab = sel;
                                Settings.WinLeft = l; Settings.WinTop = t; Settings.WinWidth = w; Settings.WinHeight = h; Settings.WinMaximized = max;
                            }
                        }
                    }
                    catch { }
                }
                var tmp = FilePath + ".tmp";
                var node = ToNode(Settings);
                File.WriteAllText(tmp, node.ToJsonString(Indented));
                File.Move(tmp, FilePath, true);
                _base = node;
                _ownWrite = File.GetLastWriteTimeUtc(FilePath);
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
