using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinXplorer.Interop;
using WinXplorer.Models;
using WinXplorer.Services;
using WinXplorer.ViewModels;

namespace WinXplorer.Views
{
    public partial class PaneView : UserControl
    {
        private PaneViewModel _vm;
        private ListBox Current => _vm?.IsThisPC == true ? Drives : _vm?.IsIcons == true ? Tiles : List;

        private sealed record Crumb(string Name, string Path);

        public PaneView()
        {
            InitializeComponent();
            Columns.Columns.Remove(ColLocation);
            DataContextChanged += (_, _) => Attach(DataContext as PaneViewModel);
            _all.Add(this);
            Loaded += (_, _) => { if (AppState.Settings.NavOnTop && _vm?.IsActive == true) PlaceNav(_vm); };

            foreach (var lb in new ListBox[] { List, Tiles, Drives })
            {
                lb.SelectionChanged += (_, _) => { if (lb == Current && _vm != null) _vm.Selected = lb.SelectedItems.Cast<FileItem>().ToList(); };
                lb.MouseDoubleClick += List_DoubleClick;
                lb.PreviewMouseLeftButtonDown += List_PreviewLeftDown;
                lb.PreviewMouseMove += List_PreviewMouseMove;
                lb.PreviewMouseLeftButtonUp += List_PreviewLeftUp;
                lb.PreviewMouseRightButtonDown += List_PreviewRightDown;
                lb.MouseRightButtonUp += List_RightUp;
                lb.PreviewMouseDown += List_PreviewMouseDown;
                lb.PreviewKeyDown += List_PreviewKeyDown;
                lb.PreviewMouseWheel += List_PreviewMouseWheel;
                lb.DragOver += List_DragOver;
                lb.DragLeave += (_, _) => ClearDropTarget();
                lb.Drop += List_Drop;
            }
            PreviewMouseDown += (_, _) => Activate();
            PreviewGotKeyboardFocus += (_, _) => Activate();
            SearchBox.TextChanged += (_, _) => UpdateSearchHint();
            // Name column takes whatever space the other columns leave (until the user resizes it)
            List.SizeChanged += (_, _) => FitNameColumn();
            // a column dragged wider / narrower: remembered for this folder (like its view and sort order)
            _colSave = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _colSave.Tick += (_, _) => { _colSave.Stop(); SaveColumns(); };
            foreach (var col in new[] { ColName, ColLocation, ColModified, ColType, ColSize })
                ((INotifyPropertyChanged)col).PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName != nameof(GridViewColumn.Width) || _fitting || _applyingCols || Mouse.LeftButton != MouseButtonState.Pressed) return;
                    if (col == ColName) _userSizedName = true;
                    _colSave.Stop(); _colSave.Start();
                };
            SizeChanged += (_, e) =>
            {
                // narrow panes (split view): give the address bar more room
                bool narrow = e.NewSize.Width < 600;
                SearchCol.MinWidth = narrow ? 70 : 120;
                SearchCol.Width = new GridLength(narrow ? 0.22 : 0.32, GridUnitType.Star);
                ForwardBtn.Visibility = e.NewSize.Width < 420 ? Visibility.Collapsed : Visibility.Visible;
            };
            ThemeManager.ThemeChanged += UpdateFrame;
            SetupHover();
        }

        private bool _fitting, _userSizedName, _applyingCols;
        private readonly System.Windows.Threading.DispatcherTimer _colSave;
        private static readonly Dictionary<string, double> DefaultWidths = new() { ["location"] = 260, ["modified"] = 150, ["type"] = 140, ["size"] = 100 };

        private IEnumerable<(string key, GridViewColumn col)> NamedColumns() => new[]
            { ("name", ColName), ("location", ColLocation), ("modified", ColModified), ("type", ColType), ("size", ColSize) };

        private void SaveColumns()
        {
            if (_vm == null || !_vm.IsDetails) return;
            _vm.SaveColumnWidths(NamedColumns().ToDictionary(c => c.key, c => Math.Round(double.IsNaN(c.col.Width) ? c.col.ActualWidth : c.col.Width)));
        }

        /// <summary>The folder's own column widths, or the automatic layout (name fills the rest).</summary>
        private void ApplyColumns()
        {
            var w = _vm?.ColumnWidths;
            _applyingCols = true;
            foreach (var (key, col) in NamedColumns())
            {
                if (w != null && w.TryGetValue(key, out var v) && v > 20) col.Width = v;
                else if (DefaultWidths.TryGetValue(key, out var d)) col.Width = d;
            }
            _applyingCols = false;
            _userSizedName = w != null && w.ContainsKey("name");
            FitNameColumn();
        }

        private void FitNameColumn()
        {
            if (_userSizedName || List.ActualWidth <= 0) return;
            double others = Columns.Columns.Where(c => c != ColName).Sum(c => double.IsNaN(c.Width) ? c.ActualWidth : c.Width);
            _fitting = true;
            ColName.Width = Math.Max(200, List.ActualWidth - others - 34);
            _fitting = false;
        }

        private void Activate()
        {
            if (_vm != null) _vm.Tab.Active = _vm;
        }

        // ================= binding to the view model =================
        private void Attach(PaneViewModel vm)
        {
            if (_vm != null)
            {
                _vm.PropertyChanged -= VmChanged;
                _vm.Tab.PropertyChanged -= TabChanged;
                _vm.RequestSelect -= OnRequestSelect;
                _vm.RequestRename -= OnRequestRename;
                _vm.RequestFocus -= FocusList;
                _vm.RequestUi -= OnRequestUi;
                _vm.Items.CollectionChanged -= ItemsChanged;
            }
            _vm = vm;
            if (vm == null) return;
            vm.PropertyChanged += VmChanged;
            vm.Tab.PropertyChanged += TabChanged;
            vm.RequestSelect += OnRequestSelect;
            vm.RequestRename += OnRequestRename;
            vm.RequestFocus += FocusList;
            vm.Refreshed += BounceItems;
            vm.RequestUi += OnRequestUi;
            BindItems();
            BuildCrumbs();
            ApplyColumns();
            UpdateFolderView();
            UpdateHeaders();
            UpdateFrame();
            UpdateSearchHint();
        }

        private void VmChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(PaneViewModel.Items):
                case nameof(PaneViewModel.ViewMode):
                case nameof(PaneViewModel.IconSize) when _vm.GroupByDate && _vm.IsIcons && _tilesGrouped != (_vm.IconSize <= GroupMaxIcon):
                case nameof(PaneViewModel.GroupByDate):
                    BindItems(); break;
                case nameof(PaneViewModel.Path):
                    BuildCrumbs(); ApplyColumns();
                    UpdateFolderView(_vm.ForceView); _vm.ForceView = false;
                    break;
                case nameof(PaneViewModel.SortBy):
                case nameof(PaneViewModel.SortDesc):
                    UpdateHeaders(); break;
                case nameof(PaneViewModel.IsActive):
                    UpdateFrame(); break;
                case nameof(PaneViewModel.IsLoading):
                case nameof(PaneViewModel.Error):
                    UpdateEmpty(); break;
                case nameof(PaneViewModel.IsRecursiveSearch):
                    if (_vm.IsRecursiveSearch && !Columns.Columns.Contains(ColLocation)) Columns.Columns.Insert(1, ColLocation);
                    else if (!_vm.IsRecursiveSearch) Columns.Columns.Remove(ColLocation);
                    FitNameColumn();
                    break;
                case nameof(PaneViewModel.SearchText):
                    UpdateSearchHint(); break;
                case nameof(PaneViewModel.SearchSummary):
                    SearchSummaryText.Text = _vm.SearchSummary ?? "";
                    SearchSummaryText.Visibility = _vm.SearchSummary != null ? Visibility.Visible : Visibility.Collapsed;
                    CrumbScroll.Visibility = _vm.SearchSummary != null ? Visibility.Hidden : Visibility.Visible;
                    break;
            }
        }

        private void TabChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TabViewModel.Split)) UpdateFrame();
        }

        private ObservableCollectionWatcher _watchedItems;
        private sealed class ObservableCollectionWatcher { public INotifyCollectionChanged Source; }

        private void BindItems()
        {
            if (_watchedItems?.Source != null) _watchedItems.Source.CollectionChanged -= ItemsChanged;
            var active = Current;
            bool hadFocus = false;
            foreach (var other in new ListBox[] { List, Tiles, Drives })
            {
                if (other == active) continue;
                hadFocus |= other.IsKeyboardFocusWithin;
                other.ItemsSource = null;
                other.Visibility = Visibility.Collapsed;
            }
            active.Visibility = Visibility.Visible;
            // small icons are grouped too: the groups are stacked, each one wraps its icons (bigger icons stay ungrouped – too heavy)
            bool groupTiles = active == Tiles && _vm.GroupByDate && !_vm.IsThisPC && _vm.IconSize <= GroupMaxIcon;
            _tilesPanel ??= Tiles.ItemsPanel;
            _tilesGrouped = groupTiles;
            var wantPanel = groupTiles ? StackedGroups : _tilesPanel;
            if (Tiles.ItemsPanel != wantPanel) { Tiles.ItemsSource = null; Tiles.ItemsPanel = wantPanel; }
            if ((active == List || groupTiles) && _vm.GroupByDate && !_vm.IsThisPC)
            {
                // Today / Yesterday / … headings (items are already sorted group by group)
                var view = new System.Windows.Data.ListCollectionView(_vm.Items);
                view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(FileItem.DateGroup)));
                active.ItemsSource = view;
            }
            else active.ItemsSource = _vm.Items;
            _vm.Items.CollectionChanged += ItemsChanged;
            _watchedItems = new ObservableCollectionWatcher { Source = _vm.Items };
            UpdateEmpty();
            if (hadFocus) FocusList();
        }

        private void ItemsChanged(object sender, NotifyCollectionChangedEventArgs e) => UpdateEmpty();

        /// <summary>Largest icon size that is still grouped by date (Small icons).</summary>
        public const int GroupMaxIcon = 64;
        private ItemsPanelTemplate _tilesPanel;
        private bool _tilesGrouped;
        private static ItemsPanelTemplate _stacked;
        private static ItemsPanelTemplate StackedGroups => _stacked ??= (ItemsPanelTemplate)System.Windows.Markup.XamlReader.Parse(
            "<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><VirtualizingStackPanel/></ItemsPanelTemplate>");

        private void UpdateEmpty()
        {
            if (_vm == null) return;
            if (_vm.HasError) { EmptyText.Text = _vm.Error; EmptyText.Visibility = Visibility.Visible; }
            else if (_vm.Items.Count == 0 && !_vm.IsLoading)
            {
                EmptyText.Text = !string.IsNullOrEmpty(_vm.SearchText) ? Loc.T("NoResults") : Loc.T("EmptyFolder");
                EmptyText.Visibility = Visibility.Visible;
            }
            else EmptyText.Visibility = Visibility.Collapsed;
        }

        private void UpdateFrame()
        {
            if (_vm == null) return;
            bool show = _vm.Tab.IsSplit && _vm.IsActive;
            Frame.BorderBrush = show ? (Brush)FindResource("AccentBrush") : Brushes.Transparent;
            Frame.SetResourceReference(Border.BackgroundProperty, _vm.Tab.IsSplit && !_vm.IsActive ? "ContentInactiveBrush" : "ContentBrush");
            // the pane you aren't working in, or the whole window when another one has the focus: a little darker
            var w = Window.GetWindow(this);
            if (w != null && !_dimHooked) { _dimHooked = true; w.Activated += (_, _) => UpdateFrame(); w.Deactivated += (_, _) => UpdateFrame(); }
            bool dim = (_vm.Tab.IsSplit && !_vm.IsActive) || (w != null && !w.IsActive);
            double to = dim ? (ThemeManager.IsDark ? 0.22 : 0.06) : 0;
            Dim.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(to, TimeSpan.FromMilliseconds(180)));
        }
        private bool _dimHooked;

        // ---------- "address bar on top": the active pane's navigation row is lent to the main window ----------
        private static readonly List<PaneView> _all = new();
        /// <summary>The place under the tabs (set by the main window).</summary>
        public static Decorator NavHost;

        /// <summary>Puts the navigation row of <paramref name="active"/> under the tabs (or every row back in its pane).</summary>
        public static void PlaceNav(PaneViewModel active)
        {
            bool top = AppState.Settings.NavOnTop && NavHost != null && active != null;
            _all.RemoveAll(v => !v.IsLoaded && PresentationSource.FromVisual(v) == null && v._vm == null);
            foreach (var v in _all)
                if (!top || v._vm != active)
                {
                    v.TakeNavBack();
                    // one address bar for the window: the other pane of a split view shows none
                    v.NavRow.Visibility = top && AppState.Settings.NavOnTop ? Visibility.Collapsed : Visibility.Visible;
                }
            if (!AppState.Settings.NavOnTop) foreach (var v in _all) v.NavRow.Visibility = Visibility.Visible;
            if (top && _all.FirstOrDefault(v => v._vm == active) is { } a && a.NavRow.Parent != NavHost)
            {
                (a.NavRow.Parent as Panel)?.Children.Remove(a.NavRow);
                a.NavRow.DataContext = a._vm;
                a.NavRow.Visibility = Visibility.Visible;
                NavHost.Child = a.NavRow;
            }
            if (NavHost != null) NavHost.Visibility = top && NavHost.Child != null ? Visibility.Visible : Visibility.Collapsed;
        }

        private void TakeNavBack()
        {
            if (NavRow.Parent is not Decorator d) return;
            d.Child = null;
            NavRow.ClearValue(DataContextProperty);
            RootGrid.Children.Insert(0, NavRow);
        }

        /// <summary>Refresh: the visible items hop up a little one after another, so you see it happened.</summary>
        private void BounceItems()
        {
            var lb = Current;
            if (lb == null || !lb.IsVisible || SystemParameters.ClientAreaAnimation == false) return;
            int n = 0;
            for (int i = 0; i < lb.Items.Count && n < 60; i++)
            {
                if (lb.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem c || !c.IsVisible) continue;
                var tt = new TranslateTransform();
                c.RenderTransform = tt;
                var a = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(n++ * 12) };
                a.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(-5, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)), new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }));
                a.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(0, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)), new System.Windows.Media.Animation.BounceEase { Bounces = 1, Bounciness = 4 }));
                tt.BeginAnimation(TranslateTransform.YProperty, a);
            }
        }

        private void SearchIcon_MouseDown(object sender, MouseButtonEventArgs e) { SearchBox.Focus(); e.Handled = true; }

        private void UpdateSearchHint()
        {
            bool empty = string.IsNullOrEmpty(SearchBox.Text);
            SearchHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            SearchClear.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UpdateHeaders()
        {
            foreach (var col in new[] { ColName, ColLocation, ColModified, ColType, ColSize })
            {
                if (col.Header is not GridViewColumnHeader h) continue;
                var key = (string)h.Tag;
                string label = key switch
                {
                    "name" => Loc.T("ColName"), "modified" => Loc.T("ColModified"), "type" => Loc.T("ColType"),
                    "size" => Loc.T("ColSize"), "location" => Loc.T("ColLocation"), _ => key
                };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new TextBlock { Text = label });
                if (_vm != null && _vm.SortBy == key)
                    sp.Children.Add(new TextBlock
                    {
                        Text = _vm.SortDesc ? "" : "",
                        Style = (Style)FindResource("Glyph"), FontSize = 9, Margin = new Thickness(6, 1, 0, 0),
                    });
                h.Content = sp;
            }
        }

        private void Header_Click(object sender, RoutedEventArgs e)
        {
            if (sender is GridViewColumnHeader h && h.Tag is string key && key != "location") _vm?.ToggleSort(key);
        }

        // ================= selection requests =================
        private void OnRequestSelect(IList<string> paths, bool scroll)
        {
            var lb = Current;
            lb.UpdateLayout();
            var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            FileItem first = null;
            lb.SelectedItems.Clear();
            if (set.Count > 0)
            {
                foreach (var i in _vm.Items)
                {
                    if (!set.Contains(i.FullPath)) continue;
                    lb.SelectedItems.Add(i);
                    first ??= i;
                }
            }
            if (scroll)
            {
                if (first != null) lb.ScrollIntoView(first);
                else FindScrollViewer(lb)?.ScrollToTop();
            }
            bool focusHere = _vm.IsActive && Keyboard.FocusedElement is not TextBox && IsVisible;
            if (focusHere)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                {
                    if (first != null && lb.ItemContainerGenerator.ContainerFromItem(first) is ListBoxItem c) c.Focus();
                    else if (lb.Items.Count > 0 && lb.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem c0 && lb.SelectedItems.Count == 0)
                    {
                        // focus the first row without selecting it, so arrow keys work right away
                        c0.Focus();
                        lb.SelectedItems.Clear();
                    }
                    else lb.Focus();
                });
            }
        }

        public void FocusList()
        {
            var lb = Current;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                var item = lb.SelectedItem ?? (lb.Items.Count > 0 ? lb.Items[0] : null);
                if (item != null && lb.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem c) c.Focus();
                else lb.Focus();
            });
        }

        private static ScrollViewer FindScrollViewer(DependencyObject d)
        {
            if (d is ScrollViewer sv) return sv;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
                if (FindScrollViewer(VisualTreeHelper.GetChild(d, i)) is ScrollViewer r) return r;
            return null;
        }

        private void OnRequestUi(string what)
        {
            if (what == "address") EnterAddressEdit();
            else if (what == "search") { SearchBox.Focus(); SearchBox.SelectAll(); }
            else if (what == "folderview") UpdateFolderView(edit: true);
        }

        private void ItemToolTip_Opening(object sender, ToolTipEventArgs e)
        {
            // hover preview on: its window shows these details already – two popups would cover each other
            if (AppState.Settings.HoverPreview) { e.Handled = true; return; }
            if ((sender as FrameworkElement)?.DataContext is FileItem f) f.RequestFolderSize();
        }

        // ================= rename =================
        private void OnRequestRename()
        {
            var pending = _vm.PendingRename;
            _vm.PendingRename = null;
            if (pending != null && _vm.Items?.FirstOrDefault(i => string.Equals(i.FullPath, pending, StringComparison.OrdinalIgnoreCase)) is { } created)
            {
                // a new folder/file: let the list show and select it first, then ask for its name
                Dispatcher.BeginInvoke(new Action(() => RenameOne(created)), System.Windows.Threading.DispatcherPriority.Background);
                return;
            }
            var sel = _vm.Selected.Where(i => !i.IsDrive).ToList();
            if (sel.Count > 1)
            {
                // several items: edit all names like text
                var done = MultiRenameWindow.Show(Window.GetWindow(this), sel);
                if (done != null && done.Count > 0) _vm.ReloadAndSelect(done[0].to);
                FocusList();
                return;
            }
            RenameOne(_vm.FirstSelected);
        }

        /// <summary>
        /// A drive's name (volume label). Only the name shown changes – the letter and every path stay the same.
        /// Windows allows it only with administrator permission, so it asks.
        /// </summary>
        private void RenameDrive(FileItem drive)
        {
            var root = drive.FullPath.Length >= 2 ? drive.FullPath[..2] : drive.FullPath;   // "F:"
            string label = "", format = "";
            try { var di = new DriveInfo(root); label = di.VolumeLabel; format = di.DriveFormat; } catch { }
            var name = InputDialog.Ask(Window.GetWindow(this), Loc.T("Rename"), Loc.F("RenameDrivePrompt", root), label);
            if (name == null) { FocusList(); return; }
            name = name.Trim();
            int max = format.StartsWith("FAT", StringComparison.OrdinalIgnoreCase) ? 11 : 32;
            if (name == label) return;
            if (name.Length > max || name.IndexOfAny("*?/\\|.,;:+=[]<>\"".ToCharArray()) >= 0)
            {
                MessageBox.Show(Window.GetWindow(this), Loc.F("RenameDriveInvalid", max), "WinXplorer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // already allowed (running as administrator), otherwise Windows' label tool with its permission prompt
            if (!Native.SetVolumeLabelW(root + "\\", name.Length == 0 ? null : name) && name.Length > 0)   // an empty name would make label.exe wait for input
            {
                try
                {
                    using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("label.exe", root + " " + name)
                    { UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden });
                    p?.WaitForExit(15000);
                    if (p != null && p.HasExited && p.ExitCode != 0)
                    {
                        MessageBox.Show(Window.GetWindow(this), Loc.F("RenameDriveFailed", root), "WinXplorer", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
                catch { return; }   // the user said no at the permission prompt
            }
            // Windows keeps drive names in a cache: tell it this one changed, or the old name stays on screen
            Native.SHChangeNotify(0x00002000 /* SHCNE_UPDATEITEM */, 0x0005 /* SHCNF_PATHW */, root + "\\", IntPtr.Zero);
            _vm.Refresh();
            MainWindow.Instance?.VM.Sidebar.Rebuild();
        }

        private void RenameOne(FileItem item)
        {
            if (item == null) return;
            if (item.IsDrive) { RenameDrive(item); return; }
            int selLen = item.IsDirectory ? item.Name.Length : Path.GetFileNameWithoutExtension(item.Name).Length;
            if (RenameInPlace(item, selLen)) return;
            var name = InputDialog.Ask(Window.GetWindow(this), Loc.T("Rename"), Loc.T("RenamePrompt"), item.Name, selLen);
            if (name == null || name == item.Name) { FocusList(); return; }
            if (FileOps.Rename(item.FullPath, name, out var err))
                _vm.ReloadAndSelect(Path.Combine(Path.GetDirectoryName(item.FullPath.TrimEnd('\\')) ?? "", name));
            else if (err != null) MessageBox.Show(Window.GetWindow(this), err, "WinXplorer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        /// <summary>Like Explorer: the name turns into an edit box where it is, name without extension selected.</summary>
        private bool RenameInPlace(FileItem item, int selLen)
        {
            var lb = Current;
            if (lb == null || lb == Drives) return false;
            lb.ScrollIntoView(item);
            lb.UpdateLayout();
            if (lb.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem c) return false;
            TextBlock tb = null;
            void Find(DependencyObject d)
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d) && tb == null; i++)
                {
                    var ch = VisualTreeHelper.GetChild(d, i);
                    if (ch is TextBlock t && t.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path.Path is "DisplayName" or "Name") tb = t;
                    else Find(ch);
                }
            }
            Find(c);
            if (tb == null || !tb.IsVisible) return false;
            bool tile = lb == Tiles;
            var box = new TextBox
            {
                Text = item.Name, FontSize = tb.FontSize, Padding = new Thickness(1, 0, 1, 0), BorderThickness = new Thickness(1),
                TextWrapping = tile ? TextWrapping.Wrap : TextWrapping.NoWrap, TextAlignment = tile ? TextAlignment.Center : TextAlignment.Left,
                MinWidth = Math.Max(tb.ActualWidth + 24, 120), MaxWidth = tile ? Math.Max(c.ActualWidth, 80) : 600,
            };
            box.SetResourceReference(TextBox.BackgroundProperty, "ContentBrush");
            box.SetResourceReference(TextBox.ForegroundProperty, "FgBrush");
            box.SetResourceReference(TextBox.BorderBrushProperty, "AccentBrush");
            box.SetResourceReference(TextBox.CaretBrushProperty, "FgBrush");
            if (tile) box.Width = Math.Max(c.ActualWidth - 4, 80);
            // a name Windows won't take: the box turns red and says why, the wrong characters marked – it stays open until fixed (Esc cancels)
            var errText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, MaxWidth = 380 };
            errText.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
            var err = new Border
            {
                Child = errText, Padding = new Thickness(8, 5, 8, 6), Margin = new Thickness(0, 2, 0, 0), CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38)), Visibility = Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            err.SetResourceReference(Border.BackgroundProperty, "BarBrush");
            var stack = new StackPanel();
            stack.Children.Add(box);
            stack.Children.Add(err);
            string problem = null;
            var red = new SolidColorBrush(Color.FromRgb(0xE8, 0x3B, 0x3F));
            void Validate()
            {
                var n = box.Text.Trim();
                problem = n == item.Name ? null : FileOps.NameProblem(item.FullPath, n, out var bad);
                if (problem == null)
                {
                    err.Visibility = Visibility.Collapsed;
                    box.SetResourceReference(TextBox.BorderBrushProperty, "AccentBrush");
                    return;
                }
                FileOps.NameProblem(item.FullPath, n, out bad);
                box.BorderBrush = red;
                errText.Inlines.Clear();
                // the name with what is wrong in red, then the reason
                int i = 0;
                while (i < n.Length)
                {
                    int j = i;
                    while (j < n.Length && bad[j] == bad[i]) j++;
                    var run = new System.Windows.Documents.Run(n[i..j]);
                    if (bad[i]) { run.Foreground = red; run.FontWeight = FontWeights.Bold; run.TextDecorations = TextDecorations.Underline; }
                    errText.Inlines.Add(run);
                    i = j;
                }
                if (n.Length > 0) errText.Inlines.Add(new System.Windows.Documents.LineBreak());
                errText.Inlines.Add(new System.Windows.Documents.Run(problem) { FontSize = 11.5 });
                err.Visibility = Visibility.Visible;
            }
            box.TextChanged += (_, _) => Validate();
            var pop = new System.Windows.Controls.Primitives.Popup
            {
                PlacementTarget = tile ? c : tb, AllowsTransparency = true, StaysOpen = false, Child = stack,
                Placement = tile ? System.Windows.Controls.Primitives.PlacementMode.Relative : System.Windows.Controls.Primitives.PlacementMode.Relative,
                HorizontalOffset = tile ? 2 : -3, VerticalOffset = tile ? tb.TranslatePoint(new Point(), c).Y - 2 : -2,
            };
            bool done = false;
            void Finish(bool commit)
            {
                if (done) return;
                if (commit && problem != null)
                {
                    // not saved and not cancelled: back to the box until the name is fixed or Esc is pressed
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (done) return;
                        if (!pop.IsOpen) pop.IsOpen = true;
                        box.Focus(); Keyboard.Focus(box);
                    }), System.Windows.Threading.DispatcherPriority.Input);
                    return;
                }
                done = true;
                pop.IsOpen = false;
                lb.RemoveHandler(ScrollViewer.ScrollChangedEvent, (ScrollChangedEventHandler)OnScroll);
                var name = box.Text.Trim();
                if (commit && name.Length > 0 && name != item.Name)
                {
                    if (FileOps.Rename(item.FullPath, name, out var err))
                        _vm.ReloadAndSelect(Path.Combine(Path.GetDirectoryName(item.FullPath.TrimEnd('\\')) ?? "", name));
                    else if (err != null) MessageBox.Show(Window.GetWindow(this), err, "WinXplorer", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                FocusList();
            }
            void OnScroll(object s, ScrollChangedEventArgs e) { if ((e.VerticalChange != 0 || e.HorizontalChange != 0) && problem == null) Finish(true); }
            box.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; Finish(true); }
                else if (e.Key == Key.Escape) { e.Handled = true; Finish(false); }
            };
            box.LostKeyboardFocus += (_, _) => Finish(true);
            pop.Closed += (_, _) => Finish(true);
            lb.AddHandler(ScrollViewer.ScrollChangedEvent, (ScrollChangedEventHandler)OnScroll);
            pop.IsOpen = true;
            Dispatcher.BeginInvoke(new Action(() => { box.Focus(); Keyboard.Focus(box); box.Select(0, Math.Min(selLen, box.Text.Length)); }), System.Windows.Threading.DispatcherPriority.Input);
            return true;
        }

        // ================= opening =================
        private FileItem ItemAt(object source)
        {
            if (source is not DependencyObject d) return null;
            var c = ItemsControl.ContainerFromElement(Current, d) as ListBoxItem;
            return c?.DataContext as FileItem;
        }

        private void OpenItem(FileItem item, bool newTab = false)
        {
            if (item == null) return;
            if (item.IsDirectory)
            {
                if (newTab) MainWindow.Instance.OpenInNewTab(item.FullPath);
                else _vm.Navigate(item.FullPath);
                return;
            }
            // view.wxv: the folder's own page (set it up when it's new / empty)
            if (!item.InArchive && item.Name.Equals(FolderViews.FileName, StringComparison.OrdinalIgnoreCase))
            {
                var dir = Path.GetDirectoryName(item.FullPath);
                if (string.Equals(dir, _vm.Path, StringComparison.OrdinalIgnoreCase)) { _plainPath = null; UpdateFolderView(force: true); }
                else _vm.Navigate(item.FullPath);
                return;
            }
            if (item.Extension == ".lnk" && Shell.ResolveShortcut(item.FullPath) is string target && Directory.Exists(target))
            {
                if (newTab) MainWindow.Instance.OpenInNewTab(target); else _vm.Navigate(target);
                return;
            }
            // iso / img / vhd…: mount (Windows or DAEMON Tools, Alcohol…) or choose an app – not a folder
            if (!item.InArchive && FileTypes.DiskImage.Contains(item.Extension))
            {
                OpenDiskImage(item);
                return;
            }
            // zip / 7z / rar...: open it like a folder
            if (!item.InArchive && AppState.Settings.OpenArchives && Archives.IsArchive(item.FullPath))
            {
                if (newTab) MainWindow.Instance.OpenInNewTab(item.FullPath); else _vm.Navigate(item.FullPath);
                return;
            }
            if (!newTab && AppState.Settings.ImagesOpenInViewer && FileTypes.IsViewableImage(item.FullPath) && !FileTypes.Design.Contains(item.Extension))
            {
                MainWindow.Instance.ShowViewer(_vm, item, toggle: false);
                return;
            }
            FileOps.Open(item.FullPath);
        }

        private void OpenSelected()
        {
            var sel = _vm.Selected.ToList();
            if (sel.Count == 0) return;
            if (sel.Count == 1) { OpenItem(sel[0]); return; }
            var dirs = sel.Where(s => s.IsDirectory).ToList();
            foreach (var d in dirs) MainWindow.Instance.OpenInNewTab(d.FullPath);
            var files = sel.Where(s => !s.IsDirectory).ToList();
            if (files.Count > 15 && MessageBox.Show(Window.GetWindow(this), Loc.T("OpenManyQ").Replace("{0}", files.Count.ToString()), "WinXplorer", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            foreach (var f in files) FileOps.Open(f.FullPath);
        }

        private void List_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            var item = ItemAt(e.OriginalSource);
            if (item != null) { OpenItem(item); e.Handled = true; }
        }

        private void List_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                var item = ItemAt(e.OriginalSource);
                if (item != null && item.IsDirectory) { MainWindow.Instance.OpenInNewTab(item.FullPath); e.Handled = true; }
            }
        }

        // ================= mouse: selection + drag =================
        private Point _downPos;
        private FileItem _downItem;
        private bool _deferSelect;

        private static bool IsInChrome(object source)
        {
            for (var d = source as DependencyObject; d != null; d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (d is ScrollBar || d is GridViewColumnHeader || d is ListBoxItem) return true;
            return false;
        }

        private void List_PreviewLeftDown(object sender, MouseButtonEventArgs e)
        {
            var lb = (ListBox)sender;
            _downPos = e.GetPosition(lb);
            _downItem = ItemAt(e.OriginalSource);
            _deferSelect = false;
            // click on the name of the only selected item (not a double click): rename after a pause, like Explorer
            _slowRenameArmed = e.ClickCount == 1 && _downItem != null && Keyboard.Modifiers == ModifierKeys.None && lb.SelectedItems.Count == 1
                && lb.SelectedItem == _downItem && e.OriginalSource is TextBlock tb && tb.Text == _downItem.Name && !_vm.InArchive && !_downItem.IsDrive;
            if (e.ClickCount > 1) _slowRename?.Stop();
            if (_downItem == null)
            {
                if (!IsInChrome(e.OriginalSource))
                {
                    if (Keyboard.Modifiers == ModifierKeys.None) lb.UnselectAll();
                    lb.Focus();
                    if (e.ClickCount == 1 && BeginMarquee(lb, e)) e.Handled = true;
                }
                return;
            }
            if (e.ClickCount > 1) return;
            if (ItemsControl.ContainerFromElement(lb, (DependencyObject)e.OriginalSource) is ListBoxItem c
                && c.IsSelected && Keyboard.Modifiers == ModifierKeys.None && lb.SelectedItems.Count > 1)
            {
                _deferSelect = true;
                c.Focus();
                e.Handled = true;
            }
        }

        private void List_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_mqList != null) { MarqueeTick(); e.Handled = true; return; }
            if (e.LeftButton != MouseButtonState.Pressed || _downItem == null) return;
            var lb = (ListBox)sender;
            var d = e.GetPosition(lb) - _downPos;
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var item = _downItem;
            _downItem = null;
            _deferSelect = false;
            if (!lb.SelectedItems.Contains(item)) { lb.SelectedItems.Clear(); lb.SelectedItems.Add(item); }
            var paths = lb.SelectedItems.Cast<FileItem>().Select(i => i.FullPath).ToArray();
            if (paths.Length == 0) return;
            if (_vm.InArchive)
            {
                // dragging out of an archive: hand Windows the paths the files will have, and extract them only
                // when the mouse button is let go over a target (like 7-Zip) – a cancelled drag extracts nothing
                var inner = paths;
                paths = Archives.FuturePaths(inner).ToArray();
                bool extracted = false, failed = false;
                QueryContinueDragEventHandler q = (_, qe) =>
                {
                    if (qe.EscapePressed) { qe.Action = DragAction.Cancel; qe.Handled = true; return; }
                    if ((qe.KeyStates & DragDropKeyStates.LeftMouseButton) != 0 || extracted) return;
                    extracted = true;
                    try { Mouse.OverrideCursor = Cursors.Wait; FileOps.Pump(() => Archives.Materialize(inner)); }
                    catch (Exception ex) { failed = true; Notify.Show(ex.Message); }
                    finally { Mouse.OverrideCursor = null; }
                    qe.Action = failed ? DragAction.Cancel : DragAction.Drop;
                    qe.Handled = true;
                };
                lb.QueryContinueDrag += q;
                try { DragDrop.DoDragDrop(lb, new DataObject(DataFormats.FileDrop, paths), DragDropEffects.Copy); }
                catch { }
                finally { lb.QueryContinueDrag -= q; }
                ClearDropTarget();
                return;
            }
            var data = new DataObject(DataFormats.FileDrop, paths);
            try { DragDrop.DoDragDrop(lb, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link); }
            catch { }
            ClearDropTarget();
        }

        private void List_PreviewLeftUp(object sender, MouseButtonEventArgs e)
        {
            var lb = (ListBox)sender;
            if (_mqList != null) { EndMarquee(); e.Handled = true; return; }
            if (_deferSelect && _downItem != null)
            {
                lb.SelectedItems.Clear();
                lb.SelectedItems.Add(_downItem);
            }
            if (_slowRenameArmed && _downItem != null && (e.GetPosition(lb) - _downPos).Length < 4)
            {
                var item = _downItem;
                _slowRename ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime() + 50) };
                _slowRename.Stop();
                _slowRename.Tag = item;
                _slowRename.Tick -= SlowRename_Tick;
                _slowRename.Tick += SlowRename_Tick;
                _slowRename.Start();
            }
            _slowRenameArmed = false;
            _deferSelect = false;
            _downItem = null;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
        private bool _slowRenameArmed;
        private System.Windows.Threading.DispatcherTimer _slowRename;

        private void SlowRename_Tick(object sender, EventArgs e)
        {
            _slowRename.Stop();
            if (_slowRename.Tag is FileItem item && _vm.Selected.Count == 1 && _vm.FirstSelected == item && Mouse.LeftButton == MouseButtonState.Released)
                RenameOne(item);
        }

        private void List_PreviewRightDown(object sender, MouseButtonEventArgs e)
        {
            var lb = (ListBox)sender;
            var item = ItemAt(e.OriginalSource);
            if (item == null)
            {
                if (!IsInChrome(e.OriginalSource)) lb.UnselectAll();
                return;
            }
            if (!lb.SelectedItems.Contains(item)) { lb.SelectedItems.Clear(); lb.SelectedItems.Add(item); }
            if (lb.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem c) c.Focus();
            e.Handled = true;
        }

        private void List_RightUp(object sender, MouseButtonEventArgs e)
        {
            if (IsInChromeScroll(e.OriginalSource)) return;
            ShowContextMenu(null);
            e.Handled = true;
        }

        private static bool IsInChromeScroll(object source)
        {
            for (var d = source as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d))
                if (d is ScrollBar || d is GridViewColumnHeader) return true;
            return false;
        }

        private void List_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
            e.Handled = true;
            if (_vm.IsDetails)
            {
                if (e.Delta > 0) { _vm.ViewMode = "icons"; _vm.IconSize = 48; }
                return;
            }
            int step = Math.Max(8, _vm.IconSize / 6);
            int next = _vm.IconSize + (e.Delta > 0 ? step : -step);
            if (next < 32) { _vm.ViewMode = "details"; return; }
            _vm.IconSize = next;
        }

        // ================= keyboard =================
        private void List_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (Hotkeys.Match(HkScope.Pane, e))
            {
                case "Properties":
                    if (_vm.SelectedPaths.Length > 0) FileOps.ShowProperties(_vm.SelectedPaths); else FileOps.ShowProperties(_vm.Path);
                    e.Handled = true; break;
                case "Open":
                    OpenSelected(); e.Handled = true; break;
                case "Delete":
                    FileOps.Delete(_vm.SelectedPaths, false); e.Handled = true; break;
                case "DeletePerm":
                    FileOps.Delete(_vm.SelectedPaths, true); e.Handled = true; break;
                case "Rename":
                    _vm.Rename(); e.Handled = true; break;
                case "CopyPath":
                    FileOps.SetClipboardText(_vm.SelectedPaths.Length > 0 ? string.Join(Environment.NewLine, _vm.SelectedPaths) : _vm.Path); e.Handled = true; break;
                case "Copy":
                    FileOps.SetClipboard(_vm.SelectedPaths, false); e.Handled = true; break;
                case "Cut":
                    FileOps.SetClipboard(_vm.SelectedPaths, true); e.Handled = true; break;
                case "Paste":
                    if (!_vm.IsThisPC) FileOps.Paste(_vm.Path); e.Handled = true; break;
                case "QuickView" when AppState.Settings.SpaceOpensViewer:
                    var f = _vm.FirstSelected;
                    // a folder shows what's inside it, without opening it
                    if (f != null && !f.IsDrive) { MainWindow.Instance.ShowViewer(_vm, f); e.Handled = true; }
                    break;
                case "Menu":
                    ShowContextMenu(KeyboardAnchor()); e.Handled = true; break;
            }
        }

        private Point? KeyboardAnchor()
        {
            var lb = Current;
            FrameworkElement el = lb.SelectedItem != null ? lb.ItemContainerGenerator.ContainerFromItem(lb.SelectedItem) as FrameworkElement : null;
            el ??= lb;
            var p = el.PointToScreen(new Point(24, Math.Min(el.ActualHeight, 24)));
            return p;
        }

        // ================= drag & drop target =================
        public static DragDropEffects DropEffectFor(DragEventArgs e, string targetDir)
        {
            var allowed = e.AllowedEffects;
            DragDropEffects want;
            if (e.KeyStates.HasFlag(DragDropKeyStates.AltKey) || e.KeyStates.HasFlag(DragDropKeyStates.ControlKey) && e.KeyStates.HasFlag(DragDropKeyStates.ShiftKey))
                want = DragDropEffects.Link;   // Alt (or Ctrl+Shift), like Explorer: a shortcut
            else if (e.KeyStates.HasFlag(DragDropKeyStates.ControlKey)) want = DragDropEffects.Copy;
            else if (e.KeyStates.HasFlag(DragDropKeyStates.ShiftKey)) want = DragDropEffects.Move;
            else
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                var srcRoot = files?.Length > 0 ? Path.GetPathRoot(files[0]) : null;
                var dstRoot = Path.GetPathRoot(targetDir ?? "");
                want = string.Equals(srcRoot, dstRoot, StringComparison.OrdinalIgnoreCase) ? DragDropEffects.Move : DragDropEffects.Copy;
            }
            if ((allowed & want) == 0) want = (allowed & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : (allowed & DragDropEffects.Move);
            return want;
        }

        private ListBoxItem _dropContainer;

        private void ClearDropTarget()
        {
            if (_dropContainer != null) _dropContainer.ClearValue(BackgroundProperty);
            _dropContainer = null;
        }

        private string DropTarget(DragEventArgs e, out ListBoxItem container)
        {
            container = null;
            var lb = Current;
            if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(lb, d) is ListBoxItem c && c.DataContext is FileItem fi && fi.IsDirectory)
            {
                container = c;
                return fi.FullPath;
            }
            return _vm.IsThisPC ? null : _vm.Path;
        }

        private void List_DragOver(object sender, DragEventArgs e)
        {
            e.Handled = true;
            ClearDropTarget();
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.None; return; }
            var target = DropTarget(e, out var container);
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (target == null || files == null || files.Any(f => string.Equals(f.TrimEnd('\\'), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            { e.Effects = DragDropEffects.None; return; }
            e.Effects = DropEffectFor(e, target);
            if (container != null)
            {
                _dropContainer = container;
                container.Background = (Brush)FindResource("DropBrush");
            }
        }

        private void List_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            var target = DropTarget(e, out _);
            ClearDropTarget();
            if (target == null || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
            var eff = DropEffectFor(e, target);
            if (eff == DragDropEffects.Link) { FileOps.CreateShortcuts(files, target); return; }
            // dropped into the folder on screen: what arrived stays selected, so it's easy to see
            var here = _vm.Path;
            FileOps.CopyOrMove(files, target, eff == DragDropEffects.Move, arrived =>
            {
                if (string.Equals(_vm.Path?.TrimEnd('\\'), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) _vm.ReloadAndSelect(arrived);
            });
        }

        // ================= navigation row =================
        private void Back_Click(object s, RoutedEventArgs e) => _vm?.GoBack();
        private void Forward_Click(object s, RoutedEventArgs e) => _vm?.GoForward();
        private void Up_Click(object s, RoutedEventArgs e) => _vm?.GoUp();
        private void Refresh_Click(object s, RoutedEventArgs e) => _vm?.Refresh();

        private void Back_RightUp(object sender, MouseButtonEventArgs e)
        {
            if (_vm.BackHistory.Count == 0) return;
            var cm = new ContextMenu();
            foreach (var p in _vm.BackHistory.Reverse().Distinct(StringComparer.OrdinalIgnoreCase).Take(20))
            {
                var path = p;
                cm.Items.Add(MainWindow.MI(FileSystem.DisplayName(path), null, () => _vm.Navigate(path)));
            }
            cm.PlacementTarget = (UIElement)sender;
            cm.Placement = PlacementMode.Bottom;
            cm.IsOpen = true;
            e.Handled = true;
        }

        private void BuildCrumbs()
        {
            var list = new List<Crumb> { new(Loc.T("ThisPC"), FileSystem.ThisPC) };
            var path = _vm.Path;
            if (!string.IsNullOrEmpty(path))
            {
                var root = Path.GetPathRoot(path) ?? path;
                list.Add(new(FileSystem.DisplayName(root), root));
                var rest = path.Length > root.Length ? path[root.Length..].Trim('\\') : "";
                var acc = root;
                foreach (var part in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
                {
                    acc = Path.Combine(acc, part);
                    list.Add(new(part, acc));
                }
            }
            Crumbs.ItemsSource = list;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => CrumbScroll.ScrollToRightEnd());
            ExitAddressEdit();
        }

        private void Crumb_Click(object sender, RoutedEventArgs e)
        {
            var p = (string)((FrameworkElement)sender).Tag;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) MainWindow.Instance.OpenInNewTab(p);
            else _vm.Navigate(p);
        }

        private void CrumbChevron_Click(object sender, RoutedEventArgs e)
        {
            var p = (string)((FrameworkElement)sender).Tag;
            var cm = new ContextMenu { MaxHeight = 520 };
            try
            {
                if (p == FileSystem.ThisPC)
                {
                    foreach (var d in DriveInfo.GetDrives())
                    {
                        var dn = d.Name;
                        cm.Items.Add(new MenuItem { Header = FileSystem.DisplayName(dn), Icon = new Image { Source = IconService.GetPathIcon(dn), Width = 16, Height = 16 } });
                        ((MenuItem)cm.Items[^1]).Click += (_, _) => _vm.Navigate(dn);
                    }
                }
                else
                {
                    var hidden = AppState.Settings.ShowHidden;
                    var dirs = new DirectoryInfo(p).EnumerateDirectories()
                        .Where(di => hidden || (di.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                        .Select(di => di.FullName).Take(400).ToList();
                    dirs.Sort((a, b) => Native.StrCmpLogicalW(Path.GetFileName(a), Path.GetFileName(b)));
                    var folderIcon = IconService.FolderIcon;
                    foreach (var dir in dirs)
                    {
                        var target = dir;
                        var mi = new MenuItem { Header = Path.GetFileName(dir), Icon = new Image { Source = folderIcon, Width = 16, Height = 16 } };
                        if (_vm.Path != null && _vm.Path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) mi.FontWeight = FontWeights.SemiBold;
                        mi.Click += (_, _) => _vm.Navigate(target);
                        cm.Items.Add(mi);
                    }
                    if (dirs.Count == 0) cm.Items.Add(new MenuItem { Header = Loc.T("NoSubfolders"), IsEnabled = false });
                }
            }
            catch (Exception ex) { cm.Items.Add(new MenuItem { Header = ex.Message, IsEnabled = false }); }
            ScrollViewer.SetVerticalScrollBarVisibility(cm, ScrollBarVisibility.Auto);
            cm.PlacementTarget = (UIElement)sender;
            cm.Placement = PlacementMode.Bottom;
            cm.IsOpen = true;
        }

        private void Crumb_DragOver(object sender, DragEventArgs e)
        {
            var p = (string)((FrameworkElement)sender).Tag;
            e.Effects = string.IsNullOrEmpty(p) || !e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.None : DropEffectFor(e, p);
            e.Handled = true;
        }

        private void Crumb_Drop(object sender, DragEventArgs e)
        {
            var p = (string)((FrameworkElement)sender).Tag;
            e.Handled = true;
            if (string.IsNullOrEmpty(p) || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
            var eff = DropEffectFor(e, p);
            if (eff == DragDropEffects.Link) { FileOps.CreateShortcuts(files, p); return; }
            FileOps.CopyOrMove(files, p, eff == DragDropEffects.Move);
        }

        private void CrumbBar_MouseUp(object sender, MouseButtonEventArgs e)
        {
            for (var d = e.OriginalSource as DependencyObject; d != null && d != CrumbBar; d = VisualTreeHelper.GetParent(d))
                if (d is ButtonBase) return;
            EnterAddressEdit();
        }

        private void EnterAddressEdit()
        {
            AddressBox.Text = _vm.IsThisPC ? Loc.T("ThisPC") : _vm.Path;
            AddressBox.Visibility = Visibility.Visible;
            CrumbBar.Visibility = Visibility.Hidden;
            AddressBox.Focus();
            AddressBox.SelectAll();
        }

        private void ExitAddressEdit()
        {
            AddressBox.Visibility = Visibility.Collapsed;
            CrumbBar.Visibility = Visibility.Visible;
        }

        private void AddressBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                var text = AddressBox.Text;
                ExitAddressEdit();
                _vm.Navigate(text);
                FocusList();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape) { ExitAddressEdit(); FocusList(); e.Handled = true; }
            else if (e.Key == Key.Tab) { CompletePath(); e.Handled = true; }
        }

        /// <summary>Tab in the address bar completes the folder name (like a terminal).</summary>
        private void CompletePath()
        {
            try
            {
                var text = Environment.ExpandEnvironmentVariables(AddressBox.Text);
                var dir = Path.GetDirectoryName(text);
                var prefix = Path.GetFileName(text);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                var match = Directory.EnumerateDirectories(dir, prefix + "*").Select(Path.GetFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (match == null) return;
                AddressBox.Text = Path.Combine(dir, match) + "\\";
                AddressBox.CaretIndex = AddressBox.Text.Length;
            }
            catch { }
        }

        private void AddressBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => ExitAddressEdit();

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { _vm.SearchText = SearchBox.Text; _vm.SearchRecursive(); e.Handled = true; }
            else if (e.Key == Key.Escape) { SearchBox.Text = ""; _vm.SearchText = ""; FocusList(); e.Handled = true; }
            else if (e.Key == Key.Down) { FocusList(); e.Handled = true; }
        }

        private void SearchClear_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = "";
            _vm.SearchText = "";
            FocusList();
        }
    }
}
