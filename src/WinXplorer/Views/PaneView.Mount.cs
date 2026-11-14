using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using WinXplorer.Interop;
using WinXplorer.Models;
using WinXplorer.Services;

namespace WinXplorer.Views
{
    /// <summary>
    /// Disc / disk images (iso, img, vhd, mdf…): Windows' own "Mount" plus whatever mounting programs put into the
    /// Windows menu (DAEMON Tools, Alcohol, WinCDEmu, PowerISO…). The new drive opens in a tab once it appears.
    /// </summary>
    public partial class PaneView
    {
        // words mounting programs use in their menu items (English + the languages WinXplorer speaks)
        private static readonly string[] MountWords =
            { "mount", "montēt", "montē ", "монтир", "смонтир", "bereitstellen", "einbinden", "zamontuj", "montuo", "monteeri", "montera", "monter", "monte " };

        private static bool IsMountItem(ShellMenuItem i)
        {
            if (i.IsSeparator || i.IsDisabled || i.Children != null) return false;
            var v = (i.Verb ?? "").ToLowerInvariant();
            if (v is "mount") return true;
            if (v.Contains("unmount") || v.Contains("eject")) return false;
            var t = (i.Text ?? "").Replace("&", "").ToLowerInvariant();
            return MountWords.Any(w => t.Contains(w)) && !t.Contains("unmount") && !t.Contains("eject");
        }

        /// <summary>Mount commands Windows' menu offers for <paramref name="path"/>; <paramref name="shell"/> must stay alive until one is run.</summary>
        private List<MenuItem> MountItems(string path, out ShellMenu shell)
        {
            var list = new List<MenuItem>();
            var hwnd = MainWindow.Instance.Hwnd;
            shell = ShellMenu.Create(hwnd, new[] { path }, Path.GetDirectoryName(path), false);
            if (shell == null) return list;
            var m = shell;
            void Walk(IEnumerable<ShellMenuItem> items, string prefix)
            {
                foreach (var i in items)
                {
                    if (i.Children != null) { Walk(i.Children, (i.Text ?? "").Replace("&", "")); continue; }
                    if (!IsMountItem(i)) continue;
                    var text = (i.Text ?? "").Replace("&", "");
                    // a program's own submenu ("DAEMON Tools Lite › Mount"): say whose it is
                    if (!string.IsNullOrEmpty(prefix) && !text.Contains(prefix, StringComparison.OrdinalIgnoreCase)) text = prefix + " › " + text;
                    var id = i.Id;
                    var mi = new MenuItem { Header = text };
                    mi.Icon = i.Icon != null ? new Image { Source = i.Icon, Width = 16, Height = 16 }
                        : new TextBlock { Text = "", Style = (Style)Application.Current.Resources["Glyph"], FontSize = 14 };
                    mi.Click += (_, _) =>
                    {
                        var before = DriveLetters();
                        Native.GetCursorPos(out var pt);
                        m.Invoke(hwnd, id, pt.X, pt.Y);
                        OpenNewDrive(before);
                    };
                    if ((i.Verb ?? "").Equals("mount", StringComparison.OrdinalIgnoreCase)) list.Insert(0, mi); else list.Add(mi);   // Windows' own first
                }
            }
            try { Walk(shell.ReadItems(3), null); } catch { }
            return list;
        }

        private static HashSet<string> DriveLetters()
        {
            try { return new HashSet<string>(DriveInfo.GetDrives().Select(d => d.Name), StringComparer.OrdinalIgnoreCase); }
            catch { return new HashSet<string>(); }
        }

        /// <summary>After mounting: open the drive that appears within ~20 s in a new tab.</summary>
        private static void OpenNewDrive(HashSet<string> before)
        {
            int ticks = 0;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            t.Tick += (_, _) =>
            {
                if (++ticks > 40) { t.Stop(); return; }
                var added = DriveLetters().Except(before, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (added == null) return;
                t.Stop();
                MainWindow.Instance?.OpenInNewTab(added, true);
            };
            t.Start();
        }

        /// <summary>Double-click on a disk image: mount it (Windows or an installed mounting program) or choose an app.</summary>
        private void OpenDiskImage(FileItem item)
        {
            var mounts = MountItems(item.FullPath, out var shell);
            if (mounts.Count == 0) { shell?.Dispose(); FileOps.OpenWith(item.FullPath); return; }
            var cm = new ContextMenu();
            foreach (var m in mounts) cm.Items.Add(m);
            cm.Items.Add(new Separator());
            cm.Items.Add(MainWindow.MI(Loc.T("OpenWith"), "", () => FileOps.OpenWith(item.FullPath)));
            cm.Closed += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => shell?.Dispose());
            cm.Placement = PlacementMode.MousePoint;
            cm.IsOpen = true;
        }

        /// <summary>The "Mount" entry of WinXplorer's menu: one item, or a submenu when several programs can mount.</summary>
        private MenuItem MountMenuItem(string path, ContextMenu owner, string label, string glyph)
        {
            var mounts = MountItems(path, out var shell);
            if (mounts.Count == 0) { shell?.Dispose(); return null; }
            owner.Closed += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => shell?.Dispose());
            if (mounts.Count == 1) return mounts[0];
            var sub = MainWindow.MI(label, glyph, null);
            foreach (var m in mounts) sub.Items.Add(m);
            return sub;
        }
    }
}
