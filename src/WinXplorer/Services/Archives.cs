using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using WinXplorer.Models;

namespace WinXplorer.Services
{
    /// <summary>
    /// Archives as folders: zip, 7z, rar, tar (+ .gz/.bz2/.xz) and zip-based formats (jar, apk, epub, nupkg...).
    /// A path like "D:\a\photos.zip\2023\img.jpg" points inside an archive. Browsing is read-only; files are
    /// extracted to a temporary folder when they are opened, dragged out or copied.
    /// New archives are written as .zip (Windows' own zip support, no extra code).
    /// </summary>
    public static class Archives
    {
        private sealed class Entry
        {
            public string Inner;          // "2023\img.jpg"
            public bool Dir;
            public long Size, Packed;
            public DateTime Modified;
            public bool Encrypted;
        }

        private sealed class Listing
        {
            public DateTime Stamp;
            public long Length;
            public Dictionary<string, List<Entry>> Children = new(StringComparer.OrdinalIgnoreCase); // folder ("" = root) → items
            public Dictionary<string, Entry> All = new(StringComparer.OrdinalIgnoreCase);
            public long Total, TotalPacked;
            public int Files, Folders;
            public bool Encrypted, Solid;
        }

        private static readonly Dictionary<string, Listing> _cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _lock = new();

        public static bool IsArchive(string path) => !string.IsNullOrEmpty(path) && FileTypes.Archive.Contains(Path.GetExtension(path));

        /// <summary>Splits "D:\x\a.zip\sub\f.txt" into ("D:\x\a.zip", "sub\f.txt"). Null when the path is not inside an archive.</summary>
        public static (string archive, string inner)? Split(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length < 4) return null;
            var p = path.TrimEnd('\\');
            int i = p.Length;
            while (i > 3)
            {
                var head = p[..i];
                if (IsArchive(head) && File.Exists(head)) return (head, i < p.Length ? p[(i + 1)..] : "");
                i = p.LastIndexOf('\\', i - 1);
                if (i < 0) break;
            }
            return null;
        }

        public static bool IsInside(string path) => Split(path) is { } s && (s.inner.Length > 0 || IsArchive(path));

        private static bool IsTarCompressed(string path)
        {
            var n = Path.GetFileName(path).ToLowerInvariant();
            return n.EndsWith(".tgz") || n.EndsWith(".tbz2") || n.EndsWith(".txz") || n.Contains(".tar.");
        }

        private static Listing Get(string archive)
        {
            var fi = new FileInfo(archive);
            lock (_lock)
                if (_cache.TryGetValue(archive, out var c) && c.Stamp == fi.LastWriteTimeUtc && c.Length == fi.Length) return c;

            var l = new Listing { Stamp = fi.LastWriteTimeUtc, Length = fi.Length };
            void Add(string key, bool dir, long size, long packed, DateTime? mod, bool enc)
            {
                if (string.IsNullOrEmpty(key)) return;
                var inner = key.Replace('/', '\\').Trim('\\');
                if (inner.Length == 0 || inner.Split('\\').Any(s => s is ".." or ".")) return;
                if (l.All.ContainsKey(inner)) { if (!dir) l.All[inner].Size = size; return; }
                // parent folders that have no entry of their own
                var parent = Path.GetDirectoryName(inner) ?? "";
                if (parent.Length > 0 && !l.All.ContainsKey(parent)) Add(parent, true, 0, 0, null, false);
                var e = new Entry { Inner = inner, Dir = dir, Size = dir ? 0 : size, Packed = packed, Modified = mod ?? DateTime.MinValue, Encrypted = enc };
                l.All[inner] = e;
                if (!l.Children.TryGetValue(parent, out var list)) l.Children[parent] = list = new List<Entry>();
                list.Add(e);
                if (dir) l.Folders++; else { l.Files++; l.Total += size; l.TotalPacked += packed; }
                if (enc) l.Encrypted = true;
            }

            if (IsTarCompressed(archive))
            {
                using var fs = File.OpenRead(archive);
                using var r = ReaderFactory.OpenReader(fs);
                while (r.MoveToNextEntry())
                    Add(r.Entry.Key, r.Entry.IsDirectory, r.Entry.Size, r.Entry.CompressedSize, r.Entry.LastModifiedTime, r.Entry.IsEncrypted);
            }
            else
            {
                using var a = ArchiveFactory.OpenArchive(archive);
                try { l.Solid = a.IsSolid; } catch { }
                foreach (var e in a.Entries)
                {
                    var key = e.Key;
                    // single-file .gz / .bz2 / .xz: the file inside has the archive's name without the extension
                    if (string.IsNullOrEmpty(key)) key = Path.GetFileNameWithoutExtension(archive);
                    Add(key, e.IsDirectory, e.Size, e.CompressedSize, e.LastModifiedTime, e.IsEncrypted);
                }
            }
            lock (_lock)
            {
                if (_cache.Count > 6) _cache.Clear();
                _cache[archive] = l;
            }
            return l;
        }

        /// <summary>Solid 7z / rar: single files can only be reached by decompressing everything before them.</summary>
        public static bool IsSolid(string archive) { try { return Get(archive).Solid; } catch { return false; } }

        /// <summary>The items of a folder inside an archive, as FileItems with virtual paths.</summary>
        public static List<FileItem> List(string path, CancellationToken ct)
        {
            var (archive, inner) = Split(path) ?? throw new DirectoryNotFoundException(path);
            var l = Get(archive);
            if (!l.Children.TryGetValue(inner, out var list))
            {
                if (inner.Length == 0) return new List<FileItem>();
                throw new DirectoryNotFoundException(path);
            }
            var stamp = File.GetLastWriteTime(archive);
            return list.Select(e => new FileItem
            {
                Name = Path.GetFileName(e.Inner),
                FullPath = archive + "\\" + e.Inner,
                IsDirectory = e.Dir,
                Size = e.Size,
                Modified = e.Modified == DateTime.MinValue ? stamp : e.Modified,
                Attributes = (uint)(e.Dir ? 0x10 : 0x20) | 0x1, // read-only
                InArchive = true,
            }).ToList();
        }

        /// <summary>"12 files, 3 folders · 4.1 MB (packed 1.2 MB)" and the first entries, for the preview panel.</summary>
        public static (int files, int folders, long total, long packed, bool encrypted, List<(string path, long size, DateTime mod, bool dir)> entries) Summary(string archive, int max)
        {
            var l = Get(archive);
            var entries = l.All.Values.OrderBy(e => e.Inner, StringComparer.OrdinalIgnoreCase).Take(max)
                .Select(e => (e.Inner, e.Size, e.Modified, e.Dir)).ToList();
            return (l.Files, l.Folders, l.Total, l.TotalPacked, l.Encrypted, entries);
        }

        // ---------------- extracting ----------------
        private static string TempRoot => Path.Combine(Path.GetTempPath(), "WinXplorer", "archives");

        /// <summary>Where an archive's files are extracted for opening (one folder per archive version).</summary>
        private static string TempFor(string archive)
        {
            var fi = new FileInfo(archive);
            // stable across runs (string.GetHashCode isn't): the same archive reuses its folder
            uint h = 2166136261;
            foreach (var c in archive.ToLowerInvariant() + "|" + fi.LastWriteTimeUtc.Ticks) h = (h ^ c) * 16777619;
            var key = h.ToString("X8");
            return Path.Combine(TempRoot, Path.GetFileNameWithoutExtension(archive) + "-" + key);
        }

        /// <summary>
        /// Where <see cref="Materialize"/> will put these items – without extracting anything yet
        /// (dragging out of an archive: the files are only extracted when they are dropped).
        /// </summary>
        public static List<string> FuturePaths(IEnumerable<string> paths)
        {
            var result = new List<string>();
            foreach (var p in paths)
            {
                if (Split(p) is not { } s || s.inner.Length == 0) { result.Add(p); continue; }
                var dir = TempFor(s.archive);
                var full = Path.Combine(dir, Norm(s.inner));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                result.Add(full);
            }
            return result;
        }

        /// <summary>
        /// Real files for items inside archives (extracted to a temporary folder) – for opening, drag &amp; drop, copy.
        /// Paths outside archives are returned unchanged.
        /// </summary>
        public static List<string> Materialize(IEnumerable<string> paths)
        {
            var result = new List<string>();
            foreach (var g in paths.GroupBy(p => Split(p)?.archive ?? "", StringComparer.OrdinalIgnoreCase))
            {
                if (g.Key.Length == 0) { result.AddRange(g); continue; }
                var dir = TempFor(g.Key);
                var wanted = g.Select(p => Split(p)!.Value.inner).Where(i => i.Length > 0).ToList();
                try { Directory.CreateDirectory(dir); Directory.SetLastWriteTime(dir, DateTime.Now); } catch { }   // in use: not cleaned up yet
                ExtractTo(g.Key, dir, wanted, overwrite: false);
                result.AddRange(wanted.Select(w => Path.Combine(dir, w)));
            }
            return result;
        }

        /// <summary>
        /// Extracts the whole archive (inner = null) or the given items (files, or folders with everything below)
        /// into <paramref name="target"/>, keeping their folder structure relative to the archive's root.
        /// </summary>
        public static void ExtractTo(string archive, string target, IList<string> inner, bool overwrite = true, Action<int> progress = null)
        {
            Directory.CreateDirectory(target);
            var full = Path.GetFullPath(target).TrimEnd('\\') + "\\";
            bool Want(string key)
            {
                if (inner == null) return true;
                key = key.Replace('/', '\\').Trim('\\');
                foreach (var i in inner)
                    if (string.Equals(key, i, StringComparison.OrdinalIgnoreCase) || key.StartsWith(i + "\\", StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
            int n = 0;
            void Write(string key, bool dir, Action<string> writeFile)
            {
                if (string.IsNullOrEmpty(key)) key = Path.GetFileNameWithoutExtension(archive);
                var rel = key.Replace('/', '\\').Trim('\\');
                if (!Want(rel)) return;
                var dest = Path.GetFullPath(Path.Combine(target, rel));
                if (!dest.StartsWith(full, StringComparison.OrdinalIgnoreCase)) return; // "..\..\" entries: never outside the target
                if (dir) { Directory.CreateDirectory(dest); return; }
                if (!overwrite && File.Exists(dest)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                writeFile(dest);
                progress?.Invoke(++n);
            }
            var opts = new ExtractionOptions { Overwrite = true, PreserveFileTime = true, ExtractFullPath = false };

            if (IsTarCompressed(archive) || Path.GetExtension(archive).Equals(".7z", StringComparison.OrdinalIgnoreCase) && inner == null)
            {
                // streaming: one pass (fast for solid 7z and compressed tar)
                using var fs = File.OpenRead(archive);
                using var r = IsTarCompressed(archive) ? ReaderFactory.OpenReader(fs) : ArchiveFactory.OpenArchive(fs).ExtractAllEntries();
                while (r.MoveToNextEntry())
                    Write(r.Entry.Key, r.Entry.IsDirectory, dest => r.WriteEntryToFile(dest, opts));
                return;
            }
            using var a = ArchiveFactory.OpenArchive(archive);
            if (a.IsSolid)
            {
                // solid 7z / rar: random access to an entry decompresses wrongly ("crc mismatch") and re-reads the
                // whole stream for every file – read it once, in order, and stop when every wanted file is out
                var left = inner == null ? null : new HashSet<string>(inner.Select(Norm), StringComparer.OrdinalIgnoreCase);
                bool onlyFiles = left != null && left.All(i => a.Entries.Any(e => !e.IsDirectory && string.Equals(Norm(e.Key), i, StringComparison.OrdinalIgnoreCase)));
                using var r = a.ExtractAllEntries();
                while (r.MoveToNextEntry())
                {
                    var k = Norm(r.Entry.Key);
                    Write(r.Entry.Key, r.Entry.IsDirectory, dest => r.WriteEntryToFile(dest, opts));
                    if (onlyFiles && left.Remove(k) && left.Count == 0) break;
                }
                return;
            }
            foreach (var e in a.Entries)
                Write(e.Key, e.IsDirectory, dest => e.WriteToFile(dest, opts));
        }

        private static string Norm(string k) => (k ?? "").Replace('/', Path.DirectorySeparatorChar).Trim(Path.DirectorySeparatorChar);

        /// <summary>Folder name for "Extract here": "photos.zip" → "photos", "x.tar.gz" → "x".</summary>
        public static string BaseName(string archive)
        {
            var n = Path.GetFileName(archive);
            foreach (var e in new[] { ".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst" })
                if (n.EndsWith(e, StringComparison.OrdinalIgnoreCase)) return n[..^e.Length];
            return Path.GetFileNameWithoutExtension(n);
        }

        // ---------------- creating ----------------
        /// <summary>Writes the given files / folders into a new .zip.</summary>
        public static void CreateZip(string zip, IList<string> sources, Action<long, long> progress = null, CancellationToken ct = default)
        {
            var tmp = zip + ".part";
            try
            {
                // first the list (folders, files with sizes) so the progress can be told in bytes
                var dirs = new List<string>();
                var files = new List<(string path, string rel, long size)>();
                long total = 0;
                foreach (var src in sources)
                {
                    var s = src.TrimEnd('\\');
                    var baseDir = Path.GetDirectoryName(s) ?? "";
                    if (Directory.Exists(s))
                    {
                        var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                        dirs.Add(Rel(baseDir, s) + "/");
                        foreach (var d in Directory.EnumerateDirectories(s, "*", opt)) dirs.Add(Rel(baseDir, d) + "/");
                        foreach (var f in new DirectoryInfo(s).EnumerateFiles("*", opt))
                        {
                            ct.ThrowIfCancellationRequested();
                            if (string.Equals(f.FullName, tmp, StringComparison.OrdinalIgnoreCase) || string.Equals(f.FullName, zip, StringComparison.OrdinalIgnoreCase)) continue;
                            files.Add((f.FullName, Rel(baseDir, f.FullName), f.Length)); total += f.Length;
                        }
                    }
                    else if (File.Exists(s)) { var l = new FileInfo(s).Length; files.Add((s, Path.GetFileName(s), l)); total += l; }
                }
                long done = 0;
                var buf = new byte[1 << 20];
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    foreach (var d in dirs) z.CreateEntry(d);
                    foreach (var (path, rel, _) in files)
                    {
                        ct.ThrowIfCancellationRequested();
                        var e = z.CreateEntry(rel, Level(path));
                        try { e.LastWriteTime = File.GetLastWriteTime(path); } catch { }
                        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                        using var output = e.Open();
                        int n;
                        while ((n = input.Read(buf, 0, buf.Length)) > 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            output.Write(buf, 0, n);
                            done += n;
                            progress?.Invoke(done, total);
                        }
                    }
                }
                progress?.Invoke(total, total);
                File.Move(tmp, zip, true);
            }
            catch
            {
                try { File.Delete(tmp); } catch { }
                throw;
            }

            static string Rel(string baseDir, string p) => Path.GetRelativePath(baseDir, p).Replace('\\', '/');
            // already-compressed files: store them (fast, they wouldn't get smaller)
            static CompressionLevel Level(string f) => FileTypes.Archive.Contains(Path.GetExtension(f)) || FileTypes.Video.Contains(Path.GetExtension(f))
                || Path.GetExtension(f).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".mp3" or ".m4a" or ".webp" or ".heic" or ".docx" or ".xlsx" or ".pptx"
                ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
        }

        /// <summary>Removes temporary extractions older than a day (called at startup).</summary>
        /// <summary>Removes copies taken out of archives (and folders left empty) – once they were copied where they belong.</summary>
        public static void DeleteTemp(IEnumerable<string> paths)
        {
            var root = TempRoot.TrimEnd('\\') + "\\";
            foreach (var p in paths)
            {
                if (p == null || !p.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                try { if (Directory.Exists(p)) Directory.Delete(p, true); else if (File.Exists(p)) File.Delete(p); } catch { }
                // empty parents up to the archives folder
                for (var d = Path.GetDirectoryName(p); d != null && d.StartsWith(root, StringComparison.OrdinalIgnoreCase); d = Path.GetDirectoryName(d))
                {
                    try { if (Directory.EnumerateFileSystemEntries(d).Any()) break; Directory.Delete(d); } catch { break; }
                }
            }
        }

        /// <summary>
        /// Files taken out of archives to open, drag or copy them are temporary: a folder not used for
        /// <paramref name="olderThan"/> (default one hour) is deleted. Runs at start and when a window closes.
        /// </summary>
        public static void CleanTemp(TimeSpan? olderThan = null)
        {
            var age = olderThan ?? TimeSpan.FromHours(1);
            try
            {
                if (!Directory.Exists(TempRoot)) return;
                foreach (var d in Directory.GetDirectories(TempRoot))
                    if (DateTime.Now - Directory.GetLastWriteTime(d) > age)
                        try { Directory.Delete(d, true); } catch { }   // files still open in a program stay until next time
            }
            catch { }
        }
    }
}
