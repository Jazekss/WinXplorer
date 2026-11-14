using System;
using System.Collections.Generic;
using System.IO;

namespace WinXplorer.Services
{
    public enum PreviewKind { None, Image, Svg, Video, Audio, Text }

    public static class FileTypes
    {
        // Decoded via Windows Imaging Component; WebP/HEIC/AVIF/RAW work when the free Microsoft Store codecs are present
        // (HEIC = Samsung/iPhone photos, SRW = Samsung RAW). Anything else falls back to the shell thumbnail.
        public static readonly HashSet<string> Image = Set(
            ".jpg .jpeg .jpe .jfif .png .apng .gif .bmp .dib .tif .tiff .ico .cur .wdp .jxr .hdp .webp .heic .heif .hif .avif .jxl " +
            ".dng .cr2 .cr3 .crw .nef .nrw .arw .srf .sr2 .orf .rw2 .raf .pef .srw .3fr .kdc .mrw .erf .x3f .psd .psb .tga .dds .exr .hdr .ai .ait");

        /// <summary>Design files: the viewer shows them (Space), but double-click opens them in their own program.</summary>
        public static readonly HashSet<string> Design = Set(".psd .psb .ai .ait");

        /// <summary>Thumbnails of these look like plain pictures: a small type icon in the corner tells them apart.</summary>
        public static readonly HashSet<string> Badged = Set(".psd .psb .ai .ait .eps .indd .idml .pdf .svg .svgz");

        /// <summary>Archives WinXplorer opens like folders.</summary>
        public static readonly HashSet<string> Archive = Set(".zip .7z .rar .tar .gz .tgz .bz2 .tbz2 .xz .txz .zst .lz .cab .jar .apk .nupkg .vsix .whl .epub .cbz .cbr");

        /// <summary>Disc / disk images: mounted as a drive (Windows or DAEMON Tools, Alcohol…), not browsed like archives.</summary>
        public static readonly HashSet<string> DiskImage = Set(".iso .img .vhd .vhdx .mdf .mds .nrg .cue .ccd .isz .b5t .b6t .cdi .bwt .dmg");

        /// <summary>Programs: show their details, never their bytes.</summary>
        public static readonly HashSet<string> Executable = Set(".exe .dll .sys .ocx .cpl .scr .com .efi .drv .mui .ax .winmd");

        // binary formats that may look like text in their first bytes
        private static readonly HashSet<string> NotText = Set(".ai .ait .eps .ps .pdf .indd .idml .psd .psb .swf .fla .sketch .fig .xd .blend .fbx .obj .stl .ttf .otf .woff .woff2");

        public static readonly HashSet<string> Svg = Set(".svg .svgz");

        public static readonly HashSet<string> Video = Set(".mp4 .m4v .mov .wmv .avi .mkv .webm .mpg .mpeg .m2ts .mts .ts .3gp .3g2 .asf .flv");

        public static readonly HashSet<string> Audio = Set(".mp3 .wav .wma .m4a .aac .flac .ogg .oga .opus .aiff .aif .mid .midi .ac3 .amr");

        public static readonly HashSet<string> Text = Set(
            ".txt .log .md .markdown .rst .csv .tsv .json .jsonc .json5 .xml .xaml .axaml .html .htm .xhtml .css .scss .sass .less " +
            ".js .mjs .cjs .jsx .ts .tsx .vue .svelte .astro .cs .csx .vb .fs .fsx .fsi .c .h .cpp .cc .cxx .hpp .hh .hxx .ino .m .mm " +
            ".java .kt .kts .scala .groovy .gradle .go .rs .zig .nim .swift .dart .py .pyw .pyi .rb .php .pl .pm .lua .r .jl .ex .exs .erl .hs .clj .cljs .elm .ml " +
            ".sh .bash .zsh .fish .ps1 .psm1 .psd1 .bat .cmd .vbs .ahk .sql .graphql .gql .proto .ini .cfg .conf .config .toml .yaml .yml .env .properties .prefs " +
            ".csproj .vbproj .fsproj .vcxproj .props .targets .sln .slnx .nuspec .resx .manifest .editorconfig .gitignore .gitattributes .gitmodules .dockerignore .npmrc .nvmrc " +
            ".lock .tf .tfvars .hcl .cmake .mk .make .asm .s .glsl .hlsl .shader .wgsl .tex .bib .srt .vtt .ass .nfo .diff .patch .reg .inf .iss .svgx .lst .lua .gd .cfm");

        public static readonly HashSet<string> TextNames = new(StringComparer.OrdinalIgnoreCase)
            { "dockerfile", "makefile", "license", "licence", "readme", "changelog", "authors", "copying", "procfile", "gemfile", "rakefile", "vagrantfile", "jenkinsfile", "cmakelists.txt", ".bashrc", ".zshrc", ".profile" };

        private static HashSet<string> Set(string s) => new(s.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

        public static PreviewKind Kind(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (Svg.Contains(ext)) return PreviewKind.Svg;
            if (Image.Contains(ext)) return PreviewKind.Image;
            if (Video.Contains(ext)) return PreviewKind.Video;
            if (Audio.Contains(ext)) return PreviewKind.Audio;
            if (Text.Contains(ext) || TextNames.Contains(Path.GetFileName(path))) return PreviewKind.Text;
            return PreviewKind.None;
        }

        public static bool MayBeText(string path)
        {
            var ext = Path.GetExtension(path);
            return !NotText.Contains(ext) && !Archive.Contains(ext) && !Executable.Contains(ext) && !DiskImage.Contains(ext);
        }

        /// <summary>
        /// Formats Windows decodes only with free Microsoft Store extensions: the extension's name key and Store link(s).
        /// HEIC photos need two – HEIF Image Extensions and HEVC Video Extensions from Device Manufacturer.
        /// </summary>
        public static (string nameKey, string[] storeIds)? MissingCodec(string path) => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".heic" or ".heif" or ".hif" => ("CodecHeic", new[] { "9PMMSR1CGPWG", "9N4WGH0Z6VHQ" }),
            ".avif" => ("CodecAvif", new[] { "9MVZQVXJBQ9V" }),
            ".webp" => ("CodecWebp", new[] { "9PG2DK419DRG" }),
            ".dng" or ".cr2" or ".cr3" or ".nef" or ".arw" or ".orf" or ".rw2" or ".raf" or ".pef" or ".srw" => ("CodecRaw", new[] { "9NCTDW2W1BH8" }),
            _ => null,
        };

        /// <summary>
        /// A video whose picture Windows can't decode (sound plays, no image): sniffs the codec tag in the container
        /// and returns the free Store extension that adds it.
        /// </summary>
        public static (string name, string storeId)? MissingVideoCodec(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var tags = new (string tag, string name, string id)[]
                {
                    ("hvc1", "HEVC Video Extensions from Device Manufacturer", "9N4WGH0Z6VHQ"), ("hev1", "HEVC Video Extensions from Device Manufacturer", "9N4WGH0Z6VHQ"),
                    ("V_MPEGH/ISO/HEVC", "HEVC Video Extensions from Device Manufacturer", "9N4WGH0Z6VHQ"),
                    ("av01", "AV1 Video Extension", "9MVZQVXJBQ9V"), ("V_AV1", "AV1 Video Extension", "9MVZQVXJBQ9V"),
                    ("vp09", "VP9 Video Extensions", "9N4D0MSMP0PT"), ("V_VP9", "VP9 Video Extensions", "9N4D0MSMP0PT"),
                    ("mpg2", "MPEG-2 Video Extension", "9N95Q1ZZPMH4"),
                };
                const int chunk = 4 << 20;
                foreach (var start in new[] { 0L, Math.Max(0, fs.Length - chunk) })
                {
                    fs.Position = start;
                    var buf = new byte[(int)Math.Min(chunk, fs.Length - start)];
                    int n = fs.Read(buf, 0, buf.Length);
                    var text = System.Text.Encoding.ASCII.GetString(buf, 0, n);
                    foreach (var t in tags) if (text.Contains(t.tag, StringComparison.Ordinal)) return (t.name, t.id);
                }
            }
            catch { }
            return null;
        }

        private static bool? _wmp;

        /// <summary>
        /// Can Windows Media Player's engine be created? WPF plays all video and audio through it; when
        /// "Windows Media Player Legacy" is removed, nothing plays in WinXplorer (other players don't need it).
        /// </summary>
        public static bool MediaPlayerAvailable()
        {
            if (_wmp is bool b) return b;
            try
            {
                var t = Type.GetTypeFromCLSID(new Guid("6BF52A52-394A-11d3-B153-00C04F79FAA6"));
                var o = t == null ? null : Activator.CreateInstance(t);
                if (o != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(o);
                _wmp = o != null;
            }
            catch { _wmp = false; }
            return _wmp.Value;
        }

        public static void OpenStore(string productId)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-windows-store://pdp/?ProductId=" + productId) { UseShellExecute = true }); } catch { }
        }

        public static bool IsViewableImage(string path)
        {
            var k = Kind(path);
            return k == PreviewKind.Image || k == PreviewKind.Svg;
        }

        /// <summary>Unknown extension: treat as text if the first bytes contain no NUL characters.</summary>
        public static bool LooksLikeText(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length > 8 * 1024 * 1024) return false;
                var buf = new byte[Math.Min(8192, (int)fs.Length)];
                int n = fs.Read(buf, 0, buf.Length);
                if (n >= 2 && (buf[0] == 0xFF && buf[1] == 0xFE || buf[0] == 0xFE && buf[1] == 0xFF)) return true; // UTF-16 BOM
                for (int i = 0; i < n; i++) if (buf[i] == 0) return false;
                return true;
            }
            catch { return false; }
        }
    }
}
