using System;
using System.Collections.Generic;
using System.Linq;
using WinXplorer.Models;
using WinXplorer.Services;

namespace WinXplorer.Services
{
    [Flags]
    public enum MenuScope { Item = 1, Background = 2, Both = 3 }

    /// <summary>A built-in command that can be placed in WinXplorer's context menu.</summary>
    public sealed record MenuCommand(string Id, string LabelKey, string Glyph, MenuScope Scope);

    /// <summary>All commands available for the customizable context menu, plus the default layouts.</summary>
    public static class MenuCatalog
    {
        public const string Separator = "sep";
        public const string ShellPrefix = "shell:";
        /// <summary>"sub:&lt;id&gt;" opens a submenu (its name is the entry's Label); everything up to the matching "subend" goes into it.</summary>
        public const string SubPrefix = "sub:";
        public const string SubEnd = "subend";
        public static bool IsSub(string id) => id != null && id.StartsWith(SubPrefix);

        public static readonly MenuCommand[] Commands =
        {
            new("open", "Open", "", MenuScope.Item),
            new("openNewTab", "OpenNewTab", "", MenuScope.Item),
            new("openOtherPane", "OpenOtherPane", "", MenuScope.Item),
            new("openWith", "OpenWith", "", MenuScope.Item),
            new("quickView", "QuickView", "", MenuScope.Item),
            new("openLocation", "OpenLocation", "", MenuScope.Item),
            new("openTarget", "OpenTargetLocation", "", MenuScope.Item),
            new("openNewWindow", "OpenNewWindow", "", MenuScope.Item),
            new("openInExplorer", "OpenInExplorer", "", MenuScope.Both),
            new("runAsAdmin", "RunAsAdmin", "", MenuScope.Item),
            new("mount", "Mount", "", MenuScope.Item),
            new("cut", "Cut", "", MenuScope.Item),
            new("copy", "Copy", "", MenuScope.Item),
            new("paste", "Paste", "", MenuScope.Both),
            new("rename", "Rename", "", MenuScope.Item),
            new("delete", "Delete", "", MenuScope.Item),
            new("compressZip", "CompressZip", "", MenuScope.Item),
            new("extractHere", "ExtractHere", "", MenuScope.Item),
            new("shortcutDesktop", "ShortcutDesktop", "", MenuScope.Item),
            new("pinStart", "PinStart", "", MenuScope.Item),
            new("pinTaskbar", "PinTaskbar", "", MenuScope.Item),
            new("copyPath", "CopyPath", "", MenuScope.Both),
            new("copyName", "CopyName", "", MenuScope.Item),
            new("copyTo", "CopyTo", "", MenuScope.Item),
            new("moveTo", "MoveTo", "", MenuScope.Item),
            new("selectAll", "SelectAll", "", MenuScope.Background),
            new("pinSidebar", "PinToSidebar", "", MenuScope.Both),
            new("terminal", "OpenTerminalHere", "", MenuScope.Both),
            new("sortBy", "Sort", "", MenuScope.Background),
            new("view", "View", "", MenuScope.Background),
            new("refresh", "Refresh", "", MenuScope.Background),
            new("undo", "Undo", "", MenuScope.Background),
            new("newFile", "New", "", MenuScope.Background),
            new("newFolder", "NewFolder", "", MenuScope.Background),
            new("newText", "TextFile", "", MenuScope.Background),
            new("properties", "Properties", "", MenuScope.Both),
            new("windowsMenu", "WindowsMenu", "", MenuScope.Both),
        };

        public static MenuCommand Get(string id) => Commands.FirstOrDefault(c => c.Id == id);

        // ---------------- quick row: icons on the first line of the menu ----------------
        /// <summary>"new:.php" = create a new .php file here.</summary>
        public const string NewPrefix = "new:";
        private static readonly string[] NotInRow = { "sortBy", "view", "newFile" };

        public static int RowMax(string size) => size switch { "large" => 5, "medium" => 6, _ => 8 };
        public static double RowButton(string size) => size switch { "large" => 46, "medium" => 39, _ => 32 };
        public static double RowGlyph(string size) => size switch { "large" => 20, "medium" => 17, _ => 14 };

        public static List<string> DefaultRow(bool items) => items
            ? new List<string> { "cut", "copy", "paste", "rename", "delete" }
            : new List<string> { "paste", "undo", "newFolder", "refresh" };

        /// <summary>The row as configured (or the default), never longer than the chosen size allows.</summary>
        public static List<string> Row(AppSettings s, bool items)
        {
            var l = (items ? s.ItemQuickRow : s.BackgroundQuickRow) ?? DefaultRow(items);
            return l.Where(id => CanBeInRow(id, items)).Distinct().Take(RowMax(s.QuickRowSize)).ToList();
        }

        public static bool CanBeInRow(string id, bool items)
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (id.StartsWith(NewPrefix)) return id.Length > NewPrefix.Length + 1;
            if (id.StartsWith(CustomCommands.Prefix)) return CustomCommands.Get(id) != null;
            return Get(id) is { } c && !NotInRow.Contains(id) && c.Scope.HasFlag(items ? MenuScope.Item : MenuScope.Background);
        }

        public static string RowLabel(string id) => id.StartsWith(NewPrefix)
            ? Loc.T("New") + ": " + NewFiles.NameOf(id[NewPrefix.Length..])
            : id.StartsWith(CustomCommands.Prefix) ? CustomCommands.Get(id)?.Label ?? id
            : Get(id) is { } c ? Loc.T(c.LabelKey) : id;

        private static List<MenuEntry> Make(params string[] ids) => ids.Select(i => new MenuEntry { Id = i, Enabled = true }).ToList();

        public static List<MenuEntry> DefaultItemMenu() => Make(
            "open", "openNewTab", "openOtherPane", "openWith", "mount", "runAsAdmin", "quickView", "openLocation", "openTarget", Separator,
            "cut", "copy", "paste", "rename", "delete", Separator,
            "compressZip", "extractHere", "copyPath", "shortcutDesktop", "pinStart", "pinTaskbar", "pinSidebar", "terminal", Separator,
            "properties", "windowsMenu");

        public static List<MenuEntry> DefaultAltMenu() => Make(
            "copyPath", "shortcutDesktop", "compressZip", "pinSidebar", Separator,
            "openOtherPane", "terminal", Separator,
            "properties", "windowsMenu");

        // ---------------- per-kind menus + Alt menu ----------------
        public static readonly string[] Types = { "folder", "image", "video", "audio", "document", "archive", "program" };
        public static string TypeLabel(string t) => Loc.T("MenuType_" + t);
        private static readonly HashSet<string> Docs = new(StringComparer.OrdinalIgnoreCase)
            { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".rtf", ".txt", ".md", ".csv", ".epub" };

        public static string TypeOf(FileItem f) =>
            f.IsDrive ? null : f.IsDirectory ? "folder"
            : FileTypes.Image.Contains(f.Extension) ? "image" : FileTypes.Video.Contains(f.Extension) ? "video"
            : FileTypes.Audio.Contains(f.Extension) ? "audio" : Docs.Contains(f.Extension) ? "document"
            : FileTypes.Archive.Contains(f.Extension) ? "archive"
            : f.Extension is ".exe" or ".msi" or ".bat" or ".cmd" or ".ps1" or ".lnk" ? "program" : null;

        /// <summary>The kind shared by all selected items (null when mixed / unknown).</summary>
        public static string TypeOf(IEnumerable<FileItem> items)
        {
            string t = null;
            foreach (var f in items)
            {
                var x = TypeOf(f);
                if (x == null || (t != null && t != x)) return null;
                t = x;
            }
            return t;
        }

        /// <summary>Which menu layout to show for these items: Alt menu, the kind's own menu, or the general one.</summary>
        public static List<MenuEntry> ItemMenuFor(AppSettings s, IEnumerable<FileItem> items, bool alt)
        {
            if (alt && s.AltMenu is { Count: > 0 }) return s.AltMenu;
            var t = TypeOf(items);
            return t != null && s.TypeMenus != null && s.TypeMenus.TryGetValue(t, out var l) && l is { Count: > 0 } ? l : s.ItemMenu;
        }

        public static List<MenuEntry> DefaultBackgroundMenu() => Make(
            "view", "sortBy", "refresh", Separator,
            "paste", "undo", Separator,
            "newFile", "newFolder", Separator,
            "terminal", "copyPath", "pinSidebar", Separator,
            "properties", "windowsMenu");

        /// <summary>Makes sure both menus exist in settings, and adds commands introduced by newer versions (disabled).</summary>
        public static void Ensure(AppSettings s)
        {
            s.ItemMenu ??= DefaultItemMenu();
            s.BackgroundMenu ??= DefaultBackgroundMenu();
            s.AltMenu ??= DefaultAltMenu();
            if (s.MenuVersion < 2)
            {
                // v2: "Undo" and the "New" submenu – add them to menus saved by older versions
                var b = s.BackgroundMenu;
                if (!b.Any(e => e.Id == "undo"))
                {
                    int i = b.FindIndex(e => e.Id == "paste");
                    b.Insert(i < 0 ? 0 : i + 1, new MenuEntry { Id = "undo" });
                }
                if (!b.Any(e => e.Id == "newFile"))
                {
                    int i = b.FindIndex(e => e.Id == "newFolder");
                    b.Insert(i < 0 ? b.Count : i, new MenuEntry { Id = "newFile" });
                    b.RemoveAll(e => e.Id == "newText");
                }
                s.MenuVersion = 2;
            }
            if (s.MenuVersion < 3)
            {
                // v3: mount disk images, run as administrator, shortcut target – they only show where they apply
                foreach (var l in new[] { s.ItemMenu }.Concat(s.TypeMenus?.Values ?? Enumerable.Empty<List<MenuEntry>>()))
                {
                    void After(string anchor, string id)
                    {
                        if (l.Any(e => e.Id == id)) return;
                        int i = l.FindIndex(e => e.Id == anchor);
                        l.Insert(i < 0 ? 0 : i + 1, new MenuEntry { Id = id });
                    }
                    After("openWith", "mount");
                    After("mount", "runAsAdmin");
                    After(l.Any(e => e.Id == "openLocation") ? "openLocation" : "quickView", "openTarget");
                }
                s.MenuVersion = 3;
            }
        }

        public static string LabelOf(MenuEntry e)
        {
            if (e.Id == Separator) return "———";
            if (IsSub(e.Id)) return string.IsNullOrWhiteSpace(e.Label) ? Loc.T("Submenu") : e.Label;
            if (e.Id == SubEnd) return Loc.T("SubmenuEnd");
            if (e.Id.StartsWith(ShellPrefix)) return e.Label ?? e.Id[ShellPrefix.Length..];
            if (e.Id.StartsWith(CustomCommands.Prefix)) return CustomCommands.Get(e.Id)?.Label ?? e.Id;
            return Get(e.Id) is { } c ? Loc.T(c.LabelKey) : e.Id;
        }
    }
}
