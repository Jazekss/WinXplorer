using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Media;
using WinXplorer.Interop;

namespace WinXplorer.Services
{
    /// <summary>An app for "Open with …": a desktop program (exe) or a Windows (Store) app.</summary>
    public sealed class AppEntry
    {
        public string Name { get; init; }
        /// <summary>Command line for CustomCommands: <c>"C:\x\app.exe" "%1"</c> or <c>shell:AppsFolder\&lt;AUMID&gt;</c>.</summary>
        public string Command { get; init; }
        public bool IsStoreApp { get; init; }
        public ImageSource Icon { get; set; }
    }

    /// <summary>
    /// The apps installed on this PC (as in the Start menu) and launching Windows apps with files.
    /// </summary>
    public static class AppCatalog
    {
        public const string AppsFolder = @"shell:AppsFolder\";
        private static List<AppEntry> _cache;

        /// <summary>Installed programs (Start menu shortcuts to an .exe) and Windows apps, with icons. Slow-ish: call off the UI thread.</summary>
        public static List<AppEntry> Load()
        {
            if (_cache != null) return _cache;
            var list = new List<AppEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] skip = { "unins", "uninstall", "setup", "install", "update", "repair", "readme", "help" };

            // desktop programs: the Start menu's shortcuts (all users + this user)
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Environment.GetFolderPath(Environment.SpecialFolder.Programs) })
            {
                IEnumerable<string> lnks;
                try { lnks = Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }); }
                catch { continue; }
                foreach (var lnk in lnks)
                {
                    var name = Path.GetFileNameWithoutExtension(lnk);
                    if (skip.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;
                    string target;
                    try { target = Shell.ResolveShortcut(lnk); } catch { continue; }
                    if (string.IsNullOrEmpty(target) || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target)) continue;
                    var file = Path.GetFileNameWithoutExtension(target);
                    if (skip.Any(s => file.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;
                    if (!seen.Add(target)) continue;
                    ImageSource icon = null;
                    try { icon = IconService.GetPathIcon(target, true); if (icon is System.Windows.Freezable f && f.CanFreeze) f.Freeze(); } catch { }
                    list.Add(new AppEntry { Name = name, Command = $"\"{target}\" \"%1\"", Icon = icon });
                }
            }

            // Windows apps (Photos, Paint, Notepad, Media Player …): the packaged ones in shell:AppsFolder
            try
            {
                dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
                dynamic folder = sh.NameSpace("shell:AppsFolder");
                foreach (dynamic it in folder.Items())
                {
                    string path = it.Path, name = it.Name;
                    if (string.IsNullOrEmpty(path) || !path.Contains('!') || string.IsNullOrEmpty(name)) continue;   // AUMID of a packaged app
                    if (skip.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;
                    if (!seen.Add(path)) continue;
                    ImageSource icon = null;
                    try { icon = Shell.GetThumbnail(AppsFolder + path, 32, Shell.SIIGBF_ICONONLY); icon?.Freeze(); } catch { }
                    list.Add(new AppEntry { Name = name, Command = AppsFolder + path, IsStoreApp = true, Icon = icon });
                }
                Marshal.ReleaseComObject(folder);
                Marshal.ReleaseComObject(sh);
            }
            catch { }

            return _cache = list.OrderBy(a => a.IsStoreApp).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        // ---------------- starting a Windows app with files ----------------
        [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IApplicationActivationManager
        {
            [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string aumid, [MarshalAs(UnmanagedType.LPWStr)] string args, int options, out uint pid);
            [PreserveSig] int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string aumid, IntPtr itemArray, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint pid);
            [PreserveSig] int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string aumid, IntPtr itemArray, out uint pid);
        }
        [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")] private class ApplicationActivationManager { }
        [DllImport("shell32.dll")] private static extern int SHCreateShellItemArrayFromIDLists(uint cidl, IntPtr[] pidls, out IntPtr array);
        [DllImport("ole32.dll")] private static extern void CoTaskMemFree(IntPtr p);

        /// <summary>Opens the files (or just the app, when there are none) in a Windows app. False when Windows refused.</summary>
        public static bool Activate(string aumid, IList<string> paths)
        {
            var pidls = new List<IntPtr>();
            IntPtr arr = IntPtr.Zero;
            try
            {
                var m = (IApplicationActivationManager)new ApplicationActivationManager();
                if (paths == null || paths.Count == 0) return m.ActivateApplication(aumid, "", 0, out _) == 0;
                foreach (var p in paths)
                    if (Shell.SHParseDisplayName(p, IntPtr.Zero, out var pidl, 0, out _) == 0 && pidl != IntPtr.Zero) pidls.Add(pidl);
                if (pidls.Count == 0 || SHCreateShellItemArrayFromIDLists((uint)pidls.Count, pidls.ToArray(), out arr) != 0) return false;
                return m.ActivateForFile(aumid, arr, "open", out _) == 0;
            }
            catch { return false; }
            finally
            {
                if (arr != IntPtr.Zero) Marshal.Release(arr);
                foreach (var p in pidls) CoTaskMemFree(p);
            }
        }
    }
}
