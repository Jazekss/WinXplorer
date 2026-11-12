using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinXplorer.Interop;
using WinXplorer.Services;

namespace WinXplorer.Views
{
    /// <summary>
    /// Crop (X or the crop button): drag a rectangle over the picture – or move / resize it by its edges and
    /// corners – then "Crop" overwrites the file (the original goes to the Recycle Bin) or "Save copy" writes
    /// "name (cropped).ext" next to it. The current rotation is applied too.
    /// </summary>
    public partial class ImageViewerWindow
    {
        private bool _crop;
        private Rect _cropN = new(0, 0, 1, 1);   // normalised to the shown (rotated) picture
        private int _cropMode;                   // 0 idle, 1 new, 2 move, 3 resize
        private int _cropEdges;                  // 1 left, 2 right, 4 top, 8 bottom
        private Point _cropFrom;
        private Rect _cropStart;
        private bool _cropBusy;

        private void Crop_Click(object s, RoutedEventArgs e)
        {
            if (_crop) { EndCrop(); return; }
            if (Img.Source is not BitmapSource || _items.Count == 0) return;
            _crop = true;
            _cropN = new Rect(0, 0, 1, 1);
            _fit = true;
            FitImage();
            CropLayer.Visibility = CropBar.Visibility = Visibility.Visible;
            PrevBtn.Visibility = NextBtn.Visibility = Visibility.Hidden;
            Stage.Cursor = Cursors.Cross;
            DrawCrop();
        }

        private void EndCrop()
        {
            _crop = false;
            _cropMode = 0;
            CropLayer.Visibility = CropBar.Visibility = Visibility.Collapsed;
            Stage.Cursor = null;
            PrevBtn.Visibility = _index > 0 ? Visibility.Visible : Visibility.Hidden;
            NextBtn.Visibility = _index < _items.Count - 1 ? Visibility.Visible : Visibility.Hidden;
        }

        private void CropCancel_Click(object s, RoutedEventArgs e) => EndCrop();

        /// <summary>Where the picture is on screen (stage coordinates).</summary>
        private Rect Shown()
        {
            var sz = ImageSize();
            return new Rect(_offset.X, _offset.Y, sz.Width * _zoom, sz.Height * _zoom);
        }

        private Rect ToScreen(Rect n)
        {
            var b = Shown();
            return new Rect(b.X + n.X * b.Width, b.Y + n.Y * b.Height, n.Width * b.Width, n.Height * b.Height);
        }

        private Point ToNorm(Point p)
        {
            var b = Shown();
            return new Point(Math.Clamp((p.X - b.X) / Math.Max(1, b.Width), 0, 1), Math.Clamp((p.Y - b.Y) / Math.Max(1, b.Height), 0, 1));
        }

        private (int w, int h) FullPixels()
        {
            int w = 0, h = 0;
            if (_cache.TryGetValue(_items[_index].FullPath, out var r)) { w = r.info.Width; h = r.info.Height; }
            if (w <= 0 && Img.Source is BitmapSource b) { w = b.PixelWidth; h = b.PixelHeight; }
            return _rotation % 180 == 0 ? (w, h) : (h, w);
        }

        private void DrawCrop()
        {
            var stage = new Rect(0, 0, Stage.ActualWidth, Stage.ActualHeight);
            var rc = ToScreen(_cropN);
            CropShade.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(stage), new RectangleGeometry(rc));
            Place(CropBox, rc);
            var g = new GeometryGroup();
            for (int i = 1; i < 3; i++)
            {
                double x = rc.X + rc.Width * i / 3, y = rc.Y + rc.Height * i / 3;
                g.Children.Add(new LineGeometry(new Point(x, rc.Top), new Point(x, rc.Bottom)));
                g.Children.Add(new LineGeometry(new Point(rc.Left, y), new Point(rc.Right, y)));
            }
            // corner handles
            foreach (var c in new[] { rc.TopLeft, rc.TopRight, rc.BottomLeft, rc.BottomRight })
                g.Children.Add(new RectangleGeometry(new Rect(c.X - 4, c.Y - 4, 8, 8)));
            CropGrid.Data = g;
            CropGrid.Fill = Brushes.White;
            var (w, h) = FullPixels();
            CropSize.Text = $"{Math.Max(1, (int)Math.Round(_cropN.Width * w))} × {Math.Max(1, (int)Math.Round(_cropN.Height * h))}";
        }

        private static void Place(FrameworkElement el, Rect r)
        {
            System.Windows.Controls.Canvas.SetLeft(el, r.X);
            System.Windows.Controls.Canvas.SetTop(el, r.Y);
            el.Width = Math.Max(0, r.Width); el.Height = Math.Max(0, r.Height);
        }

        private int EdgesAt(Point p)
        {
            var rc = ToScreen(_cropN);
            const double t = 9;
            bool inY = p.Y > rc.Top - t && p.Y < rc.Bottom + t, inX = p.X > rc.Left - t && p.X < rc.Right + t;
            int e = 0;
            if (inY && Math.Abs(p.X - rc.Left) < t) e |= 1;
            else if (inY && Math.Abs(p.X - rc.Right) < t) e |= 2;
            if (inX && Math.Abs(p.Y - rc.Top) < t) e |= 4;
            else if (inX && Math.Abs(p.Y - rc.Bottom) < t) e |= 8;
            return e;
        }

        private void CropDown(MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) { CropApply_Click(null, null); return; }
            var p = e.GetPosition(Stage);
            _cropFrom = p;
            _cropStart = _cropN;
            _cropEdges = EdgesAt(p);
            bool whole = _cropN.Width >= 0.999 && _cropN.Height >= 0.999;
            if (_cropEdges != 0) _cropMode = 3;
            else if (!whole && ToScreen(_cropN).Contains(p)) _cropMode = 2;
            else _cropMode = 1;
            Stage.CaptureMouse();
        }

        private void CropMove(MouseEventArgs e)
        {
            var p = e.GetPosition(Stage);
            if (_cropMode == 0)
            {
                int ed = EdgesAt(p);
                Stage.Cursor = ed is 5 or 10 ? Cursors.SizeNWSE : ed is 6 or 9 ? Cursors.SizeNESW : (ed & 3) != 0 ? Cursors.SizeWE : (ed & 12) != 0 ? Cursors.SizeNS
                    : ToScreen(_cropN).Contains(p) && !(_cropN.Width >= 0.999 && _cropN.Height >= 0.999) ? Cursors.SizeAll : Cursors.Cross;
                return;
            }
            var a = ToNorm(_cropFrom);
            var b = ToNorm(p);
            const double min = 0.01;
            switch (_cropMode)
            {
                case 1:
                    if ((p - _cropFrom).Length < 3) return;
                    _cropN = new Rect(a, b);
                    break;
                case 2:
                    double dx = Math.Clamp(b.X - a.X, -_cropStart.X, 1 - _cropStart.Right);
                    double dy = Math.Clamp(b.Y - a.Y, -_cropStart.Y, 1 - _cropStart.Bottom);
                    _cropN = new Rect(_cropStart.X + dx, _cropStart.Y + dy, _cropStart.Width, _cropStart.Height);
                    break;
                case 3:
                    double l = _cropStart.Left, r = _cropStart.Right, t = _cropStart.Top, bo = _cropStart.Bottom;
                    if ((_cropEdges & 1) != 0) l = Math.Min(b.X, r - min);
                    if ((_cropEdges & 2) != 0) r = Math.Max(b.X, l + min);
                    if ((_cropEdges & 4) != 0) t = Math.Min(b.Y, bo - min);
                    if ((_cropEdges & 8) != 0) bo = Math.Max(b.Y, t + min);
                    _cropN = new Rect(new Point(l, t), new Point(r, bo));
                    break;
            }
            DrawCrop();
        }

        private void CropApply_Click(object s, RoutedEventArgs e) => DoCrop(false);
        private void CropCopy_Click(object s, RoutedEventArgs e) => DoCrop(true);

        private async void DoCrop(bool copy)
        {
            if (_cropBusy || _items.Count == 0) return;
            if (_cropN.Width >= 0.999 && _cropN.Height >= 0.999 && _rotation == 0) { EndCrop(); return; }
            var item = _items[_index];
            var n = _cropN;
            int rot = _rotation;
            _cropBusy = true;
            Status.Text = Loc.T("Saving");
            string error = null, saved = null;
            await ImageLoader.RunSta(() =>
            {
                try { saved = CropToFile(item.FullPath, n, rot, copy); }
                catch (Exception ex) { error = ex.Message; }
                return 0;
            });
            _cropBusy = false;
            Status.Text = "";
            if (error != null) { MessageBox.Show(this, Loc.T("CropFailed") + "\n\n" + error, "WinXplorer", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            EndCrop();
            _cache.Remove(item.FullPath);
            Notify.Show(copy || !string.Equals(saved, item.FullPath, StringComparison.OrdinalIgnoreCase)
                ? Loc.F("CropSaved", Path.GetFileName(saved)) : Loc.F("Cropped", item.Name));
            Show(_index);
        }

        /// <summary>Crops (and rotates) the full-resolution picture and writes it. Returns the file written.</summary>
        private static string CropToFile(string path, Rect n, int rot, bool copy)
        {
            var (img, info) = ImageLoader.Load(path, 0);
            if (img is not BitmapSource src || info.FromShell) throw new InvalidOperationException(Loc.T("NoCodec"));
            if (rot != 0) src = new TransformedBitmap(src, new RotateTransform(rot));
            int W = src.PixelWidth, H = src.PixelHeight;
            int x = Math.Clamp((int)Math.Round(n.X * W), 0, W - 1), y = Math.Clamp((int)Math.Round(n.Y * H), 0, H - 1);
            int w = Math.Clamp((int)Math.Round(n.Width * W), 1, W - x), h = Math.Clamp((int)Math.Round(n.Height * H), 1, H - y);
            BitmapSource result = new CroppedBitmap(src, new Int32Rect(x, y, w, h));

            var ext = Path.GetExtension(path).ToLowerInvariant();
            BitmapEncoder enc = ext switch
            {
                ".jpg" or ".jpeg" or ".jfif" => new JpegBitmapEncoder { QualityLevel = 95 },
                ".png" => new PngBitmapEncoder(),
                ".bmp" => new BmpBitmapEncoder(),
                ".tif" or ".tiff" => new TiffBitmapEncoder(),
                ".gif" => new GifBitmapEncoder(),
                _ => null,
            };
            var dir = Path.GetDirectoryName(path)!;
            var name = Path.GetFileNameWithoutExtension(path);
            if (enc == null) { enc = new PngBitmapEncoder(); ext = ".png"; copy = true; } // no encoder for this format: a PNG next to it
            if (enc is JpegBitmapEncoder && result.Format != PixelFormats.Bgr24) result = new FormatConvertedBitmap(result, PixelFormats.Bgr24, null, 0);
            enc.Frames.Add(BitmapFrame.Create(result));

            string target = copy
                ? FileOps.UniqueName(dir, name + " (" + Loc.T("CroppedSuffix") + ")", ext)
                : Path.Combine(dir, ".wxcrop-" + Guid.NewGuid().ToString("N")[..8] + ext);
            using (var fs = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                enc.Save(fs);
            if (copy) return target;

            // overwrite: the original goes to the Recycle Bin, the cropped file takes its name
            var created = File.GetCreationTime(path);
            var op = new Native.SHFILEOPSTRUCT
            {
                wFunc = Native.FO_DELETE,
                pFrom = path + "\0\0",
                fFlags = (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMATION | 0x4 /*FOF_SILENT*/ | 0x400 /*FOF_NOERRORUI*/),
            };
            if (Native.SHFileOperationW(ref op) != 0 || File.Exists(path))
            {
                try { File.Delete(target); } catch { }
                throw new IOException(Loc.T("CropFailed"));
            }
            File.Move(target, path);
            try { File.SetCreationTime(path, created); } catch { }
            return path;
        }
    }
}
