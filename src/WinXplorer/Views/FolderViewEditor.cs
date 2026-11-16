using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinXplorer.Services;

namespace WinXplorer.Views
{
    /// <summary>
    /// The view's settings, shown in the folder itself (like the Settings page): title, what is shown and how,
    /// numbers on top, the browser button, and every project with its own name, picture, "done", "web" and "hide".
    /// Nothing is written until Save.
    /// </summary>
    public sealed class FolderViewEditor : UserControl
    {
        public event Action Saved, Cancelled;

        private readonly string _folder, _file;
        private readonly WxvView _v;
        private readonly StackPanel _page = new() { Margin = new Thickness(36, 22, 36, 40), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly StackPanel _projects = new();
        private CancellationTokenSource _cts;

        public FolderViewEditor(string folder, string file, WxvView v, bool isNew)
        {
            _folder = folder; _file = file;
            // edit a copy: Cancel leaves the file as it was
            _v = JsonSerializer.Deserialize<WxvView>(JsonSerializer.Serialize(v));
            _v.Items = new Dictionary<string, WxvItem>(_v.Items ?? new(), StringComparer.OrdinalIgnoreCase);
            SetResourceReference(BackgroundProperty, "ContentBrush");
            SetResourceReference(ForegroundProperty, "FgBrush");
            var scroll = new ScrollViewer { Content = _page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Content = scroll;
            Build(isNew);
            Unloaded += (_, _) => _cts?.Cancel();
        }

        // ---------------- building blocks (same look as Settings) ----------------
        private static TextBlock Tb(string text, double size = 12, string brush = "FgBrush")
        {
            var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return t;
        }
        private static TextBlock Dim(string text, double size = 12) => Tb(text, size, "FgDimBrush");

        private void Header(string text) => _page.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 18, 0, 8) });

        private Border CardBorder(UIElement child)
        {
            var b = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 4), Child = child };
            b.SetResourceReference(Border.BackgroundProperty, "BarBrush");
            b.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            return b;
        }

        private void Card(string glyph, string title, string desc, UIElement control)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock { Text = glyph ?? "", Style = (Style)FindResource("Glyph"), FontSize = 16, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center });
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            sp.Children.Add(Tb(title, 13));
            if (!string.IsNullOrEmpty(desc)) sp.Children.Add(Dim(desc));
            Grid.SetColumn(sp, 1); g.Children.Add(sp);
            if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 2); g.Children.Add(control);
            _page.Children.Add(CardBorder(g));
        }

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
            cb.Click += (_, _) => changed(cb.IsChecked == true);
            return cb;
        }

        private WrapPanel Chips(string group, (string key, string label)[] options, string current, Action<string> set)
        {
            var w = new WrapPanel();
            foreach (var (key, label) in options)
            {
                var k = key;
                var rb = new RadioButton { Style = (Style)FindResource("Chip"), GroupName = group + GetHashCode(), IsChecked = current == k, Content = Tb(label) };
                rb.Checked += (_, _) => set(k);
                w.Children.Add(rb);
            }
            return w;
        }

        private TextBox Input(string value, Action<string> changed, double width = 320)
        {
            var t = new TextBox { Text = value ?? "", Width = width, Height = 30, Style = (Style)FindResource("InputBox") };
            t.TextChanged += (_, _) => changed(t.Text);
            return t;
        }

        private Button Btn(string text, Action click, string glyph = null, bool accent = false)
        {
            var b = new Button { Style = (Style)FindResource(accent ? "AccentBtn" : "SubtleBtn"), Margin = new Thickness(0, 0, 8, 0) };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            if (glyph != null) sp.Children.Add(new TextBlock { Text = glyph, Style = (Style)FindResource("Glyph"), FontSize = 13, Margin = new Thickness(0, 0, 8, 0) });
            sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            b.Content = sp;
            b.Click += (_, _) => click();
            return b;
        }

        private StackPanel SaveBar()
        {
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            bar.Children.Add(Btn(Loc.T("Save"), Save, "", accent: true));
            bar.Children.Add(Btn(Loc.T("Cancel"), () => Cancelled?.Invoke()));
            return bar;
        }

        // ---------------- the page ----------------
        private void Build(bool isNew)
        {
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var bar = SaveBar(); DockPanel.SetDock(bar, Dock.Right); head.Children.Add(bar);
            head.Children.Add(new TextBlock { Text = Loc.T("WxvEditorTitle"), FontSize = 24, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            _page.Children.Add(head);
            _page.Children.Add(Dim(isNew ? Loc.T("WxvEditorNew") : Loc.F("WxvEditorFile", _file)).With(t => t.Margin = new Thickness(0, 0, 0, 8)));

            Header(Loc.T("WxvSecGeneral"));
            Card("", Loc.T("WxvTitle"), null, Input(_v.Title, s => _v.Title = s));
            Card("", Loc.T("WxvSubtitle"), Loc.T("WxvSubtitleDesc"), Input(_v.Subtitle, s => _v.Subtitle = string.IsNullOrWhiteSpace(s) ? null : s));
            Card("", Loc.T("WxvEnabled"), Loc.T("WxvEnabledDesc"), Switch(_v.Enabled, b => _v.Enabled = b));

            Header(Loc.T("WxvSecContent"));
            var pattern = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            pattern.Children.Add(Input(_v.FilePattern, s => _v.FilePattern = s, 360));
            var subs = new CheckBox { Content = Loc.T("WxvIncludeSub"), IsChecked = _v.IncludeSubfolders, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            subs.SetResourceReference(ForegroundProperty, "FgBrush");
            subs.Click += (_, _) => { _v.IncludeSubfolders = subs.IsChecked == true; Rescan(); };
            pattern.Children.Add(subs);
            pattern.Visibility = _v.Source == "files" ? Visibility.Visible : Visibility.Collapsed;
            var sourceBody = new StackPanel();
            sourceBody.Children.Add(Chips("src", new[] { ("folders", Loc.T("WxvSrcFolders")), ("categories", Loc.T("WxvSrcCategories")), ("files", Loc.T("WxvSrcFiles")) }, _v.Source,
                s => { _v.Source = s; pattern.Visibility = s == "files" ? Visibility.Visible : Visibility.Collapsed; Rescan(); }));
            sourceBody.Children.Add(pattern);
            WideCard("", Loc.T("WxvSource"), Loc.T("WxvSourceDesc"), sourceBody);

            Header(Loc.T("WxvSecLook"));
            var cols = new StackPanel { Orientation = Orientation.Horizontal };
            var colText = Tb(_v.Columns.ToString(), 13).With(t => { t.Width = 28; t.TextAlignment = TextAlignment.Right; t.VerticalAlignment = VerticalAlignment.Center; });
            var slider = new Slider { Minimum = 1, Maximum = 12, Value = _v.Columns, Width = 200, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
            slider.ValueChanged += (_, _) => { _v.Columns = (int)slider.Value; colText.Text = _v.Columns.ToString(); };
            cols.Children.Add(slider); cols.Children.Add(colText);
            Card("", Loc.T("WxvColumns"), Loc.T("WxvColumnsDesc"), cols);
            WideCard("", Loc.T("WxvCardStyle"), null, Chips("style", new[] { ("below", Loc.T("WxvStyleBelow")), ("hover", Loc.T("WxvStyleHover")), ("name", Loc.T("WxvStyleName")) }, _v.CardStyle, s => _v.CardStyle = s));
            WideCard("", Loc.T("WxvTitleFrom"), Loc.T("WxvTitleFromDesc"), Chips("tf", new[] { ("folder", Loc.T("WxvTitleFolder")), ("project", Loc.T("WxvTitleProject")) }, _v.TitleFrom, s => _v.TitleFrom = s));
            var sort = new StackPanel();
            sort.Children.Add(Chips("sort", new[] { ("name", Loc.T("WxvSortName")), ("created", Loc.T("WxvSortCreated")), ("modified", Loc.T("WxvSortModified")) }, _v.Sort, s => _v.Sort = s));
            var desc = new CheckBox { Content = Loc.T("WxvSortDesc"), IsChecked = _v.SortDesc, Margin = new Thickness(0, 8, 0, 0) };
            desc.SetResourceReference(ForegroundProperty, "FgBrush");
            desc.Click += (_, _) => _v.SortDesc = desc.IsChecked == true;
            sort.Children.Add(desc);
            WideCard("", Loc.T("WxvSort"), null, sort);

            Header(Loc.T("WxvSecInfo"));
            Card("", Loc.T("WxvStats"), Loc.T("WxvStatsDesc"), Switch(_v.ShowStats, b => _v.ShowStats = b));
            Card("", Loc.T("WxvShowCreated"), null, Switch(_v.ShowCreated, b => _v.ShowCreated = b));
            Card("", Loc.T("WxvShowAge"), Loc.T("WxvShowAgeDesc"), Switch(_v.ShowAge, b => _v.ShowAge = b));
            Card("", Loc.T("WxvShowModified"), null, Switch(_v.ShowModified, b => _v.ShowModified = b));
            WideCard("", Loc.T("WxvDoneProjects"), Loc.T("WxvDoneProjectsDesc"), Chips("done", new[] { ("show", Loc.T("WxvDoneShow")), ("last", Loc.T("WxvDoneLast")), ("hide", Loc.T("WxvDoneHide")) }, _v.DoneProjects, s => _v.DoneProjects = s));

            Header(Loc.T("WxvSecWeb"));
            Card("", Loc.T("WxvWebButton"), Loc.T("WxvWebButtonDesc"), Switch(_v.WebButton, b => _v.WebButton = b));
            Card("", Loc.T("WxvUrl"), Loc.T("WxvUrlDesc"), Input(_v.UrlTemplate, s => _v.UrlTemplate = s, 300));

            Header(Loc.T("WxvSecProjects"));
            _page.Children.Add(Dim(Loc.T("WxvProjectsDesc")).With(t => t.Margin = new Thickness(0, 0, 0, 8)));
            _page.Children.Add(_projects);
            var bottom = SaveBar(); bottom.Margin = new Thickness(0, 18, 0, 0);
            _page.Children.Add(bottom);
            Rescan();
        }

        // ---------------- projects ----------------
        private async void Rescan()
        {
            _cts?.Cancel();
            var cts = _cts = new CancellationTokenSource();
            _projects.Children.Clear();
            _projects.Children.Add(Dim(Loc.T("WxvScanning")));
            List<WxvEntry> list;
            try { list = await FolderViews.ScanAsync(_folder, _v, cts.Token); }
            catch { return; }
            if (cts.IsCancellationRequested) return;
            _projects.Children.Clear();
            if (list.Count == 0) { _projects.Children.Add(Dim(Loc.T("WxvEmpty"))); return; }
            string cat = null;
            foreach (var e in list.Take(_v.Source == "files" ? 300 : 2000))
            {
                if (e.Category != cat && !string.IsNullOrEmpty(e.Category))
                {
                    cat = e.Category;
                    _projects.Children.Add(new TextBlock { Text = cat, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 12, 0, 4) });
                }
                _projects.Children.Add(ProjectRow(e));
            }
            if (_v.Source == "files" && list.Count > 300) _projects.Children.Add(Dim(Loc.F("WxvMoreFiles", list.Count - 300)).With(t => t.Margin = new Thickness(2, 8, 0, 0)));
        }

        private FrameworkElement ProjectRow(WxvEntry e)
        {
            var item = _v.Item(e.Rel) ?? new WxvItem();
            WxvItem Edit() => _v.EnsureItem(e.Rel);
            var g = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var img = new Image { Width = 30, Height = 30, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
            void ShowImage()
            {
                var p = FolderViews.ResolveImage(_folder, _v.Item(e.Rel)?.Image) ?? e.ImagePath;
                img.Source = null;
                if (p == null) { img.Source = IconService.FolderIcon; return; }
                _ = ImageLoader.RunSta(() => { try { var (s, _) = ImageLoader.Load(p, 64); if (s != null && s.CanFreeze && !s.IsFrozen) s.Freeze(); return s; } catch { return null; } })
                    .ContinueWith(t => { if (t.Result != null) img.Source = t.Result; }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
            }
            ShowImage();
            g.Children.Add(img);

            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            var auto = e.IsFile ? Path.GetFileNameWithoutExtension(e.FolderName) : _v.TitleFrom == "project" && !string.IsNullOrWhiteSpace(e.ProjectName) ? e.ProjectName : e.FolderName;
            var title = new TextBox { Text = item.Title ?? "", Height = 28, Style = (Style)FindResource("InputBox"), ToolTip = Loc.F("WxvOwnName", auto) };
            title.TextChanged += (_, _) => Edit().Title = string.IsNullOrWhiteSpace(title.Text) ? null : title.Text;
            var hint = Dim(e.Rel + (string.IsNullOrEmpty(item.Title) ? "  ·  " + auto : ""), 11).With(t => { t.TextWrapping = TextWrapping.NoWrap; t.TextTrimming = TextTrimming.CharacterEllipsis; t.Margin = new Thickness(2, 2, 0, 0); });
            names.Children.Add(title);
            names.Children.Add(hint);
            Grid.SetColumn(names, 1); g.Children.Add(names);

            var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            CheckBox Check(string text, bool? value, bool three, string tip, Action<bool?> set)
            {
                var cb = new CheckBox { Content = text, IsChecked = value, IsThreeState = three, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = tip };
                cb.SetResourceReference(ForegroundProperty, "FgBrush");
                cb.Click += (_, _) => set(cb.IsChecked);
                return cb;
            }
            if (!e.IsFile)
            {
                tools.Children.Add(Check(Loc.T("WxvDone"), item.Done, false, null, b => Edit().Done = b == true));
                // web: ■ = found out automatically, ✓ = yes, empty = no
                tools.Children.Add(Check(Loc.T("WxvWeb"), item.Web == null ? null : item.Web, true,
                    Loc.F("WxvWebTip", e.HasWebIndex ? Loc.T("Yes") : Loc.T("No")), b => Edit().Web = b));
            }
            tools.Children.Add(Check(Loc.T("WxvHide"), item.Hidden, false, null, b => Edit().Hidden = b == true));
            var pick = new Button { Style = (Style)FindResource("SubtleBtn"), Content = Loc.T("WxvPicture"), Margin = new Thickness(0, 0, 4, 0), ToolTip = Loc.T("WxvPictureTip") };
            pick.Click += (_, _) =>
            {
                var f = PickerWindow.Files(Window.GetWindow(this), Loc.T("WxvPicture"), e.IsFile ? Path.GetDirectoryName(e.Path) : e.Path,
                    new[] { new PickerWindow.Filter(Loc.T("WxvImages"), "*.png;*.jpg;*.jpeg;*.webp;*.svg;*.ico;*.gif;*.bmp") });
                if (f == null || f.Length == 0) return;
                try { Edit().Image = FolderViews.AddImage(_folder, f[0]); ShowImage(); }
                catch (Exception ex) { Notify.Show(ex.Message); }
            };
            tools.Children.Add(pick);
            var reset = new Button { Style = (Style)FindResource("IconBtn"), Width = 28, Height = 28, ToolTip = Loc.T("WxvPictureReset"), Content = new TextBlock { Text = "", Style = (Style)FindResource("Glyph"), FontSize = 11 } };
            reset.Click += (_, _) => { if (_v.Item(e.Rel) is { } it) { it.Image = null; ShowImage(); } };
            tools.Children.Add(reset);
            Grid.SetColumn(tools, 2); g.Children.Add(tools);
            return CardBorder(g).With(b => b.Padding = new Thickness(10, 6, 10, 6));
        }

        private void Save()
        {
            if (string.IsNullOrWhiteSpace(_v.Title)) _v.Title = null;
            var err = FolderViews.Save(_file, _v);
            if (err != null) { MessageBox.Show(Window.GetWindow(this), err, "WinXplorer", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            Saved?.Invoke();
        }
    }
}
