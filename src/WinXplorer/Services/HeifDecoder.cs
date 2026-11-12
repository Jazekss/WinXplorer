using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibHeifSharp;

namespace WinXplorer.Services
{
    /// <summary>
    /// HEIC / HEIF (iPhone, Samsung photos) and AVIF without Microsoft's Store codecs: decoded with libheif
    /// (libde265 for HEVC, dav1d/libaom for AV1), shipped next to the app as libheif.dll (LGPL – replaceable).
    /// The photo's rotation stored in the file is applied by libheif.
    /// </summary>
    public static class HeifDecoder
    {
        private static bool _missing;

        public static bool Handles(string ext) => ext is ".heic" or ".heif" or ".hif" or ".avif" or ".avifs";

        /// <summary>The picture (at most <paramref name="maxPixels"/> on the long side, 0 = full size), or null.</summary>
        public static BitmapSource Load(string path, int maxPixels, out int width, out int height, out string format)
        {
            width = height = 0;
            format = Path.GetExtension(path).Equals(".avif", StringComparison.OrdinalIgnoreCase) ? "AVIF" : "HEIF";
            if (_missing) return null;
            try
            {
                using var ctx = new HeifContext(path);
                using var primary = ctx.GetPrimaryImageHandle();
                width = primary.Width; height = primary.Height;

                // thumbnails: the small preview stored in the photo is enough and much faster
                HeifImageHandle src = primary;
                if (maxPixels is > 0 and <= 400)
                {
                    var ids = primary.GetThumbnailImageIds();
                    if (ids.Count > 0)
                    {
                        var th = primary.GetThumbnailImage(ids[0]);
                        if (Math.Max(th.Width, th.Height) >= maxPixels) src = th; else th.Dispose();
                    }
                }
                try
                {
                    bool alpha = src.HasAlphaChannel;
                    using var decoded = src.Decode(HeifColorspace.Rgb, alpha ? HeifChroma.InterleavedRgba32 : HeifChroma.InterleavedRgb24,
                        new HeifDecodingOptions { ConvertHdrToEightBit = true });
                    var img = decoded;
                    HeifImage scaled = null;
                    int longSide = Math.Max(img.Width, img.Height);
                    if (maxPixels > 0 && longSide > maxPixels)
                    {
                        double f = (double)maxPixels / longSide;
                        scaled = img.ScaleImage(Math.Max(1, (int)(img.Width * f)), Math.Max(1, (int)(img.Height * f)));
                        img = scaled;
                    }
                    try { return ToBitmap(img, alpha); }
                    finally { scaled?.Dispose(); }
                }
                finally { if (!ReferenceEquals(src, primary)) src.Dispose(); }
            }
            catch (DllNotFoundException) { _missing = true; return null; }
            catch (BadImageFormatException) { _missing = true; return null; }
            catch { return null; }
        }

        private static BitmapSource ToBitmap(HeifImage img, bool alpha)
        {
            var plane = img.GetPlane(HeifChannel.Interleaved);
            int w = plane.Width, h = plane.Height, stride = plane.Stride;
            BitmapSource bmp;
            if (!alpha)
            {
                bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Rgb24, null, plane.Scan0, stride * h, stride);
            }
            else
            {
                // RGBA → BGRA (WPF has no RGBA format)
                var px = new byte[stride * h];
                Marshal.Copy(plane.Scan0, px, 0, px.Length);
                for (int y = 0; y < h; y++)
                    for (int x = 0, i = y * stride; x < w; x++, i += 4)
                        (px[i], px[i + 2]) = (px[i + 2], px[i]);
                bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
            }
            bmp.Freeze();
            return bmp;
        }
    }
}
