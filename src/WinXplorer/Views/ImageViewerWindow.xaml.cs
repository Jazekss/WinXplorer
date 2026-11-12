using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinXplorer.Models;
using WinXplorer.Services;

namespace WinXplorer.Views
{
    /// <summary>
    /// Fast image viewer (Picasa style): Space opens and closes it, wheel zooms, drag pans, arrows or the
    /// wide click zones at the left/right edge browse, filmstrip below.
    /// </summary>
    public partial class ImageViewerWindow : Window
    {
        private readonly List<FileItem> _items;
        private int _index;
        private double _zoom = 1, _fitZoom = 1;
        private bool _fit = true;
        private int _rotation;
        private Point _panStart, _offsetStart;
        private Vector _offset;
        private bool _panning;
        private int _loadId;
        private readonly Dictionary<string, (ImageSource image, ImageInfo info)> _cache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The image shown when the viewer was closed (so the file list can select it).</summary>
        public FileItem CurrentItem => _items.Count > 0 ? _items[Math.Clamp(_index, 0, _items.Count - 1)] : null;

        public ImageViewerWindow(List<FileItem> items, int index, Window owner)
        {
            InitializeComponent();
            Owner = owner;
            _items = items;
            _index = Math.Clamp(index, 0, Math.Max(0, items.Count - 1));
            Strip.ItemsSource = _items;
            Strip.Visibility = _items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            PreviewKeyDown += OnKey;
            SizeChanged += (_, _) => { if (_fit) FitImage(); else Apply(); };
            Loaded += (_, _) => { Strip.SelectedIndex = _index; Show(_index); Activate(); Focus(); };
            StateChanged += (_, _) => UpdateMaxState();
            SourceInitialized += (_, _) => ThemeManager.ApplyWindowFrame(new System.Windows.Interop.WindowInteropHelper(this).Handle);

            // a normal window over the file manager (not full screen), at most ~85% of it, centred on its monitor;
            // once the first picture is known the window shrinks to it if it's small
            var o = WindowPlacement.OwnerRect(owner, WindowPlacement.WorkArea(owner));
            WindowPlacement.Center(this, owner, o.Width * 0.85, o.Height * 0.85);
        }

        private bool _sizedToImage;

        /// <summary>Small first picture: a smaller window around it (still centred over the file manager).</summary>
        private void SizeToImage(ImageInfo info)
        {
            if (_sizedToImage || info.Width <= 0 || info.Height <= 0 || WindowState != WindowState.Normal) return;
            _sizedToImage = true;
            var dpi = VisualTreeHelper.GetDpi(this);
            double iw = info.Width / dpi.DpiScaleX, ih = info.Height / dpi.DpiScaleY;
            double chromeW = ActualWidth - Stage.ActualWidth, chromeH = ActualHeight - Stage.ActualHeight;
            double w = Math.Max(iw + chromeW + 48, 560), h = Math.Max(ih + chromeH + 48, 420);
            if (w >= Width && h >= Height) return;   // big picture: keep the large window
            WindowPlacement.Center(this, Owner, Math.Min(w, Width), Math.Min(h, Height));
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);

        private void UpdateMaxState()
        {
            bool max = WindowState == WindowState.Maximized;
            MaxGlyph.Text = max ? "" : "";
            double scale = Math.Max(1, VisualTreeHelper.GetDpi(this).DpiScaleX);
            Root.Margin = max ? new Thickness((GetSystemMetrics(32) + GetSystemMetrics(92)) / scale) : new Thickness(0);
        }

        private void Max_Click(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private async void Show(int index)
        {
            if (_items.Count == 0) { Close(); return; }
            _index = Math.Clamp(index, 0, _items.Count - 1);
            var item = _items[_index];
            int id = ++_loadId;
            NameText.Text = item.Name;
            InfoText.Text = $"{_index + 1} / {_items.Count}   ·   {FileItem.FormatSize(item.Size)}";
            PrevBtn.Visibility = _index > 0 ? Visibility.Visible : Visibility.Hidden;
            NextBtn.Visibility = _index < _items.Count - 1 ? Visibility.Visible : Visibility.Hidden;
            if (Strip.SelectedIndex != _index) { Strip.SelectedIndex = _index; Strip.ScrollIntoView(item); }
            _rotation = 0;
            if (_crop) EndCrop();

            if (!_cache.TryGetValue(item.FullPath, out var r))
            {
                Status.Text = Loc.T("Loading");
                r = await ImageLoader.RunSta(() => ImageLoader.Load(item.FullPath, 8192));
                if (id != _loadId) return;
                Remember(item.FullPath, r);
            }
            Status.Text = r.image == null ? Loc.T("NoCodec") : "";
            if (r.image == null && FileTypes.MissingCodec(item.FullPath) is { } codec)
            {
                // a link per Store extension this format needs (free, from Microsoft)
                Status.Text = Loc.F("CodecMissing", Loc.T(codec.nameKey)) + "\n\n";
                for (int i = 0; i < codec.storeIds.Length; i++)
                {
                    var sid = codec.storeIds[i];
                    var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(Loc.T("GetCodec") + (codec.storeIds.Length > 1 ? $" ({i + 1})" : "")));
                    link.Click += (_, _) => FileTypes.OpenStore(sid);
                    link.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "AccentTextBrush");
                    if (i > 0) Status.Inlines.Add(new System.Windows.Documents.Run("    "));
                    Status.Inlines.Add(link);
                }
            }
            Img.Source = r.image;
            if (r.info.Width > 0) InfoText.Text += $"   ·   {r.info.Width} × {r.info.Height}" + (string.IsNullOrEmpty(r.info.Camera) ? "" : "   ·   " + r.info.Camera);
            _shownInfo = (item, r.info);
            FillInfo();
            _fit = true;
            FitImage();
            SizeToImage(r.info);
            Prefetch(_index + 1);
            Prefetch(_index - 1);
        }

        // ---------- (i) details overlay ----------
        private static bool _infoOpen;          // remembered while the app runs
        private (FileItem item, ImageInfo info) _shownInfo;

        private void Info_Click(object s, RoutedEventArgs e) { _infoOpen = !_infoOpen; FillInfo(); }

        private void FillInfo()
        {
            InfoPanel.Visibility = _infoOpen ? Visibility.Visible : Visibility.Collapsed;
            if (!_infoOpen || _shownInfo.item is not { } it) return;
            var i = _shownInfo.info;
            InfoRows.Children.Clear();
            void Row(string label, string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                var sp = new StackPanel { Margin = new Thickness(0, 3, 0, 3) };
                sp.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xA8, 0xA8)) });
                sp.Children.Add(new TextBox { Text = value, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)), TextWrapping = TextWrapping.Wrap });
                InfoRows.Children.Add(sp);
            }
            // no name (it's in the title bar) and no folder: a narrow box that hides less of the picture
            Row(Loc.T("Size"), FileItem.FormatSize(it.Size) + (it.Size >= 1024 ? $"\n{it.Size:N0} B" : ""));
            if (i != null && i.Width > 0)
            {
                long px = i.Width * (long)i.Height;
                int g = Gcd(i.Width, i.Height);
                string ratio = i.Width / g <= 64 ? $"{i.Width / g}:{i.Height / g}" : $"{(double)i.Width / i.Height:0.###}:1";
                Row(Loc.T("Dimensions"), $"{i.Width} × {i.Height} px\n{px / 1e6:0.##} MP  ·  {ratio}");
            }
            if (i != null)
            {
                Row(Loc.T("Format"), (i.Format ?? it.Extension.TrimStart('.').ToUpperInvariant()) + (i.Frames > 1 ? $"  ·  {i.Frames} frames" : ""));
                if (i.Bits > 0) Row(Loc.T("ColorDepth"), $"{i.Bits} bit/px" + (i.Bits is 32 or 64 ? "  (alpha)" : ""));
                if (i.DpiX > 0) Row("DPI", $"{i.DpiX:0}" + (i.Width > 0 && i.DpiX > 1 ? $"  ·  {i.Width / i.DpiX * 2.54:0.#} × {i.Height / i.DpiX * 2.54:0.#} cm" : ""));
                if (i.Width > 0 && i.Bits > 0) Row(Loc.T("MemorySize"), FileItem.FormatSize(i.Width * (long)i.Height * Math.Max(i.Bits, 8) / 8));
                Row(Loc.T("Camera"), i.Camera);
                Row(Loc.T("DateTaken"), i.DateTaken);
                Row(Loc.T("Exposure"), i.Exposure);
            }
            try
            {
                var fi = new System.IO.FileInfo(it.FullPath);
                Row(Loc.T("Created"), fi.CreationTime.ToString("g"));
                Row(Loc.T("ColModified"), fi.LastWriteTime.ToString("g"));
            }
            catch { }
        }
        private static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return Math.Max(1, a); }

        private void Remember(string path, (ImageSource, ImageInfo) r)
        {
            if (_cache.Count > 6) _cache.Remove(_cache.Keys.First());
            _cache[path] = r;
        }

        private void Prefetch(int i)
        {
            if (i < 0 || i >= _items.Count) return;
            var p = _items[i].FullPath;
            if (_cache.ContainsKey(p)) return;
            _ = ImageLoader.RunSta(() => ImageLoader.Load(p, 8192)).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully) Dispatcher.BeginInvoke(() => { if (!_cache.ContainsKey(p)) Remember(p, t.Result); });
            });
        }

        private Size ImageSize()
        {
            if (Img.Source == null) return new Size(1, 1);
            double w = Img.Source.Width, h = Img.Source.Height;
            if (Img.Source is System.Windows.Media.Imaging.BitmapSource b)
            {
                // show photos at their real pixel size regardless of the file's DPI value
                var dpi = VisualTreeHelper.GetDpi(this);
                w = b.PixelWidth / dpi.DpiScaleX; h = b.PixelHeight / dpi.DpiScaleY;
            }
            return _rotation % 180 == 0 ? new Size(w, h) : new Size(h, w);
        }

        private void FitImage()
        {
            var s = ImageSize();
            double aw = Math.Max(1, Stage.ActualWidth - 40), ah = Math.Max(1, Stage.ActualHeight - 20);
            _fitZoom = Math.Min(1.0, Math.Min(aw / s.Width, ah / s.Height));
            if (Img.Source is DrawingImage) _fitZoom = Math.Min(aw / s.Width, ah / s.Height); // vectors may scale up
            _zoom = _fitZoom;
            _offset = new Vector((Stage.ActualWidth - s.Width * _zoom) / 2, (Stage.ActualHeight - s.Height * _zoom) / 2);
            Apply();
        }

        private void Apply()
        {
            if (Img.Source == null) { ZoomText.Text = ""; return; }
            var s = ImageSize();
            if (Img.Source is System.Windows.Media.Imaging.BitmapSource b)
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                Img.Width = b.PixelWidth / dpi.DpiScaleX; Img.Height = b.PixelHeight / dpi.DpiScaleY;
            }
            else { Img.Width = Img.Source.Width; Img.Height = Img.Source.Height; }

            var tg = new TransformGroup();
            tg.Children.Add(new RotateTransform(_rotation, Img.Width / 2, Img.Height / 2));
            // after rotating around the centre, move the rotated bounds back to the origin
            double dx = (s.Width - Img.Width) / 2, dy = (s.Height - Img.Height) / 2;
            tg.Children.Add(new TranslateTransform(dx, dy));
            tg.Children.Add(new ScaleTransform(_zoom, _zoom));
            tg.Children.Add(new TranslateTransform(_offset.X, _offset.Y));
            Img.RenderTransform = tg;
            RenderOptions.SetBitmapScalingMode(Img, _zoom >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
            ZoomText.Text = $"{_zoom * 100:0}%";
            if (_crop) DrawCrop();
        }

        private void ZoomAt(double factor, Point at)
        {
            double nz = Math.Clamp(_zoom * factor, 0.02, 40);
            factor = nz / _zoom;
            _offset = new Vector(at.X - (at.X - _offset.X) * factor, at.Y - (at.Y - _offset.Y) * factor);
            _zoom = nz;
            _fit = false;
            Apply();
        }

        private Point Center => new(Stage.ActualWidth / 2, Stage.ActualHeight / 2);

        private void Stage_Wheel(object sender, MouseWheelEventArgs e) => ZoomAt(e.Delta > 0 ? 1.2 : 1 / 1.2, e.GetPosition(Stage));

        private void Stage_Down(object sender, MouseButtonEventArgs e)
        {
            if (_crop) { CropDown(e); return; }
            if (e.ClickCount == 2)
            {
                if (Math.Abs(_zoom - 1) < 0.001) { _fit = true; FitImage(); }
                else { ZoomAt(1 / _zoom, e.GetPosition(Stage)); }
                return;
            }
            _panning = true;
            _panStart = e.GetPosition(Stage);
            _offsetStart = new Point(_offset.X, _offset.Y);
            Stage.CaptureMouse();
            Stage.Cursor = Cursors.SizeAll;
        }

        private void Stage_Move(object sender, MouseEventArgs e)
        {
            if (_crop) { CropMove(e); return; }
            if (!_panning) return;
            var p = e.GetPosition(Stage);
            _offset = new Vector(_offsetStart.X + p.X - _panStart.X, _offsetStart.Y + p.Y - _panStart.Y);
            _fit = false;
            Apply();
        }

        private void Stage_Up(object sender, MouseButtonEventArgs e)
        {
            if (_crop) { _cropMode = 0; Stage.ReleaseMouseCapture(); return; }
            _panning = false;
            Stage.ReleaseMouseCapture();
            Stage.Cursor = null;
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (_crop)
            {
                if (e.Key == Key.Escape) { EndCrop(); e.Handled = true; }
                else if (e.Key == Key.Enter) { CropApply_Click(null, null); e.Handled = true; }
                else if (e.Key is Key.Space or Key.Left or Key.Right or Key.Up or Key.Down or Key.Delete or Key.R) e.Handled = true;
                return;
            }
            switch (Hotkeys.Match(HkScope.Viewer, e))
            {
                case "Viewer.Close": Close(); break;
                case "Viewer.Maximize": Max_Click(null, null); break;
                case "Viewer.Next": Show(_index + 1); break;
                case "Viewer.Prev": Show(_index - 1); break;
                case "Viewer.First": Show(0); break;
                case "Viewer.Last": Show(_items.Count - 1); break;
                case "Viewer.ZoomIn": ZoomAt(1.25, Center); break;
                case "Viewer.ZoomOut": ZoomAt(0.8, Center); break;
                case "Viewer.Fit": _fit = true; FitImage(); break;
                case "Viewer.Actual": ZoomAt(1 / _zoom, Center); break;
                case "Viewer.Crop": Crop_Click(null, null); break;
                case "Viewer.RotateRight": Rotate(90); break;
                case "Viewer.RotateLeft": Rotate(-90); break;
                case "Viewer.Delete": Delete_Click(null, null); break;
                case "Viewer.Info": Info_Click(null, null); break;
                case "Viewer.Open": OpenExternal_Click(null, null); break;
                case "Viewer.Copy": FileOps.SetClipboard(new[] { _items[_index].FullPath }, false); break;
                default: return;
            }
            e.Handled = true;
        }

        private void Rotate(int deg)
        {
            _rotation = ((_rotation + deg) % 360 + 360) % 360;
            _fit = true;
            FitImage();
        }

        private void ZoomIn_Click(object s, RoutedEventArgs e) => ZoomAt(1.25, Center);
        private void ZoomOut_Click(object s, RoutedEventArgs e) => ZoomAt(0.8, Center);
        private void Fit_Click(object s, RoutedEventArgs e) { _fit = true; FitImage(); }
        private void Actual_Click(object s, RoutedEventArgs e) => ZoomAt(1 / _zoom, Center);
        private void Rotate_Click(object s, RoutedEventArgs e) => Rotate(90);
        private void Prev_Click(object s, RoutedEventArgs e) => Show(_index - 1);
        private void Next_Click(object s, RoutedEventArgs e) => Show(_index + 1);
        private void Close_Click(object s, RoutedEventArgs e) => Close();
        private void OpenExternal_Click(object s, RoutedEventArgs e) { if (_items.Count > 0) FileOps.Open(_items[_index].FullPath); }

        private void Delete_Click(object s, RoutedEventArgs e)
        {
            if (_items.Count == 0) return;
            var item = _items[_index];
            _cache.Remove(item.FullPath);
            Img.Source = null;
            FileOps.Delete(new[] { item.FullPath }, false);
            // the shell deletes asynchronously; drop it from the strip once it is gone
            _ = Task.Delay(400).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
            {
                if (File.Exists(item.FullPath)) { Show(_index); return; }
                int i = _items.IndexOf(item);
                if (i >= 0) _items.RemoveAt(i);
                Strip.Items.Refresh();
                Strip.Visibility = _items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
                if (_items.Count == 0) Close(); else Show(Math.Min(i, _items.Count - 1));
            }));
        }

        private void Strip_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (Strip.SelectedIndex >= 0 && Strip.SelectedIndex != _index) Show(Strip.SelectedIndex);
        }
    }
}
