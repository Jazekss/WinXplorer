using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WinXplorer.Interop;
using WinXplorer.Services;

namespace WinXplorer.Views
{
    /// <summary>
    /// "Scan with antivirus" for everything selected: the scan item the installed antivirus puts into Windows' menu
    /// (Microsoft Defender, ESET, Avast, Kaspersky, Malwarebytes…). When Windows' menu has none, Microsoft Defender's
    /// own command-line scanner runs in a console window that shows the result.
    /// </summary>
    public partial class PaneView
    {
        private static readonly string[] ScanWords =
            { "scan", "defender", "antivir", "skenēt", "skenē", "сканир", "проверить", "prüfen", "scannen", "skanuj", "nuskaity", "skanni", "kontroll", "skanna", "skann" };

        private static bool IsScanItem(ShellMenuItem i)
        {
            if (i.IsSeparator || i.IsDisabled || i.Children != null) return false;
            var t = (i.Text ?? "").Replace("&", "").ToLowerInvariant();
            return ScanWords.Any(w => t.Contains(w)) && !t.Contains("scanner") && !t.Contains("twain");
        }

        /// <summary>The scan entry (one item, or a submenu when several scanners are installed); null when nothing can scan.</summary>
        private MenuItem ScanMenuItem(string[] paths, string folder, ContextMenu owner, string label, string glyph)
        {
            var hwnd = MainWindow.Instance.Hwnd;
            var list = new List<MenuItem>();
            var shell = ShellMenu.Create(hwnd, paths, folder, false);
            if (shell != null)
            {
                var m = shell;
                void Walk(IEnumerable<ShellMenuItem> items, string prefix)
                {
                    foreach (var i in items)
                    {
                        if (i.Children != null) { Walk(i.Children, (i.Text ?? "").Replace("&", "")); continue; }
                        if (!IsScanItem(i)) continue;
                        var text = (i.Text ?? "").Replace("&", "").Trim();
                        if (!string.IsNullOrEmpty(prefix) && !text.Contains(prefix, StringComparison.OrdinalIgnoreCase)) text = prefix + " › " + text;
                        var id = i.Id;
                        var mi = new MenuItem { Header = text };
                        if (i.Icon != null) mi.Icon = new Image { Source = i.Icon, Width = 16, Height = 16 };
                        mi.Click += (_, _) => { Native.GetCursorPos(out var pt); m.Invoke(hwnd, id, pt.X, pt.Y); };
                        list.Add(mi);
                    }
                }
                try { Walk(shell.ReadItems(3), null); } catch { }
                owner.Closed += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => shell.Dispose());
            }
            if (list.Count == 0 && DefenderCli() is string cli)
                list.Add(MainWindow.MI(Loc.T("ScanDefender"), glyph, () => ScanWithDefender(cli, paths)));
            if (list.Count == 0) return null;
            if (list.Count == 1)
            {
                list[0].Icon ??= new TextBlock { Text = glyph, Style = (Style)Application.Current.Resources["Glyph"], FontSize = 14 };
                return list[0];
            }
            var sub = MainWindow.MI(label, glyph, null);
            foreach (var x in list) sub.Items.Add(x);
            return sub;
        }

        /// <summary>MpCmdRun.exe of Microsoft Defender (the newest platform version first), or null.</summary>
        private static string DefenderCli()
        {
            try
            {
                var plat = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows Defender\Platform");
                if (Directory.Exists(plat))
                    foreach (var d in Directory.GetDirectories(plat).OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase))
                        if (File.Exists(Path.Combine(d, "MpCmdRun.exe"))) return Path.Combine(d, "MpCmdRun.exe");
                var pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Windows Defender\MpCmdRun.exe");
                return File.Exists(pf) ? pf : null;
            }
            catch { return null; }
        }

        /// <summary>Scans each item with Defender in one console window, which stays open with the result.</summary>
        private static void ScanWithDefender(string cli, string[] paths)
        {
            var cmds = string.Join(" & ", paths.Select(p => $"\"{cli}\" -Scan -ScanType 3 -File \"{p.TrimEnd('\\')}\""));
            try { Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{cmds} & echo. & pause\"") { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "WinXplorer"); }
        }
    }
}
