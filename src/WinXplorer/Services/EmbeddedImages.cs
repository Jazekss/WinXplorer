using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinXplorer.Services
{
    /// <summary>
    /// Pictures from design files without Adobe installed:
    /// Photoshop (.psd/.psb) – the flattened image every PSD stores next to its layers;
    /// Illustrator / InDesign / EPS – the preview JPEG embedded in the file's XMP metadata.
    /// </summary>
    public static class EmbeddedImages
    {
        public static bool IsPsd(string ext) => ext is ".psd" or ".psb";
        public static bool HasXmpThumb(string ext) => ext is ".ai" or ".ait" or ".eps" or ".indd" or ".indt" or ".idml";

        /// <summary>The picture for a design file, at most <paramref name="maxPixels"/> on its long side (0 = full size).</summary>
        public static BitmapSource Load(string path, int maxPixels)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            try
            {
                if (IsPsd(ext)) return Psd(path, maxPixels);
                if (HasXmpThumb(ext)) return Xmp(path);
            }
            catch { }
            return null;
        }

        // ---------------- Photoshop ----------------
        private static BitmapSource Psd(string path, int maxPixels)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            var r = new BinaryReader(fs);
            if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "8BPS") return null;
            int version = U16(r);                  // 1 = PSD, 2 = PSB (large document)
            r.ReadBytes(6);
            int channels = U16(r);
            int height = (int)U32(r), width = (int)U32(r);
            int depth = U16(r), mode = U16(r);
            if (width <= 0 || height <= 0 || depth is not (8 or 16) || mode is not (1 or 3 or 4)) return null;

            fs.Seek(U32(r), SeekOrigin.Current);   // color mode data
            long resLen = U32(r);                  // image resources (hold a small JPEG thumbnail, id 1036)
            long resEnd = fs.Position + resLen;
            BitmapSource thumb = null;
            if (maxPixels is > 0 and <= 160) thumb = PsdThumb(r, fs, resEnd);
            if (thumb != null) return thumb;
            fs.Position = resEnd;
            long layers = version == 2 ? (long)U64(r) : U32(r);
            fs.Seek(layers, SeekOrigin.Current);

            // flattened image: channel planes, raw or PackBits
            int comp = U16(r);
            if (comp is not (0 or 1)) return null;
            int used = mode == 1 ? 1 : mode == 3 ? 3 : 4;
            if (channels < used) return null;
            int step = maxPixels > 0 ? Math.Max(1, (int)Math.Ceiling(Math.Max(width, height) / (double)maxPixels)) : 1;
            int ow = (width + step - 1) / step, oh = (height + step - 1) / step;
            int bpc = depth / 8, rowBytes = width * bpc;
            var planes = new byte[used][];
            for (int c = 0; c < used; c++) planes[c] = new byte[ow * oh];

            int[] counts = null;
            if (comp == 1)
            {
                counts = new int[channels * height];
                for (int i = 0; i < counts.Length; i++) counts[i] = version == 2 ? (int)U32(r) : U16(r);
            }
            var row = new byte[rowBytes];
            var packed = comp == 1 ? new byte[rowBytes * 2 + 256] : null;
            for (int c = 0; c < used; c++)
            {
                for (int y = 0; y < height; y++)
                {
                    if (comp == 0) ReadFull(fs, row, rowBytes);
                    else
                    {
                        int n = counts[c * height + y];
                        if (n > packed.Length) packed = new byte[n];
                        ReadFull(fs, packed, n);
                        UnpackBits(packed, n, row, rowBytes);
                    }
                    if (y % step != 0) continue;
                    var dst = planes[c];
                    int o = (y / step) * ow;
                    for (int x = 0, ox = 0; x < width; x += step, ox++) dst[o + ox] = row[x * bpc]; // 16 bit: high byte
                }
            }

            var px = new byte[ow * oh * 3];
            for (int i = 0, j = 0; i < ow * oh; i++, j += 3)
            {
                byte R, G, B;
                if (mode == 1) R = G = B = planes[0][i];
                else if (mode == 3) { R = planes[0][i]; G = planes[1][i]; B = planes[2][i]; }
                else
                {
                    // PSD stores CMYK inverted (255 = no ink)
                    double C = 1 - planes[0][i] / 255.0, M = 1 - planes[1][i] / 255.0, Y = 1 - planes[2][i] / 255.0, K = 1 - planes[3][i] / 255.0;
                    R = (byte)(255 * (1 - C) * (1 - K)); G = (byte)(255 * (1 - M) * (1 - K)); B = (byte)(255 * (1 - Y) * (1 - K));
                }
                px[j] = B; px[j + 1] = G; px[j + 2] = R;
            }
            var bmp = BitmapSource.Create(ow, oh, 96, 96, PixelFormats.Bgr24, null, px, ow * 3);
            bmp.Freeze();
            return bmp;
        }

        private static BitmapSource PsdThumb(BinaryReader r, Stream fs, long end)
        {
            while (fs.Position + 12 < end)
            {
                if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "8BIM") return null;
                int id = U16(r);
                int nameLen = r.ReadByte();
                fs.Seek((nameLen + 1) % 2 == 0 ? nameLen : nameLen + 1, SeekOrigin.Current); // pascal string, padded to even
                long len = U32(r);
                long next = fs.Position + len + (len & 1);
                if (id is 1036 or 1033 && len > 28)
                {
                    r.ReadBytes(28); // format, size, stride... then JFIF
                    var jpg = r.ReadBytes((int)len - 28);
                    var dec = BitmapDecoder.Create(new MemoryStream(jpg), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    BitmapSource f = dec.Frames[0];
                    if (id == 1033) f = SwapRB(f); // Photoshop 4 stored it as BGR
                    f.Freeze();
                    return f;
                }
                fs.Position = next;
            }
            return null;
        }

        private static BitmapSource SwapRB(BitmapSource s)
        {
            var c = new FormatConvertedBitmap(s, PixelFormats.Bgr24, null, 0);
            int st = c.PixelWidth * 3;
            var px = new byte[st * c.PixelHeight];
            c.CopyPixels(px, st, 0);
            for (int i = 0; i < px.Length; i += 3) (px[i], px[i + 2]) = (px[i + 2], px[i]);
            return BitmapSource.Create(c.PixelWidth, c.PixelHeight, 96, 96, PixelFormats.Bgr24, null, px, st);
        }

        private static void UnpackBits(byte[] src, int n, byte[] dst, int dstLen)
        {
            int i = 0, o = 0;
            while (i < n && o < dstLen)
            {
                sbyte h = (sbyte)src[i++];
                if (h >= 0)
                {
                    int cnt = Math.Min(h + 1, Math.Min(n - i, dstLen - o));
                    Buffer.BlockCopy(src, i, dst, o, cnt);
                    i += h + 1; o += cnt;
                }
                else if (h != -128)
                {
                    int cnt = Math.Min(1 - h, dstLen - o);
                    if (i >= n) break;
                    byte b = src[i++];
                    for (int k = 0; k < cnt; k++) dst[o++] = b;
                }
            }
        }

        private static void ReadFull(Stream s, byte[] buf, int n)
        {
            int got = 0;
            while (got < n)
            {
                int k = s.Read(buf, got, n - got);
                if (k <= 0) throw new EndOfStreamException();
                got += k;
            }
        }

        private static int U16(BinaryReader r) => BinaryPrimitives.ReadUInt16BigEndian(r.ReadBytes(2));
        private static uint U32(BinaryReader r) => BinaryPrimitives.ReadUInt32BigEndian(r.ReadBytes(4));
        private static ulong U64(BinaryReader r) => BinaryPrimitives.ReadUInt64BigEndian(r.ReadBytes(8));

        // ---------------- XMP thumbnail (Illustrator, InDesign, EPS) ----------------
        private static BitmapSource Xmp(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // the XMP packet sits near the start of the file (Illustrator) or the end (some exports)
            foreach (var (pos, len) in new[] { (0L, 8L << 20), (Math.Max(0, fs.Length - (4L << 20)), 4L << 20) })
            {
                fs.Position = pos;
                var buf = new byte[(int)Math.Min(len, fs.Length - pos)];
                ReadFull(fs, buf, buf.Length);
                var text = Encoding.Latin1.GetString(buf);
                int a = text.IndexOf("<xmpGImg:image>", StringComparison.Ordinal);
                if (a < 0) continue;
                a += "<xmpGImg:image>".Length;
                int b = text.IndexOf("</xmpGImg:image>", a, StringComparison.Ordinal);
                if (b < 0) continue;
                var b64 = new StringBuilder(b - a);
                for (int i = a; i < b; i++)
                {
                    char c = text[i];
                    if (c == '&') { int semi = text.IndexOf(';', i); if (semi > 0 && semi - i < 8) { i = semi; continue; } }
                    if (char.IsLetterOrDigit(c) || c is '+' or '/' or '=') b64.Append(c);
                }
                var jpg = Convert.FromBase64String(b64.ToString());
                var dec = BitmapDecoder.Create(new MemoryStream(jpg), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var f = dec.Frames[0];
                f.Freeze();
                return f;
            }
            return null;
        }
    }
}
