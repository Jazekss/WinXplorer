using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinXplorer.Interop;
using WinXplorer.Models;

namespace WinXplorer.Services
{
    public static class IconService
    {
        private static readonly ConcurrentDictionary<string, ImageSource> _generic = new();
        private static readonly ConcurrentDictionary<string, string> _types = new();
        private static readonly HashSet<string> OwnIconExts = new(StringComparer.OrdinalIgnoreCase)
            { ".exe", ".ico", ".lnk", ".url", ".cur", ".ani", ".msc", ".cpl", ".scr", ".appref-ms", ".website" };

        private static readonly BlockingCollection<FileItem> _queue = new();

        static IconService()
        {
            var t = new Thread(Worker) { IsBackground = true, Name = "IconLoader", Priority = ThreadPriority.BelowNormal };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        public static ImageSource GetIcon(FileItem f, out bool needsOwn)
        {
            needsOwn = false;
            if (f.IsDrive) { needsOwn = true; return Generic("<drive>", Native.FILE_ATTRIBUTE_DIRECTORY); }
            if (f.IsDirectory)
            {
                // read-only / system folders may have their own icon (desktop.ini) – load the real one in the background
                if ((f.Attributes & (0x1 | 0x4)) != 0 && (f.Attributes & 0x400) == 0 && !f.FullPath.StartsWith(@"\\")) needsOwn = true;
                return Generic("<dir>", Native.FILE_ATTRIBUTE_DIRECTORY);
            }
            var ext = f.Extension;
            if (f.InArchive) return UsesOwnIcon(ext) ? FileIcons.Small(ext) : Generic(ext, Native.FILE_ATTRIBUTE_NORMAL);
            if (OwnIconExts.Contains(ext)) needsOwn = true;
            else if (UsesOwnIcon(ext)) return FileIcons.Small(ext);
            return Generic(ext, Native.FILE_ATTRIBUTE_NORMAL);
        }

        // ---------- WinXplorer icons for types Windows has no real icon for ----------
        private const uint SHGFI_SYSICONINDEX = 0x4000;
        private static readonly ConcurrentDictionary<string, bool> _unknown = new(StringComparer.OrdinalIgnoreCase);
        private static int _blankIndex = int.MinValue;

        private static int SysIndex(string name)
        {
            var info = new Native.SHFILEINFO();
            Native.SHGetFileInfoW(name, Native.FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf(info), SHGFI_SYSICONINDEX | Native.SHGFI_USEFILEATTRIBUTES);
            return info.iIcon;
        }

        /// <summary>True when Windows shows only the blank "unknown file" page for this extension.</summary>
        public static bool IsUnknownType(string ext)
        {
            if (string.IsNullOrEmpty(ext)) return true;
            return _unknown.GetOrAdd(ext, e =>
            {
                if (_blankIndex == int.MinValue) _blankIndex = SysIndex("file.zz_winxplorer_unknown");
                return SysIndex("file" + e) == _blankIndex;
            });
        }

        /// <summary>Files shown with WinXplorer's own icon (code types, or types with no icon). Media keeps Windows thumbnails.</summary>
        public static bool UsesOwnIcon(string ext)
        {
            if (string.IsNullOrEmpty(ext) || OwnIconExts.Contains(ext)) return false;
            if (FileTypes.Image.Contains(ext) || FileTypes.Video.Contains(ext) || FileTypes.Svg.Contains(ext)) return false;
            if (AppState.Settings.OwnCodeIcons && FileIcons.IsCodeType(ext)) return true;
            // archives WinXplorer opens like folders get its archive folder icon
            if (AppState.Settings.OpenArchives && FileTypes.Archive.Contains(ext)) return true;
            return IsUnknownType(ext);
        }

        // ---------- big icons (icon view placeholder until the thumbnail arrives) ----------
        [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IImageList
        {
            [PreserveSig] int Add(IntPtr a, IntPtr b, out int c);
            [PreserveSig] int ReplaceIcon(int a, IntPtr b, out int c);
            [PreserveSig] int SetOverlayImage(int a, int b);
            [PreserveSig] int Replace(int a, IntPtr b, IntPtr c);
            [PreserveSig] int AddMasked(IntPtr a, int b, out int c);
            [PreserveSig] int Draw(IntPtr a);
            [PreserveSig] int Remove(int a);
            [PreserveSig] int GetIcon(int i, int flags, out IntPtr icon);
        }

        [DllImport("shell32.dll", EntryPoint = "#727")]
        private static extern int SHGetImageList(int list, ref Guid riid, out IImageList ppv);

        private static readonly ConcurrentDictionary<string, ImageSource> _big = new(StringComparer.OrdinalIgnoreCase);
        private static IImageList _jumbo, _xl;

        /// <summary>Large icon (48 or 256 px) by type, cached; shown right away in icon view instead of a stretched 16 px icon.</summary>
        public static ImageSource GetLargeIcon(FileItem f, int size)
        {
            string ext = f.IsDrive ? "<drive>" : f.IsDirectory ? "<dir>" : f.Extension;
            if (!f.IsDirectory && UsesOwnIcon(ext)) return FileIcons.Large(ext);
            bool jumbo = size > 56;
            return _big.GetOrAdd((jumbo ? "j" : "x") + ext, _ =>
            {
                try
                {
                    var info = new Native.SHFILEINFO();
                    bool dir = ext.StartsWith("<");
                    Native.SHGetFileInfoW(dir ? "folder" : "file" + ext, dir ? Native.FILE_ATTRIBUTE_DIRECTORY : Native.FILE_ATTRIBUTE_NORMAL,
                        ref info, (uint)Marshal.SizeOf(info), SHGFI_SYSICONINDEX | Native.SHGFI_USEFILEATTRIBUTES);
                    var iid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
                    if (jumbo && _jumbo == null) SHGetImageList(4 /*SHIL_JUMBO*/, ref iid, out _jumbo);
                    if (!jumbo && _xl == null) SHGetImageList(2 /*SHIL_EXTRALARGE*/, ref iid, out _xl);
                    var list = jumbo ? _jumbo : _xl;
                    if (list == null || list.GetIcon(info.iIcon, 1 /*ILD_TRANSPARENT*/, out var h) != 0 || h == IntPtr.Zero) return null;
                    try
                    {
                        var src = Imaging.CreateBitmapSourceFromHIcon(h, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        src.Freeze();
                        return src;
                    }
                    finally { Native.DestroyIcon(h); }
                }
                catch { return null; }
            });
        }

        /// <summary>16 px icon for a file type (menus, settings).</summary>
        public static ImageSource TypeIcon(string ext) => UsesOwnIcon(ext) ? FileIcons.Small(ext) : Generic(ext, Native.FILE_ATTRIBUTE_NORMAL);

        public static ImageSource FolderIcon => Generic("<dir>", Native.FILE_ATTRIBUTE_DIRECTORY);

        public static void QueueOwnIcon(FileItem f) => _queue.Add(f);

        private static ImageSource Generic(string ext, uint attr)
        {
            return _generic.GetOrAdd(ext, e =>
            {
                var name = e.StartsWith("<") ? "folder" : "file" + e;
                return FromShell(name, attr, Native.SHGFI_ICON | Native.SHGFI_SMALLICON | Native.SHGFI_USEFILEATTRIBUTES);
            });
        }

        /// <summary>Real icon of an existing path (used for sidebar, drives, exe...).</summary>
        public static ImageSource GetPathIcon(string path, bool large = false)
        {
            return FromShell(path, 0, Native.SHGFI_ICON | (large ? Native.SHGFI_LARGEICON : Native.SHGFI_SMALLICON));
        }

        private static IImageList _smallList, _largeList;

        /// <summary>Icon of a path without overlays (no shortcut arrow) – pinned apps in the sidebar.</summary>
        public static ImageSource GetPathIconNoOverlay(string path, bool large = false)
        {
            try
            {
                var info = new Native.SHFILEINFO();
                if (Native.SHGetFileInfoW(path, 0, ref info, (uint)Marshal.SizeOf(info), SHGFI_SYSICONINDEX | (large ? Native.SHGFI_LARGEICON : Native.SHGFI_SMALLICON)) == IntPtr.Zero)
                    return GetPathIcon(path, large);
                var iid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
                if (large && _largeList == null) SHGetImageList(0 /*SHIL_LARGE*/, ref iid, out _largeList);
                if (!large && _smallList == null) SHGetImageList(1 /*SHIL_SMALL*/, ref iid, out _smallList);
                var list = large ? _largeList : _smallList;
                // ILD_TRANSPARENT without ILD_OVERLAYMASK: the plain icon, no link arrow
                if (list == null || list.GetIcon(info.iIcon, 1, out var h) != 0 || h == IntPtr.Zero) return GetPathIcon(path, large);
                try
                {
                    var src = Imaging.CreateBitmapSourceFromHIcon(h, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
                finally { Native.DestroyIcon(h); }
            }
            catch { return GetPathIcon(path, large); }
        }

        private static ImageSource FromShell(string path, uint attr, uint flags)
        {
            var info = new Native.SHFILEINFO();
            var r = Native.SHGetFileInfoW(path, attr, ref info, (uint)Marshal.SizeOf(info), flags);
            if (r == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch { return null; }
            finally { Native.DestroyIcon(info.hIcon); }
        }

        private static void Worker()
        {
            foreach (var f in _queue.GetConsumingEnumerable())
            {
                try
                {
                    var img = FromShell(f.FullPath, 0, Native.SHGFI_ICON | Native.SHGFI_SMALLICON);
                    if (img != null) Application.Current?.Dispatcher.BeginInvoke(() => f.Icon = img);
                }
                catch { }
            }
        }

        public static string GetTypeName(FileItem f)
        {
            if (f.IsDrive)
            {
                try
                {
                    return new DriveInfo(f.FullPath).DriveType switch
                    {
                        DriveType.Fixed => Loc.T("LocalDisk"),
                        DriveType.Removable => Loc.T("RemovableDisk"),
                        DriveType.Network => Loc.T("NetworkDrive"),
                        DriveType.CDRom => Loc.T("OpticalDrive"),
                        _ => Loc.T("Drive"),
                    };
                }
                catch { return Loc.T("Drive"); }
            }
            var key = f.IsDirectory ? "<dir>" : f.Extension;
            return _types.GetOrAdd(key, k =>
            {
                var info = new Native.SHFILEINFO();
                Native.SHGetFileInfoW(k == "<dir>" ? "folder" : "file" + k,
                    k == "<dir>" ? Native.FILE_ATTRIBUTE_DIRECTORY : Native.FILE_ATTRIBUTE_NORMAL,
                    ref info, (uint)Marshal.SizeOf(info), Native.SHGFI_TYPENAME | Native.SHGFI_USEFILEATTRIBUTES);
                return string.IsNullOrEmpty(info.szTypeName) ? (k.Length > 1 ? k[1..].ToUpperInvariant() + " " + Loc.T("File") : Loc.T("File")) : info.szTypeName;
            });
        }

        public static string GetDisplayName(string path)
        {
            var info = new Native.SHFILEINFO();
            Native.SHGetFileInfoW(path, 0, ref info, (uint)Marshal.SizeOf(info), Native.SHGFI_DISPLAYNAME);
            return string.IsNullOrEmpty(info.szDisplayName) ? path : info.szDisplayName;
        }
    }
}
