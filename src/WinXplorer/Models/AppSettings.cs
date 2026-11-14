using System.Collections.Generic;

namespace WinXplorer.Models
{
    /// <summary>App = a program, shortcut or any file: clicking it opens it (new kinds go at the end – settings store the number).</summary>
    public enum SidebarKind { Folder, Drives, ThisPC, Separator, App, Frequent }

    public sealed class SidebarEntryData
    {
        public SidebarKind Kind { get; set; }
        public string Path { get; set; }
        /// <summary>Optional custom label; when empty the folder name is used.</summary>
        public string Label { get; set; }
    }

    /// <summary>One entry of the customizable context menu.</summary>
    public sealed class MenuEntry
    {
        /// <summary>Built-in command id (see MenuCatalog), "sep", or "shell:&lt;key&gt;" for a Windows menu item.</summary>
        public string Id { get; set; }
        /// <summary>Text shown for Windows menu items (built-in items are localized).</summary>
        public string Label { get; set; }
        public bool Enabled { get; set; } = true;
    }

    /// <summary>A context-menu command the user made: name + command line (see Services.CustomCommands).</summary>
    public sealed class CustomCommand
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public string Command { get; set; }
    }

    /// <summary>How one folder is shown (remembered per folder).</summary>
    public sealed class FolderView
    {
        public string View { get; set; }
        public int IconSize { get; set; }
        public string SortBy { get; set; }
        public bool SortDesc { get; set; }
        public bool GroupByDate { get; set; }
        /// <summary>Details view column widths the user dragged (name, modified, type, size, location); null = automatic.</summary>
        public Dictionary<string, double> Columns { get; set; }
    }

    public sealed class AppSettings
    {
        public string Language { get; set; } = "auto";   // auto | en | lv | de | ... | custom file name
        public string Theme { get; set; } = "system";    // system | light | dark
        public string Accent { get; set; } = "system";   // system | #RRGGBB

        /// <summary>custom = WinXplorer menu (editable in Settings), windows = classic Windows menu.</summary>
        public string ContextMenuMode { get; set; } = "custom";
        public List<MenuEntry> ItemMenu { get; set; }
        public List<MenuEntry> BackgroundMenu { get; set; }
        /// <summary>Own menus per file kind ("folder", "image", "video", "audio", "document", "archive", "program"); missing = ItemMenu.</summary>
        public Dictionary<string, List<MenuEntry>> TypeMenus { get; set; }
        /// <summary>Second menu: Alt + right-click on items.</summary>
        public List<MenuEntry> AltMenu { get; set; }
        public int MenuVersion { get; set; }
        /// <summary>Icon row at the top of the WinXplorer menu (command ids, "new:.ext" = new file); null = default.</summary>
        public List<string> ItemQuickRow { get; set; }
        public List<string> BackgroundQuickRow { get; set; }
        /// <summary>small (up to 8 icons) | medium (6) | large (5).</summary>
        public string QuickRowSize { get; set; } = "small";
        public List<CustomCommand> CustomCommands { get; set; }
        /// <summary>How often each folder was opened (sidebar "Frequent folders").</summary>
        public Dictionary<string, int> FolderVisits { get; set; }
        public bool ShowHidden { get; set; }
        public bool ShowExtensions { get; set; } = true;
        public bool ShowPreview { get; set; } = true;
        public bool RestoreTabs { get; set; } = true;
        /// <summary>Ctrl+T / + opens This PC instead of the current folder.</summary>
        public bool NewTabThisPC { get; set; }
        public bool CompactRows { get; set; }
        public bool SpaceOpensViewer { get; set; } = true;
        /// <summary>Resting the mouse on an item for a second opens a small preview beside it.</summary>
        public bool HoverPreview { get; set; }
        /// <summary>…and video / audio there start playing at once.</summary>
        public bool HoverAutoPlay { get; set; } = true;
        /// <summary>Double-click / Enter on a picture opens WinXplorer's viewer instead of the default app.</summary>
        public bool ImagesOpenInViewer { get; set; } = true;
        /// <summary>Double-click on a zip / 7z / rar opens it like a folder.</summary>
        public bool OpenArchives { get; set; } = true;
        /// <summary>WinXplorer is offered in Windows' "Open with" menu for archives.</summary>
        public bool ArchiveOpenWith { get; set; } = true;
        /// <summary>Folders, drives and Win+E open in WinXplorer instead of Windows Explorer (off by default).</summary>
        public bool DefaultFileManager { get; set; }
        public bool ConfirmDelete { get; set; } = true;
        /// <summary>Count every folder's size for the size column (background, more disk activity).</summary>
        public bool ShowFolderSizes { get; set; }
        /// <summary>Address bar + search right under the tabs (one for the active pane, like Windows Explorer) instead of one per pane.</summary>
        public bool NavOnTop { get; set; }
        /// <summary>Background file-name index of fixed drives for instant search.</summary>
        public bool SearchIndex { get; set; } = true;
        public bool IndexSkipWindows { get; set; } = true;
        /// <summary>WinXplorer's own icons for code/dev file types even when Windows has an icon for them.</summary>
        public bool OwnCodeIcons { get; set; } = true;
        /// <summary>Extensions offered under New (besides Folder); empty = defaults.</summary>
        public List<string> NewFileTypes { get; set; }
        /// <summary>Keyboard shortcuts changed by the user: command id → key combinations ("Ctrl+Shift+N"). Missing = default.</summary>
        public Dictionary<string, List<string>> Hotkeys { get; set; }

        public double SidebarWidth { get; set; } = 220;
        public double PreviewWidth { get; set; } = 320;

        public string ViewMode { get; set; } = "details"; // details | icons  (default for folders without their own view)
        public string DefaultSortBy { get; set; } = "name";
        public bool DefaultSortDesc { get; set; }
        public bool DefaultGroupByDate { get; set; }
        /// <summary>Folders whose view was changed: path → view, sort, grouping, icon size.</summary>
        public Dictionary<string, FolderView> FolderViews { get; set; }
        public int IconSize { get; set; } = 96;

        public double WinLeft { get; set; } = double.NaN;
        public double WinTop { get; set; } = double.NaN;
        public double WinWidth { get; set; } = 1200;
        public double WinHeight { get; set; } = 760;
        public bool WinMaximized { get; set; }

        public List<SidebarEntryData> Sidebar { get; set; }
        public List<string> HiddenDrives { get; set; } = new();
        public List<SavedTab> Tabs { get; set; } = new();
        public int SelectedTab { get; set; }
    }

    public sealed class SavedTab
    {
        public string Left { get; set; }
        public string Right { get; set; }
        public string Split { get; set; } = "none"; // none | vertical | horizontal
    }
}
