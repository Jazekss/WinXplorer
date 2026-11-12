using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using WinXplorer.Interop;
using WinXplorer.Models;

namespace WinXplorer.Services
{
    public static class FileSystem
    {
        /// <summary>Virtual location that lists all drives.</summary>
        public const string ThisPC = "";

        private static string LongPath(string dir)
        {
            if (dir.StartsWith(@"\\?\")) return dir;
            if (dir.StartsWith(@"\\")) return @"\\?\UNC\" + dir[2..];
            return @"\\?\" + dir;
        }

        /// <summary>Fast directory listing using FindFirstFileEx (basic info + large fetch).</summary>
        public static List<FileItem> List(string dir, bool showHidden, CancellationToken ct)
        {
            var list = new List<FileItem>(256);
            var pattern = Path.Combine(LongPath(dir), "*");
            var h = Native.FindFirstFileExW(pattern, Native.FindExInfoBasic, out var fd, Native.FindExSearchNameMatch, IntPtr.Zero, Native.FIND_FIRST_EX_LARGE_FETCH);
            if (h == Native.INVALID_HANDLE_VALUE)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == 18 /*no more files*/) return list;
                throw new Win32Exception(err);
            }
            try
            {
                do
                {
                    if (ct.IsCancellationRequested) return list;
                    var item = Make(dir, ref fd, showHidden, null);
                    if (item != null) list.Add(item);
                } while (Native.FindNextFileW(h, out fd));
            }
            finally { Native.FindClose(h); }
            return list;
        }

        private static FileItem Make(string dir, ref Native.WIN32_FIND_DATA fd, bool showHidden, string location)
        {
            var name = fd.cFileName;
            if (name == "." || name == "..") return null;
            if (!showHidden && (fd.dwFileAttributes & (Native.FILE_ATTRIBUTE_HIDDEN | Native.FILE_ATTRIBUTE_SYSTEM)) != 0) return null;
            bool isDir = (fd.dwFileAttributes & Native.FILE_ATTRIBUTE_DIRECTORY) != 0;
            return new FileItem
            {
                Name = name,
                FullPath = Path.Combine(dir, name),
                IsDirectory = isDir,
                Attributes = fd.dwFileAttributes,
                Size = isDir ? 0 : ((long)fd.nFileSizeHigh << 32) | fd.nFileSizeLow,
                Modified = SafeTime(fd.ftLastWriteTime),
                Created = SafeTime(fd.ftCreationTime),
                Location = location,
            };
        }

        /// <summary>One file or folder's details (null if it no longer exists).</summary>
        public static FileItem Stat(string path, string location)
        {
            var h = Native.FindFirstFileExW(LongPath(path.TrimEnd('\\')), Native.FindExInfoBasic, out var fd, Native.FindExSearchNameMatch, IntPtr.Zero, 0);
            if (h == Native.INVALID_HANDLE_VALUE) return null;
            Native.FindClose(h);
            return Make(Path.GetDirectoryName(path.TrimEnd('\\')) ?? "", ref fd, true, location);
        }

        private static DateTime SafeTime(long ft)
        {
            try { return ft <= 0 ? DateTime.MinValue : DateTime.FromFileTime(ft); } catch { return DateTime.MinValue; }
        }

        /// <summary>
        /// Lists drives. Every drive is queried in parallel and a drive that does not answer quickly
        /// (disconnected network share, sleeping disk) is listed with its plain name instead of blocking.
        /// </summary>
        /// <summary>A disc drive without a disc or a card reader without a card: not shown (Explorer lists them anyway).</summary>
        public static bool IsEmptyDrive(DriveInfo d)
        {
            try { return d.DriveType is DriveType.CDRom or DriveType.Removable && !d.IsReady; }
            catch { return false; }
        }

        public static List<FileItem> Drives()
        {
            var drives = DriveInfo.GetDrives();
            var tasks = new System.Threading.Tasks.Task<FileItem>[drives.Length];
            for (int i = 0; i < drives.Length; i++)
            {
                var d = drives[i];
                tasks[i] = System.Threading.Tasks.Task.Run(() =>
                {
                    if (IsEmptyDrive(d)) return null;
                    long free = 0, total = 0;
                    string label = null;
                    try { if (d.IsReady) { free = d.AvailableFreeSpace; total = d.TotalSize; } } catch { }
                    try { label = IconService.GetDisplayName(d.Name); } catch { }
                    return DriveItem(d.Name, label, free, total);
                });
            }
            try { System.Threading.Tasks.Task.WaitAll(tasks, 2500); } catch { }
            var list = new List<FileItem>(drives.Length);
            for (int i = 0; i < drives.Length; i++)
            {
                if (!tasks[i].IsCompletedSuccessfully) list.Add(DriveItem(drives[i].Name, null, 0, 0));
                else if (tasks[i].Result != null) list.Add(tasks[i].Result);
            }
            return list;
        }

        private static FileItem DriveItem(string root, string label, long free, long total) => new()
        {
            Name = string.IsNullOrEmpty(label) ? root : label,
            FullPath = root,
            IsDirectory = true,
            IsDrive = true,
            Size = free,
            TotalSize = total,
        };

        /// <summary>Directory.Exists with a time limit. Returns null when the location did not answer in time.</summary>
        public static bool? DirectoryExists(string path, int timeoutMs = 2500)
        {
            var t = System.Threading.Tasks.Task.Run(() => Directory.Exists(path));
            return t.Wait(timeoutMs) ? t.Result : null;
        }

        public static bool? FileExists(string path, int timeoutMs = 2500)
        {
            var t = System.Threading.Tasks.Task.Run(() => File.Exists(path));
            return t.Wait(timeoutMs) ? t.Result : null;
        }

        /// <summary>Recursive search. Calls onBatch periodically with new results.</summary>
        public static void Search(string root, string query, bool showHidden, Action<List<FileItem>> onBatch, CancellationToken ct)
        {
            var rx = NameQuery.Build(query, out var exq);

            var batch = new List<FileItem>();
            var last = Environment.TickCount64;
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0 && !ct.IsCancellationRequested)
            {
                var dir = stack.Pop();
                var h = Native.FindFirstFileExW(Path.Combine(LongPath(dir), "*"), Native.FindExInfoBasic, out var fd,
                    Native.FindExSearchNameMatch, IntPtr.Zero, Native.FIND_FIRST_EX_LARGE_FETCH);
                if (h == Native.INVALID_HANDLE_VALUE) continue;
                try
                {
                    do
                    {
                        var name = fd.cFileName;
                        if (name == "." || name == "..") continue;
                        if (!showHidden && (fd.dwFileAttributes & (Native.FILE_ATTRIBUTE_HIDDEN | Native.FILE_ATTRIBUTE_SYSTEM)) != 0) continue;
                        bool isDir = (fd.dwFileAttributes & Native.FILE_ATTRIBUTE_DIRECTORY) != 0;
                        if (isDir && (fd.dwFileAttributes & Native.FILE_ATTRIBUTE_REPARSE_POINT) == 0)
                            stack.Push(Path.Combine(dir, name));
                        bool match = rx != null ? NameQuery.IsMatch(rx, exq, name) : name.Contains(query, StringComparison.OrdinalIgnoreCase);
                        if (match) batch.Add(Make(dir, ref fd, true, dir));
                    } while (Native.FindNextFileW(h, out fd) && !ct.IsCancellationRequested);
                }
                finally { Native.FindClose(h); }

                if (batch.Count > 0 && Environment.TickCount64 - last > 150)
                {
                    onBatch(batch);
                    batch = new List<FileItem>();
                    last = Environment.TickCount64;
                }
            }
            if (batch.Count > 0 && !ct.IsCancellationRequested) onBatch(batch);
        }

        /// <summary>Turns user input from the address bar into a real path.</summary>
        public static string Resolve(string input, string current)
        {
            if (input == null) return null;
            var s = input.Trim().Trim('"');
            if (s.Length == 0) return current ?? ThisPC;
            var lower = s.ToLowerInvariant();
            switch (lower)
            {
                case "this pc": case "šis dators": case "computer": case "dators": return ThisPC;
                case "desktop": case "darbvirsma": return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                case "documents": case "dokumenti": return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                case "downloads": case "lejupielādes": return Native.GetKnownFolder(Native.FOLDERID_Downloads);
                case "pictures": case "attēli": return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                case "home": case "~": return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            s = Environment.ExpandEnvironmentVariables(s);
            if (s.StartsWith("~\\") || s.StartsWith("~/")) s = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), s[2..]);
            if (s.Length == 2 && s[1] == ':') s += "\\";
            if (!Path.IsPathRooted(s) && !string.IsNullOrEmpty(current)) s = Path.Combine(current, s);
            try { s = Path.GetFullPath(s); } catch { }
            return s;
        }

        public static string GetParent(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var p = Path.GetDirectoryName(path.TrimEnd('\\'));
            if (string.IsNullOrEmpty(p)) return ThisPC;
            if (p.Length == 2 && p[1] == ':') p += "\\";
            return p;
        }

        public static string DisplayName(string path)
        {
            if (path == ThisPC) return Loc.T("ThisPC");
            var trimmed = path.TrimEnd('\\');
            if (trimmed.Length == 2 && trimmed[1] == ':')
            {
                try { return IconService.GetDisplayName(path); } catch { return path; }
            }
            var n = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(n) ? path : n;
        }
    }
}
