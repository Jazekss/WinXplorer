using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using WinXplorer.Interop;
using WinXplorer.Models;
using WinXplorer.Services;

namespace WinXplorer.Views
{
    /// <summary>The Settings page, shown inside a tab. The sidebar lists its sections (see MainWindow.SettingsNav).</summary>
    public sealed partial class SettingsView : UserControl
    {
        public sealed record Section(string Key, string Glyph, string Title);

        public static IReadOnlyList<Section> Sections => new[]
        {
            new Section("general", "", Loc.T("SetGeneral")),
            new Section("appearance", "", Loc.T("SetAppearance")),
            new Section("menu", "", Loc.T("SetContextMenu")),
            new Section("newfiles", "", Loc.T("SetNewFiles")),
            new Section("sidebar", "", Loc.T("SetSidebar")),
            new Section("help", "", Loc.T("SetHelp")),
            new Section("system", "\uE9D9", Loc.T("SetSystem")),
            new Section("about", "", Loc.T("SetAbout")),
        };

        public static SettingsView Current { get; private set; }
        public static string CurrentSection { get; set; } = "general";

        private readonly ScrollViewer _scroll;
        private readonly StackPanel _page;

        public SettingsView()
        {
            Current = this;
            Background = (Brush)FindResource("ContentBrush");
            SetResourceReference(BackgroundProperty, "ContentBrush");
            SetResourceReference(ForegroundProperty, "FgBrush");
            _page = new StackPanel { Margin = new Thickness(36, 26, 36, 40), MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
            _scroll = new ScrollViewer { Content = _page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            // lists inside the page (menu editor…) must not swallow the wheel: at their end the page scrolls on
            _scroll.PreviewMouseWheel += (_, e) =>
            {
                if (e.OriginalSource is not DependencyObject d) return;
                for (var p = d; p != null && p != _scroll; p = System.Windows.Media.VisualTreeHelper.GetParent(p))
                {
                    if (p is ScrollViewer inner && inner.ScrollableHeight > 0)
                    {
                        bool canMove = e.Delta > 0 ? inner.VerticalOffset > 0 : inner.VerticalOffset < inner.ScrollableHeight - 0.5;
                        if (canMove) return;   // the inner list still scrolls itself
                    }
                }
                _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - e.Delta);
                e.Handled = true;
            };
            Content = _scroll;
            Loaded += (_, _) => Show(CurrentSection);
            Unloaded += (_, _) => { if (Current == this) Current = null; };
        }

        public void Show(string key)
        {
            CurrentSection = key ?? "general";
            _page.Children.Clear();
            _scroll.ScrollToTop();
            var sec = Sections.FirstOrDefault(s => s.Key == CurrentSection) ?? Sections[0];
            switch (sec.Key)
            {
                case "general": BuildGeneral(); break;
                case "appearance": BuildAppearance(); break;
                case "menu": BuildMenu(); break;
                case "newfiles": BuildNewFiles(); break;
                case "sidebar": BuildSidebar(); break;
                case "help": BuildHelp(); break;
                case "system": BuildSystem(); break;
                case "about": BuildAbout(); break;
            }
        }

        private static AppSettings S => AppState.Settings;
        private static void Save() => AppState.Save();

        // ================= building blocks =================
        private void Header(string text) =>
            _page.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 14, 0, 8) });

        private static TextBlock Dim(string text, double size = 12) => Tb(text, size, "FgDimBrush");

        private static TextBlock Tb(string text, double size = 12, string brush = "FgBrush")
        {
            var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return t;
        }

        private static Border CardBorder(UIElement child)
        {
            var b = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 4), Child = child };
            b.SetResourceReference(Border.BackgroundProperty, "BarBrush");
            b.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            return b;
        }

        /// <summary>A setting row: glyph, title, description on the left, the control on the right.</summary>
        private void Card(string glyph, string title, string desc, UIElement control)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ic = new TextBlock { Text = glyph ?? "", Style = (Style)FindResource("Glyph"), FontSize = 16, HorizontalAlignment = HorizontalAlignment.Left };
            g.Children.Add(ic);
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            sp.Children.Add(Tb(title, 13));
            if (!string.IsNullOrEmpty(desc)) sp.Children.Add(Dim(desc));
            Grid.SetColumn(sp, 1); g.Children.Add(sp);
            if (control != null)
            {
                if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(control, 2); g.Children.Add(control);
            }
            _page.Children.Add(CardBorder(g));
        }

        /// <summary>A card with title/description above and a free-form body below (chips, swatches, editors).</summary>
        private void WideCard(string glyph, string title, string desc, UIElement body)
        {
            var sp = new StackPanel();
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.Children.Add(new TextBlock { Text = glyph ?? "", Style = (Style)FindResource("Glyph"), FontSize = 16, HorizontalAlignment = HorizontalAlignment.Left });
            var t = new StackPanel();
            t.Children.Add(Tb(title, 13));
            if (!string.IsNullOrEmpty(desc)) t.Children.Add(Dim(desc));
            Grid.SetColumn(t, 1); head.Children.Add(t);
            sp.Children.Add(head);
            if (body is FrameworkElement fe) fe.Margin = new Thickness(34, 12, 0, 0);
            sp.Children.Add(body);
            _page.Children.Add(CardBorder(sp));
        }

        private CheckBox Switch(bool value, Action<bool> changed)
        {
            var cb = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = value };
            cb.Click += (_, _) => { changed(cb.IsChecked == true); Save(); };
            return cb;
        }

        private RadioButton Chip(string group, string text, bool isChecked, Action onSelect)
        {
            var rb = new RadioButton { Style = (Style)FindResource("Chip"), GroupName = group, IsChecked = isChecked, Content = Tb(text) };
            rb.Checked += (_, _) => onSelect();
            return rb;
        }

        private Button Btn(string text, Action click, string glyph = null, bool accent = false)
        {
            var b = new Button { Style = (Style)FindResource(accent ? "AccentBtn" : "SubtleBtn"), Margin = new Thickness(0, 0, 8, 0) };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            if (glyph != null) sp.Children.Add(new TextBlock { Text = glyph, Style = (Style)FindResource("Glyph"), FontSize = 13, Margin = new Thickness(0, 0, 8, 0) });
            var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(t);
            b.Content = sp;
            b.Click += (_, _) => click();
            return b;
        }

        private static void ForEachPane(Action<ViewModels.PaneViewModel> a)
        {
            foreach (var p in MainWindow.Instance.VM.AllPanes) a(p);
        }

        // ================= General =================
        private void BuildGeneral()
        {
            Header(Loc.T("Language"));
            var langs = new WrapPanel();
            langs.Children.Add(Chip("lang", Loc.T("LangAuto"), S.Language == "auto" || string.IsNullOrEmpty(S.Language), () => SetLanguage("auto")));
            foreach (var l in Loc.Available())
                langs.Children.Add(Chip("lang", l.Name + (l.IsCustom ? " *" : ""), S.Language == l.Code, () => SetLanguage(l.Code)));
            var langBody = new StackPanel();
            langBody.Children.Add(langs);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            row.Children.Add(Btn(Loc.T("RestartNow"), Restart, ""));
            row.Children.Add(Btn(Loc.T("AddTranslation"), AddTranslation, ""));
            langBody.Children.Add(row);
            WideCard("", Loc.T("Language"), Loc.T("LanguageDesc"), langBody);

            Header(Loc.T("SetStartup"));
            Card("", Loc.T("RestoreTabs"), Loc.T("RestoreTabsDesc"), Switch(S.RestoreTabs, v => S.RestoreTabs = v));
            Card("", Loc.T("NewTabThisPC"), Loc.T("NewTabThisPCDesc"), Switch(S.NewTabThisPC, v => S.NewTabThisPC = v));
            Card("", Loc.T("SpaceViewer"), Loc.T("SpaceViewerDesc"), Switch(S.SpaceOpensViewer, v => S.SpaceOpensViewer = v));
            Card("\uE8FF", Loc.T("HoverPreview"), Loc.T("HoverPreviewDesc"), Switch(S.HoverPreview, v => S.HoverPreview = v));
            Card("\uE768", Loc.T("HoverAutoPlay"), Loc.T("HoverAutoPlayDesc"), Switch(S.HoverAutoPlay, v => S.HoverAutoPlay = v));
            Card("", Loc.T("DblClickViewer"), Loc.T("DblClickViewerDesc"), Switch(S.ImagesOpenInViewer, v => S.ImagesOpenInViewer = v));
            // archives: browse them in WinXplorer (with its archive icon) or hand them to the program Windows uses for them
            var arch = new WrapPanel();
            void SetArchives(bool own) { S.OpenArchives = own; Save(); ForEachPane(p => p.Refresh()); }
            arch.Children.Add(Chip("archives", Loc.T("ArchivesInWinXplorer"), S.OpenArchives, () => SetArchives(true)));
            arch.Children.Add(Chip("archives", Loc.T("ArchivesInDefaultApp"), !S.OpenArchives, () => SetArchives(false)));
            WideCard("\uE7B8", Loc.T("OpenArchives"), Loc.T("OpenArchivesDesc"), arch);
            Card("\uEC50", Loc.T("DefaultFM"), Loc.T("DefaultFMDesc"), Switch(S.DefaultFileManager, v =>
            {
                S.DefaultFileManager = v;
                new System.Threading.Thread(() => DefaultFileManager.Apply(v)) { IsBackground = true }.Start();
                Notify.Show(Loc.T(v ? "DefaultFMOn" : "DefaultFMOff"));
            }));
            Card("\uE7B8", Loc.T("ArchiveOpenWith"), Loc.T("ArchiveOpenWithDesc"), Switch(S.ArchiveOpenWith, v => { S.ArchiveOpenWith = v; new System.Threading.Thread(() => Associations.Apply(v)) { IsBackground = true }.Start(); }));

            Header(Loc.T("SetFiles"));
            Card("", Loc.T("NavOnTop"), Loc.T("NavOnTopDesc"), Switch(S.NavOnTop, v => { S.NavOnTop = v; Save(); PaneView.PlaceNav(null); }));   // shown once you leave Settings
            Card("", Loc.T("ShowHidden"), null, Switch(S.ShowHidden, v => { S.ShowHidden = v; ForEachPane(p => p.Refresh()); }));
            Card("", Loc.T("ShowExtensions"), null, Switch(S.ShowExtensions, v => { S.ShowExtensions = v; ForEachPane(p => p.NotifyRowHeight()); }));
            Card("\uE7B8", Loc.T("ShowFolderSizes"), Loc.T("ShowFolderSizesDesc"), Switch(S.ShowFolderSizes, v => { S.ShowFolderSizes = v; ForEachPane(p => p.Refresh()); }));
            Card("", Loc.T("ConfirmDelete"), Loc.T("ConfirmDeleteDesc"), Switch(S.ConfirmDelete, v => S.ConfirmDelete = v));

            Header(Loc.T("FastSearch"));
            Card("", Loc.T("FastSearch"), Loc.T("FastSearchDesc"), Switch(S.SearchIndex, v => { S.SearchIndex = v; if (v) SearchIndex.Start(); }));
            Card("", Loc.T("IndexSkipWindows"), Loc.T("IndexSkipWindowsDesc"), Switch(S.IndexSkipWindows, v => S.IndexSkipWindows = v));
            var status = Dim(SearchIndex.Status());
            void Upd() => Dispatcher.BeginInvoke(() => status.Text = SearchIndex.Status());
            SearchIndex.Changed += Upd;
            Unloaded += (_, _) => SearchIndex.Changed -= Upd;
            Card("", Loc.T("IndexStatus"), null, Btn(Loc.T("IndexRebuild"), () => { SearchIndex.Rebuild(); Upd(); }, ""));
            ((Grid)((Border)_page.Children[^1]).Child).Children.OfType<StackPanel>().First().Children.Add(status);
        }

        // ================= New files =================
        private void BuildNewFiles()
        {
            Card("", Loc.T("SetNewFiles"), Loc.T("NewFilesDesc"), null);
            var enabled = NewFiles.Enabled.ToList();
            void SaveList() { S.NewFileTypes = enabled.ToList(); Save(); }

            Header(Loc.T("NewFilesChosen"));
            // the order here is the order in the New menu: drag a row or use the arrows
            var chosen = new StackPanel();
            void Move(string ext, int to)
            {
                int from = enabled.IndexOf(ext);
                if (from < 0) return;
                to = Math.Clamp(to, 0, enabled.Count - 1);
                if (to == from) return;
                enabled.RemoveAt(from); enabled.Insert(to, ext);
                SaveList(); FillChosen();
            }
            void FillChosen()
            {
                chosen.Children.Clear();
                for (int i = 0; i < enabled.Count; i++)
                    chosen.Children.Add(NewTypeRow(enabled[i], i, enabled.Count, Move, x => { enabled.Remove(x); SaveList(); Show("newfiles"); }));
            }
            FillChosen();
            _page.Children.Add(CardBorder(chosen));

            Header(Loc.T("NewFilesAvailable"));
            var avail = new WrapPanel();
            foreach (var t in NewFiles.Catalog.Where(c => !enabled.Contains(c.Ext)))
            {
                var x = t.Ext;
                avail.Children.Add(TypeChip(x, false, () => { enabled.Add(x); SaveList(); Show("newfiles"); }));
            }
            // own extension
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var box = new TextBox { Style = (Style)FindResource("InputBox"), Width = 140, Height = 30, ToolTip = ".astro, .kt, .gd…" };
            void AddOwn()
            {
                var ext = NewFiles.Normalize(box.Text);
                if (ext.Length < 2 || ext.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || enabled.Contains(ext)) return;
                enabled.Add(ext); SaveList(); Show("newfiles");
            }
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { AddOwn(); e.Handled = true; } };
            row.Children.Add(box);
            var add = Btn(Loc.T("AddExtension"), AddOwn, "");
            add.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(add);
            var panel = new StackPanel();
            panel.Children.Add(avail);
            panel.Children.Add(row);
            _page.Children.Add(CardBorder(panel));

            var reset = Btn(Loc.T("Reset"), () => { S.NewFileTypes = null; Save(); Show("newfiles"); }, "");
            reset.HorizontalAlignment = HorizontalAlignment.Left;
            reset.Margin = new Thickness(0, 10, 0, 0);
            _page.Children.Add(reset);

            Header(Loc.T("FileIconsTitle"));
            Card("", Loc.T("OwnCodeIcons"), Loc.T("OwnCodeIconsDesc"), Switch(S.OwnCodeIcons, v => { S.OwnCodeIcons = v; ForEachPane(p => p.Refresh()); }));
            Card("", Loc.T("CustomIcons"), Loc.F("CustomIconsDesc", FileIcons.CustomDir), Btn(Loc.T("Open"), () =>
            {
                try { Directory.CreateDirectory(FileIcons.CustomDir); FileOps.Open(FileIcons.CustomDir); } catch { }
            }));
        }

        /// <summary>A row of the New menu list: grip (drag to reorder), icon, name, up / down / remove.</summary>
        private FrameworkElement NewTypeRow(string ext, int index, int count, Action<string, int> move, Action<string> remove)
        {
            var g = new Grid { Background = Brushes.Transparent, AllowDrop = true, Tag = ext };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var grip = new TextBlock { Text = "", Style = (Style)FindResource("Glyph"), FontSize = 12, Cursor = Cursors.SizeAll, Opacity = 0.6, ToolTip = Loc.T("DragToReorder") };
            g.Children.Add(grip);
            var icon = new Image { Source = FileIcons.Small(ext), Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left };
            Grid.SetColumn(icon, 1); g.Children.Add(icon);
            var name = Tb(NewFiles.NameOf(ext), 12);
            name.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(name, 2); g.Children.Add(name);
            var btns = new StackPanel { Orientation = Orientation.Horizontal };
            var up = SmallBtn("", Loc.T("MoveUp"), () => move(ext, index - 1)); up.IsEnabled = index > 0;
            var down = SmallBtn("", Loc.T("MoveDown"), () => move(ext, index + 1)); down.IsEnabled = index < count - 1;
            btns.Children.Add(up); btns.Children.Add(down);
            btns.Children.Add(SmallBtn("", Loc.T("Remove"), () => remove(ext)));
            Grid.SetColumn(btns, 3); g.Children.Add(btns);

            Point? start = null;
            grip.PreviewMouseLeftButtonDown += (_, e) => { start = e.GetPosition(g); e.Handled = true; };
            grip.PreviewMouseLeftButtonUp += (_, _) => start = null;
            grip.MouseMove += (_, e) =>
            {
                if (start is not { } p0 || e.LeftButton != MouseButtonState.Pressed) return;
                var d = e.GetPosition(g) - p0;
                if (Math.Abs(d.Y) < 4 && Math.Abs(d.X) < 4) return;
                start = null;
                DragDrop.DoDragDrop(g, new DataObject("WinXplorer.NewType", ext), DragDropEffects.Move);
            };
            g.DragOver += (_, e) =>
            {
                e.Effects = e.Data.GetDataPresent("WinXplorer.NewType") ? DragDropEffects.Move : DragDropEffects.None;
                e.Handled = true;
            };
            g.Drop += (_, e) =>
            {
                if (e.Data.GetData("WinXplorer.NewType") is string dragged && dragged != ext)
                    move(dragged, index);
                e.Handled = true;
            };
            return g;
        }

        private FrameworkElement TypeChip(string ext, bool selected, Action toggle)
        {
            var b = new Button { Style = (Style)FindResource("SubtleBtn"), Margin = new Thickness(0, 0, 6, 6), ToolTip = NewFiles.NameOf(ext) };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new Image { Source = FileIcons.Small(ext), Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) });
            sp.Children.Add(new TextBlock { Text = NewFiles.NameOf(ext), VerticalAlignment = VerticalAlignment.Center });
            var g = new TextBlock { Text = selected ? "" : "", Style = (Style)FindResource("Glyph"), FontSize = 10, Margin = new Thickness(10, 0, 0, 0) };
            g.SetResourceReference(TextBlock.ForegroundProperty, selected ? "FgDimBrush" : "AccentTextBrush");
            sp.Children.Add(g);
            b.Content = sp;
            if (selected) b.SetResourceReference(Button.BackgroundProperty, "SelectedBrush");
            b.Click += (_, _) => toggle();
            return b;
        }

        private void SetLanguage(string code)
        {
            if (S.Language == code) return;
            S.Language = code;
            Save();
        }

        private static void Restart()
        {
            var main = MainWindow.Instance;
            var exe = Environment.ProcessPath;
            main.Close();
            try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false }); } catch { }
        }

        private void AddTranslation()
        {
            try
            {
                var template = Loc.ExportTemplate();
                MessageBox.Show(Window.GetWindow(this), Loc.F("AddTranslationInfo", Loc.CustomDir), "WinXplorer", MessageBoxButton.OK, MessageBoxImage.Information);
                FileOps.Open(Loc.CustomDir);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "WinXplorer"); }
        }

        // ================= Appearance =================
        private void BuildAppearance()
        {
            Header(Loc.T("Theme"));
            var themes = new WrapPanel();
            foreach (var (k, l) in new[] { ("system", "ThemeSystem"), ("light", "ThemeLight"), ("dark", "ThemeDark") })
                themes.Children.Add(Chip("theme", Loc.T(l), S.Theme == k, () => { S.Theme = k; ThemeManager.Apply(); Save(); }));
            WideCard("", Loc.T("Theme"), Loc.T("ThemeDesc"), themes);

            var sw = new WrapPanel();
            var sys = new RadioButton { Style = (Style)FindResource("Swatch"), GroupName = "accent", IsChecked = S.Accent == "system" || string.IsNullOrEmpty(S.Accent), ToolTip = Loc.T("AccentSystem") };
            sys.Background = new LinearGradientBrush(Color.FromRgb(0x00, 0x78, 0xD4), Color.FromRgb(0xE3, 0x00, 0x8C), 45);
            sys.Content = new TextBlock { Text = "", Style = (Style)FindResource("Glyph"), FontSize = 11, Foreground = Brushes.White };
            sys.Checked += (_, _) => SetAccent("system");
            sw.Children.Add(sys);
            foreach (var hex in ThemeManager.AccentPresets)
            {
                var c = (Color)ColorConverter.ConvertFromString(hex);
                var rb = new RadioButton { Style = (Style)FindResource("Swatch"), GroupName = "accent", Background = new SolidColorBrush(c), IsChecked = string.Equals(S.Accent, hex, StringComparison.OrdinalIgnoreCase), ToolTip = hex };
                rb.Checked += (_, _) => SetAccent(hex);
                sw.Children.Add(rb);
            }
            WideCard("", Loc.T("AccentColor"), Loc.T("AccentDesc"), sw);

            Header(Loc.T("SetLayout"));
            Card("", Loc.T("CompactRows"), null, Switch(S.CompactRows, v => { S.CompactRows = v; ForEachPane(p => p.NotifyRowHeight()); }));
            Card("", Loc.T("PreviewPane"), Loc.T("PreviewTip"), Switch(S.ShowPreview, v => MainWindow.Instance.SetPreviewVisible(v)));
        }

        private static void SetAccent(string v)
        {
            S.Accent = v;
            ThemeManager.Apply();
            Save();
        }

        // ================= Sidebar =================
        private void BuildSidebar()
        {
            var sb = MainWindow.Instance.VM.Sidebar;
            Card("", Loc.T("SidebarHow"), Loc.T("SidebarHowDesc"), null);
            Card("", Loc.T("ShowAllDrives"), null, Btn(Loc.T("Apply"), () => sb.ShowAllDrives()));
            Card("\uE7F4", Loc.T("AddThisPC"), null, Btn(Loc.T("Apply"), () => { if (!sb.HasKind(SidebarKind.ThisPC)) sb.AddSpecial(SidebarKind.ThisPC); }));
            Card("\uE81C", Loc.T("FrequentFolders"), Loc.T("FrequentFoldersDesc"), Switch(sb.ShowFrequent, v => sb.ShowFrequent = v));
            Card("", Loc.T("ResetSidebar"), Loc.T("ResetSidebarDesc"), Btn(Loc.T("Reset"), () => sb.Reset()));
        }

        // ================= About =================
        private void BuildAbout()
        {
            var v = typeof(App).Assembly.GetName().Version;
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new Image { Source = App.LoadAppIcon(), Width = 48, Height = 48, Margin = new Thickness(0, 0, 16, 0) });
            var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            t.Children.Add(Tb("WinXplorer " + v?.ToString(3), 18));
            t.Children.Add(Dim(Loc.T("AboutText")));
            head.Children.Add(t);
            _page.Children.Add(CardBorder(head));

            var donate = new StackPanel { Orientation = Orientation.Horizontal };
            donate.Children.Add(Btn(Loc.T("Donate") + " ☕", () => FileOps.OpenUrl(AppState.DonateUrl), "", true));
            WideCard("", Loc.T("DonateTitle"), Loc.T("DonateDesc"), donate);

            Header(Loc.T("SetTools"));
            Card("", Loc.T("OpenSettingsFolder"), AppState.DataDir, Btn(Loc.T("Open"), () => FileOps.Open(AppState.DataDir)));
            Card("", Loc.T("OpenInExplorer"), null, Btn(Loc.T("Open"), () => FileOps.OpenInExplorer(MainWindow.Instance.VM.Tabs.FirstOrDefault(x => !x.IsSettings)?.Active.Path ?? "")));
        }

        // ================= Context menu =================
        private void BuildMenu()
        {
            var built = MenuCatalog.BuiltKeys(S);
            bool all = MenuCatalog.AllBuilt(S);

            // style for every menu not built in the builder: WinXplorer's or the classic Windows one
            var modes = new WrapPanel();
            modes.Children.Add(Chip("menumode", Loc.T("MenuModeCustom"), S.ContextMenuMode != "windows", () => { S.ContextMenuMode = "custom"; Save(); }));
            modes.Children.Add(Chip("menumode", Loc.T("MenuModeWindows"), S.ContextMenuMode == "windows", () => { S.ContextMenuMode = "windows"; Save(); }));
            if (all) { modes.IsEnabled = false; modes.Opacity = 0.45; }   // nothing uses them any more
            var body = new StackPanel();
            body.Children.Add(modes);
            // which menus are fully custom (made in the builder)
            var status = Tb(built.Count == 0 ? Loc.T("MenuFullNone")
                : all ? Loc.T("MenuFullAll")
                : Loc.F("MenuFullSome", built.Count, string.Join(", ", built.Select(MenuCatalog.FullTitle))));
            status.Margin = new Thickness(0, 10, 0, 0);
            if (built.Count > 0) status.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
            body.Children.Add(status);
            WideCard("", Loc.T("MenuMode"), Loc.T(all ? "MenuModeDescAll" : "MenuModeDesc"), body);

            var openBuilder = Btn(Loc.T("MenuBuilderOpen"), () =>
            {
                if (MenuBuilderWindow.Open(Window.GetWindow(this))) Dispatcher.BeginInvoke(new Action(() => Show("menu")));
            }, "", accent: true);
            Card("", Loc.T("MenuBuilder"), Loc.T("MenuBuilderDesc"), openBuilder);
        }

        private Button SmallBtn(string glyph, string tip, Action a)
        {
            var b = new Button { Style = (Style)FindResource("IconBtn"), MinWidth = 26, MinHeight = 26, Padding = new Thickness(4), ToolTip = tip };
            b.Content = new TextBlock { Text = glyph, Style = (Style)FindResource("Glyph"), FontSize = 11 };
            b.Click += (_, _) => a();
            return b;
        }
    }
}
