using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using WinXplorer.Interop;

namespace WinXplorer.Services
{
    /// <summary>Short message shown at the bottom of the window ("Deleted 3 items – Ctrl+Z to undo").</summary>
    public static class Notify
    {
        public static event Action<string> Message;
        public static void Show(string text) => Application.Current?.Dispatcher.BeginInvoke(() => Message?.Invoke(text));
    }

    /// <summary>
    /// Undo (Ctrl+Z) for what WinXplorer did: rename, move, copy, new folder/file and delete to the Recycle Bin.
    /// Permanent deletes can't be undone. The history lives in memory for this window.
    /// </summary>
    public static class UndoService
    {
        private abstract class Op { public string Text; public abstract void Undo(); }

        private sealed class RenameOp : Op
        {
            public string From, To;
            public override void Undo()
            {
                if (!Native.MoveFileExW(To, From, 0)) throw new System.ComponentModel.Win32Exception();
            }
        }

        private sealed class RenameManyOp : Op
        {
            public List<(string from, string to)> Items;
            public override void Undo()
            {
                var errors = new List<string>();
                FileOps.RenameBatch(Items.Select(i => (i.to, i.from)).ToList(), errors, record: false);
                if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors.Take(5)));
            }
        }

        private sealed class MoveOp : Op
        {
            public List<(string from, string to)> Items;
            public override void Undo()
            {
                // move every item back to the folder it came from (same volume = instant)
                foreach (var g in Items.Where(i => Exists(i.to)).GroupBy(i => Path.GetDirectoryName(i.from)))
                    ShellOp(Native.FO_MOVE, g.Select(i => i.to).ToArray(), g.Key);
            }
        }

        private sealed class CreatedOp : Op
        {
            public List<string> Paths;
            public override void Undo()
            {
                var p = Paths.Where(Exists).ToArray();
                if (p.Length > 0) ShellOp(Native.FO_DELETE, p, null);
            }
        }

        private sealed class RecycleOp : Op
        {
            public List<string> Paths;
            public DateTime When;
            public override void Undo()
            {
                int restored = RecycleBin.Restore(Paths, When);
                if (restored == 0) throw new InvalidOperationException(Loc.T("UndoNotFound"));
            }
        }

        private static readonly LinkedList<Op> _stack = new();
        private static readonly object _lock = new();
        private const int Max = 100;

        public static bool CanUndo { get { lock (_lock) return _stack.Count > 0; } }

        private static void Push(Op op)
        {
            lock (_lock)
            {
                _stack.AddLast(op);
                while (_stack.Count > Max) _stack.RemoveFirst();
            }
        }

        private static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

        private static string Name(string p) => Path.GetFileName(p.TrimEnd('\\'));

        private static string Count(IReadOnlyCollection<string> items) => items.Count == 1 ? "\"" + Name(items.First()) + "\"" : Loc.Items(items.Count);

        // ---------- recording ----------
        public static void Renamed(string from, string to) =>
            Push(new RenameOp { From = from, To = to, Text = Loc.F("UndoRename", Name(from), Name(to)) });

        public static void RenamedMany(List<(string from, string to)> items)
        {
            if (items.Count == 1) { Renamed(items[0].from, items[0].to); return; }
            if (items.Count > 0) Push(new RenameManyOp { Items = items, Text = Loc.F("UndoRenameMany", Loc.Items(items.Count)) });
        }

        public static void Created(string path) =>
            Push(new CreatedOp { Paths = new() { path }, Text = Loc.F("UndoCreate", Name(path)) });

        public static void Recycled(IReadOnlyList<string> paths, DateTime when) =>
            Push(new RecycleOp { Paths = paths.ToList(), When = when, Text = Loc.F("UndoDelete", Count(paths)) });

        /// <summary>Called after a copy/move finished: works out what ended up where by comparing the target folder before and after.</summary>
        public static void CopiedOrMoved(string[] src, string destDir, bool move, HashSet<string> before)
        {
            HashSet<string> after;
            try { after = Listing(destDir); } catch { return; }
            var added = after.Where(n => !before.Contains(n)).ToList();
            if (move)
            {
                var pairs = new List<(string, string)>();
                foreach (var s in src)
                {
                    if (Exists(s)) continue; // not moved (cancelled / skipped)
                    var name = Name(s);
                    var target = Path.Combine(destDir, name);
                    if (Exists(target)) pairs.Add((s.TrimEnd('\\'), target));
                }
                if (pairs.Count > 0) Push(new MoveOp { Items = pairs, Text = Loc.F("UndoMove", Count(pairs.Select(p => p.Item1).ToList())) });
            }
            else if (added.Count > 0)
            {
                var paths = added.Select(n => Path.Combine(destDir, n)).ToList();
                Push(new CreatedOp { Paths = paths, Text = Loc.F("UndoCopy", Count(paths)) });
            }
        }

        public static HashSet<string> Listing(string dir)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in Directory.EnumerateFileSystemEntries(dir)) set.Add(Path.GetFileName(e));
            return set;
        }

        // ---------- undo ----------
        public static void Undo()
        {
            Op op;
            lock (_lock)
            {
                if (_stack.Count == 0) { Notify.Show(Loc.T("NothingToUndo")); return; }
                op = _stack.Last.Value;
                _stack.RemoveLast();
            }
            var t = new Thread(() =>
            {
                try { op.Undo(); Notify.Show(Loc.T("Undone") + ": " + op.Text); }
                catch (Exception ex) { Notify.Show(Loc.T("UndoFailed") + ": " + ex.Message); }
            }) { IsBackground = true, Name = "Undo" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        private static void ShellOp(uint func, string[] from, string to)
        {
            var op = new Native.SHFILEOPSTRUCT
            {
                hwnd = FileOps.MainHwnd,
                wFunc = func,
                pFrom = string.Join("\0", from) + "\0\0",
                pTo = to == null ? null : to + "\0\0",
                fFlags = (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMATION | Native.FOF_NOCONFIRMMKDIR),
            };
            int r = Native.SHFileOperationW(ref op);
            if (r != 0 && op.fAnyOperationsAborted == 0) throw new System.ComponentModel.Win32Exception(r);
        }
    }

    /// <summary>
    /// Puts items back from the Recycle Bin. Each deleted item is stored as $Rxxxx in &lt;drive&gt;\$Recycle.Bin\&lt;user SID&gt;,
    /// with a matching $Ixxxx file that holds the original path and deletion time.
    /// </summary>
    public static class RecycleBin
    {
        public static int Restore(IEnumerable<string> originals, DateTime deletedAfter)
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            if (sid == null) return 0;
            int n = 0;
            foreach (var g in originals.GroupBy(p => Path.GetPathRoot(p), StringComparer.OrdinalIgnoreCase))
            {
                var bin = Path.Combine(g.Key, "$Recycle.Bin", sid);
                if (!Directory.Exists(bin)) continue;
                var entries = new List<(string info, string original, DateTime when)>();
                foreach (var i in Directory.EnumerateFiles(bin, "$I*"))
                    if (Parse(i) is { } e) entries.Add((i, e.path, e.when));
                foreach (var orig in g)
                {
                    var clean = orig.TrimEnd('\\');
                    var hit = entries.Where(e => string.Equals(e.original.TrimEnd('\\'), clean, StringComparison.OrdinalIgnoreCase) && e.when >= deletedAfter.AddMinutes(-1))
                                     .OrderByDescending(e => e.when).FirstOrDefault();
                    if (hit.info == null) continue;
                    var data = Path.Combine(bin, "$R" + Path.GetFileName(hit.info)[2..]);
                    if (File.Exists(clean) || Directory.Exists(clean)) continue; // something new is there – don't overwrite
                    Directory.CreateDirectory(Path.GetDirectoryName(clean)!);
                    if (Directory.Exists(data)) Directory.Move(data, clean);
                    else if (File.Exists(data)) File.Move(data, clean);
                    else continue;
                    try { File.Delete(hit.info); } catch { }
                    n++;
                }
            }
            return n;
        }

        private static (string path, DateTime when)? Parse(string infoFile)
        {
            try
            {
                var b = File.ReadAllBytes(infoFile);
                if (b.Length < 28) return null;
                long version = BitConverter.ToInt64(b, 0);
                var when = DateTime.FromFileTime(BitConverter.ToInt64(b, 16));
                string path;
                if (version >= 2)
                {
                    int len = BitConverter.ToInt32(b, 24);
                    path = System.Text.Encoding.Unicode.GetString(b, 28, Math.Min(len * 2, b.Length - 28));
                }
                else path = System.Text.Encoding.Unicode.GetString(b, 24, Math.Min(520, b.Length - 24));
                return (path.TrimEnd('\0'), when);
            }
            catch { return null; }
        }
    }
}
