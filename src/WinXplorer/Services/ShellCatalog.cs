using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WinXplorer.Interop;

namespace WinXplorer.Services
{
    /// <summary>What Windows puts into its context menu (7-Zip, WinRAR, Send to…), read once for the menu editors.</summary>
    internal static class ShellCatalog
    {
        /// <summary>Items Windows offers for a sample file and a folder (<paramref name="background"/> = for the folder background).</summary>
        public static List<ShellMenuItem> Load(IntPtr hwnd, bool background)
        {
            var all = new List<ShellMenuItem>();
            try
            {
                var tmpDir = Path.Combine(Path.GetTempPath(), "WinXplorer");
                Directory.CreateDirectory(tmpDir);
                var sampleFile = Path.Combine(tmpDir, "sample.txt");
                if (!File.Exists(sampleFile)) File.WriteAllText(sampleFile, "WinXplorer");
                var sampleDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

                IEnumerable<(string[] paths, string folder)> sources = !background
                    ? new[] { (new[] { sampleFile }, tmpDir), (new[] { sampleDir }, Path.GetDirectoryName(sampleDir)) }
                    : new[] { ((string[])null, sampleDir) };
                foreach (var (paths, folder) in sources)
                {
                    using var m = ShellMenu.Create(hwnd, paths, folder, true);
                    if (m == null) continue;
                    foreach (var i in m.ReadItems(1))
                        if (!i.IsSeparator && !Duplicate(i) && !all.Any(a => a.Key == i.Key || a.Text == i.Text)) all.Add(i);
                }
            }
            catch { }
            return all;
        }

        /// <summary>Windows items WinXplorer's own menu already has (copy, paste, properties...): not offered again.</summary>
        public static bool Duplicate(ShellMenuItem i)
        {
            var v = (i.Verb ?? "").ToLowerInvariant();
            if (v is "copy" or "cut" or "paste" or "properties" or "delete" or "rename" or "link" or "pastelink" or "open" or "opennewwindow" or "opennewtab" or "copyaspath")
                return true;
            var t = (i.Text ?? "").Replace("&", "").Trim().ToLowerInvariant();
            return t is "copy" or "cut" or "paste" or "properties" or "delete" or "rename" or "kopēt" or "izgriezt" or "ielīmēt" or "rekvizīti" or "dzēst" or "pārdēvēt";
        }
    }
}
