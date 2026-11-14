using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Threading;

namespace WinXplorer.Services
{
    /// <summary>
    /// "WinXplorer opens folders" covers double-clicks and links through the registry, but some programs open a folder
    /// window inside Explorer directly – torrent clients, browsers' "Show in folder", installers (Windows'
    /// SHOpenFolderAndSelectItems never asks the registry). While WinXplorer runs, such a new Explorer window showing an
    /// ordinary folder is closed and the folder (with the file that was to be selected) opens in WinXplorer instead.
    /// Explorer windows for Home, Control Panel, Recycle Bin, zip folders… are left alone, and so is Explorer when it is
    /// asked for on purpose (WinXplorer's "Open in Explorer"). Only one WinXplorer process watches (a named mutex).
    /// </summary>
    public static class ExplorerWatch
    {
        private static bool _started;
        private static WinEventProc _proc;   // kept alive: Windows calls it
        private static IntPtr _hook;
        private static Dispatcher _ui;

        private static string AllowFile => Path.Combine(AppState.DataDir, "explorer-allow");

        /// <summary>The next Explorer window (within a few seconds) is wanted – leave it alone. Works across WinXplorer processes.</summary>
        public static void AllowNext()
        {
            try { Directory.CreateDirectory(AppState.DataDir); File.WriteAllText(AllowFile, DateTime.UtcNow.Ticks.ToString()); } catch { }
        }

        private static bool Allowed()
        {
            try { return long.TryParse(File.ReadAllText(AllowFile), out var t) && DateTime.UtcNow.Ticks - t < TimeSpan.FromSeconds(8).Ticks; }
            catch { return false; }
        }

        /// <summary>Called on the UI thread once the first window is up; the hook is installed when this process gets the mutex.</summary>
        public static void Start()
        {
            if (_started) return;
            _started = true;
            _ui = Dispatcher.CurrentDispatcher;
            var t = new Thread(() =>
            {
                var m = new Mutex(false, @"Local\WinXplorer.ExplorerWatch");
                try { m.WaitOne(); } catch (AbandonedMutexException) { }   // the previous watcher exited: ours now
                _ui.BeginInvoke(Install);
                Thread.Sleep(Timeout.Infinite);   // holds the mutex until the process ends
                GC.KeepAlive(m);
            }) { IsBackground = true, Name = "ExplorerWatch" };
            t.Start();
        }


        private static void Install()
        {
            _proc = OnEvent;
            _hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        }

        private static void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return;
            var cls = new StringBuilder(32);
            if (GetClassName(hwnd, cls, cls.Capacity) == 0 || cls.ToString() != "CabinetWClass") return;
            if (Allowed() || !DefaultFileManager.IsActive()) return;
            TakeOver(hwnd);
        }

        /// <summary>Waits until Explorer knows what the window shows, then hands ordinary folders to WinXplorer.</summary>
        private static void TakeOver(IntPtr hwnd)
        {
            int tries = 0;
            string path = null;
            var timer = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = TimeSpan.FromMilliseconds(150) };
            timer.Tick += (_, _) =>
            {
                if (++tries > 25 || !IsWindow(hwnd)) { timer.Stop(); return; }
                dynamic win = Find(hwnd);
                if (win == null) return;
                try
                {
                    if (path == null)
                    {
                        string p = win.Document.Folder.Self.Path;
                        if (string.IsNullOrEmpty(p)) return;
                        // Home, This PC, Control Panel, Recycle Bin, libraries, zip folders: Explorer's business
                        if (p.StartsWith("::") || p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(p)) { timer.Stop(); return; }
                        path = p;
                        return;   // one more tick: the file to be selected is selected a moment after the window shows
                    }
                    timer.Stop();
                    string select = null;
                    try
                    {
                        foreach (dynamic it in win.Document.SelectedItems()) { select = it.Path; break; }
                    }
                    catch { }
                    try { win.Quit(); } catch { PostMessage(hwnd, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero); }
                    var target = select != null && (File.Exists(select) || Directory.Exists(select)) ? select : path;
                    var arg = target.EndsWith("\\") ? target + "\\" : target;   // "C:\\" – a lone backslash would escape the quote
                    Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "\"" + arg + "\"") { UseShellExecute = false });
                }
                catch { timer.Stop(); }
            };
            timer.Start();
        }

        private static object Find(IntPtr hwnd)
        {
            try
            {
                var t = Type.GetTypeFromProgID("Shell.Application");
                if (t == null) return null;
                dynamic shell = Activator.CreateInstance(t);
                foreach (dynamic w in shell.Windows())
                {
                    try { if (new IntPtr((long)w.HWND) == hwnd) return w; } catch { }
                }
            }
            catch { }
            return null;
        }

        private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        private const uint EVENT_OBJECT_SHOW = 0x8002, WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;
        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    }
}
