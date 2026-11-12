using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace WinXplorer.Services
{
    /// <summary>
    /// Search box syntax (names only, no buttons – the magnifier's tooltip lists it):
    ///   photo        name contains "photo" (any case)
    ///   *.png        wildcards: * any text, ? one character – whole name
    ///   ^Icons       case-sensitive ("Icons", not "icons")
    ///   "Icons"      exact name (with or without its extension)
    ///   a|b          either one: *.png|*.jpg
    ///   -tmp         leave out names containing "tmp" (after another term: *.log -old)
    ///   /regex/      regular expression
    /// Returns null for a plain "contains" search (the fast path).
    /// </summary>
    public static class NameQuery
    {
        public static Regex Build(string query, out Regex exclude)
        {
            exclude = null;
            query = (query ?? "").Trim();
            if (query.Length == 0) return null;
            if (query.Length > 2 && query[0] == '/' && query[^1] == '/')
            {
                try { return new Regex(query[1..^1], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)); }
                catch { return new Regex(Regex.Escape(query), RegexOptions.IgnoreCase); }
            }
            if (query.IndexOfAny(new[] { '*', '?', '^', '"', '|' }) < 0 && !query.Contains(" -") && !query.StartsWith('-')) return null;

            // " -word" excludes
            var excl = new List<string>();
            var words = query.Split(' ');
            var keep = new List<string>();
            foreach (var w in words)
                if (w.Length > 1 && w[0] == '-') excl.Add(Part(w[1..], contains: true)); else keep.Add(w);
            if (excl.Count > 0) exclude = new Regex(string.Join("|", excl), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var rest = string.Join(" ", keep).Trim();
            if (rest.Length == 0) return new Regex("", RegexOptions.None);   // only excludes: everything else
            var alts = rest.Split('|').Select(a => a.Trim()).Where(a => a.Length > 0).Select(a => Part(a, contains: false));
            return new Regex(string.Join("|", alts), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static string Part(string a, bool contains)
        {
            bool cs = a.StartsWith('^');
            if (cs) a = a[1..];
            string body;
            if (a.Length > 1 && a[0] == '"' && a[^1] == '"')
            {
                var n = Regex.Escape(a[1..^1]);
                body = "^" + n + @"(\.[^.]*)?$";
            }
            else if (a.IndexOfAny(new[] { '*', '?' }) >= 0)
                body = "^" + Regex.Escape(a).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            else body = Regex.Escape(a);
            return cs ? "(?-i:" + body + ")" : "(?:" + body + ")";
        }

        /// <summary>One matcher for the three search paths (current folder, index, folder walk).</summary>
        public static Func<string, bool> Matcher(string query)
        {
            var q = (query ?? "").Trim();
            var rx = Build(q, out var ex);
            if (rx == null) return n => n.Contains(q, StringComparison.OrdinalIgnoreCase);
            return n => rx.IsMatch(n) && (ex == null || !ex.IsMatch(n));
        }

        public static bool IsMatch(Regex rx, Regex ex, ReadOnlySpan<char> name) => rx.IsMatch(name) && (ex == null || !ex.IsMatch(name));
    }
}
