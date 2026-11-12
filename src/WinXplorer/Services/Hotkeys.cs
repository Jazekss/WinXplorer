using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Input;

namespace WinXplorer.Services
{
    /// <summary>Where a shortcut works. Main and Pane are active together (the file window), so they can clash with each other.</summary>
    public enum HkScope { Main, Pane, Viewer, Quick }

    /// <summary>One key combination, e.g. Ctrl+Shift+N.</summary>
    public readonly record struct Gesture(Key Key, ModifierKeys Mods)
    {
        public bool HasChordModifier => (Mods & (ModifierKeys.Control | ModifierKeys.Alt)) != 0;
        public bool IsFunctionKey => Key >= Key.F1 && Key <= Key.F24;

        /// <summary>Storage form: "Ctrl+Shift+N" with WPF key names.</summary>
        public string Serialize()
        {
            var parts = new List<string>();
            if (Mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (Mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (Mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            // some keys share a value with an alias (PageDown = Next); store the readable name
            parts.Add(Key switch { Key.PageDown => "PageDown", Key.PageUp => "PageUp", Key.Return => "Return", Key.CapsLock => "CapsLock", Key.PrintScreen => "PrintScreen", _ => Key.ToString() });
            return string.Join("+", parts);
        }

        public static Gesture? Parse(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var mods = ModifierKeys.None;
            Key? key = null;
            foreach (var p in s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (p.ToLowerInvariant())
                {
                    case "ctrl": case "control": mods |= ModifierKeys.Control; break;
                    case "shift": mods |= ModifierKeys.Shift; break;
                    case "alt": mods |= ModifierKeys.Alt; break;
                    default:
                        if (Enum.TryParse<Key>(p, true, out var k)) key = k; else return null;
                        break;
                }
            }
            return key is { } kk && kk != Key.None ? new Gesture(kk, mods) : null;
        }

        /// <summary>Text for keycaps: "Ctrl + Shift + N", "Alt + ←", "Del".</summary>
        public string Display()
        {
            var parts = new List<string>();
            if (Mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (Mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (Mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            parts.Add(KeyName(Key));
            return string.Join(" + ", parts);
        }

        public static string KeyName(Key k) => k switch
        {
            Key.Left => "←", Key.Right => "→", Key.Up => "↑", Key.Down => "↓",
            Key.Back => "Backspace", Key.Delete => "Del", Key.Insert => "Ins", Key.Return => "Enter", Key.Escape => "Esc",
            Key.PageUp => "PgUp", Key.PageDown => "PgDn", Key.Space => "Space", Key.Apps => "Menu",
            Key.OemPlus => "+", Key.OemMinus => "−", Key.OemComma => ",", Key.OemPeriod => ".",
            Key.Add => "Num +", Key.Subtract => "Num −", Key.Multiply => "Num *", Key.Divide => "Num /", Key.Decimal => "Num .",
            Key.Oem2 => "/", Key.Oem1 => ";", Key.Oem3 => "`", Key.Oem4 => "[", Key.Oem5 => "\\", Key.Oem6 => "]", Key.Oem7 => "'",
            >= Key.D0 and <= Key.D9 => ((char)('0' + (k - Key.D0))).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (k - Key.NumPad0),
            _ => k.ToString(),
        };
    }

    public sealed record HkCommand(string Id, HkScope Scope, string Label, string[] Defaults)
    {
        public string Group => Scope switch
        {
            HkScope.Viewer => "HkViewer",
            HkScope.Quick => "HkQuick",
            _ => Hotkeys.GroupOf(Id),
        };
    }

    /// <summary>
    /// Every remappable keyboard shortcut: command id → key combinations. Defaults live here,
    /// the user's changes are stored in AppSettings.Hotkeys (only the commands that differ from the defaults).
    /// </summary>
    public static class Hotkeys
    {
        private static HkCommand C(string id, HkScope scope, string label, params string[] keys) => new(id, scope, label, keys);

        public static readonly HkCommand[] All =
        {
            // navigation
            C("Back", HkScope.Main, "HkBack", "Alt+Left", "Back"),
            C("Forward", HkScope.Main, "HkForward", "Alt+Right"),
            C("Up", HkScope.Main, "HkUp", "Alt+Up"),
            C("Address", HkScope.Main, "HkAddress", "Ctrl+L", "Alt+D"),
            C("Search", HkScope.Main, "HkSearch", "Ctrl+F", "F3", "Ctrl+E"),
            C("Refresh", HkScope.Main, "HkRefresh", "F5"),
            C("OtherPane", HkScope.Main, "HkOtherPane", "F6"),
            // tabs & windows
            C("NewTab", HkScope.Main, "HkNewTab", "Ctrl+T"),
            C("CloseTab", HkScope.Main, "HkCloseTab", "Ctrl+W"),
            C("NextTab", HkScope.Main, "HkNextTabOnly", "Ctrl+Tab"),
            C("PrevTab", HkScope.Main, "HkPrevTab", "Ctrl+Shift+Tab"),
            C("NewWindow", HkScope.Main, "HkNewWindow", "Ctrl+N"),
            C("Preview", HkScope.Main, "HkPreview", "Ctrl+Shift+P"),
            C("Settings", HkScope.Main, "HkSettings", "Ctrl+OemComma"),
            // files
            C("Open", HkScope.Pane, "HkOpen", "Return"),
            C("QuickView", HkScope.Pane, "HkQuickView", "Space"),
            C("Rename", HkScope.Pane, "HkRename", "F2"),
            C("Delete", HkScope.Pane, "HkRecycle", "Delete"),
            C("DeletePerm", HkScope.Pane, "HkDeletePerm", "Shift+Delete"),
            C("Copy", HkScope.Pane, "HkCopy", "Ctrl+C"),
            C("Cut", HkScope.Pane, "HkCut", "Ctrl+X"),
            C("Paste", HkScope.Pane, "HkPaste", "Ctrl+V"),
            C("CopyPath", HkScope.Pane, "HkCopyPath", "Ctrl+Shift+C"),
            C("NewFolder", HkScope.Main, "HkNewFolder", "Ctrl+Shift+N"),
            C("Undo", HkScope.Main, "HkUndo", "Ctrl+Z"),
            C("Properties", HkScope.Pane, "HkProperties", "Alt+Return"),
            C("Menu", HkScope.Pane, "HkMenu", "Shift+F10", "Apps"),
            // picture viewer
            C("Viewer.Prev", HkScope.Viewer, "HkPrevPic", "Left", "PageUp", "Up", "Back"),
            C("Viewer.Next", HkScope.Viewer, "HkNextPic", "Right", "PageDown", "Down"),
            C("Viewer.First", HkScope.Viewer, "HkFirst", "Home"),
            C("Viewer.Last", HkScope.Viewer, "HkLast", "End"),
            C("Viewer.ZoomIn", HkScope.Viewer, "HkZoomIn", "OemPlus", "Shift+OemPlus", "Add"),
            C("Viewer.ZoomOut", HkScope.Viewer, "HkZoomOut", "OemMinus", "Subtract"),
            C("Viewer.Fit", HkScope.Viewer, "HkFitWindow", "D0", "NumPad0"),
            C("Viewer.Actual", HkScope.Viewer, "HkActualSize", "D1", "NumPad1"),
            C("Viewer.RotateRight", HkScope.Viewer, "HkRotateRight", "R"),
            C("Viewer.Info", HkScope.Viewer, "HkViewerInfo", "I"),
            C("Viewer.RotateLeft", HkScope.Viewer, "HkRotateLeft", "Shift+R"),
            C("Viewer.Crop", HkScope.Viewer, "HkCrop", "X"),
            C("Viewer.Copy", HkScope.Viewer, "HkCopyImage", "Ctrl+C"),
            C("Viewer.Open", HkScope.Viewer, "HkOpenDefault", "Return"),
            C("Viewer.Delete", HkScope.Viewer, "HkDeleteImage", "Delete"),
            C("Viewer.Maximize", HkScope.Viewer, "HkMaximize", "F11"),
            C("Viewer.Close", HkScope.Viewer, "HkClose", "Space", "Escape"),
            // quick view
            C("Quick.Prev", HkScope.Quick, "HkPrevFile", "Left", "PageUp"),
            C("Quick.Next", HkScope.Quick, "HkNextFile", "Right", "PageDown"),
            C("Quick.First", HkScope.Quick, "HkFirst", "Home"),
            C("Quick.Last", HkScope.Quick, "HkLast", "End"),
            C("Quick.Open", HkScope.Quick, "HkOpenDefault", "Return"),
            C("Quick.Close", HkScope.Quick, "HkClose", "Space", "Escape"),
        };

        private static readonly string[] NavIds = { "Back", "Forward", "Up", "Address", "Search", "Refresh", "OtherPane" };
        private static readonly string[] TabIds = { "NewTab", "CloseTab", "NextTab", "PrevTab", "NewWindow", "Preview", "Settings" };
        internal static string GroupOf(string id) => NavIds.Contains(id) ? "HkNavigation" : TabIds.Contains(id) ? "HkTabs" : "HkFiles";

        public static HkCommand Find(string id) => All.FirstOrDefault(c => c.Id == id);

        /// <summary>True while Settings is waiting for a new combination – no shortcut may fire then.</summary>
        public static bool Capturing { get; set; }

        private static Dictionary<string, List<Gesture>> _map;

        private static Dictionary<string, List<Gesture>> Map => _map ??= Build();

        private static List<Gesture> Defaults(HkCommand c) => c.Defaults.Select(Gesture.Parse).Where(g => g != null).Select(g => g.Value).ToList();

        private static Dictionary<string, List<Gesture>> Build()
        {
            var user = AppState.Settings.Hotkeys;
            var d = new Dictionary<string, List<Gesture>>();
            foreach (var c in All)
            {
                d[c.Id] = user != null && user.TryGetValue(c.Id, out var list) && list != null
                    ? list.Select(Gesture.Parse).Where(g => g != null).Select(g => g.Value).Distinct().ToList()
                    : Defaults(c);
            }
            return d;
        }

        public static IReadOnlyList<Gesture> Get(string id) => Map.TryGetValue(id, out var l) ? l : Array.Empty<Gesture>();

        public static bool IsDefault(string id) => Find(id) is { } c && Get(id).SequenceEqual(Defaults(c));

        public static bool AllDefault => All.All(c => IsDefault(c.Id));

        /// <summary>Main and Pane work at the same time (the file window); Viewer and Quick are windows of their own.</summary>
        private static bool Overlap(HkScope a, HkScope b) => a == b || (a is HkScope.Main or HkScope.Pane && b is HkScope.Main or HkScope.Pane);

        /// <summary>The key pressed, as a gesture (Windows key ignored). Null for a lone modifier.</summary>
        public static Gesture? FromEvent(KeyEventArgs e)
        {
            var key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, Key.DeadCharProcessed => e.DeadCharProcessedKey, _ => e.Key };
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
                or Key.LWin or Key.RWin or Key.None or Key.System) return null;
            if (key == Key.Enter) key = Key.Return;
            return new Gesture(key, Keyboard.Modifiers & ~ModifierKeys.Windows);
        }

        /// <summary>The command bound to the pressed key in this scope, or null.</summary>
        public static string Match(HkScope scope, KeyEventArgs e)
        {
            if (Capturing || FromEvent(e) is not { } g) return null;
            foreach (var c in All)
                if (c.Scope == scope && Map[c.Id].Contains(g)) return c.Id;
            return null;
        }

        /// <summary>Is the keyboard focus in a text field (a single key there should type, not run a command)?</summary>
        public static bool InText => Keyboard.FocusedElement is TextBox or PasswordBox or RichTextBox
            || Keyboard.FocusedElement?.GetType().Name == "TextArea";

        /// <summary>Keys a text field uses itself: anything without Ctrl/Alt except F1–F24 and Esc.</summary>
        public static bool IsTextKey(Gesture g) => !g.HasChordModifier && !g.IsFunctionKey && g.Key != Key.Escape;

        /// <summary>Commands (other than <paramref name="id"/>) that already use <paramref name="g"/> where <paramref name="id"/> also works.</summary>
        public static List<HkCommand> Conflicts(string id, Gesture g)
        {
            var me = Find(id);
            return All.Where(c => c.Id != id && Overlap(c.Scope, me.Scope) && Get(c.Id).Contains(g)).ToList();
        }

        /// <summary>Replaces gesture number <paramref name="index"/> (or adds one when index &lt; 0) and takes it away from clashing commands.</summary>
        public static List<HkCommand> Assign(string id, int index, Gesture g)
        {
            var clashes = Conflicts(id, g);
            foreach (var c in clashes) Map[c.Id].Remove(g);
            var list = Map[id];
            if (!list.Contains(g))
            {
                if (index >= 0 && index < list.Count) list[index] = g; else list.Add(g);
            }
            else if (index >= 0 && index < list.Count && list[index] != g) list.RemoveAt(index);
            Persist();
            return clashes;
        }

        public static void Remove(string id, int index)
        {
            var list = Map[id];
            if (index >= 0 && index < list.Count) { list.RemoveAt(index); Persist(); }
        }

        public static void Reset(string id)
        {
            if (Find(id) is not { } c) return;
            Map[id] = Defaults(c);
            Persist();
        }

        public static void ResetAll()
        {
            AppState.Settings.Hotkeys = null;
            _map = null;
            AppState.Save();
        }

        private static void Persist()
        {
            var over = new Dictionary<string, List<string>>();
            foreach (var c in All)
                if (!IsDefault(c.Id)) over[c.Id] = Get(c.Id).Select(g => g.Serialize()).ToList();
            AppState.Settings.Hotkeys = over.Count > 0 ? over : null;
            AppState.Save();
        }
    }
}
