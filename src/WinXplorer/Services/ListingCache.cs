using System;
using System.Collections.Generic;
using WinXplorer.Models;

namespace WinXplorer.Services
{
    /// <summary>
    /// Recently opened folders kept in memory, so going back / reopening a folder shows it instantly.
    /// Every hit is checked against the disk right after it is shown (see PaneViewModel.LoadAsync),
    /// so a stale entry is corrected within moments. Least recently used folders are dropped first.
    /// </summary>
    public static class ListingCache
    {
        private sealed class Entry { public string Key; public List<FileItem> Items; }

        private const int MaxFolders = 60, MaxItems = 150_000, MaxPerFolder = 40_000;
        private static readonly LinkedList<Entry> _lru = new();
        private static readonly Dictionary<string, LinkedListNode<Entry>> _map = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _lock = new();
        private static int _total;

        private static string Key(string path, bool hidden) => (hidden ? "h|" : "v|") + (path ?? "");

        public static bool TryGet(string path, bool hidden, out List<FileItem> items)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(Key(path, hidden), out var node))
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    items = new List<FileItem>(node.Value.Items);
                    return true;
                }
            }
            items = null;
            return false;
        }

        public static void Put(string path, bool hidden, List<FileItem> items)
        {
            if (items == null || items.Count > MaxPerFolder) { Remove(path); return; }
            lock (_lock)
            {
                var key = Key(path, hidden);
                if (_map.TryGetValue(key, out var old)) { _total -= old.Value.Items.Count; _lru.Remove(old); _map.Remove(key); }
                var node = _lru.AddFirst(new Entry { Key = key, Items = new List<FileItem>(items) });
                _map[key] = node;
                _total += items.Count;
                while (_lru.Count > MaxFolders || _total > MaxItems)
                {
                    var last = _lru.Last;
                    _lru.RemoveLast();
                    _map.Remove(last.Value.Key);
                    _total -= last.Value.Items.Count;
                }
            }
        }

        public static void Remove(string path)
        {
            lock (_lock)
                foreach (var hidden in new[] { true, false })
                    if (_map.Remove(Key(path, hidden), out var node)) { _lru.Remove(node); _total -= node.Value.Items.Count; }
        }
    }
}
