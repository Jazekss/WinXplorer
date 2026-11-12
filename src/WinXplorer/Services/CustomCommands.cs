using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Media;
using WinXplorer.Models;

namespace WinXplorer.Services
{
    /// <summary>
    /// The user's own context-menu commands: a name and a command line, e.g. <c>code "%1"</c>.
    /// %1 = the (first) selected item, %* = all selected items (quoted), %d = the current folder.
    /// Without any placeholder the selected item is added at the end.
    /// </summary>
    public static class CustomCommands
    {
        public const string Prefix = "custom:";
        /// <summary>Marker at the end of a command line: start the program without passing the selection.</summary>
        public const string NoItem = "%-";

        public static List<CustomCommand> All => AppState.Settings.CustomCommands ??= new List<CustomCommand>();

        public static CustomCommand Get(string id) => All.FirstOrDefault(c => c.Id == id);

        public static CustomCommand Add(string label, string command)
        {
            var c = new CustomCommand { Id = Prefix + Guid.NewGuid().ToString("N")[..8], Label = label, Command = command };
            All.Add(c);
            return c;
        }

        public static void Remove(string id)
        {
            All.RemoveAll(c => c.Id == id);
            var s = AppState.Settings;
            s.ItemMenu?.RemoveAll(e => e.Id == id);
            s.BackgroundMenu?.RemoveAll(e => e.Id == id);
            s.ItemQuickRow?.Remove(id);
            s.BackgroundQuickRow?.Remove(id);
        }

        /// <summary>The program a command line starts: "C:\x\y.exe" args, or the first word (looked up on PATH).</summary>
        public static (string exe, string args) Split(string commandLine)
        {
            var s = (commandLine ?? "").Trim();
            if (s.StartsWith('"'))
            {
                int end = s.IndexOf('"', 1);
                return end < 0 ? (s.Trim('"'), "") : (s[1..end], s[(end + 1)..].Trim());
            }
            int sp = s.IndexOf(' ');
            return sp < 0 ? (s, "") : (s[..sp], s[(sp + 1)..].Trim());
        }

        public static string ResolveExe(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return exe;
            exe = Environment.ExpandEnvironmentVariables(exe);
            if (File.Exists(exe)) return exe;
            return FileOps.FindOnPath(exe) ?? exe;
        }

        public static ImageSource IconOf(CustomCommand c)
        {
            try
            {
                var app = Split(c.Command).exe;
                if (app.StartsWith(AppCatalog.AppsFolder, StringComparison.OrdinalIgnoreCase))
                    return Interop.Shell.GetThumbnail(app, 16, Interop.Shell.SIIGBF_ICONONLY);
                var exe = ResolveExe(Split(c.Command).exe);
                return File.Exists(exe) ? IconService.GetPathIcon(exe) : null;
            }
            catch { return null; }
        }

        public static void Run(CustomCommand c, IList<string> paths, string folder)
        {
            if (c == null) return;
            static string Q(string p) => "\"" + p + "\"";
            // a Windows (Store) app: Windows starts it with the files itself
            var appExe = Split(c.Command).exe;
            if (appExe.StartsWith(AppCatalog.AppsFolder, StringComparison.OrdinalIgnoreCase))
            {
                var aumid = appExe[AppCatalog.AppsFolder.Length..];
                var files = paths?.Where(File.Exists).ToList() ?? new List<string>();
                if (!AppCatalog.Activate(aumid, files))
                    try { Process.Start(new ProcessStartInfo("explorer.exe", appExe) { UseShellExecute = true }); } catch (Exception ex) { Notify.Show(c.Label + ": " + ex.Message); }
                return;
            }
            var first = paths?.Count > 0 ? paths[0] : folder ?? "";
            var all = paths?.Count > 0 ? string.Join(" ", paths.Select(Q)) : Q(folder ?? "");
            var (exe, args) = Split(c.Command);
            // nothing is added when the command passes the item itself, opens a fixed link, or says "%-" (nothing)
            bool placeholder = args.Contains("%1") || args.Contains("%*") || args.Contains("%d", StringComparison.OrdinalIgnoreCase)
                               || args.Contains(NoItem) || args.Contains("://");
            args = args.Replace(NoItem, "").Replace("%*", all).Replace("%1", first).Replace("%d", folder ?? "").Replace("%D", folder ?? "").Trim();
            if (!placeholder) args = (args + " " + Q(first)).Trim();
            args = Environment.ExpandEnvironmentVariables(args);
            try
            {
                var psi = new ProcessStartInfo(ResolveExe(exe), args) { UseShellExecute = true };
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) psi.WorkingDirectory = folder;
                Process.Start(psi);
            }
            catch (Exception ex) { Notify.Show(c.Label + ": " + ex.Message); }
        }
    }
}
