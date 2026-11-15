using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinXplorer.Models;
using WinXplorer.Services;
using WinXplorer.Views;

namespace WinXplorer
{
    /// <summary>
    /// WinXplorer's own "choose a folder / choose a file" window (instead of Windows' dialog): the sidebar with your
    /// pinned and frequent folders and drives on the left, the folder's contents in the middle, an address box on top,
    /// and – when picking files – the file type at the bottom. No tabs: pick, confirm, done.
    /// </summary>
    public sealed class PickerWindow : Window
    {
        /// <summary>One entry of the file type list: "Programs" + "*.exe;*.lnk".</summary>
        public sealed record Filter(string Label, string Patterns);

        private readonly bool _files, _multi;
        private readonly List<Filter> _filters;
        private readonly TextBox _address = new() { Height = 30 };
        private readonly TextBox _name = new() { Height = 30 };
        private readonly ComboBox _type = new() { MinWidth = 220, Height = 30, Margin = new Thickness(8, 0, 0, 0) };
        private readonly ListBox _side = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        private readonly ListBox _list = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent, SelectionMode = SelectionMode.Extended };
        private readonly TextBlock _empty = new() { Margin = new Thickness(12), Visibility = Visibility.Collapsed };
        private readonly Button _ok, _upBtn, _backBtn;
        private readonly Stack<string> _history = new();
        private string _path;
        private CancellationTokenSource _cts;
        private string[] _result;

        /// <summary>Choose one (or several) folders. Null when cancelled.</summary>
        public static string[] Folders(Window owner, string title, string start, bool multi = false) =>
            Show(new PickerWindow(title, false, multi, null), owner, start);

        /// <summary>Choose one folder. Null when cancelled.</summary>
        public static string Folder(Window owner, string title, string start) => Folders(owner, title, start)?.FirstOrDefault();

        /// <summary>Choose file(s); <paramref name="filters"/> fills the file type list at the bottom (first = default).</summary>
        public static string[] Files(Window owner, string title, string start, IEnumerable<Filter> filters, bool multi = false) =>
            Show(new PickerWindow(title, true, multi, filters?.ToList()), owner, start);

        private static string[] Show(PickerWindow w, Window owner, string start)
        {
            if (owner != null && owner.IsVisible) { w.Owner = owner; w.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
            else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            w.Loaded += (_, _) => w.Go(Valid(start) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), false);
            return w.ShowDialog() == true ? w._result : null;
        }

        private static string Valid(string p)
        {
            if (p == null) return null;
            if (p == FileSystem.ThisPC) return p;
            try { return Directory.Exists(p) ? p : File.Exists(p) ? Path.GetDirectoryName(p) : null; } catch { return null; }
        }

        private PickerWindow(string title, bool files, bool multi, List<Filter> filters)
        {
            _files = files; _multi = multi;
            _filters = filters is { Count: > 0 } ? filters : new List<Filter> { new(Loc.T("AllFiles"), "*.*") };
            Title = title ?? Loc.T(files ? "PickFile" : "PickFolder");
            var wa = SystemParameters.WorkArea;
            Width = Math.Min(940, wa.Width - 40); Height = Math.Min(620, wa.Height - 40); MinWidth = 560; MinHeight = 380;
            ShowInTaskbar = false;
            Icon = App.LoadAppIcon();
            SourceInitialized += (_, _) => ThemeManager.ApplyWindowFrame(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            SetResourceReference(BackgroundProperty, "BarBrush");
            SetResourceReference(ForegroundProperty, "FgBrush");
            SetResourceReference(FontFamilyProperty, "UiFont");
            if (!_multi) _list.SelectionMode = SelectionMode.Single;

            // ---- top: back, up, address, new folder ----
            _backBtn = IconButton("", Loc.T("HkBack"), () => { if (_history.Count > 0) Go(_history.Pop(), false); });
            _upBtn = IconButton("", Loc.T("HkUp"), () => { if (!string.IsNullOrEmpty(_path)) Go(FileSystem.GetParent(_path)); });
            var newFolder = IconButton("", Loc.T("NewFolder"), NewFolder);
            _address.Style = (Style)FindResource("InputBox");
            _address.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                var p = FileSystem.Resolve(_address.Text, _path);
                if (p == FileSystem.ThisPC || Directory.Exists(p)) Go(p);
                else if (_files && File.Exists(p)) Finish(new[] { p });
                else { _address.Text = _path == FileSystem.ThisPC ? Loc.T("ThisPC") : _path; System.Media.SystemSounds.Beep.Play(); }
                e.Handled = true;
            };
            var top = new DockPanel { Margin = new Thickness(12, 12, 12, 8) };
            top.Children.Add(_backBtn); top.Children.Add(_upBtn);
            DockPanel.SetDock(newFolder, Dock.Right); top.Children.Add(newFolder);
            _address.Margin = new Thickness(6, 0, 6, 0);
            top.Children.Add(_address);

            // ---- left: the sidebar as set up in WinXplorer (pinned, frequent, This PC, drives) ----
            ScrollViewer.SetHorizontalScrollBarVisibility(_side, ScrollBarVisibility.Disabled);
            _side.ItemContainerStyle = (Style)FindResource("EditorItem");
            _side.SetResourceReference(ForegroundProperty, "FgBrush");
            _side.SelectionChanged += (_, _) =>
            {
                if (_suppressSide) return;
                if ((_side.SelectedItem as ListBoxItem)?.Tag is string p) Go(p);
            };
            FillSide();
            var sideBorder = new Border { BorderThickness = new Thickness(0, 0, 1, 0), Child = _side, Padding = new Thickness(6, 0, 6, 0) };
            sideBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            sideBorder.SetResourceReference(Border.BackgroundProperty, "SidebarBrush");

            // ---- middle: folders (and files when picking files) ----
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
            VirtualizingPanel.SetIsVirtualizing(_list, true);
            _list.ItemContainerStyle = (Style)FindResource("EditorItem");
            _list.SetResourceReference(ForegroundProperty, "FgBrush");
            _list.MouseDoubleClick += (_, e) => { if (ItemFrom(e.OriginalSource) is FileItem f) Activate(f); };
            _list.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && _list.SelectedItem is ListBoxItem { Tag: FileItem f }) { Activate(f); e.Handled = true; }
                else if (e.Key == Key.Back) { _upBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); e.Handled = true; }
            };
            _list.SelectionChanged += (_, _) => SelectionToName();
            _empty.SetResourceReference(TextBlock.ForegroundProperty, "FgDimBrush");
            var middle = new Grid();
            middle.Children.Add(_list);
            middle.Children.Add(_empty);
            var content = new Border { Child = middle };
            content.SetResourceReference(Border.BackgroundProperty, "ContentBrush");

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.Children.Add(sideBorder);
            Grid.SetColumn(content, 1); body.Children.Add(content);

            // ---- bottom: name, file type, OK / Cancel ----
            _name.Style = (Style)FindResource("InputBox");
            _name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Accept(); e.Handled = true; } };
            foreach (var f in _filters) _type.Items.Add(new ComboBoxItem { Content = f.Label + "  (" + f.Patterns.Replace(";", ", ") + ")", Tag = f });
            _type.SelectedIndex = 0;
            _type.SelectionChanged += (_, _) => Go(_path, false);
            _ok = new Button { Content = Loc.T(_files ? "PickOpen" : "PickSelectFolder"), Style = (Style)FindResource("AccentBtn"), MinWidth = 120, Margin = new Thickness(8, 0, 8, 0), IsDefault = false };
            _ok.Click += (_, _) => Accept();
            var cancel = new Button { Content = Loc.T("Cancel"), Style = (Style)FindResource("SubtleBtn"), MinWidth = 90, IsCancel = true, Padding = new Thickness(12, 4, 12, 4) };
            var bottom = new DockPanel { Margin = new Thickness(12, 10, 12, 12) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            if (_files) buttons.Children.Add(_type);
            buttons.Children.Add(_ok); buttons.Children.Add(cancel);
            DockPanel.SetDock(buttons, Dock.Right); bottom.Children.Add(buttons);
            var nameLabel = new TextBlock { Text = Loc.T(_files ? "PickFileName" : "PickFolderName"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            bottom.Children.Add(nameLabel);
            bottom.Children.Add(_name);

            var root = new DockPanel();
            DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
            var line = new Border { Height = 1 }; line.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
            DockPanel.SetDock(line, Dock.Top); root.Children.Add(line);
            DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
            var line2 = new Border { Height = 1 }; line2.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
            DockPanel.SetDock(line2, Dock.Bottom); root.Children.Add(line2);
            root.Children.Add(body);
            Content = root;
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Back && Keyboard.FocusedElement is not TextBox) { _upBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); e.Handled = true; }
                else if (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt) { _backBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); e.Handled = true; }
            };
            Closed += (_, _) => _cts?.Cancel();
        }

        private Button IconButton(string glyph, string tip, Action a)
        {
            var b = new Button { Style = (Style)FindResource("IconBtn"), Width = 32, Height = 30, ToolTip = tip, Margin = new Thickness(0, 0, 2, 0) };
            b.Content = new TextBlock { Text = glyph, Style = (Style)FindResource("Glyph"), FontSize = 14 };
            b.Click += (_, _) => a();
            return b;
        }

        private void FillSide()
        {
            _side.Items.Clear();
            var items = MainWindow.Instance?.VM?.Sidebar?.Items;
            void Head(string text)
            {
                var t = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 11, Opacity = 0.7, Margin = new Thickness(4, _side.Items.Count == 0 ? 6 : 12, 0, 2) };
                _side.Items.Add(new ListBoxItem { Content = t, IsEnabled = false, Focusable = false });
            }
            void Entry(string name, ImageSource icon, string path, string tip)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 3, 2, 3) };
                if (icon != null) sp.Children.Add(new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) });
                else sp.Children.Add(new TextBlock { Text = path == FileSystem.ThisPC ? "" : "", Style = (Style)FindResource("Glyph"), FontSize = 14, Width = 16, Margin = new Thickness(0, 0, 8, 0) });
                sp.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                _side.Items.Add(new ListBoxItem { Content = sp, Tag = path, ToolTip = tip });
            }
            if (items == null || items.Count == 0)
            {
                Entry(Loc.T("ThisPC"), null, FileSystem.ThisPC, null);
                foreach (var d in FileSystem.Drives()) Entry(d.Name, IconService.GetPathIcon(d.FullPath), d.FullPath, d.FullPath);
                return;
            }
            foreach (var i in items)
            {
                if (i.IsSeparator)
                {
                    if (i.IsHeader) Head(i.Header);
                    else if (_side.Items.Count > 0) _side.Items.Add(new ListBoxItem { Content = new Border { Height = 1, Opacity = 0.3, Background = Brushes.Gray, Margin = new Thickness(4, 4, 4, 4) }, IsEnabled = false, Focusable = false });
                    continue;
                }
                if (i.Kind == SidebarKind.App) continue;   // programs can't be opened as folders
                Entry(i.Name, i.Icon, i.Path, i.ToolTip);
            }
        }

        // ---------------- navigation ----------------
        private async void Go(string path, bool remember = true)
        {
            if (path == null) return;
            if (remember && _path != null && !string.Equals(_path, path, StringComparison.OrdinalIgnoreCase)) _history.Push(_path);
            _path = path;
            _address.Text = path == FileSystem.ThisPC ? Loc.T("ThisPC") : path;
            _upBtn.IsEnabled = path != FileSystem.ThisPC;
            _backBtn.IsEnabled = _history.Count > 0;
            _cts?.Cancel();
            var cts = _cts = new CancellationTokenSource();
            List<FileItem> found;
            var filter = (_type.SelectedItem as ComboBoxItem)?.Tag as Filter ?? _filters[0];
            var patterns = filter.Patterns.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            try
            {
                found = await Task.Run(() =>
                {
                    if (path == FileSystem.ThisPC) return FileSystem.Drives();
                    var all = FileSystem.List(path, AppState.Settings.ShowHidden, cts.Token);
                    return all.Where(f => f.IsDirectory || _files && Matches(f.Name, patterns))
                              .OrderBy(f => !f.IsDirectory).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                }, cts.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { found = new(); _empty.Text = ex.Message; }
            if (cts.IsCancellationRequested) return;
            _list.Items.Clear();
            foreach (var f in found) _list.Items.Add(Row(f));
            _empty.Visibility = found.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (found.Count == 0 && string.IsNullOrEmpty(_empty.Text)) _empty.Text = Loc.T("PickEmpty");
            if (found.Count > 0) _empty.Text = "";
            // the sidebar shows where we are
            var hit = _side.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is string p && string.Equals(p.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            if (!ReferenceEquals(_side.SelectedItem, hit)) { _suppressSide = true; _side.SelectedItem = hit; _suppressSide = false; }
            SelectionToName();
        }

        private bool _suppressSide;

        private static bool Matches(string name, string[] patterns)
        {
            foreach (var p in patterns)
            {
                if (p is "*" or "*.*") return true;
                if (p.StartsWith("*.") && p.IndexOfAny(new[] { '*', '?' }, 2) < 0)
                {
                    if (name.EndsWith(p[1..], StringComparison.OrdinalIgnoreCase)) return true;
                }
                else if (System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(p, name)) return true;
            }
            return false;
        }

        private ListBoxItem Row(FileItem f)
        {
            var g = new Grid { Margin = new Thickness(4, 2, 4, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            var icon = new Image { Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left };
            icon.Source = f.IsDrive ? IconService.GetPathIcon(f.FullPath) : IconService.GetIcon(f, out _);
            g.Children.Add(icon);
            var name = new TextBlock { Text = f.IsDrive ? FileSystem.DisplayName(f.FullPath) : f.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            if (f.IsHidden) name.Opacity = 0.6;
            Grid.SetColumn(name, 1); g.Children.Add(name);
            var date = new TextBlock { Text = f.IsDrive ? "" : f.ModifiedText, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(date, 2); g.Children.Add(date);
            var size = new TextBlock { Text = f.IsDrive ? (f.HasUsage ? Loc.Free(FileItem.FormatSize(f.Size)) : "") : f.IsDirectory ? "" : FileItem.FormatSize(f.Size), Opacity = 0.65, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(size, 3); g.Children.Add(size);
            return new ListBoxItem { Content = g, Tag = f };
        }

        private static FileItem ItemFrom(object src)
        {
            for (var d = src as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
                if (d is ListBoxItem li) return li.Tag as FileItem;
            return null;
        }

        private void Activate(FileItem f)
        {
            if (f.IsDirectory || f.IsDrive) Go(f.FullPath);
            else if (_files) Finish(new[] { f.FullPath });
        }

        private IEnumerable<FileItem> Selected => _list.SelectedItems.OfType<ListBoxItem>().Select(i => i.Tag).OfType<FileItem>();

        private void SelectionToName()
        {
            var sel = Selected.Where(f => _files ? !f.IsDirectory && !f.IsDrive : true).ToList();
            if (sel.Count == 1) _name.Text = sel[0].IsDrive ? sel[0].FullPath : sel[0].Name;
            else if (sel.Count > 1) _name.Text = string.Join(" ", sel.Select(s => "\"" + s.Name + "\""));
            else if (!_files) _name.Text = _path == FileSystem.ThisPC ? "" : FileSystem.DisplayName(_path);
            _ok.IsEnabled = _files || _path != FileSystem.ThisPC || sel.Count > 0;
        }

        private void NewFolder()
        {
            if (string.IsNullOrEmpty(_path)) return;
            var name = InputDialog.Ask(this, Loc.T("NewFolder"), Loc.T("NewFolder"), Loc.T("NewFolder"));
            if (string.IsNullOrWhiteSpace(name)) return;
            try
            {
                var p = Path.Combine(_path, name.Trim());
                Directory.CreateDirectory(p);
                Go(p);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "WinXplorer", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        private void Accept()
        {
            var sel = Selected.ToList();
            if (_files)
            {
                var files = sel.Where(f => !f.IsDirectory && !f.IsDrive).Select(f => f.FullPath).ToList();
                if (files.Count == 0)
                {
                    // a typed name or path
                    var typed = _name.Text.Trim().Trim('"');
                    if (typed.Length == 0)
                    {
                        if (sel.Count == 1) Activate(sel[0]);
                        return;
                    }
                    var p = FileSystem.Resolve(typed, _path);
                    if (Directory.Exists(p)) { Go(p); return; }
                    if (!File.Exists(p)) { System.Media.SystemSounds.Beep.Play(); return; }
                    files.Add(p);
                }
                Finish(files.ToArray());
            }
            else
            {
                var dirs = sel.Where(f => f.IsDirectory || f.IsDrive).Select(f => f.FullPath).ToList();
                if (dirs.Count == 0)
                {
                    if (_path == FileSystem.ThisPC) return;
                    dirs.Add(_path);
                }
                Finish(dirs.ToArray());
            }
        }

        private void Finish(string[] paths)
        {
            _result = _multi ? paths : paths.Take(1).ToArray();
            DialogResult = true;
        }
    }
}
