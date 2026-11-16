using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using WinXplorer.Interop;
using WinXplorer.Models;
using WinXplorer.Services;

namespace WinXplorer.Views
{
    /// <summary>Context menus of the file list: WinXplorer's customizable menu, or the classic Windows one.</summary>
    public partial class PaneView
    {
        private void ShowContextMenu(Point? screenPoint)
        {
            int x, y;
            if (screenPoint is Point sp) { x = (int)sp.X; y = (int)sp.Y; }
            else { Native.GetCursorPos(out var pt); x = pt.X; y = pt.Y; }

            var sel = _vm.Selected.ToList();
            List<FileItem> group = null;
            string parent = _vm.Path;
            if (sel.Count > 0)
            {
                // the shell menu needs items from one folder
                parent = Path.GetDirectoryName(sel[0].FullPath.TrimEnd('\\'));
                group = sel.Where(s => string.Equals(Path.GetDirectoryName(s.FullPath.TrimEnd('\\')), parent, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            if (_vm.IsThisPC && group == null)
            {
                var cm0 = new ContextMenu();
                cm0.Items.Add(MainWindow.MI(Loc.T("Refresh"), "", () => _vm.Refresh()));
                cm0.Items.Add(MainWindow.MI(Loc.T("OpenInExplorer"), "", () => FileOps.OpenInExplorer("shell:MyComputerFolder")));
                cm0.IsOpen = true;
                return;
            }

            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);   // Ctrl + right-click: the second menu (Alt closes menus in Windows)
            // a menu built in the menu builder wins for its own kind; the others keep the chosen style
            var full = shift ? null : MenuCatalog.FullFor(AppState.Settings, group, alt && group != null);
            if (full != null) ShowCustomMenu(group, parent, x, y, alt, full);
            else if (alt && group != null) ShowCustomMenu(group, parent, x, y, alt: true);   // second menu (Settings › Context menu › Ctrl)
            else if (AppState.Settings.ContextMenuMode == "windows" || shift) ShowWindowsMenu(group, parent, x, y, shift);
            else ShowCustomMenu(group, parent, x, y);
        }

        // ---------------- classic Windows menu (with WinXplorer extras on top) ----------------
        private void ShowWindowsMenu(List<FileItem> group, string parent, int x, int y, bool extended)
        {
            var main = MainWindow.Instance;
            var hwnd = main.Hwnd;
            var custom = new List<ShellContextMenu.CustomItem>();
            if (group != null)
            {
                var one = group.Count == 1 ? group[0] : null;
                if (one != null && one.IsDirectory)
                {
                    custom.Add(new(Loc.T("OpenNewTab"), () => main.OpenInNewTab(one.FullPath, true)));
                    custom.Add(new(Loc.T("OpenOtherPane"), () => main.OpenInOtherPane(_vm, one.FullPath)));
                    custom.Add(new(Loc.T("PinToSidebar"), () => main.PinToSidebar(one.FullPath)));
                    custom.Add(new(Loc.T("OpenTerminalHere"), () => FileOps.OpenTerminal(one.FullPath)));
                }
                if (one != null && !one.IsDirectory)
                    custom.Add(new(Loc.T("QuickView"), () => main.ShowViewer(_vm, one)));
                if (_vm.IsRecursiveSearch && one != null)
                    custom.Add(new(Loc.T("OpenLocation"), () => _vm.Navigate(one.FullPath)));
                custom.Add(new(Loc.T("CopyPath"), () => FileOps.SetClipboardText(string.Join(Environment.NewLine, group.Select(g => g.FullPath)))));
                ShellContextMenu.Show(hwnd, group.Select(g => g.FullPath).ToArray(), parent, custom, verb => InterceptVerb(verb, group), x, y, extended);
            }
            else
            {
                var path = _vm.Path;
                custom.Add(new(Loc.T("Refresh"), () => _vm.Refresh()));
                if (UndoService.CanUndo) custom.Add(new(Loc.T("Undo") + "\tCtrl+Z", UndoService.Undo));
                custom.Add(new(Loc.T("NewFolder"), () => _vm.NewFolder()));
                if (FileOps.CanPaste()) custom.Add(new(Loc.T("Paste"), () => FileOps.Paste(path)));
                custom.Add(new(Loc.T("OpenTerminalHere"), () => FileOps.OpenTerminal(path)));
                custom.Add(new(Loc.T("CopyPath"), () => FileOps.SetClipboardText(path)));
                custom.Add(new(Loc.T("PinToSidebar"), () => main.PinToSidebar(path)));
                ShellContextMenu.Show(hwnd, null, path, custom, verb => InterceptVerb(verb, null), x, y, extended);
            }
        }

        private bool InterceptVerb(string verb, List<FileItem> items)
        {
            switch (verb?.ToLowerInvariant())
            {
                case "rename":
                    _vm.Rename(); return true;
                case "open":
                case "explore":
                    if (items != null && items.Count > 0 && items.All(i => i.IsDirectory))
                    {
                        if (items.Count == 1) _vm.Navigate(items[0].FullPath);
                        else foreach (var i in items) MainWindow.Instance.OpenInNewTab(i.FullPath);
                        return true;
                    }
                    return false;
                case "opennewwindow":
                case "opennewtab":
                    if (items != null && items.All(i => i.IsDirectory))
                    {
                        foreach (var i in items) MainWindow.Instance.OpenInNewTab(i.FullPath, true);
                        return true;
                    }
                    return false;
                case "newfolder":
                    _vm.NewFolder(); return true;
                case "refresh":
                    _vm.Refresh(); return true;
            }
            return false;
        }

        // ---------------- WinXplorer menu (layout from Settings › Context menu) ----------------
        private void ShowCustomMenu(List<FileItem> group, string parent, int x, int y, bool alt = false, FullMenu full = null)
        {
            var main = MainWindow.Instance;
            var s = AppState.Settings;
            List<MenuEntry> layout;
            if (full != null)
            {
                // the builder's menu: what the user placed, then Properties and the Windows menu – always last
                layout = full.Entries.Where(e => !MenuCatalog.FixedLast.Contains(e.Id)).ToList();
                layout.Add(new MenuEntry { Id = MenuCatalog.Separator });
                layout.AddRange(MenuCatalog.FixedLast.Select(id => new MenuEntry { Id = id }));
            }
            else
            {
                layout = (group != null ? MenuCatalog.ItemMenuFor(s, group, alt) : s.BackgroundMenu).ToList();
                // pictures, video, music: the apps Windows recommends for them, right below "Open with"
                if (group != null && !alt && !_vm.InArchive && OpenWithApps.AppliesTo(MenuCatalog.TypeOf(group)))
                {
                    int at = layout.FindIndex(e => e.Id == "openWith" && e.Enabled);
                    if (at < 0) at = layout.FindIndex(e => e.Id == "open" && e.Enabled);
                    layout.Insert(at + 1, new MenuEntry { Id = AutoApps });
                }
            }
            var entries = layout.Where(e => e.Enabled).ToList();
            var paths = group?.Select(g => g.FullPath).ToArray();
            var one = group?.Count == 1 ? group[0] : null;
            var target = group == null ? _vm.Path : one?.IsDirectory == true ? one.FullPath : null; // folder the command acts in

            // Windows items are only read when the layout contains some
            ShellMenu shell = null;
            List<ShellMenuItem> shellItems = null;
            if (entries.Any(e => e.Id.StartsWith(MenuCatalog.ShellPrefix)))
            {
                shell = ShellMenu.Create(main.Hwnd, paths, parent, false);
                shellItems = shell?.ReadItems() ?? new List<ShellMenuItem>();
            }

            var cm = new ContextMenu();
            ScrollViewer.SetVerticalScrollBarVisibility(cm, ScrollBarVisibility.Auto);
            // while the quick row is being filled, the commands' menu items are collected instead of added
            string collecting = null;
            var rowItems = new Dictionary<string, MenuItem>();
            ItemCollection into = cm.Items;   // the menu, or the submenu being filled
            void Add(object o)
            {
                if (collecting == null) into.Add(o);
                else if (o is MenuItem mi) rowItems[collecting] = mi;
            }

            void Emit(MenuEntry e)
            {
                if (e.Id == MenuCatalog.Separator) { Add(new Separator()); return; }
                if (e.Id.StartsWith(MenuCatalog.ShellPrefix))
                {
                    var key = e.Id[MenuCatalog.ShellPrefix.Length..];
                    var si = FindShellItem(shellItems, key);
                    if (si != null) Add(ShellToWpf(si, shell, main, group, x, y));
                    return;
                }
                if (e.Id.StartsWith(CustomCommands.Prefix))
                {
                    // the user's own command (Settings › Context menu)
                    var cc = CustomCommands.Get(e.Id);
                    if (cc == null || _vm.InArchive || _vm.IsThisPC && group == null) return;
                    var cmi = MainWindow.MI(cc.Label, null, () => CustomCommands.Run(cc, paths, _vm.Path));
                    if (CustomCommands.IconOf(cc) is { } ico) cmi.Icon = new Image { Source = ico, Width = 16, Height = 16 };
                    else cmi.Icon = new TextBlock { Text = "\uE756", Style = (Style)Application.Current.Resources["Glyph"], FontSize = 14 };
                    Add(cmi);
                    return;
                }
                if (e.Id == AutoApps)
                {
                    // one item per recommended app; the default program is "Open" already
                    var ext = group[0].Extension;
                    var def = DefaultAppName(ext);
                    foreach (var app in OpenWithApps.For(ext).Where(a => !string.Equals(a.Name, def, StringComparison.CurrentCultureIgnoreCase)).Take(3))
                    {
                        var a = app;
                        var ami = MainWindow.MI(Loc.F("OpenWithName", a.Name), null, () => OpenWithApps.Open(a, paths));
                        if (a.Icon != null) ami.Icon = new Image { Source = a.Icon, Width = 16, Height = 16 };
                        Add(ami);
                    }
                    return;
                }
                if (e.Id.StartsWith(MenuCatalog.NewPrefix))
                {
                    var ext = e.Id[MenuCatalog.NewPrefix.Length..];
                    if (!_vm.InArchive && !_vm.IsThisPC)
                    {
                        var nmi = MainWindow.MI(MenuCatalog.RowLabel(e.Id), null, () => _vm.NewFile(ext));
                        nmi.Icon = new Image { Source = FileIcons.Small(ext), Width = 16, Height = 16 };
                        Add(nmi);
                    }
                    return;
                }
                var cmd = MenuCatalog.Get(e.Id);
                if (cmd == null) return;
                MenuItem M(Action a, string gesture = null) => MainWindow.MI(Loc.T(cmd.LabelKey), cmd.Glyph, a, null, gesture);
                bool hasFiles = group != null;
                // archives are read-only
                if (_vm.InArchive && e.Id is "cut" or "paste" or "rename" or "delete" or "compressZip" or "newFile" or "newFolder" or "newText" or "terminal" or "pinSidebar" or "undo") return;
                switch (e.Id)
                {
                    case "open" when hasFiles:
                        var open = M(OpenSelected, "Enter"); open.FontWeight = FontWeights.SemiBold; Add(open); break;
                    case "openNewTab" when hasFiles && group.All(g => g.IsDirectory):
                        Add(M(() => { foreach (var g in group) main.OpenInNewTab(g.FullPath, group.Count == 1); })); break;
                    case "openOtherPane" when one?.IsDirectory == true:
                        Add(M(() => main.OpenInOtherPane(_vm, one.FullPath))); break;
                    case "openWith" when one != null && !one.IsDirectory:
                        Add(M(() => FileOps.OpenWith(one.FullPath))); break;
                    case "quickView" when one != null && !one.IsDirectory:
                        Add(M(() => main.ShowViewer(_vm, one), "Space")); break;
                    case "openLocation" when one != null && _vm.IsRecursiveSearch:
                        Add(M(() => _vm.Navigate(one.FullPath))); break;
                    case "openTarget" when one != null && !_vm.InArchive && one.Extension == ".lnk":
                        Add(M(() =>
                        {
                            // the shortcut's target: its folder opens with the target selected (a folder target opens itself)
                            var t = Shell.ResolveShortcut(one.FullPath);
                            if (string.IsNullOrEmpty(t) || !File.Exists(t) && !Directory.Exists(t)) { Notify.Show(Loc.T("TargetMissing")); return; }
                            _vm.Navigate(t);
                        })); break;
                    case "openNewWindow" when hasFiles && group.All(g => g.IsDirectory):
                        Add(M(() => { foreach (var g in group) MainWindow.OpenNewWindow(g.FullPath); })); break;
                    case "openInExplorer" when !_vm.InArchive && !(_vm.IsThisPC && !hasFiles):
                        Add(M(() =>
                        {
                            if (one?.IsDirectory == true) FileOps.OpenInExplorer(one.FullPath);
                            else if (hasFiles) FileOps.ShowInExplorer(paths[0]);
                            else FileOps.OpenInExplorer(_vm.Path);
                        })); break;
                    case "runAsAdmin" when one != null && !_vm.InArchive && one.Extension is ".exe" or ".msi" or ".bat" or ".cmd" or ".com" or ".lnk":
                        Add(M(() => FileOps.RunAsAdmin(one.FullPath))); break;
                    case "mount" when one != null && !one.IsDirectory && !_vm.InArchive && FileTypes.DiskImage.Contains(one.Extension):
                        if (MountMenuItem(one.FullPath, cm, Loc.T(cmd.LabelKey), cmd.Glyph) is { } mount) Add(mount);
                        break;
                    case "copyName" when hasFiles:
                        Add(M(() => FileOps.SetClipboardText(string.Join(Environment.NewLine, group.Select(g => g.Name))))); break;
                    case "copyTo" when hasFiles && !group.Any(g => g.IsDrive):
                        Add(M(() => CopyMoveTo(paths, false))); break;
                    case "moveTo" when hasFiles && !_vm.InArchive && !group.Any(g => g.IsDrive):
                        Add(M(() => CopyMoveTo(paths, true))); break;
                    case "selectAll" when !hasFiles:
                        Add(M(() => { Current?.SelectAll(); Current?.Focus(); }, "Ctrl+A")); break;
                    case "cut" when hasFiles:
                        Add(M(() => FileOps.SetClipboard(paths, true), "Ctrl+X")); break;
                    case "copy" when hasFiles:
                        Add(M(() => FileOps.SetClipboard(paths, false), "Ctrl+C")); break;
                    case "paste" when target != null && FileOps.CanPaste():
                        Add(M(() => FileOps.Paste(target), group == null ? "Ctrl+V" : null)); break;
                    case "rename" when one != null && !_vm.InArchive:
                        Add(M(() => _vm.Rename(), "F2")); break;
                    case "rename" when group != null && group.Count > 1 && !group.Any(g => g.IsDrive):
                        Add(MainWindow.MI(Loc.T("RenameMany"), cmd.Glyph, () => _vm.Rename(), null, "F2")); break;
                    case "delete" when hasFiles && !group.Any(g => g.IsDrive):
                        Add(M(() => FileOps.Delete(paths, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)), "Del")); break;
                    case "compressZip" when hasFiles && !group.Any(g => g.IsDrive):
                        Add(M(() => ZipWindow.Start(Window.GetWindow(this), paths, z =>
                        {
                            // still looking at that folder: select the new archive there
                            if (!string.Equals(_vm.Path?.TrimEnd('\\'), Path.GetDirectoryName(z)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return false;
                            _vm.ReloadAndSelect(z);
                            return true;
                        }))); break;
                    case "extractHere" when hasFiles && !_vm.InArchive && group.All(g => !g.IsDirectory && Archives.IsArchive(g.FullPath)):
                        Add(M(() => { foreach (var g in group) FileOps.ExtractArchive(g.FullPath, null, d => _vm.ReloadAndSelect(d)); })); break;
                    case "extractHere" when _vm.InArchive && Archives.Split(_vm.Path) is { } here:
                        // inside an archive: the selection (or everything) into a folder next to the archive
                        var innerSel = hasFiles ? group.Select(g => Archives.Split(g.FullPath)?.inner).Where(i => !string.IsNullOrEmpty(i)).ToList() : null;
                        if (!hasFiles && here.inner.Length > 0) innerSel = new List<string> { here.inner };
                        Add(MainWindow.MI(Loc.T(hasFiles ? "ExtractSelected" : "ExtractAll"), cmd.Glyph, () => FileOps.ExtractArchive(here.archive, innerSel,
                            d => MainWindow.Instance.OpenInNewTab(d)), null)); break;
                    case "shortcutDesktop" when hasFiles && !_vm.InArchive:
                        Add(M(() => FileOps.CreateShortcuts(paths, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)))); break;
                    case "pinStart" or "pinTaskbar" when hasFiles && !_vm.InArchive && paths.Length == 1
                                                      && Path.GetExtension(paths[0]).ToLowerInvariant() is ".exe" or ".lnk":
                        {
                            var p0 = paths[0];
                            var verbs = cmd.Id == "pinStart" ? new[] { "pintostartscreen", "unpinfromstartscreen" } : new[] { "taskbarpin", "taskbarunpin" };
                            Add(M(() =>
                            {
                                var hwnd = new System.Windows.Interop.WindowInteropHelper(Window.GetWindow(this)).Handle;
                                if (!InvokeShellVerb(hwnd, p0, verbs)) Notify.Show(Loc.T("PinBlocked"));
                            }));
                            break;
                        }
                    case "copyPath":
                        Add(M(() => FileOps.SetClipboardText(hasFiles ? string.Join(Environment.NewLine, paths) : _vm.Path), "Ctrl+Shift+C")); break;
                    case "pinSidebar" when target != null:
                        Add(M(() => main.PinToSidebar(target))); break;
                    case "terminal" when target != null:
                        Add(M(() => FileOps.OpenTerminal(target))); break;
                    case "sortBy" when !hasFiles:
                        var sort = M(null); MainWindow.FillSortMenu(sort.Items, _vm); Add(sort); break;
                    case "view" when !hasFiles:
                        var view = M(null); main.FillViewMenu(view.Items, _vm); Add(view); break;
                    case "refresh" when !hasFiles:
                        Add(M(() => _vm.Refresh(), "F5")); break;
                    case "newFile" when !hasFiles:
                        var nf = M(null); main.FillNewMenu(nf.Items, _vm); Add(nf); break;
                    case "undo" when !hasFiles && UndoService.CanUndo:
                        Add(M(UndoService.Undo, "Ctrl+Z")); break;
                    case "newFolder" when !hasFiles:
                        Add(M(() => _vm.NewFolder(), "Ctrl+Shift+N")); break;
                    case "newText" when !hasFiles:
                        Add(M(() => _vm.NewTextFile())); break;
                    case "properties":
                        Add(M(() => FileOps.ShowProperties(one?.FullPath ?? (hasFiles ? paths[0] : _vm.Path)), "Alt+Enter")); break;
                    case "windowsMenu":
                        Add(M(() => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => ShowWindowsMenu(group, parent, x, y, false)))); break;
                }
                        }

            var row = full != null
                ? (full.Row ?? new()).Where(id => MenuCatalog.CanBeInRow(id, group != null) && !MenuCatalog.FixedLast.Contains(id)).Distinct().Take(8).ToList()
                : MenuCatalog.Row(s, group != null);
            var rowSize = full != null ? MenuCatalog.RowSizeFor(row.Count) : s.QuickRowSize;
            foreach (var id in row) { collecting = id; Emit(new MenuEntry { Id = id }); }
            collecting = null;
            // the layout, with submenus: "sub:…" opens one, "subend" closes it (a hidden submenu hides what's inside)
            var stack = new Stack<ItemCollection>();
            var subs = new HashSet<MenuItem>();
            int skip = 0;
            foreach (var e in layout)
            {
                if (MenuCatalog.IsSub(e.Id))
                {
                    if (skip > 0 || !e.Enabled) { skip++; continue; }
                    var sm = MainWindow.MI(MenuCatalog.LabelOf(e), "", null);
                    subs.Add(sm);
                    into.Add(sm);
                    stack.Push(into);
                    into = sm.Items;
                    continue;
                }
                if (e.Id == MenuCatalog.SubEnd)
                {
                    if (skip > 0) skip--;
                    else if (stack.Count > 0) into = stack.Pop();
                    continue;
                }
                if (skip > 0 || !e.Enabled || row.Contains(e.Id)) continue;
                Emit(e);
            }
            into = cm.Items;
            CleanUp(cm.Items, subs);
            if (row.Count > 0)
            {
                cm.Items.Insert(0, QuickRow(cm, row, rowItems, rowSize));
                cm.Items.Insert(1, new Separator());
            }

            // collapse separators that ended up at the edges or next to each other
            CollapseSeparators(cm.Items);
            if (cm.Items.Count == 0) { shell?.Dispose(); ShowWindowsMenu(group, parent, x, y, false); return; }

            cm.Closed += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => shell?.Dispose());
            cm.Placement = PlacementMode.AbsolutePoint;
            double scale = AppState.DpiScale <= 0 ? 1 : AppState.DpiScale;
            cm.HorizontalOffset = x / scale;
            cm.VerticalOffset = y / scale;
            cm.IsOpen = true;
        }

        private const string AutoApps = "autoApps";

        /// <summary>Friendly name of the program that opens this type by default (to leave it out of the app list).</summary>
        private static string DefaultAppName(string ext)
        {
            try
            {
                uint n = 0;
                Native.AssocQueryStringW(0, 4 /* ASSOCSTR_FRIENDLYAPPNAME */, ext, null, null, ref n);
                if (n == 0) return null;
                var sb = new System.Text.StringBuilder((int)n);
                return Native.AssocQueryStringW(0, 4, ext, null, sb, ref n) == 0 ? sb.ToString() : null;
            }
            catch { return null; }
        }

        private static void CollapseSeparators(ItemCollection items)
        {
            for (int i = items.Count - 1; i >= 0; i--)
                if (items[i] is Separator && (i == 0 || i == items.Count - 1 || items[i - 1] is Separator)) items.RemoveAt(i);
        }

        /// <summary>Submenus left with nothing that applies here are dropped; separators inside them are tidied.</summary>
        private static void CleanUp(ItemCollection items, HashSet<MenuItem> subs)
        {
            for (int i = items.Count - 1; i >= 0; i--)
            {
                if (items[i] is not MenuItem mi || !subs.Contains(mi)) continue;
                CleanUp(mi.Items, subs);
                CollapseSeparators(mi.Items);
                if (mi.Items.Count == 0) items.RemoveAt(i);
            }
        }

        /// <summary>Copy to / Move to: pick the destination folder.</summary>
        private void CopyMoveTo(string[] paths, bool move)
        {
            var dest = PickerWindow.Folder(Window.GetWindow(this), Loc.T(move ? "MoveTo" : "CopyTo"), _vm.InArchive || _vm.IsThisPC ? null : _vm.Path);
            if (dest == null) return;
            FileOps.CopyOrMove(paths, dest, move);
        }

        private static ControlTemplate _rowTemplate;

        /// <summary>
        /// The icon row on the first line of the menu. Each button runs the same command its menu item would;
        /// commands that don't apply here (paste with an empty clipboard…) stay visible but disabled, so the row keeps its layout.
        /// </summary>
        private static MenuItem QuickRow(ContextMenu cm, List<string> row, Dictionary<string, MenuItem> items, string size)
        {
            double bw = MenuCatalog.RowButton(size), gs = MenuCatalog.RowGlyph(size);
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2, 4, 2) };
            foreach (var id in row)
            {
                items.TryGetValue(id, out var mi);
                var cmd = MenuCatalog.Get(id);
                var label = mi?.Header as string ?? MenuCatalog.RowLabel(id);
                var gesture = mi?.InputGestureText;
                var b = new Button
                {
                    Style = (Style)Application.Current.Resources["IconBtn"], Width = bw, Height = bw, Margin = new Thickness(0, 0, 2, 0), Focusable = false,
                    ToolTip = string.IsNullOrEmpty(gesture) ? label : label + "   " + gesture, IsEnabled = mi != null && mi.IsEnabled,
                };
                ToolTipService.SetShowOnDisabled(b, true);
                if (id.StartsWith(MenuCatalog.NewPrefix))
                    b.Content = new Image { Source = FileIcons.Small(id[MenuCatalog.NewPrefix.Length..]), Width = gs + 2, Height = gs + 2 };
                else if (mi?.Icon is Image im)
                    b.Content = new Image { Source = im.Source, Width = gs + 2, Height = gs + 2 };
                else if (mi?.Icon is TextBlock gt && cmd == null)
                    b.Content = new TextBlock { Text = gt.Text, Style = (Style)Application.Current.Resources["Glyph"], FontSize = gs };
                else
                    b.Content = new TextBlock { Text = cmd?.Glyph ?? "", Style = (Style)Application.Current.Resources["Glyph"], FontSize = gs };
                if (!b.IsEnabled) b.Opacity = 0.35;
                b.Click += (_, _) =>
                {
                    cm.IsOpen = false;
                    mi?.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, mi));
                };
                panel.Children.Add(b);
            }
            _rowTemplate ??= (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='MenuItem'><ContentPresenter ContentSource='Header'/></ControlTemplate>");
            return new MenuItem { Header = panel, Template = _rowTemplate, StaysOpenOnClick = true, Focusable = false };
        }

        private static ShellMenuItem FindShellItem(List<ShellMenuItem> items, string key)
        {
            if (items == null) return null;
            foreach (var i in items)
                if (!i.IsSeparator && (i.Key == key || "text:" + i.Text?.ToLowerInvariant() == key)) return i;
            return null;
        }

        private object ShellToWpf(ShellMenuItem si, ShellMenu shell, MainWindow main, List<FileItem> group, int x, int y)
        {
            if (si.IsSeparator) return new Separator();
            var mi = new MenuItem { Header = si.Text, IsEnabled = !si.IsDisabled };
            if (si.Icon != null) mi.Icon = new Image { Source = si.Icon, Width = 16, Height = 16 };
            if (si.Children != null)
            {
                foreach (var c in si.Children) mi.Items.Add(ShellToWpf(c, shell, main, group, x, y));
            }
            else
            {
                var id = si.Id; var verb = si.Verb;
                mi.Click += (_, _) =>
                {
                    if (verb != null && InterceptVerb(verb, group)) return;
                    shell.Invoke(main.Hwnd, id, x, y);
                };
            }
            return mi;
        }
    
        /// <summary>Runs one of Windows' own menu commands for a file (by verb), e.g. Pin to Start. False when Windows doesn't offer it.</summary>
        private static bool InvokeShellVerb(IntPtr hwnd, string path, string[] verbs)
        {
            using var m = ShellMenu.Create(hwnd, new[] { path }, Path.GetDirectoryName(path), false);
            if (m == null) return false;
            IEnumerable<ShellMenuItem> All(IEnumerable<ShellMenuItem> l) => l.SelectMany(x => new[] { x }.Concat(x.Children == null ? Enumerable.Empty<ShellMenuItem>() : All(x.Children)));
            var it = All(m.ReadItems()).FirstOrDefault(x => x.Verb != null && verbs.Contains(x.Verb.ToLowerInvariant()));
            if (it == null) return false;
            m.Invoke(hwnd, it.Id, 0, 0);
            return true;
        }
}
}
