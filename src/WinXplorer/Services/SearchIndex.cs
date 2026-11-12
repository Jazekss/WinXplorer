using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using WinXplorer.Interop;
using WinXplorer.Models;

namespace WinXplorer.Services
{
    /// <summary>
    /// Fast file-name search for whole drives.
    ///
    /// Every fixed drive is crawled (in a short-lived child process, low priority) into a compact table saved to
    /// %LOCALAPPDATA%\WinXplorer: all names in one char buffer + parent links, ~11 bytes + the name per file.
    /// Nothing is loaded while you browse: the first search on a drive maps that drive's file (instant), a minute
    /// after the last search it is unmapped again, and an index older than a few minutes is rebuilt in the
    /// background right after the search. Changes seen by a drive watcher are merged in meanwhile.
    /// </summary>
    public static class SearchIndex
    {
        private const byte FDir = 1, FHidden = 2;

        /// <summary>
        /// One drive's index, read straight from its file (memory-mapped): it does not sit in the process heap,
        /// every WinXplorer window shares the same pages, and Windows can drop them when memory is needed.
        /// </summary>
        private sealed unsafe class Volume : IDisposable
        {
            public string Root;              // "C:\"
            public uint Serial;
            public int Count;
            public DateTime Built;
            public string File;
            public int Users;                // searches running on it
            public bool Retired;             // replaced by a newer index: unmap when the last search ends
            public DateTime LastUsed, Checked;
            private MemoryMappedFile _mmf;
            private MemoryMappedViewAccessor _view;
            private int* _off; private ushort* _len; private int* _parent; private byte* _flags; private char* _chars;

            public ReadOnlySpan<char> Name(int i) => new(_chars + _off[i], _len[i]);
            public string NameString(int i) => new(_chars, _off[i], _len[i]);
            public int Parent(int i) => _parent[i];
            public byte Flags(int i) => _flags[i];

            public static Volume Map(string file, string root, uint serial)
            {
                FileStream fs = null; MemoryMappedFile mmf = null; MemoryMappedViewAccessor view = null;
                bool acquired = false;
                try
                {
                    fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    long len = fs.Length;
                    if (len < HeaderSize) { fs.Dispose(); return null; }
                    mmf = MemoryMappedFile.CreateFromFile(fs, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, false);
                    view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                    byte* p = null;
                    view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
                    acquired = true;
                    p += view.PointerOffset;
                    int count = *(int*)(p + 16), chars = *(int*)(p + 20);
                    if (*(int*)p != Magic || *(uint*)(p + 4) != serial || count <= 0 || chars < 0
                        || len < HeaderSize + 11L * count + 2L * chars)
                        throw new InvalidDataException();
                    var v = new Volume
                    {
                        Root = root, Serial = serial, Count = count, File = file,
                        Built = DateTime.FromBinary(*(long*)(p + 8)),
                        _mmf = mmf, _view = view,
                    };
                    byte* q = p + HeaderSize;
                    v._off = (int*)q; q += 4L * count;
                    v._len = (ushort*)q; q += 2L * count;
                    v._parent = (int*)q; q += 4L * count;
                    v._flags = q; q += count;
                    v._chars = (char*)q;
                    return v;
                }
                catch (Exception ex)
                {
                    if (ex is not InvalidDataException) Log($"map {Path.GetFileName(file)}: {ex.Message}");
                    if (acquired) try { view.SafeMemoryMappedViewHandle.ReleasePointer(); } catch { }
                    view?.Dispose(); mmf?.Dispose(); fs?.Dispose();
                    return null;
                }
            }

            public void Dispose()
            {
                if (_view == null) return;
                try { _view.SafeMemoryMappedViewHandle.ReleasePointer(); } catch { }
                _view.Dispose(); _mmf.Dispose();
                _view = null; _mmf = null;
            }
        }

        private sealed class Builder
        {
            public char[] Chars = new char[1 << 20];
            public int CharCount;
            public int[] Off = new int[1 << 16];
            public ushort[] Len = new ushort[1 << 16];
            public int[] Parent = new int[1 << 16];
            public byte[] Flags = new byte[1 << 16];
            public int Count;

            public int Add(string name, int parent, byte flags)
            {
                if (Count == Off.Length)
                {
                    int n = Count * 2;
                    Array.Resize(ref Off, n); Array.Resize(ref Len, n); Array.Resize(ref Parent, n); Array.Resize(ref Flags, n);
                }
                if (CharCount + name.Length > Chars.Length) Array.Resize(ref Chars, Math.Max(Chars.Length * 2, CharCount + name.Length));
                name.CopyTo(0, Chars, CharCount, name.Length);
                Off[Count] = CharCount; Len[Count] = (ushort)Math.Min(name.Length, ushort.MaxValue); Parent[Count] = parent; Flags[Count] = flags;
                CharCount += name.Length;
                return Count++;
            }

        }

        private static readonly Dictionary<string, Volume> _vols = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, (string text, int done)> _progress = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _lock = new();
        private static readonly List<FileSystemWatcher> _watchers = new();
        // changes seen since the volume was crawled
        private static readonly List<(string path, bool dir, long ticks)> _added = new();
        private static readonly Dictionary<string, long> _removed = new(StringComparer.OrdinalIgnoreCase);
        // what is on disk for each drive (read from the file header only – nothing is mapped until a search)
        private sealed class Known { public string File; public DateTime Built; public int Count; }
        private static readonly Dictionary<string, Known> _known = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _building = new(StringComparer.OrdinalIgnoreCase);
        private static Timer _sweep;
        private static bool _started, _skipWindows = true;
        private const int IdleUnmapSeconds = 60, RefreshAfterMinutes = 10;

        private static void Log(string text)
        {
            try
            {
                var f = Path.Combine(Dir, "index.log");
                if (System.IO.File.Exists(f) && new FileInfo(f).Length > 64 * 1024) System.IO.File.Delete(f);
                System.IO.File.AppendAllText(f, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{Environment.ProcessId}] {text}\n");
            }
            catch { }
        }

        public static event Action Changed;

        private static string Dir => AppState.DataDir;
        // a new file for every rebuild (index-C-<ticks>.bin): other windows may still have the old one mapped
        private static string NewFileFor(string root) => Path.Combine(Dir, $"index-{root[0]}-{DateTime.Now.Ticks}.bin");
        private static List<string> FilesFor(string root)
        {
            try
            {
                return Directory.GetFiles(Dir, $"index-{root[0]}*.bin")
                    .Where(f => { var n = Path.GetFileNameWithoutExtension(f); return n.Length == 7 || n[7] == '-'; })
                    .OrderByDescending(f => System.IO.File.GetLastWriteTimeUtc(f)).ToList();
            }
            catch { return new List<string>(); }
        }

        /// <summary>Makes <paramref name="v"/> the drive's index; the old one is unmapped once no search uses it.</summary>
        private static void Install(Volume v)
        {
            lock (_lock)
            {
                if (_vols.TryGetValue(v.Root, out var old) && old != v)
                {
                    old.Retired = true;
                    if (old.Users == 0) old.Dispose();
                }
                _vols[v.Root] = v;
            }
        }

        private static bool InUse(string f)
        {
            lock (_lock) return _vols.Values.Any(v => string.Equals(v.File, f, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Deletes older index files of a drive (fails quietly while another window still maps one).</summary>
        private static void DeleteOld(string root, string keep)
        {
            foreach (var f in FilesFor(root))
                if (!string.Equals(f, keep, StringComparison.OrdinalIgnoreCase) && !InUse(f))
                    try { System.IO.File.Delete(f); } catch (Exception ex) { Log("delete: " + ex.Message); }
        }

        /// <summary>Folders skipped by default: system files nobody searches by name and that change constantly.</summary>
        private static readonly string[] SkipNames = { "$Recycle.Bin", "System Volume Information", "$WinREAgent", "Config.Msi", "$SysReset", "$Windows.~WS", "$Windows.~BT", "Recovery" };

        private static bool Skip(string fullDir, string root)
        {
            var rel = fullDir.Length > root.Length ? fullDir[root.Length..] : "";
            if (rel.Length == 0) return false;
            foreach (var s in SkipNames) if (rel.Equals(s, StringComparison.OrdinalIgnoreCase)) return true;
            if (rel.Equals("Windows", StringComparison.OrdinalIgnoreCase) && _skipWindows) return true;
            return false;
        }

        // ================= lifecycle =================
        public static void Start()
        {
            if (_started || !AppState.Settings.SearchIndex) return;
            _started = true;
            var t = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "SearchIndex" };
            t.Start();
        }

        private static IEnumerable<DriveInfo> Drives()
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                bool ok;
                try { ok = d.DriveType == DriveType.Fixed && d.IsReady && d.DriveFormat is "NTFS" or "ReFS" or "exFAT" or "FAT32"; } catch { ok = false; }
                if (ok) yield return d;
            }
        }

        private static void Run()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                _skipWindows = AppState.Settings.IndexSkipWindows;
                var roots = Drives().Select(d => d.RootDirectory.FullName).ToList();
                // 1) what's saved → searchable right away (only the header is read; the index is mapped on first search)
                foreach (var r in roots)
                {
                    var k = FindKnown(r, Serial(r));
                    if (k != null) lock (_lock) _known[r] = k;
                }
                Changed?.Invoke();
                foreach (var r in roots) Watch(r);
                // indexes nobody searched for a minute are unmapped again
                _sweep = new Timer(_ => Sweep(), null, 30_000, 30_000);

                // 2) drives without any index get one now (in a child process); the rest refresh after a search
                Thread.Sleep(5_000);
                foreach (var r in roots)
                {
                    Known k; lock (_lock) _known.TryGetValue(r, out k);
                    if (k != null) DeleteOld(r, k.File);
                    else BuildInChild(new List<string> { r });
                }
            }
            catch (Exception ex) { Log("run: " + ex); }
        }

        private static readonly HashSet<string> _stale = new(StringComparer.OrdinalIgnoreCase);

        private static Known FindKnown(string root, uint serial)
        {
            foreach (var f in FilesFor(root))
            {
                try
                {
                    using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var h = new byte[HeaderSize];
                    if (fs.Read(h, 0, HeaderSize) != HeaderSize) continue;
                    int count = BitConverter.ToInt32(h, 16), chars = BitConverter.ToInt32(h, 20);
                    if (BitConverter.ToInt32(h, 0) != Magic || BitConverter.ToUInt32(h, 4) != serial || count <= 0 || chars < 0
                        || fs.Length < HeaderSize + 11L * count + 2L * chars) continue;
                    return new Known { File = f, Count = count, Built = DateTime.FromBinary(BitConverter.ToInt64(h, 8)) };
                }
                catch { }
            }
            return null;
        }

        /// <summary>Unmaps indexes that no search used for a while – the memory goes back to Windows.</summary>
        private static void Sweep()
        {
            lock (_lock)
            {
                foreach (var v in _vols.Values.Where(v => v.Users == 0 && (DateTime.UtcNow - v.LastUsed).TotalSeconds > IdleUnmapSeconds).ToList())
                {
                    _vols.Remove(v.Root);
                    v.Dispose();
                }
            }
        }

        /// <summary>The drive's index, mapped now if it isn't (cheap: Windows reads the pages as the search touches them).</summary>
        private static Volume Acquire(string drive)
        {
            lock (_lock)
            {
                _vols.TryGetValue(drive, out var v);
                var now = DateTime.UtcNow;
                if (v == null || (now - v.Checked).TotalSeconds > 30)
                {
                    // another window may have written a newer file meanwhile
                    var serial = Serial(drive);
                    var k = FindKnown(drive, serial);
                    if (k != null) _known[drive] = k;
                    if (k != null && (v == null || !string.Equals(v.File, k.File, StringComparison.OrdinalIgnoreCase)))
                    {
                        var nv = Volume.Map(k.File, drive, serial);
                        if (nv != null)
                        {
                            if (v != null) { v.Retired = true; if (v.Users == 0) v.Dispose(); }
                            _vols[drive] = v = nv;
                        }
                    }
                    if (v != null) v.Checked = now;
                }
                if (v == null) return null;
                v.Users++;
                v.LastUsed = now;
                return v;
            }
        }

        private static void Release(Volume v)
        {
            lock (_lock)
            {
                v.Users--;
                v.LastUsed = DateTime.UtcNow;
                if (v.Retired && v.Users == 0) v.Dispose();
            }
        }

        /// <summary>After a search: if the drive's index is older than a few minutes, rebuild it in the background.</summary>
        private static void RefreshIfOld(string drive)
        {
            lock (_lock)
            {
                if (_building.Contains(drive)) return;
                if (_known.TryGetValue(drive, out var k) && (DateTime.Now - k.Built).TotalMinutes < RefreshAfterMinutes && !_stale.Contains(drive)) return;
                _building.Add(drive);
            }
            new Thread(() => BuildInChild(new List<string> { drive })) { IsBackground = true, Name = "IndexRefresh" }.Start();
        }

        /// <summary>Rebuild all drives now (Settings › Rebuild).</summary>
        public static void Rebuild()
        {
            _skipWindows = AppState.Settings.IndexSkipWindows;
            new Thread(() => BuildInChild(Drives().Select(d => d.RootDirectory.FullName).ToList())) { IsBackground = true }.Start();
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetVolumeInformationW(string root, IntPtr name, int nameSize, out uint serial, out uint maxLen, out uint flags, IntPtr fs, int fsSize);

        private static uint Serial(string root) => GetVolumeInformationW(root, IntPtr.Zero, 0, out var s, out _, out _, IntPtr.Zero, 0) ? s : 0;

        // ================= crawl =================
        // The crawl runs in a short-lived child process ("WinXplorer.exe --build-index C:\"): its working memory
        // (hundreds of MB for a big drive) goes back to Windows the moment it exits, instead of staying in the
        // file manager. The child writes the index file; this process then maps it.

        /// <summary>Child process entry: crawls the given drives, prints progress, exits.</summary>
        public static void BuildMain(string[] args)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                _skipWindows = args.Contains("--skip-windows");
                try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                foreach (var root in args.Where(a => a.Length == 3 && a[1] == ':' && a[2] == '\\'))
                {
                    var file = CrawlToFile(root, n => { Console.WriteLine(root + "|" + n); Console.Out.Flush(); });
                    Console.WriteLine(root + "|done|" + file);
                    Console.Out.Flush();
                }
            }
            catch (Exception ex) { Log("build: " + ex); }
        }

        private static void BuildInChild(List<string> roots)
        {
            if (roots.Count == 0) return;
            long started = DateTime.Now.Ticks;
            // one build per drive across all WinXplorer windows
            var locks = new List<Mutex>();
            foreach (var r in roots.ToList())
            {
                var m = new Mutex(false, "WinXplorer.SearchIndex.Build." + r[0]);
                bool got;
                try { got = m.WaitOne(0); } catch (AbandonedMutexException) { got = true; }
                if (got) locks.Add(m); else { m.Dispose(); roots.Remove(r); lock (_lock) _building.Remove(r); }
            }
            if (roots.Count == 0) return;
            try
            {
                var psi = new ProcessStartInfo(Environment.ProcessPath ?? "WinXplorer.exe")
                {
                    UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
                };
                psi.ArgumentList.Add("--build-index");
                if (_skipWindows) psi.ArgumentList.Add("--skip-windows");
                foreach (var r in roots) psi.ArgumentList.Add(r);
                using var p = Process.Start(psi);
                if (p == null) return;
                string line;
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    var parts = line.Split('|');
                    if (parts.Length == 2 && int.TryParse(parts[1], out var n))
                    {
                        lock (_lock) _progress[parts[0]] = (parts[0], n);
                        Changed?.Invoke();
                    }
                    else if (parts.Length == 3 && parts[1] == "done")
                        Finish(parts[0], parts[2], started);
                }
                p.WaitForExit();
            }
            catch (Exception ex) { Log("child: " + ex.Message); }
            finally
            {
                lock (_lock) foreach (var r in roots) { _progress.Remove(r); _building.Remove(r); }
                foreach (var m in locks) { try { m.ReleaseMutex(); } catch { } m.Dispose(); }
                Changed?.Invoke();
            }
        }

        private static void Finish(string root, string file, long started)
        {
            var serial = Serial(root);
            var k = FindKnown(root, serial);
            if (k == null || !string.Equals(k.File, file, StringComparison.OrdinalIgnoreCase)) { Log($"finish {root}: no index ({file})"); return; }
            lock (_lock)
            {
                _known[root] = k;
                _progress.Remove(root);
                _stale.Remove(root);
                // forget changes the crawl already saw
                _added.RemoveAll(a => a.path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && a.ticks < started);
                foreach (var key in _removed.Where(r => r.Key.StartsWith(root, StringComparison.OrdinalIgnoreCase) && r.Value < started).Select(r => r.Key).ToList()) _removed.Remove(key);
            }
            // mapped right now (a search is going on)? switch it to the new file; otherwise it's mapped on the next search
            bool mapped; lock (_lock) mapped = _vols.ContainsKey(root);
            if (mapped && Volume.Map(file, root, serial) is { } vol) { vol.LastUsed = vol.Checked = DateTime.UtcNow; Install(vol); }
            DeleteOld(root, file);
            Changed?.Invoke();
        }

        private static string CrawlToFile(string root, Action<int> progress)
        {
            var b = new Builder();
            int rootIdx = b.Add(root.TrimEnd('\\'), -1, FDir);
            var stack = new Stack<(string path, int idx)>();
            stack.Push((root, rootIdx));
            int dirs = 0;
            while (stack.Count > 0)
            {
                var (dir, idx) = stack.Pop();
                var h = Native.FindFirstFileExW((dir.EndsWith('\\') ? @"\\?\" + dir : @"\\?\" + dir + "\\") + "*", Native.FindExInfoBasic, out var fd,
                    Native.FindExSearchNameMatch, IntPtr.Zero, Native.FIND_FIRST_EX_LARGE_FETCH);
                if (h == Native.INVALID_HANDLE_VALUE) continue;
                try
                {
                    do
                    {
                        var name = fd.cFileName;
                        if (name == "." || name == "..") continue;
                        bool isDir = (fd.dwFileAttributes & Native.FILE_ATTRIBUTE_DIRECTORY) != 0;
                        byte flags = (byte)((isDir ? FDir : 0) | ((fd.dwFileAttributes & (Native.FILE_ATTRIBUTE_HIDDEN | Native.FILE_ATTRIBUTE_SYSTEM)) != 0 ? FHidden : 0));
                        var full = dir.EndsWith('\\') ? dir + name : dir + "\\" + name;
                        if (isDir && Skip(full, root)) continue;
                        int ni = b.Add(name, idx, flags);
                        if (isDir && (fd.dwFileAttributes & Native.FILE_ATTRIBUTE_REPARSE_POINT) == 0) stack.Push((full, ni));
                    } while (Native.FindNextFileW(h, out fd));
                }
                finally { Native.FindClose(h); }
                if (++dirs % 2000 == 0) progress?.Invoke(b.Count);
            }
            return Save(b, root, Serial(root), DateTime.Now);
        }

        // ================= live updates =================
        private static void Watch(string root)
        {
            try
            {
                var w = new FileSystemWatcher(root) { IncludeSubdirectories = true, InternalBufferSize = 64 * 1024, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName };
                w.Created += (_, e) => Added(e.FullPath, root);
                w.Deleted += (_, e) => Removed(e.FullPath);
                w.Renamed += (_, e) => { Removed(e.OldFullPath); Added(e.FullPath, root); };
                w.Error += (_, _) => { lock (_lock) _stale.Add(root); };
                w.EnableRaisingEvents = true;
                lock (_lock) _watchers.Add(w);
            }
            catch { }
        }

        private static void Added(string path, string root)
        {
            if (path.Contains("\\$Recycle.Bin", StringComparison.OrdinalIgnoreCase) || path.Contains("\\System Volume Information", StringComparison.OrdinalIgnoreCase)) return;
            bool dir = Directory.Exists(path);
            lock (_lock)
            {
                _removed.Remove(path);
                _added.Add((path, dir, DateTime.Now.Ticks));
                if (_added.Count > 200_000) { _added.RemoveRange(0, 100_000); _stale.Add(root); }
            }
        }

        private static void Removed(string path)
        {
            lock (_lock)
            {
                _removed[path] = DateTime.Now.Ticks;
                _added.RemoveAll(a => string.Equals(a.path, path, StringComparison.OrdinalIgnoreCase));
            }
        }

        // ================= save / load =================
        private const int Magic = 0x31495857; // "WXI1"
        private const int HeaderSize = 24;     // magic, serial, built, count, chars

        private static string Save(Builder b, string root, uint serial, DateTime built)
        {
            try
            {
                var file = NewFileFor(root);
                var tmp = Path.ChangeExtension(file, ".tmp");
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                using (var w = new BinaryWriter(fs))
                {
                    w.Write(Magic); w.Write(serial); w.Write(built.ToBinary()); w.Write(b.Count); w.Write(b.CharCount);
                    w.Write(MemoryMarshal.AsBytes(b.Off.AsSpan(0, b.Count)));
                    w.Write(MemoryMarshal.AsBytes(b.Len.AsSpan(0, b.Count)));
                    w.Write(MemoryMarshal.AsBytes(b.Parent.AsSpan(0, b.Count)));
                    w.Write(b.Flags.AsSpan(0, b.Count));
                    w.Write(MemoryMarshal.AsBytes(b.Chars.AsSpan(0, b.CharCount)));
                }
                System.IO.File.Move(tmp, file, true);
                return file;
            }
            catch (Exception ex) { Log("save: " + ex.Message); return null; }
        }

        // ================= search =================
        public static bool IsReady(string path)
        {
            if (!AppState.Settings.SearchIndex || string.IsNullOrEmpty(path)) return false;
            var root = Path.GetPathRoot(path);
            lock (_lock) return root != null && _known.ContainsKey(root);
        }

        /// <summary>"C: 812 345 items, updated 10:32" for Settings.</summary>
        public static string Status()
        {
            lock (_lock)
            {
                var parts = new List<string>();
                foreach (var kv in _known.OrderBy(k => k.Key))
                    parts.Add(Loc.F("IndexDrive", kv.Key.TrimEnd('\\'), kv.Value.Count.ToString("N0"), kv.Value.Built.ToString("g")));
                foreach (var p in _progress) parts.Add(Loc.F("IndexBuilding", p.Key.TrimEnd('\\'), p.Value.done.ToString("N0")));
                return parts.Count == 0 ? Loc.T(AppState.Settings.SearchIndex ? "IndexStarting" : "IndexOff") : string.Join("\n", parts);
            }
        }

        private static string FullPath(Volume v, int i, Dictionary<int, string> cache)
        {
            if (i == 0) return v.Root.TrimEnd('\\');
            if (cache.TryGetValue(i, out var s)) return s;
            int pi = v.Parent(i);
            var parent = pi <= 0 ? v.Root.TrimEnd('\\') : FullPath(v, pi, cache);
            s = parent + "\\" + v.NameString(i);
            if ((v.Flags(i) & FDir) != 0) cache[i] = s;
            return s;
        }

        /// <summary>
        /// Searches names under <paramref name="root"/>. Returns null when no index covers that drive yet.
        /// Results are real FileItems (size/dates read for each hit), at most <paramref name="max"/>.
        /// </summary>
        public static List<FileItem> Search(string root, string query, bool showHidden, int max, CancellationToken ct, out bool truncated)
        {
            truncated = false;
            var drive = Path.GetPathRoot(root);
            if (drive == null) return null;
            var v = Acquire(drive);
            if (v == null) return null;
            List<(string path, bool dir)> added;
            Dictionary<string, long> removed;
            lock (_lock)
            {
                added = _added.Where(a => a.path.StartsWith(drive, StringComparison.OrdinalIgnoreCase)).Select(a => (a.path, a.dir)).ToList();
                removed = new Dictionary<string, long>(_removed, StringComparer.OrdinalIgnoreCase);
            }
            try { return SearchIn(v, root, drive, query, showHidden, max, ct, added, removed, out truncated); }
            finally
            {
                Release(v);
                RefreshIfOld(drive);
            }
        }

        private static List<FileItem> SearchIn(Volume v, string root, string drive, string query, bool showHidden, int max, CancellationToken ct,
            List<(string path, bool dir)> added, Dictionary<string, long> removed, out bool truncated)
        {
            truncated = false;
            query = query.Trim();
            var rx = NameQuery.Build(query, out var exq);
            bool Match(ReadOnlySpan<char> name) => rx != null ? NameQuery.IsMatch(rx, exq, name) : name.Contains(query, StringComparison.OrdinalIgnoreCase);

            var rootClean = root.TrimEnd('\\');
            var prefix = rootClean + "\\";
            bool wholeDrive = string.Equals(rootClean + "\\", drive, StringComparison.OrdinalIgnoreCase);
            var removedDirs = removed.Keys.Select(k => k + "\\").ToList();
            bool Gone(string p)
            {
                if (removed.ContainsKey(p)) return true;
                foreach (var d in removedDirs) if (p.StartsWith(d, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }

            var paths = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cache = new Dictionary<int, string>();
            for (int i = 1; i < v.Count; i++)
            {
                if ((i & 0xFFFF) == 0 && ct.IsCancellationRequested) return null;
                if (!showHidden && (v.Flags(i) & FHidden) != 0) continue;
                if (!Match(v.Name(i))) continue;
                var full = FullPath(v, i, cache);
                if (!wholeDrive && !full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (removed.Count > 0 && Gone(full)) continue;
                if (!seen.Add(full)) continue;
                paths.Add(full);
                if (paths.Count >= max) { truncated = true; break; }
            }
            foreach (var a in added)
            {
                if (paths.Count >= max) { truncated = true; break; }
                if (!wholeDrive && !a.path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Match(Path.GetFileName(a.path)) || Gone(a.path) || !seen.Add(a.path)) continue;
                paths.Add(a.path);
            }

            // read size / dates / attributes of each hit (also drops anything deleted meanwhile)
            var items = new List<FileItem>(paths.Count);
            foreach (var p in paths)
            {
                if (ct.IsCancellationRequested) return null;
                var fi = FileSystem.Stat(p, Path.GetDirectoryName(p));
                if (fi != null && (showHidden || !fi.IsHidden)) items.Add(fi);
            }
            return items;
        }
    }
}
