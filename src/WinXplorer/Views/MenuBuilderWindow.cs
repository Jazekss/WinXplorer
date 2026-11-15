using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using WinXplorer.Interop;
using WinXplorer.Models;
using WinXplorer.Services;
using WinXplorer.Views;

namespace WinXplorer
{
    /// <summary>
    /// The context-menu builder: every command on the left, your apps and own commands next to it, and all menus
    /// (files, folders, images, video… Ctrl menu, folder background) side by side on the right. Drag a command into a
    /// menu – onto the top row it becomes an icon. Apply turns the result into the "Fully custom" menu style.
    /// </summary>
    public sealed class MenuBuilderWindow : Window
    {
        private const string Fmt = "WinXplorer.BuilderItem";
        private const int RowSlots = 8, MinSlots = 5, FreeSlots = 2;
        private const double BlockWidth = 304;

        /// <summary>What is being dragged: a command from the palette, or an entry / row icon of a menu.</summary>
        private sealed class Payload
        {
            public string Id, Label;
            public string Block;        // null = from the palette
            public int Index = -1;
            public bool InRow;
            public MenuScope Scope = MenuScope.Both;
        }

        private readonly Dictionary<string, FullMenu> _menus;
        private readonly Dictionary<string, Border> _blocks = new();
        private readonly Dictionary<string, ImageSource> _shellIcons = new();
        private List<(ShellMenuItem item, MenuScope scope)> _shell;
        private readonly StackPanel _palette = new();
        private readonly StackPanel _apps = new();
        private readonly Grid _columns = new();
        private readonly TextBox _search = new() { Height = 30, Margin = new Thickness(0, 0, 0, 8) };
        private bool _dirty, _closingOk;

        /// <summary>Opens the builder; true when the user applied it (the "Fully custom" style is on).</summary>
        public static bool Open(Window owner)
        {
            var w = new MenuBuilderWindow();
            if (owner != null && owner.IsVisible) w.Owner = owner;
            return w.ShowDialog() == true;
        }

        private MenuBuilderWindow()
        {
            // work on a copy: nothing changes until Apply
            _menus = new Dictionary<string, FullMenu>();
            foreach (var k in MenuCatalog.FullKeys)
            {
                var src = AppState.Settings.FullMenus != null && AppState.Settings.FullMenus.TryGetValue(k, out var m) ? m : null;
                _menus[k] = new FullMenu
                {
                    Row = src?.Row?.Where(id => !MenuCatalog.FixedLast.Contains(id)).ToList() ?? new(),
                    Entries = src?.Entries?.Where(e => !MenuCatalog.FixedLast.Contains(e.Id)).Select(e => new MenuEntry { Id = e.Id, Label = e.Label, Enabled = true }).ToList() ?? new(),
                };
            }

            Title = Loc.T("MenuBuilderTitle");
            var wa = SystemParameters.WorkArea;
            // big by default, never past the screen
            Width = Math.Max(800, wa.Width * 0.92); Height = Math.Max(560, wa.Height * 0.9);
            Width = Math.Min(Width, wa.Width); Height = Math.Min(Height, wa.Height);
            Left = wa.Left + (wa.Width - Width) / 2; Top = wa.Top + (wa.Height - Height) / 2;
            MinWidth = 760; MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.Manual;
            ShowInTaskbar = false;
            Icon = App.LoadAppIcon();
            SourceInitialized += (_, _) => ThemeManager.ApplyWindowFrame(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            SetResourceReference(BackgroundProperty, "BarBrush");
            SetResourceReference(ForegroundProperty, "FgBrush");
            SetResourceReference(FontFamilyProperty, "UiFont");

            // ---- column 1: all commands ----
            _search.Style = (Style)FindResource("InputBox");
            _search.TextChanged += (_, _) => FillPalette();
            var paletteScroll = new ScrollViewer { Content = _palette, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var col1 = new DockPanel();
            var h1 = Heading(Loc.T("BuilderAll")); DockPanel.SetDock(h1, Dock.Top); col1.Children.Add(h1);
            DockPanel.SetDock(_search, Dock.Top); col1.Children.Add(_search);
            col1.Children.Add(paletteScroll);

            // ---- column 2: your apps and own commands ----
            var col2 = new DockPanel();
            var h2 = Heading(Loc.T("BuilderApps")); DockPanel.SetDock(h2, Dock.Top); col2.Children.Add(h2);
            var d2 = Dim(Loc.T("BuilderAppsDesc")); d2.Margin = new Thickness(0, 0, 0, 8); DockPanel.SetDock(d2, Dock.Top); col2.Children.Add(d2);
            var addBtns = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            addBtns.Children.Add(Btn(Loc.T("AddOpenWithApp"), "", AddApp));
            addBtns.Children.Add(Btn(Loc.T("NewCommand"), "", NewCommand));
            DockPanel.SetDock(addBtns, Dock.Bottom); col2.Children.Add(addBtns);
            col2.Children.Add(new ScrollViewer { Content = _apps, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });

            // ---- column 3: all menus, side by side and stacked ----
            var menusScroll = new ScrollViewer { Content = _columns, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var col3 = new DockPanel();
            var h3 = Heading(Loc.T("BuilderMenus")); DockPanel.SetDock(h3, Dock.Top); col3.Children.Add(h3);
            var hint = Dim(Loc.T("BuilderHint")); hint.Margin = new Thickness(0, 0, 0, 10); DockPanel.SetDock(hint, Dock.Top); col3.Children.Add(hint);
            col3.Children.Add(menusScroll);
            menusScroll.SizeChanged += (_, e) => { if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 1) Arrange(e.NewSize.Width); };

            var body = new Grid { Margin = new Thickness(16, 14, 16, 0) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.Children.Add(Panel(col1));
            var p2 = Panel(col2); Grid.SetColumn(p2, 2); body.Children.Add(p2);
            Grid.SetColumn(col3, 4); body.Children.Add(col3);

            // ---- bottom: Apply / Cancel ----
            var apply = new Button { Content = Loc.T("Apply"), Style = (Style)FindResource("AccentBtn"), MinWidth = 110, Margin = new Thickness(0, 0, 8, 0) };
            apply.Click += (_, _) => Apply();
            var cancel = new Button { Content = Loc.T("Cancel"), Style = (Style)FindResource("SubtleBtn"), MinWidth = 100, Padding = new Thickness(12, 4, 12, 4) };
            cancel.Click += (_, _) => Close();
            var bottom = new DockPanel { Margin = new Thickness(16, 12, 16, 14), LastChildFill = false };
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(apply); right.Children.Add(cancel);
            DockPanel.SetDock(right, Dock.Right); bottom.Children.Add(right);
            var note = Dim(Loc.T("BuilderApplyNote")); note.VerticalAlignment = VerticalAlignment.Center; note.MaxWidth = 700;
            bottom.Children.Add(note);

            var root = new DockPanel();
            DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
            root.Children.Add(body);
            Content = root;

            PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
            Closing += (_, e) =>
            {
                if (_closingOk || !_dirty) return;
                // don't lose the work by accident
                if (!InputDialog.Confirm(this, Loc.T("MenuBuilderTitle"), Loc.T("BuilderDiscard"), Loc.T("BuilderDiscardOk"))) e.Cancel = true;
            };

            foreach (var k in MenuCatalog.FullKeys) _blocks[k] = new Border { Width = BlockWidth, Margin = new Thickness(0, 0, 12, 12) };
            FillPalette();
            FillApps();
            foreach (var k in MenuCatalog.FullKeys) FillBlock(k);
            Loaded += (_, _) => Arrange(menusScroll.ActualWidth);
        }

        // ================= layout helpers =================
        private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 0, 0, 8) };

        private static TextBlock Dim(string text, double size = 12)
        {
            var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, "FgDimBrush");
            return t;
        }

        private Border Panel(UIElement child)
        {
            var b = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(12), Child = child };
            b.SetResourceReference(Border.BackgroundProperty, "ContentBrush");
            b.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            return b;
        }

        private Button Btn(string text, string glyph, Action click)
        {
            var b = new Button { Style = (Style)FindResource("SubtleBtn"), Margin = new Thickness(0, 0, 0, 6), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = glyph, Style = (Style)FindResource("Glyph"), FontSize = 13, Margin = new Thickness(0, 0, 8, 0) });
            sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            b.Content = sp;
            b.Click += (_, _) => click();
            return b;
        }

        private Button SmallBtn(string glyph, string tip, Action a, double size = 22)
        {
            var b = new Button { Style = (Style)FindResource("IconBtn"), Width = size, Height = size, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), ToolTip = tip, Focusable = false };
            b.Content = new TextBlock { Text = glyph, Style = (Style)FindResource("Glyph"), FontSize = size * 0.45 };
            b.Click += (_, _) => a();
            return b;
        }

        /// <summary>The icon of a command: its glyph, a program's own icon, a file type icon, or Windows' icon.</summary>
        private FrameworkElement IconOf(string id, double size)
        {
            ImageSource img = null;
            string glyph = null;
            if (id.StartsWith(MenuCatalog.NewPrefix)) img = FileIcons.Small(id[MenuCatalog.NewPrefix.Length..]);
            else if (id.StartsWith(CustomCommands.Prefix)) { if (CustomCommands.Get(id) is { } c) img = CustomCommands.IconOf(c); glyph = ""; }
            else if (id.StartsWith(MenuCatalog.ShellPrefix)) { _shellIcons.TryGetValue(id, out img); glyph = ""; }
            else if (MenuCatalog.IsSub(id)) glyph = "";
            else if (id == MenuCatalog.Separator) glyph = "";
            else glyph = MenuCatalog.Get(id)?.Glyph ?? "";
            if (img != null) return new Image { Source = img, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
            var t = new TextBlock { Text = glyph ?? "", Style = (Style)FindResource("Glyph"), FontSize = size - 2, Width = size, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
            return t;
        }

        private static string LabelOf(string id, string label) => MenuCatalog.LabelOf(new MenuEntry { Id = id, Label = label });

        // ================= drag & drop plumbing =================
        private Point _downAt;
        private Func<Payload> _downPayload;
        private FrameworkElement _downSource;

        private void Draggable(FrameworkElement el, Func<Payload> payload)
        {
            el.Cursor = Cursors.SizeAll;
            el.PreviewMouseLeftButtonDown += (_, e) =>
            {
                for (var d = e.OriginalSource as DependencyObject; d != null && d != el; d = VisualTreeHelper.GetParent(d))
                    if (d is ButtonBase) return;   // the × / ✎ buttons stay clickable
                _downAt = e.GetPosition(this); _downPayload = payload; _downSource = el;
            };
            el.PreviewMouseMove += (_, e) =>
            {
                if (_downSource != el || e.LeftButton != MouseButtonState.Pressed) return;
                var d = e.GetPosition(this) - _downAt;
                if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                var p = _downPayload();
                _downSource = null;
                if (p == null) return;
                el.Opacity = 0.5;
                try { DragDrop.DoDragDrop(el, new DataObject(Fmt, p), DragDropEffects.Copy | DragDropEffects.Move); }
                finally { el.Opacity = 1; }
            };
            el.PreviewMouseLeftButtonUp += (_, _) => _downSource = null;
        }

        private void DropTarget(Border el, Func<Payload, bool> can, Action<Payload> drop)
        {
            el.AllowDrop = true;
            void Mark(bool on)
            {
                if (on) { el.SetResourceReference(Border.BorderBrushProperty, "AccentBrush"); el.SetResourceReference(Border.BackgroundProperty, "SelectedBrush"); }
                else RestoreLook(el);
            }
            el.DragEnter += (_, e) => Over(e);
            el.DragOver += (_, e) => Over(e);
            el.DragLeave += (_, e) => { Mark(false); e.Handled = true; };
            el.Drop += (_, e) =>
            {
                Mark(false);
                e.Handled = true;
                if (e.Data.GetData(Fmt) is not Payload p || !can(p)) return;
                // rebuild after the drag has finished (the dragged element may be part of what is rebuilt)
                Dispatcher.BeginInvoke(new Action(() => { drop(p); _dirty = true; }));
            };
            void Over(DragEventArgs e)
            {
                e.Handled = true;
                if (e.Data.GetData(Fmt) is Payload p && can(p))
                {
                    e.Effects = p.Block == null ? DragDropEffects.Copy : DragDropEffects.Move;
                    Mark(true);
                }
                else { e.Effects = DragDropEffects.None; Mark(false); }
            }
        }

        /// <summary>Each slot remembers how it looks when nothing is dragged over it.</summary>
        private static readonly DependencyProperty LookProperty = DependencyProperty.RegisterAttached("Look", typeof(string), typeof(MenuBuilderWindow));

        private static void SetLook(Border b, string look)
        {
            b.SetValue(LookProperty, look);
            RestoreLook(b);
        }

        private static void RestoreLook(Border b)
        {
            var look = (string)b.GetValue(LookProperty);
            b.SetResourceReference(Border.BorderBrushProperty, look == "empty" ? "InputBorderBrush" : "BorderBrush");
            if (look == "filled") b.SetResourceReference(Border.BackgroundProperty, "BarBrush");
            else b.Background = Brushes.Transparent;
        }

        // ================= palette (column 1) =================
        private void FillPalette()
        {
            _palette.Children.Clear();
            var q = _search.Text.Trim();
            bool Match(string text) => q.Length == 0 || (text ?? "").Contains(q, StringComparison.CurrentCultureIgnoreCase);
            void Group(string title, IEnumerable<(string id, string label, MenuScope scope, ImageSource icon)> list)
            {
                var l = list.Where(x => Match(x.label)).ToList();
                if (l.Count == 0) return;
                var h = Dim(title, 11); h.FontWeight = FontWeights.SemiBold; h.Margin = new Thickness(2, _palette.Children.Count == 0 ? 0 : 12, 0, 4);
                _palette.Children.Add(h);
                foreach (var x in l) _palette.Children.Add(PaletteTile(x.id, x.label, x.scope, x.icon));
            }

            Group(Loc.T("BuilderStructure"), new[]
            {
                (MenuCatalog.Separator, Loc.T("Separator"), MenuScope.Both, (ImageSource)null),
                (MenuCatalog.SubPrefix, Loc.T("SubmenuAdd"), MenuScope.Both, (ImageSource)null),
            });
            Group(Loc.T("MenuForItems"), MenuCatalog.Commands.Where(c => !MenuCatalog.FixedLast.Contains(c.Id) && c.Scope.HasFlag(MenuScope.Item) && !c.Scope.HasFlag(MenuScope.Background))
                .Select(c => (c.Id, Loc.T(c.LabelKey), c.Scope, (ImageSource)null)));
            Group(Loc.T("BuilderBoth"), MenuCatalog.Commands.Where(c => !MenuCatalog.FixedLast.Contains(c.Id) && c.Scope == MenuScope.Both)
                .Select(c => (c.Id, Loc.T(c.LabelKey), c.Scope, (ImageSource)null)));
            Group(Loc.T("MenuForBackground"), MenuCatalog.Commands.Where(c => !MenuCatalog.FixedLast.Contains(c.Id) && c.Scope == MenuScope.Background)
                .Select(c => (c.Id, Loc.T(c.LabelKey), c.Scope, (ImageSource)null)));
            Group(Loc.T("BuilderNewFiles"), NewFiles.Enabled.Select(e => (MenuCatalog.NewPrefix + e, MenuCatalog.RowLabel(MenuCatalog.NewPrefix + e), MenuScope.Both, FileIcons.Small(e))));

            var wh = Dim(Loc.T("MenuAddWindows"), 11); wh.FontWeight = FontWeights.SemiBold; wh.Margin = new Thickness(2, 12, 0, 4);
            _palette.Children.Add(wh);
            if (_shell == null)
            {
                var b = Btn(Loc.T("MenuLoadWindows"), "", () =>
                {
                    Mouse.OverrideCursor = Cursors.Wait;
                    try
                    {
                        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                        var items = ShellCatalog.Load(hwnd, false).Select(i => (i, MenuScope.Item)).ToList();
                        foreach (var i in ShellCatalog.Load(hwnd, true))
                            if (!items.Any(x => x.i.Key == i.Key)) items.Add((i, MenuScope.Background));
                        _shell = items;
                        foreach (var (i, _) in _shell) if (i.Icon != null) _shellIcons[MenuCatalog.ShellPrefix + i.Key] = i.Icon;
                    }
                    finally { Mouse.OverrideCursor = null; }
                    FillPalette();
                    foreach (var k in MenuCatalog.FullKeys) FillBlock(k);
                });
                _palette.Children.Add(b);
            }
            else
            {
                foreach (var (i, scope) in _shell.Where(x => Match(x.item.Text)))
                    _palette.Children.Add(PaletteTile(MenuCatalog.ShellPrefix + i.Key, i.Text, scope, i.Icon));
            }
        }

        private FrameworkElement PaletteTile(string id, string label, MenuScope scope, ImageSource icon)
        {
            var b = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 0, 2), Background = Brushes.Transparent };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(icon != null ? new Image { Source = icon, Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left } : IconOf(id, 16).With(f => f.HorizontalAlignment = HorizontalAlignment.Left));
            var t = new TextBlock { Text = (label ?? id).Replace("&", ""), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(t, 1); g.Children.Add(t);
            b.Child = g;
            b.ToolTip = label;
            b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
            b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
            Draggable(b, () => new Payload { Id = id, Label = label, Scope = scope });
            return b;
        }

        // ================= your apps and commands (column 2) =================
        private void FillApps()
        {
            _apps.Children.Clear();
            var all = CustomCommands.All.ToList();
            if (all.Count == 0) { _apps.Children.Add(Dim(Loc.T("BuilderNoApps")).With(t => t.Margin = new Thickness(2, 4, 0, 0))); return; }
            foreach (var cc in all)
            {
                var c = cc;
                var b = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 4, 2, 4), Margin = new Thickness(0, 0, 0, 2), Background = Brushes.Transparent };
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.Children.Add(IconOf(c.Id, 16).With(f => f.HorizontalAlignment = HorizontalAlignment.Left));
                var t = new TextBlock { Text = c.Label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = c.Command };
                Grid.SetColumn(t, 1); g.Children.Add(t);
                var tools = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Hidden };
                tools.Children.Add(SmallBtn("", Loc.T("Edit"), () =>
                {
                    if (!CommandWindow.Edit(this, c)) return;
                    AppState.Save();
                    FillApps(); foreach (var k in MenuCatalog.FullKeys) FillBlock(k);
                }));
                tools.Children.Add(SmallBtn("", Loc.T("Delete"), () =>
                {
                    CustomCommands.Remove(c.Id);
                    AppState.Save();
                    foreach (var m in _menus.Values) { m.Row.Remove(c.Id); m.Entries.RemoveAll(e => e.Id == c.Id); }
                    FillApps(); foreach (var k in MenuCatalog.FullKeys) FillBlock(k);
                }));
                Grid.SetColumn(tools, 2); g.Children.Add(tools);
                b.Child = g;
                b.MouseEnter += (_, _) => { b.SetResourceReference(Border.BackgroundProperty, "HoverBrush"); tools.Visibility = Visibility.Visible; };
                b.MouseLeave += (_, _) => { b.Background = Brushes.Transparent; tools.Visibility = Visibility.Hidden; };
                Draggable(b, () => new Payload { Id = c.Id, Label = c.Label });
                _apps.Children.Add(b);
            }
        }

        private void AddApp()
        {
            var app = AppPickerWindow.Pick(this);
            if (app == null) return;
            var c = new CustomCommand { Label = Loc.F("OpenWithName", app.Name), Command = app.Command };
            if (!CommandWindow.Edit(this, c)) return;
            CustomCommands.Add(c.Label, c.Command);
            AppState.Save();
            FillApps();
        }

        private void NewCommand()
        {
            var c = new CustomCommand();
            if (!CommandWindow.Edit(this, c)) return;
            CustomCommands.Add(c.Label, c.Command);
            AppState.Save();
            FillApps();
        }

        // ================= menus (column 3) =================
        private int _cols;

        /// <summary>As many columns as fit; the menus fill them top to bottom (images above video…), so it stays compact.</summary>
        private void Arrange(double width)
        {
            int cols = Math.Max(1, (int)((width - 4) / (BlockWidth + 12)));
            if (cols == _cols && _columns.Children.Count > 0) return;
            _cols = cols;
            foreach (var b in _blocks.Values) (b.Parent as Panel)?.Children.Remove(b);
            _columns.Children.Clear();
            _columns.ColumnDefinitions.Clear();
            var stacks = new List<StackPanel>();
            for (int i = 0; i < cols; i++)
            {
                _columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var sp = new StackPanel();
                Grid.SetColumn(sp, i);
                _columns.Children.Add(sp);
                stacks.Add(sp);
            }
            // general menus first, then the kinds; spread evenly by order
            int n = MenuCatalog.FullKeys.Length, per = (int)Math.Ceiling(n / (double)cols);
            for (int i = 0; i < n; i++) stacks[Math.Min(cols - 1, i / per)].Children.Add(_blocks[MenuCatalog.FullKeys[i]]);
        }

        private bool IsItems(string key) => key != "background";

        private bool Allowed(string key, Payload p)
        {
            if (p.Id == MenuCatalog.Separator || p.Id == MenuCatalog.SubPrefix || MenuCatalog.IsSub(p.Id) || p.Id == MenuCatalog.SubEnd) return true;
            if (p.Id.StartsWith(MenuCatalog.ShellPrefix) || p.Id.StartsWith(MenuCatalog.NewPrefix) || p.Id.StartsWith(CustomCommands.Prefix))
                return p.Block != null || p.Scope.HasFlag(IsItems(key) ? MenuScope.Item : MenuScope.Background);
            return MenuCatalog.Get(p.Id) is { } c && c.Scope.HasFlag(IsItems(key) ? MenuScope.Item : MenuScope.Background);
        }

        private void FillBlock(string key)
        {
            var m = _menus[key];
            var card = _blocks[key];
            card.CornerRadius = new CornerRadius(8);
            card.BorderThickness = new Thickness(1);
            card.Padding = new Thickness(12, 10, 12, 12);
            card.SetResourceReference(Border.BackgroundProperty, "ContentBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            var sp = new StackPanel();

            // header: icon + name, what it's for, tools
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var tools = new StackPanel { Orientation = Orientation.Horizontal };
            tools.Children.Add(SmallBtn("", Loc.T("BuilderFill"), () =>
            {
                var cur = MenuCatalog.CurrentFor(AppState.Settings, key);
                m.Row = cur.Row; m.Entries = cur.Entries;
                _dirty = true; FillBlock(key);
            }, 26));
            tools.Children.Add(SmallBtn("", Loc.T("BuilderClear"), () => { m.Row.Clear(); m.Entries.Clear(); _dirty = true; FillBlock(key); }, 26));
            DockPanel.SetDock(tools, Dock.Right); head.Children.Add(tools);
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            var glyph = new TextBlock { Text = MenuCatalog.FullGlyph(key), Style = (Style)FindResource("Glyph"), FontSize = 16, Margin = new Thickness(0, 0, 8, 0) };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
            title.Children.Add(glyph);
            title.Children.Add(new TextBlock { Text = MenuCatalog.FullTitle(key), FontWeight = FontWeights.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(title);
            sp.Children.Add(head);
            var sub = Dim((key switch { "item" => Loc.T("BlockItemDesc"), "alt" => Loc.T("MenuAltNote"), "background" => Loc.T("BlockBackgroundDesc"), _ => Loc.F("BlockTypeDesc", MenuCatalog.TypeLabel(key)) })
                          + "  ·  " + Loc.T(m.IsEmpty ? "BuilderEmpty" : "BuilderBuilt"), 11);
            sub.Margin = new Thickness(0, -4, 0, 8);
            sp.Children.Add(sub);

            // the icon row: 8 slots, icons sized by how many there are
            var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            double icon = MenuCatalog.RowGlyph(MenuCatalog.RowSizeFor(m.Row.Count)) + 2;
            double slot = (BlockWidth - 24 - 2 - RowSlots * 3) / RowSlots;
            for (int i = 0; i < RowSlots; i++)
            {
                int at = i;
                var cell = new Border { Width = slot, Height = slot, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 3, 0) };
                if (i < m.Row.Count)
                {
                    var id = m.Row[i];
                    SetLook(cell, "filled");
                    var g = new Grid();
                    g.Children.Add(IconOf(id, icon).With(f => { f.HorizontalAlignment = HorizontalAlignment.Center; f.VerticalAlignment = VerticalAlignment.Center; }));
                    var x = SmallBtn("", Loc.T("Remove"), () => { m.Row.RemoveAt(at); _dirty = true; FillBlock(key); }, 15);
                    x.HorizontalAlignment = HorizontalAlignment.Right; x.VerticalAlignment = VerticalAlignment.Top; x.Margin = new Thickness(0, -5, -5, 0);
                    x.Visibility = Visibility.Hidden;
                    x.SetResourceReference(BackgroundProperty, "PopupBrush");
                    g.Children.Add(x);
                    cell.Child = g;
                    cell.ToolTip = MenuCatalog.RowLabel(id);
                    cell.MouseEnter += (_, _) => x.Visibility = Visibility.Visible;
                    cell.MouseLeave += (_, _) => x.Visibility = Visibility.Hidden;
                    Draggable(cell, () => new Payload { Id = id, Block = key, Index = at, InRow = true });
                }
                else
                {
                    SetLook(cell, "empty");
                    cell.BorderThickness = new Thickness(1);
                    cell.Child = new TextBlock { Text = "+", Opacity = 0.35, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    cell.ToolTip = Loc.T("BuilderRowSlot");
                }
                DropTarget(cell, p => CanRow(key, p), p => DropRow(key, Math.Min(at, m.Row.Count), p));
                row.Children.Add(cell);
            }
            sp.Children.Add(row);
            var line = new Border { Height = 1, Margin = new Thickness(0, 0, 0, 8) }; line.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
            sp.Children.Add(line);

            // the entries, then free slots (at least 5 in all, always 2 free)
            int depth = 0;
            for (int i = 0; i < m.Entries.Count; i++)
            {
                int at = i;
                var e = m.Entries[i];
                if (e.Id == MenuCatalog.SubEnd) depth = Math.Max(0, depth - 1);
                var cell = new Border { Height = 30, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), Margin = new Thickness(depth * 16, 0, 0, 3), Padding = new Thickness(6, 0, 2, 0) };
                SetLook(cell, "filled");
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var tools2 = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Hidden };
                if (e.Id == MenuCatalog.Separator)
                {
                    var sep = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
                    sep.SetResourceReference(Border.BackgroundProperty, "FgFaintBrush");
                    Grid.SetColumnSpan(sep, 2); g.Children.Add(sep);
                }
                else if (e.Id == MenuCatalog.SubEnd)
                {
                    var t = Dim(Loc.T("SubmenuEnd"), 11); t.FontStyle = FontStyles.Italic; t.VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetColumnSpan(t, 2); g.Children.Add(t);
                    cell.Height = 22;
                }
                else
                {
                    g.Children.Add(IconOf(e.Id, 16).With(f => f.HorizontalAlignment = HorizontalAlignment.Left));
                    var t = new TextBlock { Text = LabelOf(e.Id, e.Label).Replace("&", ""), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                    if (MenuCatalog.IsSub(e.Id)) t.FontWeight = FontWeights.SemiBold;
                    if (m.Row.Contains(e.Id)) t.Opacity = 0.5;   // also in the row: the menu shows it there only
                    Grid.SetColumn(t, 1); g.Children.Add(t);
                    if (MenuCatalog.IsSub(e.Id))
                        tools2.Children.Add(SmallBtn("", Loc.T("Rename"), () =>
                        {
                            var name = InputDialog.Ask(this, Loc.T("Submenu"), Loc.T("SubmenuName"), e.Label);
                            if (string.IsNullOrWhiteSpace(name)) return;
                            e.Label = name.Trim(); _dirty = true; FillBlock(key);
                        }));
                }
                if (e.Id != MenuCatalog.SubEnd)
                    tools2.Children.Add(SmallBtn("", Loc.T("Remove"), () => { RemoveEntry(m, at); _dirty = true; FillBlock(key); }));
                Grid.SetColumn(tools2, 2); g.Children.Add(tools2);
                cell.Child = g;
                cell.MouseEnter += (_, _) => tools2.Visibility = Visibility.Visible;
                cell.MouseLeave += (_, _) => tools2.Visibility = Visibility.Hidden;
                if (e.Id != MenuCatalog.SubEnd) Draggable(cell, () => new Payload { Id = e.Id, Label = e.Label, Block = key, Index = at });
                DropTarget(cell, p => CanEntry(key, at, p), p => DropEntry(key, at, p));
                sp.Children.Add(cell);
                if (MenuCatalog.IsSub(e.Id)) depth++;
            }
            int free = Math.Max(FreeSlots, MinSlots - m.Entries.Count);
            for (int i = 0; i < free; i++)
            {
                var cell = new Border { Height = 30, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 3) };
                SetLook(cell, "empty");
                var t = Dim(Loc.T("BuilderDrop"), 11); t.Opacity = 0.6; t.HorizontalAlignment = HorizontalAlignment.Center; t.VerticalAlignment = VerticalAlignment.Center;
                cell.Child = t;
                DropTarget(cell, p => CanEntry(key, m.Entries.Count, p), p => DropEntry(key, m.Entries.Count, p));
                sp.Children.Add(cell);
            }
            // Properties and the Windows menu close every built menu – shown, but not movable or removable
            var fixedLine = new Border { Height = 1, Margin = new Thickness(0, 4, 0, 6) }; fixedLine.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
            sp.Children.Add(fixedLine);
            foreach (var id in MenuCatalog.FixedLast)
            {
                var g = new Grid { Height = 26, Opacity = 0.6, ToolTip = Loc.T("BuilderFixed"), Margin = new Thickness(7, 0, 4, 0) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.Children.Add(IconOf(id, 16).With(f => f.HorizontalAlignment = HorizontalAlignment.Left));
                var t = new TextBlock { Text = LabelOf(id, null), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(t, 1); g.Children.Add(t);
                var lk = new TextBlock { Text = "", Style = (Style)FindResource("Glyph"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(lk, 2); g.Children.Add(lk);
                sp.Children.Add(g);
            }
            card.Child = sp;
        }

        // ================= drop rules =================
        private bool CanRow(string key, Payload p)
        {
            var m = _menus[key];
            if (!MenuCatalog.CanBeInRow(p.Id, IsItems(key))) return false;
            if (p.Block == key && p.InRow) return true;   // reorder
            return !m.Row.Contains(p.Id) && m.Row.Count < RowSlots;
        }

        private void DropRow(string key, int at, Payload p)
        {
            var m = _menus[key];
            if (p.Block == key && p.InRow)
            {
                if (p.Index < 0 || p.Index >= m.Row.Count) return;
                var id = m.Row[p.Index];
                m.Row.RemoveAt(p.Index);
                if (at > p.Index) at--;
                m.Row.Insert(Math.Clamp(at, 0, m.Row.Count), id);
            }
            else
            {
                // a menu entry of this menu dragged up becomes an icon (it leaves the list)
                if (p.Block == key && p.Index >= 0 && p.Index < m.Entries.Count && m.Entries[p.Index].Id == p.Id) m.Entries.RemoveAt(p.Index);
                m.Row.Insert(Math.Clamp(at, 0, m.Row.Count), p.Id);
            }
            FillBlock(key);
            if (p.Block != null && p.Block != key) FillBlock(p.Block);
        }

        private bool CanEntry(string key, int at, Payload p)
        {
            var m = _menus[key];
            if (!Allowed(key, p)) return false;
            if (p.Block == key && !p.InRow) return true;   // reorder
            bool repeatable = p.Id == MenuCatalog.Separator || p.Id == MenuCatalog.SubPrefix || MenuCatalog.IsSub(p.Id);
            return repeatable || !m.Entries.Any(e => e.Id == p.Id);
        }

        /// <summary>Rows an entry covers: itself, or a whole submenu up to its end mark.</summary>
        private static int RangeOf(List<MenuEntry> l, int start)
        {
            if (!MenuCatalog.IsSub(l[start].Id)) return 1;
            int depth = 0;
            for (int i = start; i < l.Count; i++)
            {
                if (MenuCatalog.IsSub(l[i].Id)) depth++;
                else if (l[i].Id == MenuCatalog.SubEnd && --depth == 0) return i - start + 1;
            }
            return l.Count - start;
        }

        private void DropEntry(string key, int at, Payload p)
        {
            var m = _menus[key];
            at = Math.Clamp(at, 0, m.Entries.Count);
            if (p.Block == key && !p.InRow)
            {
                // move inside this menu (a submenu moves with everything in it)
                if (p.Index < 0 || p.Index >= m.Entries.Count) return;
                int n = RangeOf(m.Entries, p.Index);
                if (at >= p.Index && at <= p.Index + n) return;
                var part = m.Entries.GetRange(p.Index, n);
                m.Entries.RemoveRange(p.Index, n);
                if (at > p.Index) at -= n;
                m.Entries.InsertRange(at, part);
            }
            else if (p.Id == MenuCatalog.SubPrefix)
            {
                var name = InputDialog.Ask(this, Loc.T("Submenu"), Loc.T("SubmenuName"), Loc.T("Submenu"));
                if (string.IsNullOrWhiteSpace(name)) return;
                m.Entries.Insert(at, new MenuEntry { Id = MenuCatalog.SubPrefix + Guid.NewGuid().ToString("N")[..8], Label = name.Trim() });
                m.Entries.Insert(at + 1, new MenuEntry { Id = MenuCatalog.SubEnd });
            }
            else if (p.InRow && p.Block != null)
            {
                // an icon dragged down into the list: it becomes a menu entry again
                var src = _menus[p.Block];
                if (p.Block == key) src.Row.Remove(p.Id);
                if (!m.Entries.Any(e => e.Id == p.Id)) m.Entries.Insert(at, new MenuEntry { Id = p.Id });
            }
            else if (p.Block != null && MenuCatalog.IsSub(p.Id))
            {
                // a submenu copied from another menu: with its contents, under a new id
                var src = _menus[p.Block].Entries;
                if (p.Index < 0 || p.Index >= src.Count) return;
                var part = src.GetRange(p.Index, RangeOf(src, p.Index))
                    .Select(e => new MenuEntry { Id = MenuCatalog.IsSub(e.Id) ? MenuCatalog.SubPrefix + Guid.NewGuid().ToString("N")[..8] : e.Id, Label = e.Label })
                    .Where(e => e.Id == MenuCatalog.Separator || e.Id == MenuCatalog.SubEnd || MenuCatalog.IsSub(e.Id) || !m.Entries.Any(x => x.Id == e.Id))
                    .ToList();
                m.Entries.InsertRange(at, part);
            }
            else m.Entries.Insert(at, new MenuEntry { Id = p.Id, Label = p.Label });
            FillBlock(key);
        }

        private static void RemoveEntry(FullMenu m, int at)
        {
            if (at < 0 || at >= m.Entries.Count) return;
            // a submenu goes with its end mark; what was inside stays, one level up
            if (MenuCatalog.IsSub(m.Entries[at].Id))
            {
                int n = RangeOf(m.Entries, at);
                if (n > 1 && m.Entries[at + n - 1].Id == MenuCatalog.SubEnd) m.Entries.RemoveAt(at + n - 1);
            }
            m.Entries.RemoveAt(at);
        }

        // ================= apply =================
        private void Apply()
        {
            var s = AppState.Settings;
            s.FullMenus = _menus.Where(kv => !kv.Value.IsEmpty).ToDictionary(kv => kv.Key, kv => kv.Value);
            if (s.ContextMenuMode == "full") s.ContextMenuMode = "custom";
            AppState.Save();
            _closingOk = true;
            DialogResult = true;
        }
    }
}
