using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using WinXplorer.Services;

namespace WinXplorer.Views
{
    /// <summary>
    /// A folder shown through its view.wxv: title, numbers, and the projects (or pictures) as cards – instead of the
    /// file list. The bar on top opens the plain folder or the view's settings.
    /// </summary>
    public sealed class FolderViewControl : UserControl
    {
        public event Action<string, bool> OpenFolder;   // path, new tab
        public event Action OpenPlain, OpenEditor;

        private readonly string _folder, _file;
        private readonly WxvView _v;
        private readonly StackPanel _body = new();
        private readonly WrapPanel _stats = new() { Margin = new Thickness(0, 12, 0, 4) };
        private readonly TextBlock _status = new() { Margin = new Thickness(6, 20, 0, 0) };
        private CancellationTokenSource _cts;
        private static readonly SemaphoreSlim _decoders = new(4);
        private static readonly ConcurrentDictionary<string, ImageSource> _thumbs = new(StringComparer.OrdinalIgnoreCase);

        public FolderViewControl(string folder, string file, WxvView v)
        {
            _folder = folder; _file = file; _v = v;
            Focusable = true;
            SetResourceReference(BackgroundProperty, "ContentBrush");
            SetResourceReference(ForegroundProperty, "FgBrush");
            _status.SetResourceReference(TextBlock.ForegroundProperty, "FgDimBrush");

            // ---- header: title, subtitle; plain folder + settings on the right ----
            var head = new DockPanel();
            var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            tools.Children.Add(ToolBtn("", Loc.T("WxvOpenPlain"), () => OpenPlain?.Invoke()));
            tools.Children.Add(ToolBtn("", Loc.T("WxvSettings"), () => OpenEditor?.Invoke()));
            DockPanel.SetDock(tools, Dock.Right); head.Children.Add(tools);
            var titles = new StackPanel();
            titles.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(v.Title) ? FileSystem.DisplayName(folder) : v.Title, FontSize = 26, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrWhiteSpace(v.Subtitle)) titles.Children.Add(Dim(v.Subtitle, 13).With(t => t.Margin = new Thickness(0, 2, 0, 0)));
            head.Children.Add(titles);

            var top = new StackPanel { Margin = new Thickness(28, 20, 28, 6) };
            top.Children.Add(head);
            top.Children.Add(_stats);
            _body.Margin = new Thickness(22, 0, 22, 28);
            var scroll = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var root = new DockPanel();
            DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
            root.Children.Add(scroll);
            Content = root;
            _status.Text = Loc.T("Loading");
            _body.Children.Add(_status);
            Loaded += (_, _) => Reload();
            Unloaded += (_, _) => _cts?.Cancel();
        }

        private static TextBlock Dim(string text, double size = 12)
        {
            var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, "FgDimBrush");
            return t;
        }

        private Button ToolBtn(string glyph, string text, Action a)
        {
            var b = new Button { Style = (Style)FindResource("SubtleBtn"), Margin = new Thickness(6, 0, 0, 0), ToolTip = text };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = glyph, Style = (Style)FindResource("Glyph"), FontSize = 14, Margin = new Thickness(0, 0, 8, 0) });
            sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            b.Content = sp;
            b.Click += (_, _) => a();
            return b;
        }

        // ---------------- content ----------------
        public async void Reload()
        {
            _cts?.Cancel();
            var cts = _cts = new CancellationTokenSource();
            List<WxvEntry> all;
            try { all = await FolderViews.ScanAsync(_folder, _v, cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _body.Children.Clear(); _status.Text = ex.Message; _body.Children.Add(_status); return; }
            if (cts.IsCancellationRequested) return;
            Fill(all);
        }

        private void Fill(List<WxvEntry> all)
        {
            var shown = all.Where(e => _v.Item(e.Rel)?.Hidden != true).ToList();
            bool Done(WxvEntry e) => _v.Item(e.Rel)?.Done == true;
            if (_v.DoneProjects == "hide") shown = shown.Where(e => !Done(e)).ToList();

            // numbers on top
            _stats.Children.Clear();
            if (_v.ShowStats && shown.Count > 0)
            {
                var visible = all.Where(e => _v.Item(e.Rel)?.Hidden != true).ToList();
                if (_v.Source == "files") Stat("", Loc.Items(visible.Count));
                else
                {
                    Stat("", Loc.F("WxvStatProjects", visible.Count));
                    if (_v.Source == "categories") Stat("", Loc.F("WxvStatCategories", visible.Select(e => e.Category).Distinct().Count()));
                    int done = visible.Count(Done);
                    if (done > 0) { Stat("", Loc.F("WxvStatDone", done)); Stat("", Loc.F("WxvStatActive", visible.Count - done)); }
                    var newest = visible.MaxBy(e => e.Created);
                    var oldest = visible.MinBy(e => e.Created);
                    if (newest != null) Stat("", Loc.F("WxvStatNewest", FolderViews.TitleOf(_v, newest), newest.Created.ToString("d")));
                    if (oldest != null && oldest != newest) Stat("", Loc.F("WxvStatOldest", FolderViews.TitleOf(_v, oldest), oldest.Created.ToString("d")));
                }
            }

            IEnumerable<WxvEntry> Sorted(IEnumerable<WxvEntry> l)
            {
                IOrderedEnumerable<WxvEntry> o = _v.DoneProjects == "last" ? l.OrderBy(Done) : l.OrderBy(_ => 0);
                o = _v.Sort switch
                {
                    "created" => _v.SortDesc ? o.ThenByDescending(e => e.Created) : o.ThenBy(e => e.Created),
                    "modified" => _v.SortDesc ? o.ThenByDescending(e => e.Modified) : o.ThenBy(e => e.Modified),
                    _ => _v.SortDesc ? o.ThenByDescending(e => FolderViews.TitleOf(_v, e), StringComparer.CurrentCultureIgnoreCase)
                                     : o.ThenBy(e => FolderViews.TitleOf(_v, e), StringComparer.CurrentCultureIgnoreCase),
                };
                return o;
            }

            _body.Children.Clear();
            if (shown.Count == 0) { _status.Text = Loc.T("WxvEmpty"); _body.Children.Add(_status); return; }
            var groups = shown.GroupBy(e => e.Category ?? "").OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);
            foreach (var g in groups)
            {
                if (g.Key.Length > 0)
                {
                    var h = new TextBlock { Text = g.Key, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, _body.Children.Count == 0 ? 6 : 22, 0, 6) };
                    var cnt = Dim("  " + g.Count(), 13);
                    var hp = new StackPanel { Orientation = Orientation.Horizontal };
                    hp.Children.Add(h); hp.Children.Add(cnt.With(c => c.VerticalAlignment = VerticalAlignment.Bottom).With(c => c.Margin = new Thickness(0, 0, 0, 8)));
                    _body.Children.Add(hp);
                }
                var grid = new UniformGrid { Columns = Math.Clamp(_v.Columns, 1, 12), Margin = new Thickness(0, 4, 0, 0) };
                foreach (var e in Sorted(g)) grid.Children.Add(Card(e));
                _body.Children.Add(grid);
            }
        }

        private void Stat(string glyph, string text)
        {
            var b = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 4, 12, 4), Margin = new Thickness(0, 0, 8, 6), BorderThickness = new Thickness(1) };
            b.SetResourceReference(Border.BackgroundProperty, "BarBrush");
            b.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var g = new TextBlock { Text = glyph, Style = (Style)FindResource("Glyph"), FontSize = 12, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
            g.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
            sp.Children.Add(g);
            sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            b.Child = sp;
            _stats.Children.Add(b);
        }

        // ---------------- one card ----------------
        private FrameworkElement Card(WxvEntry e)
        {
            var item = _v.Item(e.Rel);
            bool done = item?.Done == true;
            bool web = _v.WebButton && FolderViews.IsWeb(_v, e);
            string title = FolderViews.TitleOf(_v, e);
            string style = _v.CardStyle ?? "below";

            var card = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(6), Cursor = Cursors.Hand, ClipToBounds = true };
            card.SetResourceReference(Border.BackgroundProperty, "BarBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            var stack = new Grid();
            stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // picture area (keeps a 16:10 shape at any card width)
            var pic = new Grid { Height = 120 };
            card.SizeChanged += (_, a) => { if (a.WidthChanged) pic.Height = Math.Max(60, a.NewSize.Width * (e.IsFile ? 0.75 : 0.62)); };
            var img = new Image { Stretch = Stretch.Uniform, Margin = e.IsFile ? new Thickness(0) : new Thickness(18), Opacity = done ? 0.55 : 1 };
            if (e.IsFile) img.Stretch = Stretch.UniformToFill;
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            pic.Children.Add(img);
            var custom = FolderViews.ResolveImage(_folder, item?.Image);
            var src = custom ?? e.ImagePath;
            if (src == null) { img.Source = IconService.GetLargeIcon(new Models.FileItem { Name = e.FolderName, FullPath = e.Path, IsDirectory = true }, 256); img.Margin = new Thickness(30); }
            else _ = LoadThumb(src, img);

            if (done)
            {
                var badge = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2, 9, 3), Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
                badge.Background = new SolidColorBrush(Color.FromRgb(0x10, 0x89, 0x3E));
                var bt = new StackPanel { Orientation = Orientation.Horizontal };
                bt.Children.Add(new TextBlock { Text = "", Style = (Style)FindResource("Glyph"), FontSize = 10, Foreground = Brushes.White, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                bt.Children.Add(new TextBlock { Text = Loc.T("WxvDone"), Foreground = Brushes.White, FontSize = 11 });
                badge.Child = bt;
                pic.Children.Add(badge);
            }

            // buttons on hover: open in browser
            var hoverTools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6), Visibility = Visibility.Hidden };
            if (web)
            {
                var url = FolderViews.UrlOf(_v, e);
                var wb = new Button { Style = (Style)FindResource("AccentBtn"), Padding = new Thickness(8, 4, 10, 4), ToolTip = url, MinWidth = 0, Cursor = Cursors.Arrow };
                var wsp = new StackPanel { Orientation = Orientation.Horizontal };
                wsp.Children.Add(new TextBlock { Text = "", Style = (Style)FindResource("Glyph"), FontSize = 12, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                wsp.Children.Add(new TextBlock { Text = Loc.T("WxvBrowser"), FontSize = 12 });
                wb.Content = wsp;
                wb.Click += (_, a) => { a.Handled = true; OpenUrl(url); };
                hoverTools.Children.Add(wb);
            }
            pic.Children.Add(hoverTools);

            // name (and dates) under the picture, or over it on hover
            var text = new StackPanel { Margin = new Thickness(12, 8, 12, 10) };
            text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = e.Rel });
            var meta = new List<string>();
            if (_v.ShowCreated) meta.Add(Loc.F("WxvCreated", e.Created.ToString("d")));
            if (_v.ShowAge) meta.Add(Loc.F("WxvDays", Math.Max(0, (int)(DateTime.Now - e.Created).TotalDays)));
            if (_v.ShowModified) meta.Add(Loc.F("WxvModified", e.Modified.ToString("d")));
            if (meta.Count > 0 && style != "name") text.Children.Add(Dim(string.Join("  ·  ", meta), 11).With(t => { t.TextWrapping = TextWrapping.NoWrap; t.TextTrimming = TextTrimming.CharacterEllipsis; t.Margin = new Thickness(0, 2, 0, 0); }));

            Grid.SetRow(pic, 0); stack.Children.Add(pic);
            if (style == "below")
            {
                Grid.SetRow(text, 1); stack.Children.Add(text);
            }
            else
            {
                // over the picture, only while the mouse is on the card
                var over = new Border { VerticalAlignment = VerticalAlignment.Bottom, Child = text, Visibility = Visibility.Hidden };
                over.SetResourceReference(Border.BackgroundProperty, "PopupBrush");
                over.Opacity = 0.94;
                pic.Children.Add(over);
                card.MouseEnter += (_, _) => over.Visibility = Visibility.Visible;
                card.MouseLeave += (_, _) => over.Visibility = Visibility.Hidden;
                card.ToolTip = null;
            }
            card.Child = stack;

            card.MouseEnter += (_, _) => { hoverTools.Visibility = Visibility.Visible; card.SetResourceReference(Border.BorderBrushProperty, "AccentBrush"); };
            card.MouseLeave += (_, _) => { hoverTools.Visibility = Visibility.Hidden; card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); };
            card.MouseLeftButtonUp += (_, a) => { if (a.ChangedButton == MouseButton.Left) Open(e, Keyboard.Modifiers.HasFlag(ModifierKeys.Control)); };
            card.MouseUp += (_, a) => { if (a.ChangedButton == MouseButton.Middle) Open(e, true); };
            card.MouseRightButtonUp += (_, a) => { a.Handled = true; Menu(e, web); };
            return card;
        }

        private void Open(WxvEntry e, bool newTab)
        {
            if (e.IsFile) FileOps.Open(e.Path);
            else OpenFolder?.Invoke(e.Path, newTab);
        }

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Notify.Show(ex.Message); }
        }

        private void Menu(WxvEntry e, bool web)
        {
            var cm = new ContextMenu();
            cm.Items.Add(MainWindow.MI(Loc.T("Open"), "", () => Open(e, false)));
            if (!e.IsFile) cm.Items.Add(MainWindow.MI(Loc.T("OpenNewTab"), "", () => Open(e, true)));
            if (web) cm.Items.Add(MainWindow.MI(Loc.T("WxvBrowser"), "", () => OpenUrl(FolderViews.UrlOf(_v, e))));
            if (e.IsFile) cm.Items.Add(MainWindow.MI(Loc.T("OpenLocation"), "", () => OpenFolder?.Invoke(Path.GetDirectoryName(e.Path), false)));
            cm.Items.Add(new Separator());
            if (!e.IsFile)
            {
                bool done = _v.Item(e.Rel)?.Done == true;
                cm.Items.Add(MainWindow.MI(Loc.T(done ? "WxvMarkActive" : "WxvMarkDone"), "", () => { _v.EnsureItem(e.Rel).Done = !done; SaveAndReload(); }));
            }
            cm.Items.Add(MainWindow.MI(Loc.T("WxvHide"), "", () => { _v.EnsureItem(e.Rel).Hidden = true; SaveAndReload(); }));
            cm.Items.Add(MainWindow.MI(Loc.T("WxvSettings"), "", () => OpenEditor?.Invoke()));
            cm.IsOpen = true;
        }

        private void SaveAndReload()
        {
            var err = FolderViews.Save(_file, _v);
            if (err != null) Notify.Show(err);
            Reload();
        }

        private static async Task LoadThumb(string path, Image target)
        {
            DateTime stamp;
            try { stamp = File.GetLastWriteTimeUtc(path); } catch { return; }
            var key = path + "|" + stamp.Ticks;
            if (_thumbs.TryGetValue(key, out var cached)) { target.Source = cached; return; }
            await _decoders.WaitAsync();
            try
            {
                var img = await ImageLoader.RunSta(() =>
                {
                    try
                    {
                        var (src, _) = ImageLoader.Load(path, 360);
                        if (src != null && src.CanFreeze && !src.IsFrozen) src.Freeze();
                        return src;
                    }
                    catch { return null; }
                });
                if (img == null) return;
                _thumbs[key] = img;
                target.Source = img;
            }
            finally { _decoders.Release(); }
        }
    }
}
