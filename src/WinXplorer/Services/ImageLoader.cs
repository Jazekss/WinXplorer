using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;
using WinXplorer.Interop;

namespace WinXplorer.Services
{
    public sealed class ImageInfo
    {
        public int Width, Height;
        public double DpiX;
        public string Format;
        public int Bits;
        public string Camera;
        public string DateTaken;
        public string Exposure;
        public int Frames = 1;
        public bool FromShell;
    }

    public static class ImageLoader
    {
        /// <summary>Runs work on a fresh STA thread (WPF imaging + SVG rendering are happiest there).</summary>
        public static Task<T> RunSta<T>(Func<T> f)
        {
            var tcs = new TaskCompletionSource<T>();
            var t = new Thread(() =>
            {
                try { tcs.SetResult(f()); } catch (Exception ex) { tcs.SetException(ex); }
            }) { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            return tcs.Task;
        }

        public static ImageSource LoadSvg(string path)
        {
            try
            {
                var settings = new WpfDrawingSettings { IncludeRuntime = true, TextAsGeometry = false, OptimizePath = true };
                using var reader = new FileSvgReader(settings);
                DrawingGroup d;
                if (path.EndsWith(".svgz", StringComparison.OrdinalIgnoreCase))
                {
                    using var fs = File.OpenRead(path);
                    using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress);
                    d = reader.Read(gz);
                }
                else d = reader.Read(path);
                if (d == null) return null;
                var img = new DrawingImage(d);
                img.Freeze();
                return img;
            }
            catch { return null; }
        }

        /// <summary>Loads an image for display. maxPixels limits the decoded size (0 = full resolution).</summary>
        public static (ImageSource image, ImageInfo info) Load(string path, int maxPixels)
        {
            var info = new ImageInfo();
            if (FileTypes.Kind(path) == PreviewKind.Svg)
            {
                var svg = LoadSvg(path);
                info.Format = "SVG";
                if (svg != null) { info.Width = (int)svg.Width; info.Height = (int)svg.Height; }
                return (svg, info);
            }
            var ext = Path.GetExtension(path).ToLowerInvariant();
            // HEIC / AVIF: our own decoder (works without Microsoft's Store codecs)
            if (HeifDecoder.Handles(ext))
            {
                var heif = HeifDecoder.Load(path, maxPixels, out var hw, out var hh, out var hf);
                if (heif != null)
                {
                    info.Width = hw; info.Height = hh; info.Format = hf; info.Bits = 24;
                    return (heif, info);
                }
            }
            // Illustrator files carry a PDF: Windows' PDF engine draws them sharp at any zoom (the stored preview is tiny)
            if (ext is ".ai" or ".ait" && (maxPixels <= 0 || maxPixels > 512))
            {
                var pdf = PdfRender.FirstPage(path, maxPixels > 0 ? Math.Min(maxPixels, 4096) : 4096);
                if (pdf != null)
                {
                    info.Width = pdf.PixelWidth; info.Height = pdf.PixelHeight;
                    info.Format = "Adobe Illustrator";
                    return (pdf, info);
                }
            }
            if (EmbeddedImages.IsPsd(ext) || EmbeddedImages.HasXmpThumb(ext))
            {
                // Photoshop / Illustrator without Adobe's codecs: read the picture stored inside the file
                var emb = EmbeddedImages.Load(path, maxPixels);
                if (emb != null)
                {
                    info.Width = emb.PixelWidth; info.Height = emb.PixelHeight;
                    info.Format = EmbeddedImages.IsPsd(ext) ? "Photoshop" : "Adobe (" + Loc.T("ShellPreview") + ")";
                    return (emb, info);
                }
            }
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                var frame = decoder.Frames[0];
                info.Width = frame.PixelWidth;
                info.Height = frame.PixelHeight;
                info.DpiX = frame.DpiX;
                info.Bits = frame.Format.BitsPerPixel;
                info.Frames = decoder.Frames.Count;
                info.Format = decoder.CodecInfo?.FriendlyName?.Replace(" Decoder", "").Replace("Decoder", "").Trim();
                int rotation = 0;
                if (frame.Metadata is BitmapMetadata md)
                {
                    try { info.Camera = string.Join(" ", new[] { md.CameraManufacturer, md.CameraModel }).Trim(); } catch { }
                    try { info.DateTaken = md.DateTaken; } catch { }
                    try
                    {
                        var o = md.GetQuery("System.Photo.Orientation");
                        if (o is ushort u) rotation = u switch { 6 => 90, 3 => 180, 8 => 270, _ => 0 };
                    }
                    catch { }
                    try
                    {
                        var parts = new List<string>();
                        if (md.GetQuery("System.Photo.FNumber") is double fn) parts.Add($"f/{fn:0.#}");
                        if (md.GetQuery("System.Photo.ExposureTime") is double et) parts.Add(et < 1 ? $"1/{Math.Round(1 / et)}s" : $"{et:0.#}s");
                        if (md.GetQuery("System.Photo.ISOSpeed") is ushort iso) parts.Add($"ISO {iso}");
                        if (md.GetQuery("System.Photo.FocalLength") is double fl) parts.Add($"{fl:0}mm");
                        info.Exposure = string.Join("  ", parts);
                    }
                    catch { }
                }

                fs.Position = 0;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.StreamSource = fs;
                if (maxPixels > 0 && Math.Max(info.Width, info.Height) > maxPixels)
                {
                    if (info.Width >= info.Height) bmp.DecodePixelWidth = maxPixels; else bmp.DecodePixelHeight = maxPixels;
                }
                bmp.EndInit();
                bmp.Freeze();

                BitmapSource result = bmp;
                if (rotation != 0)
                {
                    result = new TransformedBitmap(bmp, new RotateTransform(rotation));
                    result.Freeze();
                    if (rotation != 180) (info.Width, info.Height) = (info.Height, info.Width);
                }
                return (result, info);
            }
            catch
            {
                // No WIC codec: ask the shell (uses any installed thumbnail handler).
                var thumb = Shell.GetThumbnail(path, maxPixels > 0 ? Math.Min(maxPixels, 2048) : 2048, Shell.SIIGBF_BIGGERSIZEOK | Shell.SIIGBF_THUMBNAILONLY);
                info.FromShell = true;
                if (thumb != null) { info.Width = thumb.PixelWidth; info.Height = thumb.PixelHeight; }
                return (thumb, info);
            }
        }
    }
}
