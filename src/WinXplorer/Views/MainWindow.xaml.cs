using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinXplorer.Interop;
using WinXplorer.Models;
using WinXplorer.Services;
using WinXplorer.ViewModels;
using WinXplorer.Views;

namespace WinXplorer
{
    public partial class MainWindow : Window
    {
        public static MainWindow Instance { get; private set; }
        public MainViewModel VM { get; } = new();
        public IntPtr Hwnd { get; private set; }

        private readonly DispatcherTimer _previewTimer;
        private readonly DispatcherTimer _deviceTimer;
        private PaneViewModel _watchedPane;

        public static readonly DependencyProperty TabWidthProperty =
            DependencyProperty.Register(nameof(TabWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(210.0));
        public double TabWidth { get => (double)GetValue(TabWidthProperty); set => SetValue(TabWidthProperty, value); }

        public static readonly DependencyProperty TabsNarrowProperty =
            DependencyProperty.Register(nameof(TabsNarrow), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
        public bool TabsNarrow { get => (bool)GetValue(TabsNarrowProperty); set => SetValue(TabsNarrowProperty, value); }

        /// <summary>True for windows opened for one folder (tab dragged out, Ctrl+N): they don't overwrite the saved session.</summary>
        private readonly bool _isSecondary;

        public MainWindow(StartOptions opt)
        {
            Instance = this;
            InitializeComponent();
            // a new window (or a torn-off tab) fades in instead of popping up
            Loaded += (_, _) => (Content as UIElement)?.BeginAnimation(OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop });
            DataContext = VM;
            opt ??= new StartOptions();
            var startPath = opt.Path;
            _isSecondary = startPath != null;

            var icon = App.LoadAppIcon();
            AppIcon.Source = icon;
            if (icon != null) Icon = RenderIcon(icon, 64);

            var s = AppState.Settings;
            Width = s.WinWidth; Height = s.WinHeight;
            if (!double.IsNaN(s.WinLeft) && !double.IsNaN(s.WinTop)
                && s.WinLeft < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100
                && s.WinTop < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100
                && s.WinLeft > SystemParameters.VirtualScreenLeft - 100 && s.WinTop >= SystemParameters.VirtualScreenTop - 10)
            { Left = s.WinLeft; Top = s.WinTop; }
            else WindowStartupLocation = WindowStartupLocation.CenterScreen;
            if (opt.Bounds is Rect rb && rb.Left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100
                && rb.Top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100 && rb.Right > SystemParameters.VirtualScreenLeft + 100)
            {
                // a folder window reopened after a restart: where and how big it was
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = rb.Left; Top = rb.Top; Width = rb.Width; Height = rb.Height;
            }
            if ((opt.Bounds != null ? opt.Maximized : s.WinMaximized) && opt.ScreenPos == null) WindowState = WindowState.Maximized;
            if (opt.ScreenPos is Point sp)
            {
                // torn-off tab: put the new window's tab strip under the mouse (converted to DIPs once the DPI is known)
                WindowStartupLocation = WindowStartupLocation.Manual;
                double dpi = Math.Max(1, VisualTreeHelper.GetDpi(this).DpiScaleX);
                Left = sp.X / dpi - 120; Top = sp.Y / dpi - 20;
                Width = Math.Min(Width, 1100); Height = Math.Min(Height, 720);
            }

            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(110) };
            _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); UpdatePreview(); };
            _deviceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _deviceTimer.Tick += (_, _) =>
            {
                _deviceTimer.Stop();
                VM.Sidebar.Rebuild();
                foreach (var p in VM.AllPanes.ToList())
                    if (p.IsThisPC) p.Refresh();
            };

            SidebarCol.Width = new GridLength(Math.Max(0, s.SidebarWidth));
            ApplyPreviewVisibility();

            TabStrip.ItemsSource = VM.Tabs;
            TabHost.ItemsSource = VM.Tabs;
            SidebarList.ItemsSource = VM.Sidebar.Items;
            VM.Sidebar.Rebuild();

            PaneViewModel.AnySelectionChanged += p => { if (p == VM.ActivePane) SchedulePreview(); };
            PaneViewModelSelectionBridge.ActivePaneChanged += _ => { UpdateChrome(); SchedulePreview(); };
            Views.PaneView.NavHost = NavHost;
            VM.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.IsSettingsActive)) ApplySettingsMode();
                if (e.PropertyName == nameof(MainViewModel.SelectedTab)) BringSelectedTabIntoView();
                if (e.PropertyName is nameof(MainViewModel.SelectedTab) or nameof(MainViewModel.IsSettingsActive))
                    Dispatcher.BeginInvoke(new Action(() => Views.PaneView.PlaceNav(VM.IsSettingsActive ? null : VM.ActivePane)), System.Windows.Threading.DispatcherPriority.Loaded);
            };
            VM.Tabs.CollectionChanged += (_, _) => UpdateTabWidths();
            TabArea.SizeChanged += (_, _) => UpdateTabWidths();
            SettingsNav.ItemsSource = SettingsView.Sections;
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };
            Notify.Message += ShowToast;

            if (startPath != null)
            {
                var saved = opt.Split != null && opt.Split != "none" ? new SavedTab { Left = startPath, Right = opt.Right ?? startPath, Split = opt.Split } : null;
                VM.NewTab(startPath, true, -1, saved);
            }
            else if (s.RestoreTabs && s.Tabs.Count > 0)
            {
                foreach (var t in s.Tabs) VM.NewTab(t.Left, false, -1, t);
                VM.SelectedTab = VM.Tabs[Math.Clamp(s.SelectedTab, 0, VM.Tabs.Count - 1)];
            }
            else VM.NewTab(FileSystem.ThisPC);
            if (opt.OpenSettings) VM.OpenSettings();

            SourceInitialized += OnSourceInitialized;
            StateChanged += (_, _) => UpdateMaxState();
            ThemeManager.ThemeChanged += () => ThemeManager.ApplyWindowFrame(Hwnd);
            Closing += OnClosing;
            PreviewKeyDown += OnPreviewKeyDown;
            Activated += (_, _) => FileOps.ValidateCut();
            // the sidebar's frequent block follows the folders you open (refreshed when idle, not on every click)
            FolderUsage.Changed += () => Dispatcher.BeginInvoke(new Action(() => VM.Sidebar.RefreshFrequent()), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            FileOps.CutChanged += () => { foreach (var p in VM.AllPanes) foreach (var it in p.Items ?? new()) it.RefreshOpacity(); };
            PreviewMouseDown += (_, e) =>
            {
                if (e.ChangedButton == MouseButton.XButton1) { VM.ActivePane?.GoBack(); e.Handled = true; }
                else if (e.ChangedButton == MouseButton.XButton2) { VM.ActivePane?.GoForward(); e.Handled = true; }
            };
            UpdateChrome();
        }

        private static ImageSource RenderIcon(ImageSource img, int size)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawImage(img, new Rect(0, 0, size, size));
            var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        // ================= window chrome =================
        private void OnSourceInitialized(object sender, EventArgs e)
        {
            Hwnd = new WindowInteropHelper(this).Handle;
            FileOps.MainHwnd = Hwnd;
            var src = HwndSource.FromHwnd(Hwnd);
            src.AddHook(ShellContextMenu.WndProcHook);
            src.AddHook(DeviceHook);
            src.AddHook(CopyDataHook);
            src.AddHook(EndSessionHook);
            ThemeManager.ApplyWindowFrame(Hwnd);
            AppState.DpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            UpdateMaxState();
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            AppState.DpiScale = newDpi.DpiScaleX;
        }

        /// <summary>
        /// Windows restarts / signs out: remember this window (the main one restores its saved tabs) and ask Windows
        /// to start WinXplorer once at the next sign-in, which reopens them – like Explorer's "restore folder windows".
        /// </summary>
        private IntPtr EndSessionHook(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg != 0x0011 /*WM_QUERYENDSESSION*/ || _endSessionSaved) return IntPtr.Zero;
            _endSessionSaved = true;
            try
            {
                string line;
                var b = RestoreBounds;
                bool max = WindowState == WindowState.Maximized;
                if (!_isSecondary)
                {
                    var st0 = AppState.Settings;
                    st0.WinMaximized = max;
                    if (!b.IsEmpty) { st0.WinLeft = b.Left; st0.WinTop = b.Top; st0.WinWidth = b.Width; st0.WinHeight = b.Height; }
                    VM.SaveSession(); AppState.Save(); line = "--session";
                }
                else
                {
                    var t = VM.SelectedTab is { IsSettings: false } st ? st : VM.Tabs.FirstOrDefault(x => !x.IsSettings);
                    if (t == null) return IntPtr.Zero;
                    line = StartOptions.Build(t.Primary.Path, null, t.IsSplit ? t.Split : null, t.Secondary?.Path);
                    if (!b.IsEmpty) line += FormattableString.Invariant($" --bounds {b.Left:0},{b.Top:0},{b.Width:0},{b.Height:0}") + (max ? ",max" : "");
                }
                using var m = new System.Threading.Mutex(false, "WinXplorer.Restore");
                m.WaitOne(2000);
                try
                {
                    File.AppendAllText(Path.Combine(AppState.DataDir, "restore.txt"), line + "\n");
                    using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\RunOnce");
                    k.SetValue("WinXplorerRestore", "\"" + Environment.ProcessPath + "\" --restore-windows");
                }
                finally { m.ReleaseMutex(); }
            }
            catch { }
            return IntPtr.Zero;
        }
        private bool _endSessionSaved;

        /// <summary>"--restore-windows" (next sign-in): starts one WinXplorer per window that was open.</summary>
        public static void RestoreWindows()
        {
            var f = Path.Combine(AppState.DataDir, "restore.txt");
            try
            {
                var lines = File.ReadAllLines(f).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();
                File.Delete(f);
                foreach (var a in lines)
                    Process.Start(new ProcessStartInfo(Environment.ProcessPath!, a == "--session" ? "" : a) { UseShellExecute = false });
            }
            catch { }
        }

        private IntPtr DeviceHook(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == 0x0219 /*WM_DEVICECHANGE*/ && (w.ToInt32() == 0x8000 || w.ToInt32() == 0x8004))
            { _deviceTimer.Stop(); _deviceTimer.Start(); }
            return IntPtr.Zero;
        }

        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);

        private void UpdateMaxState()
        {
            bool max = WindowState == WindowState.Maximized;
            MaxGlyph.Text = max ? "" : "";
            if (max)
            {
                double scale = AppState.DpiScale <= 0 ? 1 : AppState.DpiScale;
                double frame = (GetSystemMetrics(32 /*SM_CXSIZEFRAME*/) + GetSystemMetrics(92 /*SM_CXPADDEDBORDER*/)) / scale;
                Root.Margin = new Thickness(frame);
            }
            else Root.Margin = new Thickness(0);
        }

        private void Min_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void Max_Click(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void Close_Click(object s, RoutedEventArgs e) => Close();

        private void OnClosing(object sender, CancelEventArgs e)
        {
            var st = AppState.Settings;
            st.WinMaximized = WindowState == WindowState.Maximized;
            var b = RestoreBounds;
            if (!b.IsEmpty) { st.WinLeft = b.Left; st.WinTop = b.Top; st.WinWidth = b.Width; st.WinHeight = b.Height; }
            st.SidebarWidth = SidebarCol.ActualWidth;
            if (st.ShowPreview && !VM.IsSettingsActive) st.PreviewWidth = PreviewCol.ActualWidth;
            if (!_isSecondary) VM.SaveSession();
            AppState.Save(keepSavedSession: _isSecondary);
            Preview.Clear();
        }

        // ================= state sync =================
        private void UpdateChrome()
        {
            var pane = VM.ActivePane;
            Views.PaneView.PlaceNav(VM.IsSettingsActive ? null : pane);
            if (_watchedPane != pane)
            {
                if (_watchedPane != null) _watchedPane.PropertyChanged -= ActivePane_PropertyChanged;
                _watchedPane = pane;
                if (pane != null) pane.PropertyChanged += ActivePane_PropertyChanged;
            }
            var tab = VM.SelectedTab;
            SplitNone.IsChecked = tab?.Split == "none";
            SplitV.IsChecked = tab?.Split == "vertical";
            SplitH.IsChecked = tab?.Split == "horizontal";
            VM.Sidebar.UpdateCurrent(pane?.Path);
            Title = (pane?.Title ?? tab?.Title ?? "") + " - WinXplorer";
        }

        /// <summary>While the Settings tab is shown: sidebar lists the sections, file commands are disabled.</summary>
        private void ApplySettingsMode()
        {
            bool on = VM.IsSettingsActive;
            SettingsNav.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            SidebarScroll.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            SidebarAddBtn.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            CommandsLeft.IsEnabled = !on;
            CommandsRight.IsEnabled = !on;
            if (on)
            {
                SettingsNav.SelectedItem = SettingsView.Sections.FirstOrDefault(x => x.Key == SettingsView.CurrentSection);
                SettingsNav.SelectedIndex = Math.Max(0, SettingsView.Sections.ToList().FindIndex(x => x.Key == SettingsView.CurrentSection));
            }
            ApplyPreviewVisibility();
        }

        private void SettingsNav_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SettingsNav.SelectedItem is SettingsView.Section sec && sec.Key != SettingsView.CurrentSection)
                SettingsView.Current?.Show(sec.Key);
        }

        private readonly DispatcherTimer _toastTimer;

        private void ShowToast(string text)
        {
            ToastText.Text = text;
            Toast.Visibility = Visibility.Visible;
            Toast.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        public void OpenSettings(string section = null)
        {
            if (section != null)
            {
                if (SettingsView.Current != null) SettingsView.Current.Show(section);
                else SettingsView.CurrentSection = section;
            }
            VM.OpenSettings();
            SettingsNav.SelectedIndex = Math.Max(0, SettingsView.Sections.ToList().FindIndex(x => x.Key == SettingsView.CurrentSection));
        }

        private void ActivePane_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PaneViewModel.Path)) UpdateChrome();
        }

        private void SchedulePreview()
        {
            _previewTimer.Stop();
            _previewTimer.Start();
        }

        private void UpdatePreview()
        {
            if (!AppState.Settings.ShowPreview) return;
            var pane = VM.ActivePane;
            Preview.Show(pane?.FirstSelected, pane);
        }

        public void SetPreviewVisible(bool show)
        {
            if (AppState.Settings.ShowPreview == show) return;
            PreviewToggle_Click(null, null);
        }

        private void ApplyPreviewVisibility()
        {
            bool show = AppState.Settings.ShowPreview && !VM.IsSettingsActive;
            PreviewToggle.IsChecked = show;
            PreviewCol.Width = show ? new GridLength(Math.Max(200, AppState.Settings.PreviewWidth)) : new GridLength(0);
            PreviewSplitCol.Width = show ? new GridLength(5) : new GridLength(0);
            PreviewSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            Preview.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) Preview.Clear(); else SchedulePreview();
        }

        private void Splitter_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            AppState.Settings.SidebarWidth = SidebarCol.ActualWidth;
            if (AppState.Settings.ShowPreview && PreviewCol.ActualWidth > 0) AppState.Settings.PreviewWidth = PreviewCol.ActualWidth;
        }

        // ================= public API for panes =================
        public void OpenInNewTab(string path, bool select = false)
        {
            int idx = VM.Tabs.IndexOf(VM.SelectedTab) + 1;
            VM.NewTab(path, select, idx);
        }

        public void OpenInOtherPane(PaneViewModel from, string path)
        {
            var tab = from.Tab;
            if (!tab.IsSplit) tab.Split = "vertical";
            var other = tab.Primary == from ? tab.Secondary : tab.Primary;
            other.Navigate(path);
            UpdateChrome();
        }

        public void PinToSidebar(string path) => VM.Sidebar.AddFolder(path);

        /// <summary>Space: pictures open in the image viewer, every other file in the quick view (text, code, media, documents).</summary>
        public void ShowViewer(PaneViewModel pane, FileItem item, bool toggle = true)
        {
            // Space toggles: a second press closes the open viewer
            if (_viewer != null) { _viewer.Close(); if (toggle) return; }
            if (item.InArchive && item.IsDirectory) return;   // a folder inside an archive: open it instead (Enter)
            if (item.InArchive)
            {
                // inside an archive: extract it and show that file
                var real = FileOps.Real(item.FullPath);
                var fi = real == null ? null : FileSystem.Stat(real, System.IO.Path.GetDirectoryName(real));
                if (fi == null) return;
                item = fi;
            }
            if (item.IsDirectory || !FileTypes.IsViewableImage(item.FullPath))
            {
                // folders too: ← → in the quick view go through everything in the list
                var files = pane.Items.Where(i => !i.IsDrive && !(i.IsDirectory && i.InArchive)).ToList();
                int fi = files.IndexOf(item);
                if (fi < 0) { files = new() { item }; fi = 0; }
                var q = new QuickViewWindow(files, fi, pane, this);
                _viewer = q;
                q.Closed += (_, _) =>
                {
                    _viewer = null;
                    var last = q.CurrentItem;
                    Activate();
                    if (last != null && pane.Items.Contains(last)) pane.SelectAndFocus(last.FullPath);
                };
                q.Show();
                return;
            }
            var images = pane.Items.Where(i => !i.IsDirectory && FileTypes.IsViewableImage(i.FullPath)).ToList();
            int idx = images.IndexOf(item);
            if (idx < 0) { images = new() { item }; idx = 0; }
            var w = new ImageViewerWindow(images, idx, this);
            _viewer = w;
            w.Closed += (_, _) =>
            {
                _viewer = null;
                var last = w.CurrentItem;
                Activate();
                if (last != null && pane.Items.Contains(last)) pane.SelectAndFocus(last.FullPath);
            };
            w.Show();
        }

        private Window _viewer;

        // ================= keyboard =================
        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Hotkeys.Capturing || Hotkeys.FromEvent(e) is not { } g) return;
            var pane = VM.ActivePane;

            // Ctrl+1…9: go to tab N (9 = last) – fixed, not remappable
            if (g.Mods == ModifierKeys.Control && g.Key >= Key.D1 && g.Key <= Key.D9)
            {
                int i = g.Key == Key.D9 ? VM.Tabs.Count - 1 : g.Key - Key.D1;
                if (i >= 0 && i < VM.Tabs.Count) VM.SelectedTab = VM.Tabs[i];
                e.Handled = true;
                return;
            }

            var cmd = Hotkeys.Match(HkScope.Main, e);
            if (cmd == null) return;
            // a plain key (no Ctrl/Alt) typed into a text field belongs to the text field
            if (Hotkeys.InText && Hotkeys.IsTextKey(g)) return;
            switch (cmd)
            {
                case "NewTab": OpenInNewTab(NewTabPath, true); break;
                case "CloseTab": if (VM.SelectedTab != null) CloseTabAnimated(VM.SelectedTab); break;
                case "NextTab": VM.CycleTab(1); break;
                case "PrevTab": VM.CycleTab(-1); break;
                case "Back": pane?.GoBack(); break;
                case "Forward": pane?.GoForward(); break;
                case "Up": pane?.GoUp(); break;
                case "Address": pane?.FocusAddress(); break;
                case "Search": pane?.FocusSearch(); break;
                case "Refresh": pane?.Refresh(); break;
                case "OtherPane":
                    if (VM.SelectedTab?.IsSplit != true) return;
                    var t = VM.SelectedTab; t.Active = t.Other; t.Active.Focus(); break;
                case "NewFolder": pane?.NewFolder(); break;
                case "NewWindow": OpenNewWindow(pane?.Path ?? FileSystem.ThisPC); break;
                case "Settings": VM.OpenSettings(); break;
                case "Undo":
                    if (Hotkeys.InText) return;
                    UndoService.Undo(); break;
                case "Preview": PreviewToggle_Click(null, null); break;
                default: return;
            }
            e.Handled = true;
        }

        // ================= tabs =================
        private void NewTab_Click(object s, RoutedEventArgs e) => OpenInNewTab(NewTabPath, true);

        /// <summary>Where a new tab starts: the current folder, or This PC (Settings › General).</summary>
        private string NewTabPath => AppState.Settings.NewTabThisPC ? FileSystem.ThisPC : VM.ActivePane?.Path ?? FileSystem.ThisPC;

        /// <summary>Opens a folder in a new WinXplorer window (separate process, so windows never block each other).</summary>
        public static void OpenNewWindow(string path, Point? screenPos = null, TabViewModel layout = null)
        {
            try
            {
                var args = StartOptions.Build(path, screenPos, layout?.IsSplit == true ? layout.Split : null, layout?.Secondary?.Path);
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = false });
            }
            catch { }
        }

        // ---- tab dragging: reorder inside the strip, drop outside the window to open it in a new window ----
        private TabViewModel _dragTab;
        private Point _tabDownPos;
        private bool _tabDragging, _tabTorn;

        private void Tab_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && ((FrameworkElement)sender).DataContext is TabViewModel t)
            {
                VM.SelectedTab = t;
                _dragTab = t;
                _tabDownPos = e.GetPosition(TabStrip);
                _tabDragging = _tabTorn = false;
            }
        }

        private void TabStrip_MouseMove(object sender, MouseEventArgs e)
        {
            if (_dragTab == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { EndTabDrag(false); return; }
            var pos = e.GetPosition(TabStrip);
            if (!_tabDragging)
            {
                if (Math.Abs(pos.X - _tabDownPos.X) < SystemParameters.MinimumHorizontalDragDistance * 2 &&
                    Math.Abs(pos.Y - _tabDownPos.Y) < SystemParameters.MinimumVerticalDragDistance * 2) return;
                _tabDragging = true;
                TabStrip.CaptureMouse();
            }

            // torn off: pulled clearly away from the tab strip (and there's another tab left)
            // (also when it's the only tab: dropping it on another WinXplorer window merges the windows)
            var inWin = e.GetPosition(this);
            bool outside = inWin.X < 0 || inWin.Y < 0 || inWin.X > ActualWidth || inWin.Y > ActualHeight;
            bool torn = !_dragTab.IsSettings && (pos.Y < -40 || pos.Y > TabStrip.ActualHeight + 50 || outside);
            if (torn != _tabTorn)
            {
                _tabTorn = torn;
                TabGhostText.Text = _dragTab.Title;
                TabGhost.IsOpen = torn;
                Mouse.OverrideCursor = torn ? Cursors.Hand : null;
            }
            if (torn)
            {
                var screen = PointToScreen(e.GetPosition(this));
                double dpi = Math.Max(1, AppState.DpiScale);
                TabGhost.HorizontalOffset = screen.X / dpi + 12;
                TabGhost.VerticalOffset = screen.Y / dpi + 8;
                return;
            }

            // reorder: the slot under the mouse
            double slot = TabWidth + 2;
            int to = Math.Clamp((int)(pos.X / slot), 0, VM.Tabs.Count - 1);
            int from = VM.Tabs.IndexOf(_dragTab);
            if (from >= 0 && to != from) VM.Tabs.Move(from, to);
            // auto-scroll when dragging past the visible edge
            var inScroll = e.GetPosition(TabScroll).X;
            if (inScroll < 20) TabScroll.ScrollToHorizontalOffset(TabScroll.HorizontalOffset - 12);
            else if (inScroll > TabScroll.ActualWidth - 20) TabScroll.ScrollToHorizontalOffset(TabScroll.HorizontalOffset + 12);
        }

        private void TabStrip_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragTab == null) return;
            bool wasDragging = _tabDragging;
            EndTabDrag(true);
            if (wasDragging) e.Handled = true;
        }

        private void TabStrip_LostCapture(object sender, MouseEventArgs e)
        {
            // LostMouseCapture bubbles from children too: only react when the strip itself lost it
            if (_tabDragging && e.OriginalSource == TabStrip && Mouse.Captured != TabStrip) EndTabDrag(false);
        }

        private void EndTabDrag(bool drop)
        {
            var tab = _dragTab;
            bool torn = _tabTorn;
            _dragTab = null;
            _tabDragging = _tabTorn = false;
            TabGhost.IsOpen = false;
            Mouse.OverrideCursor = null;
            if (TabStrip.IsMouseCaptured) TabStrip.ReleaseMouseCapture();
            if (!drop || !torn || tab == null || !VM.Tabs.Contains(tab)) return;
            Native.GetCursorPos(out var pt);
            // dropped on another WinXplorer window: move the tab there (like Windows Explorer)
            if (SendTabToWindowAt(pt.X, pt.Y, tab))
            {
                if (VM.Tabs.Count == 1) Close(); else VM.CloseTab(tab);
                return;
            }
            if (VM.Tabs.Count < 2) return; // the only tab dropped on nothing – keep it
            OpenNewWindow(tab.Primary.Path, new Point(pt.X, pt.Y), tab);
            CloseTabAnimated(tab);
        }

        // ---- moving a tab to another WinXplorer window (separate process) via WM_COPYDATA ----
        [StructLayout(LayoutKind.Sequential)]
        private struct COPYDATASTRUCT { public IntPtr dwData; public int cbData; public IntPtr lpData; }
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Native.POINT p);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeoutW(IntPtr h, int msg, IntPtr w, ref COPYDATASTRUCT l, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
        private const int WM_COPYDATA = 0x004A;
        private static readonly IntPtr TabMagic = new(0x57585442); // "WXTB"

        [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int pid);

        /// <summary>Folder already open in another WinXplorer window: brings that window and tab up (true) instead of opening a duplicate.</summary>
        public static bool ActivateExisting(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var msg = "FIND\n" + path;
            var mem = Marshal.StringToHGlobalUni(msg);
            try
            {
                using var me = Process.GetCurrentProcess();
                foreach (var p in Process.GetProcessesByName(me.ProcessName))
                    using (p)
                    {
                        if (p.Id == me.Id) continue;
                        var h = p.MainWindowHandle;
                        if (h == IntPtr.Zero) continue;
                        AllowSetForegroundWindow(p.Id);
                        var cds = new COPYDATASTRUCT { dwData = TabMagic, cbData = (msg.Length + 1) * 2, lpData = mem };
                        SendMessageTimeoutW(h, WM_COPYDATA, IntPtr.Zero, ref cds, 0x2, 1500, out var res);
                        if (res == new IntPtr(1)) { SetForegroundWindow(h); return true; }
                    }
            }
            catch { }
            finally { Marshal.FreeHGlobal(mem); }
            return false;
        }

        private bool SendTabToWindowAt(int x, int y, TabViewModel tab)
        {
            try
            {
                var target = GetAncestor(WindowFromPoint(new Native.POINT { X = x, Y = y }), 2 /*GA_ROOT*/);
                if (target == IntPtr.Zero || target == Hwnd) return false;
                GetWindowThreadProcessId(target, out var pid);
                if (pid == (uint)Environment.ProcessId) return false;
                using (var p = Process.GetProcessById((int)pid))
                    if (!string.Equals(p.ProcessName, Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase)) return false;
                var msg = string.Join("\n", "TAB", tab.Primary.Path ?? "", tab.IsSplit ? tab.Split : "none", tab.Secondary?.Path ?? "", x.ToString(), y.ToString());
                var mem = Marshal.StringToHGlobalUni(msg);
                try
                {
                    var cds = new COPYDATASTRUCT { dwData = TabMagic, cbData = (msg.Length + 1) * 2, lpData = mem };
                    SendMessageTimeoutW(target, WM_COPYDATA, Hwnd, ref cds, 0x2 /*SMTO_ABORTIFHUNG*/, 3000, out var res);
                    if (res != new IntPtr(1)) return false;
                }
                finally { Marshal.FreeHGlobal(mem); }
                SetForegroundWindow(target);
                return true;
            }
            catch { return false; }
        }

        private IntPtr CopyDataHook(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg != WM_COPYDATA) return IntPtr.Zero;
            try
            {
                var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(l);
                if (cds.dwData != TabMagic || cds.lpData == IntPtr.Zero) return IntPtr.Zero;
                var parts = Marshal.PtrToStringUni(cds.lpData, cds.cbData / 2 - 1).Split('\n');
                if (parts.Length == 2 && parts[0] == "FIND")
                {
                    // another launch asks: is this folder already open here? then show it instead of a second window
                    static string N(string p) => (p ?? "").TrimEnd('\\');
                    var want = N(parts[1]);
                    // a file (Explorer was asked to show it selected): its folder, with the file selected
                    string select = null;
                    if (File.Exists(want)) { select = want; want = N(Path.GetDirectoryName(want)); }
                    var tab = VM.Tabs.FirstOrDefault(t => !t.IsSettings && (string.Equals(N(t.Primary?.Path), want, StringComparison.OrdinalIgnoreCase)
                        || t.IsSplit && string.Equals(N(t.Secondary?.Path), want, StringComparison.OrdinalIgnoreCase)));
                    if (tab == null) return IntPtr.Zero;
                    handled = true;
                    VM.SelectedTab = tab;
                    if (select != null)
                    {
                        var pane = string.Equals(N(tab.Primary?.Path), want, StringComparison.OrdinalIgnoreCase) ? tab.Primary : tab.Secondary;
                        pane?.SelectAndFocus(select);
                    }
                    if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                    Activate();
                    return new IntPtr(1);
                }
                if (parts.Length < 6 || parts[0] != "TAB") return IntPtr.Zero;
                handled = true;
                var saved = parts[2] != "none" ? new SavedTab { Left = parts[1], Right = parts[3], Split = parts[2] } : null;
                int index = VM.Tabs.Count;
                if (int.TryParse(parts[4], out var sx) && int.TryParse(parts[5], out var sy))
                {
                    var pt = TabStrip.PointFromScreen(new Point(sx, sy));
                    if (pt.Y > -60 && pt.Y < TabStrip.ActualHeight + 60)
                        index = Math.Clamp((int)Math.Round(pt.X / (TabWidth + 2)), 0, VM.Tabs.Count);
                }
                VM.NewTab(parts[1], true, index, saved);
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                return new IntPtr(1);
            }
            catch { return IntPtr.Zero; }
        }

        /// <summary>Tabs share the strip: wide when few, narrower as more open, then the strip scrolls.</summary>
        private void UpdateTabWidths()
        {
            const double Max = 210, Min = 92, Gap = 2;
            double avail = TabArea.ActualWidth - NewTabBtn.ActualWidth - 6 - 56; // keep some title bar to drag the window
            int n = Math.Max(1, VM.Tabs.Count);
            if (avail <= 0) return;
            double w = Math.Clamp(Math.Floor(avail / n) - Gap, Min, Max);
            TabWidth = w;
            TabsNarrow = w < 140;
            TabScroll.MaxWidth = Math.Max(Min, avail);
            BringSelectedTabIntoView();
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdateTabOverflow);
        }

        private void BringSelectedTabIntoView()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (VM.SelectedTab != null && TabStrip.ItemContainerGenerator.ContainerFromItem(VM.SelectedTab) is FrameworkElement c)
                    c.BringIntoView();
            });
        }

        private void Tab_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle && ((FrameworkElement)sender).DataContext is TabViewModel t)
            {
                CloseTabAnimated(t); e.Handled = true;
            }
        }

        private void Tab_DragOver(object sender, DragEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is TabViewModel t && VM.SelectedTab != t) VM.SelectedTab = t;
        }

        private void CloseTab_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is TabViewModel t) CloseTabAnimated(t);
        }

        /// <summary>The tab shrinks and fades away, then closes.</summary>
        private void CloseTabAnimated(TabViewModel t)
        {
            if (VM.Tabs.Count <= 1 || TabStrip.ItemContainerGenerator.ContainerFromItem(t) is not FrameworkElement cp
                || VisualTreeHelper.GetChildrenCount(cp) == 0 || VisualTreeHelper.GetChild(cp, 0) is not FrameworkElement bd || bd.Tag is "closing")
            { if (VM.Tabs.Contains(t)) VM.CloseTab(t); return; }
            bd.Tag = "closing";
            bd.IsHitTestVisible = false;
            var d = TimeSpan.FromMilliseconds(140);
            bd.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, d));
            var w = new System.Windows.Media.Animation.DoubleAnimation(bd.ActualWidth, 0, d) { EasingFunction = new System.Windows.Media.Animation.CubicEase() };
            w.Completed += (_, _) => { if (VM.Tabs.Contains(t)) VM.CloseTab(t); };
            bd.BeginAnimation(WidthProperty, w);
        }

        private void TabScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateTabOverflow();

        /// <summary>Fade the strip where tabs continue off-screen and show how many are hidden on each side.</summary>
        private void UpdateTabOverflow()
        {
            double offset = TabScroll.HorizontalOffset, view = TabScroll.ViewportWidth, extent = TabScroll.ExtentWidth;
            double slot = TabWidth + 2;
            int n = VM.Tabs.Count;
            int left = 0, right = 0;
            if (extent > view + 1 && slot > 0)
            {
                left = Math.Clamp((int)Math.Floor((offset + slot * 0.5) / slot), 0, n);
                right = Math.Clamp(n - (int)Math.Ceiling((offset + view - slot * 0.5) / slot), 0, n);
            }
            TabsMoreLeft.Visibility = left > 0 ? Visibility.Visible : Visibility.Collapsed;
            TabsMoreRight.Visibility = right > 0 ? Visibility.Visible : Visibility.Collapsed;
            TabsMoreLeftText.Text = left.ToString();
            TabsMoreRightText.Text = right.ToString();
            TabsMoreLeft.ToolTip = Loc.F("TabsHiddenLeft", left);
            TabsMoreRight.ToolTip = Loc.F("TabsHiddenRight", right);
            if (left == 0 && right == 0 || view <= 0) { TabScroll.OpacityMask = null; return; }
            double f = Math.Min(0.3, 70 / view);
            var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            mask.GradientStops.Add(new GradientStop(left > 0 ? Color.FromArgb(0, 0, 0, 0) : Colors.Black, 0));
            mask.GradientStops.Add(new GradientStop(Colors.Black, left > 0 ? f : 0));
            mask.GradientStops.Add(new GradientStop(Colors.Black, right > 0 ? 1 - f : 1));
            mask.GradientStops.Add(new GradientStop(right > 0 ? Color.FromArgb(0, 0, 0, 0) : Colors.Black, 1));
            TabScroll.OpacityMask = mask;
        }

        private void TabsMoreLeft_Click(object sender, RoutedEventArgs e) => TabScroll.ScrollToHorizontalOffset(TabScroll.HorizontalOffset - TabScroll.ViewportWidth * 0.75);
        private void TabsMoreRight_Click(object sender, RoutedEventArgs e) => TabScroll.ScrollToHorizontalOffset(TabScroll.HorizontalOffset + TabScroll.ViewportWidth * 0.75);

        private void TabScroll_Wheel(object sender, MouseWheelEventArgs e)
        {
            TabScroll.ScrollToHorizontalOffset(TabScroll.HorizontalOffset - e.Delta / 2.0);
            e.Handled = true;
        }
        // ================= command bar =================
        private PaneViewModel Pane => VM.ActivePane;

        private void Cut_Click(object s, RoutedEventArgs e) => FileOps.SetClipboard(Pane?.SelectedPaths, true);
        private void Copy_Click(object s, RoutedEventArgs e) => FileOps.SetClipboard(Pane?.SelectedPaths, false);
        private void Paste_Click(object s, RoutedEventArgs e) { if (Pane != null && !Pane.IsThisPC) FileOps.Paste(Pane.Path); }
        private void Rename_Click(object s, RoutedEventArgs e) => Pane?.Rename();
        private void Delete_Click(object s, RoutedEventArgs e) => FileOps.Delete(Pane?.SelectedPaths, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        private void CopyPath_Click(object s, RoutedEventArgs e)
        {
            if (Pane == null) return;
            var paths = Pane.SelectedPaths;
            FileOps.SetClipboardText(paths.Length > 0 ? string.Join(Environment.NewLine, paths) : Pane.Path);
        }
        private void Properties_Click(object s, RoutedEventArgs e)
        {
            if (Pane == null) return;
            var p = Pane.FirstSelected?.FullPath ?? Pane.Path;
            if (!string.IsNullOrEmpty(p)) FileOps.ShowProperties(p);
        }
        private void Terminal_Click(object s, RoutedEventArgs e) => FileOps.OpenTerminal(Pane?.Path);
        private void Donate_Click(object s, RoutedEventArgs e) => FileOps.OpenUrl(AppState.DonateUrl);

        private void Split_Click(object sender, RoutedEventArgs e)
        {
            if (VM.SelectedTab != null) VM.SelectedTab.Split = (string)((FrameworkElement)sender).Tag;
            UpdateChrome();
        }

        private void PreviewToggle_Click(object sender, RoutedEventArgs e)
        {
            if (AppState.Settings.ShowPreview && PreviewCol.ActualWidth > 0) AppState.Settings.PreviewWidth = PreviewCol.ActualWidth;
            AppState.Settings.ShowPreview = !AppState.Settings.ShowPreview;
            ApplyPreviewVisibility();
        }

        // ---------- menu helpers ----------
        internal static MenuItem MI(string header, string glyph, Action action, bool? isChecked = null, string gesture = null)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
            if (glyph != null) mi.Icon = new TextBlock { Text = glyph, Style = (Style)Application.Current.Resources["Glyph"], FontSize = 14 };
            if (isChecked == true) mi.IsChecked = true;
            if (action != null) mi.Click += (_, _) => action();
            return mi;
        }

        private static void Open(ContextMenu cm, FrameworkElement target)
        {
            cm.PlacementTarget = target;
            cm.Placement = PlacementMode.Bottom;
            cm.IsOpen = true;
        }

        /// <summary>Folder + the file types chosen in Settings › New files.</summary>
        internal void FillNewMenu(ItemCollection items, PaneViewModel p)
        {
            var folder = MI(Loc.T("Folder"), null, () => p?.NewFolder(), null, "Ctrl+Shift+N");
            folder.Icon = new Image { Source = IconService.FolderIcon, Width = 16, Height = 16 };
            items.Add(folder);
            items.Add(new Separator());
            foreach (var ext in NewFiles.Enabled)
            {
                var x = ext;
                var mi = MI(NewFiles.NameOf(x), null, () => p?.NewFile(x));
                mi.Icon = new Image { Source = IconService.TypeIcon(x), Width = 16, Height = 16 };
                items.Add(mi);
            }
            items.Add(new Separator());
            // a page for this folder (projects, categories, a gallery) – view.wxv
            if (p != null && !p.IsThisPC && !p.InArchive)
            {
                var view = MI(Loc.T("WxvNew"), null, () => { if (Views.PaneView.CreateViewFile(p.Path)) p.EditFolderView(); });
                view.Icon = new Image { Source = FileIcons.ViewFile(), Width = 16, Height = 16 };
                items.Add(view);
                items.Add(new Separator());
            }
            items.Add(MI(Loc.T("EditNewList"), "\uE70F", () => OpenSettings("newfiles")));
        }

        private void NewMenu_Click(object sender, RoutedEventArgs e)
        {
            var cm = new ContextMenu();
            FillNewMenu(cm.Items, Pane);
            cm.Items.Add(new Separator());
            cm.Items.Add(MI(Loc.T("NewTab"), "\uE8A7", () => OpenInNewTab(NewTabPath, true), null, "Ctrl+T"));
            cm.Items.Add(MI(Loc.T("NewWindow"), "\uE737", () => OpenNewWindow(Pane?.Path ?? FileSystem.ThisPC), null, "Ctrl+N"));
            Open(cm, NewBtn);
        }

        internal static void FillSortMenu(ItemCollection items, PaneViewModel p)
        {
            foreach (var (key, label) in new[] { ("name", "ColName"), ("modified", "ColModified"), ("type", "ColType"), ("size", "ColSize") })
                items.Add(MI(Loc.T(label), null, () => p.SortBy = key, p.SortBy == key));
            items.Add(new Separator());
            items.Add(MI(Loc.T("Ascending"), null, () => p.SortDesc = false, !p.SortDesc));
            items.Add(MI(Loc.T("Descending"), null, () => p.SortDesc = true, p.SortDesc));
            items.Add(new Separator());
            items.Add(MI(Loc.T("GroupByDate"), null, () => p.GroupByDate = !p.GroupByDate, p.GroupByDate));
        }

        internal void FillViewMenu(ItemCollection items, PaneViewModel p)
        {
            var s = AppState.Settings;
            items.Add(MI(Loc.T("ViewDetails"), "", () => p.ViewMode = "details", p.IsDetails));
            foreach (var (size, label) in new[] { (48, "IconsSmall"), (96, "IconsMedium"), (160, "IconsLarge"), (256, "IconsXL") })
                items.Add(MI(Loc.T(label), "", () => { p.ViewMode = "icons"; p.IconSize = size; }, p.IsIcons && p.IconSize == size));
            items.Add(new Separator());
            items.Add(MI(Loc.T("CompactRows"), null, () => { s.CompactRows = !s.CompactRows; ForEachPane(x => x.NotifyRowHeight()); }, s.CompactRows));
            items.Add(MI(Loc.T("ShowHidden"), null, () => { s.ShowHidden = !s.ShowHidden; ForEachPane(x => x.Refresh()); }, s.ShowHidden, ""));
            items.Add(MI(Loc.T("ShowExtensions"), null, () => { s.ShowExtensions = !s.ShowExtensions; ForEachPane(x => x.NotifyRowHeight()); }, s.ShowExtensions));
            items.Add(MI(Loc.T("PreviewPane"), null, () => PreviewToggle_Click(null, null), s.ShowPreview, "Ctrl+Shift+P"));
            items.Add(new Separator());
            items.Add(MI(Loc.T("ViewForAll"), null, () => { p.ApplyViewToAllFolders(); Notify.Show(Loc.T("ViewForAllDone")); }));
        }

        private void SortMenu_Click(object sender, RoutedEventArgs e)
        {
            var p = Pane; if (p == null) return;
            var cm = new ContextMenu();
            FillSortMenu(cm.Items, p);
            Open(cm, SortBtn);
        }

        private void ViewMenu_Click(object sender, RoutedEventArgs e)
        {
            var p = Pane; if (p == null) return;
            var cm = new ContextMenu();
            FillViewMenu(cm.Items, p);
            Open(cm, ViewBtn);
        }

        private void ForEachPane(Action<PaneViewModel> a)
        {
            foreach (var p in VM.AllPanes.ToList()) a(p);
        }

        private void MoreMenu_Click(object sender, RoutedEventArgs e)
        {
            var cm = new ContextMenu();
            cm.Items.Add(MI(Loc.T("Settings"), "", () => VM.OpenSettings(), null, "Ctrl+,"));
            cm.Items.Add(MI(Loc.T("SetContextMenu"), "", () => OpenSettings("menu")));
            cm.Items.Add(new Separator());
            cm.Items.Add(MI(Loc.T("OpenInExplorer"), "", () => FileOps.OpenInExplorer(Pane?.Path ?? "")));
            cm.Items.Add(new Separator());
            cm.Items.Add(MI(Loc.T("Donate") + " ☕", "", () => FileOps.OpenUrl(AppState.DonateUrl)));
            cm.Items.Add(MI(Loc.T("About"), "", () => OpenSettings("about")));
            Open(cm, MoreBtn);
        }

        // ================= sidebar =================
        private Point _sbDown;
        private SidebarItem _sbDownItem;

        private static SidebarItem ItemFrom(object source) => (source as FrameworkElement)?.DataContext as SidebarItem
                                                              ?? ((source as FrameworkContentElement)?.DataContext as SidebarItem);

        private void Sidebar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _sbDown = e.GetPosition(SidebarList);
            _sbDownItem = ItemFrom(e.OriginalSource);
        }

        private void Sidebar_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _sbDownItem == null) return;
            var d = e.GetPosition(SidebarList) - _sbDown;
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var item = _sbDownItem; _sbDownItem = null;
            var data = new DataObject("WinXplorer.Sidebar", item);
            DragDrop.DoDragDrop(SidebarList, data, DragDropEffects.Move);
            ClearHints();
        }

        private void Sidebar_MouseUp(object sender, MouseButtonEventArgs e)
        {
            var item = ItemFrom(e.OriginalSource);
            if (item == null || item.IsSeparator || item != _sbDownItem && _sbDownItem != null) { _sbDownItem = null; return; }
            _sbDownItem = null;
            if (item.Kind == SidebarKind.App) { FileOps.Open(item.Path); return; }
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) OpenInNewTab(item.Path, true);
            else { Pane?.Navigate(item.Path); Pane?.Focus(); }
        }

        private void Sidebar_MiddleUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            var item = ItemFrom(e.OriginalSource);
            if (item == null || item.IsSeparator) return;
            if (item.Kind == SidebarKind.App) FileOps.Open(item.Path); else OpenInNewTab(item.Path, false);
        }

        /// <summary>Everything that can be added to the sidebar (the "+ Add" button and right-click anywhere in the sidebar).</summary>
        private void FillSidebarAddMenu(ItemCollection items, int insertAt)
        {
            var sb = VM.Sidebar;
            items.Add(MI(Loc.T("AddFolder") + "…", "\uE8F4", () => AddFolderDialog(insertAt)));
            items.Add(MI(Loc.T("AddApp") + "…", "\uECAA", () => AddAppDialog(insertAt)));
            if (Pane != null && !Pane.IsThisPC) items.Add(MI(Loc.T("PinCurrent"), "\uE718", () => sb.AddFolder(Pane.Path, insertAt)));
            items.Add(new Separator());
            items.Add(MI(Loc.T("AddHeader") + "…", "\uE8D2", () =>
            {
                var n = InputDialog.Ask(this, Loc.T("HeaderTitle"), Loc.T("HeaderPrompt"), "");
                if (!string.IsNullOrWhiteSpace(n)) sb.AddHeader(n, insertAt);
            }));
            items.Add(MI(Loc.T("AddSeparator"), "\uE738", () => sb.AddHeader(null, insertAt)));
            if (!sb.HasKind(SidebarKind.ThisPC)) items.Add(MI(Loc.T("AddThisPC"), "\uE7F4", () => sb.AddSpecial(SidebarKind.ThisPC)));
            if (!sb.ShowFrequent) items.Add(MI(Loc.T("AddFrequent"), "\uE81C", () => sb.ShowFrequent = true));
            if (!sb.HasKind(SidebarKind.Drives) || AppState.Settings.HiddenDrives.Count > 0) items.Add(MI(Loc.T("ShowAllDrives"), "\uEDA2", () => sb.ShowAllDrives()));
            items.Add(new Separator());
            items.Add(MI(Loc.T("ResetSidebar"), "\uE777", () => sb.Reset()));
        }

        private void Sidebar_RightUp(object sender, MouseButtonEventArgs e)
        {
            var item = ItemFrom(e.OriginalSource);
            var sb = VM.Sidebar;
            var cm = new ContextMenu();
            if (item != null && item.Kind == SidebarKind.App)
            {
                cm.Items.Add(MI(Loc.T("Open"), "\uE8E5", () => FileOps.Open(item.Path)));
                cm.Items.Add(MI(Loc.T("OpenLocation"), "\uED43", () => Pane?.Navigate(item.Path)));
                cm.Items.Add(new Separator());
            }
            else if (item != null && !item.IsSeparator)
            {
                cm.Items.Add(MI(Loc.T("Open"), "\uE8E5", () => Pane?.Navigate(item.Path)));
                cm.Items.Add(MI(Loc.T("OpenNewTab"), "\uE8A7", () => OpenInNewTab(item.Path, true)));
                cm.Items.Add(MI(Loc.T("OpenOtherPane"), "\uE89F", () => { if (Pane != null) OpenInOtherPane(Pane, item.Path); }));
                cm.Items.Add(new Separator());
            }
            if (item != null && !item.IsSeparator && !item.IsDrive)
                cm.Items.Add(MI(Loc.T("RenameLabel"), "\uE8AC", () =>
                {
                    var n = InputDialog.Ask(this, Loc.T("RenameLabel"), Loc.T("RenameLabelPrompt"), item.Name);
                    if (n != null) sb.Rename(item, n);
                }));
            if (item != null && item.IsSeparator)
                cm.Items.Add(MI(item.IsHeader ? Loc.T("RenameHeader") : Loc.T("GiveHeaderTitle"), "\uE8AC", () =>
                {
                    var n = InputDialog.Ask(this, Loc.T("HeaderTitle"), Loc.T("HeaderPrompt"), item.Header);
                    if (n != null) sb.Rename(item, n);
                }));
            if (item != null)
            {
                cm.Items.Add(MI(Loc.T("MoveUp"), "\uE70E", () => sb.MoveBy(item, -1)));
                cm.Items.Add(MI(Loc.T("MoveDown"), "\uE70D", () => sb.MoveBy(item, 1)));
                cm.Items.Add(MI(item.IsDrive ? Loc.T("HideDrive") : Loc.T("RemoveFromSidebar"), "\uE711", () => sb.Remove(item)));
                if (item.IsDrive) cm.Items.Add(MI(Loc.T("RemoveDrives"), null, () => sb.RemoveDrivesGroup()));
                cm.Items.Add(new Separator());
            }
            // "Add" right here: new entries go below the clicked one (or at the end)
            var add = MI(Loc.T("Add"), "\uE710", null);
            FillSidebarAddMenu(add.Items, item != null ? sb.DataIndexOf(item) + 1 : -1);
            if (item == null) foreach (var mi in add.Items.Cast<object>().ToList()) { add.Items.Remove(mi); cm.Items.Add(mi); }
            else cm.Items.Add(add);
            if (item != null && !item.IsSeparator && !string.IsNullOrEmpty(item.Path))
            {
                cm.Items.Add(new Separator());
                cm.Items.Add(MI(Loc.T("Properties"), "\uE946", () => FileOps.ShowProperties(item.Path)));
            }
            cm.PlacementTarget = SidebarScroll;
            cm.Placement = PlacementMode.MousePoint;
            cm.IsOpen = true;
            e.Handled = true;
        }

        private void SidebarAdd_Click(object sender, RoutedEventArgs e)
        {
            var cm = new ContextMenu();
            FillSidebarAddMenu(cm.Items, -1);
            cm.PlacementTarget = SidebarAddBtn;
            cm.Placement = PlacementMode.Top;
            cm.IsOpen = true;
        }

        private void AddFolderDialog(int insertAt)
        {
            var picked = PickerWindow.Folders(this, Loc.T("AddFolder"), Pane != null && !Pane.IsThisPC ? Pane.Path : null, multi: true);
            if (picked != null)
                foreach (var f in picked) VM.Sidebar.AddFolder(f, insertAt < 0 ? -1 : insertAt++);
        }

        private void AddAppDialog(int insertAt)
        {
            var picked = PickerWindow.Files(this, Loc.T("AddApp"), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) + @"\Programs",
                new[] { new PickerWindow.Filter(Loc.T("AppsFilter"), "*.exe;*.lnk;*.bat;*.cmd;*.ps1;*.url;*.appref-ms"), new PickerWindow.Filter(Loc.T("AllFiles"), "*.*") }, multi: true);
            if (picked != null)
                foreach (var f in picked) VM.Sidebar.AddApp(f, insertAt < 0 ? -1 : insertAt++);
        }

        private void ClearHints()
        {
            foreach (var i in VM.Sidebar.Items) i.Hint = DropHint.None;
        }

        private (SidebarItem item, DropHint hint) HitSidebar(DragEventArgs e, bool isReorder)
        {
            var item = ItemFrom(e.OriginalSource);
            if (item == null) return (null, DropHint.None);
            if (SidebarList.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement c || c.ActualHeight <= 0) return (item, DropHint.None);
            double y = e.GetPosition(c).Y / c.ActualHeight;
            if (isReorder || item.IsSeparator) return (item, y < 0.5 ? DropHint.Above : DropHint.Below);
            if (y < 0.22) return (item, DropHint.Above);
            if (y > 0.78) return (item, DropHint.Below);
            return (item, item.Kind == SidebarKind.ThisPC ? DropHint.None : DropHint.Into);
        }

        private void Sidebar_DragOver(object sender, DragEventArgs e)
        {
            bool reorder = e.Data.GetDataPresent("WinXplorer.Sidebar");
            bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
            ClearHints();
            if (!reorder && !files) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
            var (item, hint) = HitSidebar(e, reorder);
            if (item != null) item.Hint = hint;
            if (reorder) e.Effects = DragDropEffects.Move;
            else if (hint == DropHint.Into) e.Effects = item.Kind == SidebarKind.App ? DragDropEffects.Copy : PaneView.DropEffectFor(e, item.Path);
            else e.Effects = DragDropEffects.Link;
            e.Handled = true;
        }

        private void Sidebar_DragLeave(object sender, DragEventArgs e) => ClearHints();

        private void Sidebar_Drop(object sender, DragEventArgs e)
        {
            var sb = VM.Sidebar;
            bool reorder = e.Data.GetDataPresent("WinXplorer.Sidebar");
            var (item, hint) = HitSidebar(e, reorder);
            ClearHints();
            int index = item == null ? int.MaxValue : sb.DataIndexOf(item) + (hint == DropHint.Below ? 1 : 0);
            if (reorder)
            {
                if (e.Data.GetData("WinXplorer.Sidebar") is SidebarItem dragged && dragged.Data != item?.Data)
                    sb.Move(dragged, index == int.MaxValue ? AppState.Settings.Sidebar.Count : index);
                return;
            }
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
            if (hint == DropHint.Into && item != null && item.Kind == SidebarKind.App)
            {
                // files dropped on a pinned program open in it (like dropping on a desktop shortcut)
                FileOps.Open(item.Path, string.Join(" ", paths.Select(p => "\"" + p + "\"")));
                return;
            }
            if (hint == DropHint.Into && item != null)
            {
                var eff = PaneView.DropEffectFor(e, item.Path);
                FileOps.CopyOrMove(paths, item.Path, eff == DragDropEffects.Move);
                return;
            }
            // folders are pinned as folders; programs, shortcuts and other files as items that open on click
            foreach (var p in paths)
            {
                if (Directory.Exists(p)) sb.AddFolder(p, index == int.MaxValue ? -1 : index);
                else if (File.Exists(p)) sb.AddApp(p, index == int.MaxValue ? -1 : index);
                else continue;
                if (index != int.MaxValue) index++;
            }
        }
    }
}
