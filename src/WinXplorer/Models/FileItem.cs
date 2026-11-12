using System;
using System.IO;
using System.Windows.Media;
using WinXplorer.Services;
using WinXplorer.ViewModels;

namespace WinXplorer.Models
{
    public sealed class FileItem : ObservableObject
    {
        public string Name { get; init; }
        public string FullPath { get; init; }
        public bool IsDirectory { get; init; }
        public bool IsDrive { get; init; }
        public long Size { get; init; }
        public DateTime Modified { get; init; }
        public DateTime Created { get; init; }
        public uint Attributes { get; init; }
        /// <summary>For drives: total size (Size = free space).</summary>
        public long TotalSize { get; init; }
        /// <summary>Inside a zip / 7z / rar...: the path is virtual, the file is extracted when opened.</summary>
        public bool InArchive { get; init; }
        /// <summary>Folder that contains the item (shown for search results).</summary>
        public string Location { get; init; }

        public string Extension => IsDirectory ? "" : Path.GetExtension(Name).ToLowerInvariant();
        public bool IsHidden => (Attributes & 0x2) != 0;
        /// <summary>Shortcut (.lnk/.url) or symbolic link / junction: an arrow over the icon, like Explorer.</summary>
        public bool IsLink => !IsDrive && (IsSymlink || Extension is ".lnk" or ".url");
        public bool IsSymlink => !IsDrive && (Attributes & 0x400) != 0 && (Attributes & 0x5D1000) == 0; // not OneDrive / cloud placeholders
        public double ItemOpacity => FileOps.IsCut(FullPath) ? 0.4 : IsHidden ? 0.55 : 1.0;
        public void RefreshOpacity() => OnPropertyChanged(nameof(ItemOpacity));

        public string DisplayName => IsDirectory || AppState.Settings.ShowExtensions || string.IsNullOrEmpty(Extension)
            ? Name : Path.GetFileNameWithoutExtension(Name);

        private string _typeName;
        public string TypeName => _typeName ??= IconService.GetTypeName(this);

        public string SizeText
        {
            get
            {
                if (IsDrive) return TotalSize > 0 ? $"{Loc.Free(FormatSize(Size))} / {FormatSize(TotalSize)}" : "";
                if (IsDirectory) return _dirSize >= 0 ? (_dirPartial ? "> " : "") + FormatSize(_dirSize) : "";
                return FormatSize(Size);
            }
        }

        private long _dirSize = -1;
        private bool _dirPartial;

        /// <summary>Folder size counted in the background (Settings › Show folder sizes).</summary>
        internal void SetDirSize(long size, bool partial)
        {
            _dirSize = size; _dirPartial = partial;
            OnPropertyChanged(nameof(SizeText));
        }

        public double DriveUsage => IsDrive && TotalSize > 0 ? 1.0 - (double)Size / TotalSize : 0;
        public bool HasUsage => IsDrive && TotalSize > 0;
        public bool DriveAlmostFull => DriveUsage > 0.9;

        /// <summary>Heading for "group by date" (Today, Yesterday, …).</summary>
        public string DateGroup => DateGroups.Name(DateGroups.Rank(Modified));

        public string ModifiedText => Modified == DateTime.MinValue ? "" : Modified.ToString("g");

        public static string FormatSize(long b)
        {
            if (b < 1024) return b + " B";
            string[] u = { "KB", "MB", "GB", "TB", "PB" };
            double v = b; int i = -1;
            do { v /= 1024; i++; } while (v >= 1024 && i < u.Length - 1);
            return (v >= 100 ? v.ToString("0") : v >= 10 ? v.ToString("0.#") : v.ToString("0.##")) + " " + u[i];
        }

        // ---- icons & thumbnails (lazy; requested only when a row/tile becomes visible) ----
        private ImageSource _icon;
        private bool _iconRequested;
        public ImageSource Icon
        {
            get
            {
                if (!_iconRequested)
                {
                    _iconRequested = true;
                    _icon = IconService.GetIcon(this, out bool needsOwn);
                    if (needsOwn) IconService.QueueOwnIcon(this);
                }
                return _icon;
            }
            set => Set(ref _icon, value);
        }

        private ImageSource _thumb;
        private int _thumbSize;
        public ImageSource Thumbnail
        {
            get
            {
                int want = AppState.ThumbnailRequestSize;
                // code / unknown types keep WinXplorer's icon: Windows would only give a blank page
                bool own = InArchive || !IsDirectory && IconService.UsesOwnIcon(Extension);
                if (_thumbSize != want && !own)
                {
                    _thumbSize = want;
                    ThumbnailService.Request(this, want);
                }
                // until the real thumbnail is there, show a big icon of the type (not a stretched 16 px one)
                return _thumb ?? IconService.GetLargeIcon(this, want) ?? Icon;
            }
        }

        internal void SetThumbnail(ImageSource img)
        {
            _thumb = img;
            OnPropertyChanged(nameof(Thumbnail));
            OnPropertyChanged(nameof(Badge));
            OnPropertyChanged(nameof(BadgeSize));
        }

        /// <summary>Type icon drawn in the thumbnail's corner for PSD / AI / PDF / SVG (not on the smallest tiles).</summary>
        public ImageSource Badge => _thumb != null && AppState.Settings.IconSize >= 64 && FileTypes.Badged.Contains(Extension)
            ? IconService.GetLargeIcon(this, 32) ?? Icon : null;
        public double BadgeSize => AppState.Settings.IconSize >= 160 ? 28 : 22;

        internal void RefreshDisplay() => OnPropertyChanged(nameof(DisplayName));

        // ---- hover tooltip: where it is, how big, when created / modified ----
        private string _folderSize;
        private bool _sizeRequested;

        public string ToolTipText
        {
            get
            {
                // no name (it is right there); the folder only for search results, where it isn't obvious
                var sb = new System.Text.StringBuilder();
                void Line(string s) { if (sb.Length > 0) sb.Append('\n'); sb.Append(s); }
                if (IsDrive) { if (TotalSize > 0) Line(SizeText); }
                else if (IsDirectory) Line(Loc.T("Size") + ": " + (_folderSize ?? Loc.T("Counting")));
                else Line(Loc.T("Size") + ": " + FormatSize(Size));
                if (!IsDrive && !string.IsNullOrEmpty(Location)) Line(Loc.T("Location") + ": " + Location);
                if (Created != DateTime.MinValue) Line(Loc.T("Created") + ": " + Created.ToString("g"));
                if (Modified != DateTime.MinValue) Line(Loc.T("ColModified") + ": " + Modified.ToString("g"));
                return sb.Length == 0 ? Name : sb.ToString();
            }
        }

        /// <summary>Called when the tooltip opens: counts the folder's size in the background (max ~3 s).</summary>
        public void RequestFolderSize()
        {
            if (!IsDirectory || IsDrive || _sizeRequested) return;
            _sizeRequested = true;
            var path = FullPath;
            System.Threading.Tasks.Task.Run(() =>
            {
                long bytes = 0; int files = 0, dirs = 0; bool partial = false;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                    foreach (var fi in new DirectoryInfo(path).EnumerateFileSystemInfos("*", opt))
                    {
                        if (fi is FileInfo f) { bytes += f.Length; files++; } else dirs++;
                        if ((files & 255) == 0 && sw.ElapsedMilliseconds > 3000) { partial = true; break; }
                    }
                }
                catch { }
                var text = (partial ? "> " : "") + FormatSize(bytes) + "  (" + Loc.FilesFolders(files, dirs) + ")";
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => { _folderSize = text; OnPropertyChanged(nameof(ToolTipText)); });
            });
        }
    }
}
