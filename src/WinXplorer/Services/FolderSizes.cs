using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using WinXplorer.Models;

namespace WinXplorer.Services
{
    /// <summary>
    /// Optional (Settings › Show folder sizes): the size column shows how much each folder holds. Counted on a
    /// low-priority background thread, one folder after another, cancelled when the folder view changes; results
    /// are cached for 10 minutes. Network paths are skipped (too slow). A folder that takes over 15 s shows "> size".
    /// </summary>
    public static class FolderSizes
    {
        private static readonly ConcurrentDictionary<string, (long size, bool partial, DateTime at)> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<object, CancellationTokenSource> Running = new();

        public static void Fill(object owner, IEnumerable<FileItem> items)
        {
            if (Running.TryRemove(owner, out var old)) old.Cancel();
            if (!AppState.Settings.ShowFolderSizes || items == null) return;
            var todo = new List<FileItem>();
            foreach (var i in items)
            {
                if (!i.IsDirectory || i.IsDrive || i.InArchive || i.FullPath.StartsWith(@"\\")) continue;
                if (Cache.TryGetValue(i.FullPath, out var c) && DateTime.UtcNow - c.at < TimeSpan.FromMinutes(10)) i.SetDirSize(c.size, c.partial);
                else todo.Add(i);
            }
            if (todo.Count == 0) return;
            var cts = new CancellationTokenSource();
            Running[owner] = cts;
            var ui = Application.Current?.Dispatcher;
            new Thread(() =>
            {
                foreach (var item in todo)
                {
                    if (cts.IsCancellationRequested) return;
                    var (size, partial) = Count(item.FullPath, cts.Token);
                    if (cts.IsCancellationRequested) return;
                    Cache[item.FullPath] = (size, partial, DateTime.UtcNow);
                    ui?.BeginInvoke(() => item.SetDirSize(size, partial));
                }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "FolderSizes" }.Start();
        }

        private static (long, bool) Count(string path, CancellationToken ct)
        {
            long bytes = 0; int n = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", opt))
                {
                    bytes += f.Length;
                    if ((++n & 511) == 0 && (ct.IsCancellationRequested || sw.ElapsedMilliseconds > 15000)) return (bytes, true);
                }
            }
            catch { }
            return (bytes, false);
        }
    }
}
