using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WinXplorer.Services
{
    /// <summary>
    /// Counts how often folders are opened, for the sidebar's "Frequent folders" block.
    /// Kept small: at most 300 folders; older counts fade (halved) when the table grows.
    /// </summary>
    public static class FolderUsage
    {
        public static event Action Changed;
        private static Dictionary<string, int> Visits
        {
            get
            {
                var s = AppState.Settings;
                // the settings file loads it case-sensitive: paths are not
                if (s.FolderVisits == null || s.FolderVisits.Comparer != StringComparer.OrdinalIgnoreCase)
                {
                    var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    if (s.FolderVisits != null) foreach (var kv in s.FolderVisits) d[kv.Key] = (d.TryGetValue(kv.Key, out var n) ? n : 0) + kv.Value;
                    s.FolderVisits = d;
                }
                return s.FolderVisits;
            }
        }

        public static void Record(string path)
        {
            if (string.IsNullOrEmpty(path) || path == FileSystem.ThisPC || path.Length <= 3 || Archives.Split(path) != null) return;
            if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || path.StartsWith("::")) return;
            var v = Visits;
            v[path] = v.TryGetValue(path, out var n) ? n + 1 : 1;
            if (v.Count > 300)
            {
                foreach (var k in v.Keys.ToList()) { v[k] /= 2; if (v[k] == 0) v.Remove(k); }
            }
            Changed?.Invoke();
        }

        public static void Forget(string path)
        {
            if (path != null && Visits.Remove(path)) Changed?.Invoke();
        }

        /// <summary>The most used folders (opened at least twice), skipping the ones pinned in the sidebar.</summary>
        public static List<string> Top(int count, ICollection<string> skip)
        {
            return Visits.Where(kv => kv.Value >= 2 && !skip.Contains(kv.Key))
                .OrderByDescending(kv => kv.Value)
                .Select(kv => kv.Key)
                // local folders that were deleted drop out; network paths aren't checked (could hang)
                .Where(p => p.StartsWith(@"\\") || SafeExists(p))
                .Take(count).ToList();
        }

        private static bool SafeExists(string p) { try { return Directory.Exists(p); } catch { return false; } }
    }
}
