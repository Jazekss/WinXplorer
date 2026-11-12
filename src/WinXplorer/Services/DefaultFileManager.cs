using System;
using Microsoft.Win32;

namespace WinXplorer.Services
{
    /// <summary>
    /// "WinXplorer opens folders": double-click on a folder or drive, folder links and Win+E start WinXplorer
    /// instead of Windows Explorer. Only the current user's registry (HKCU) is touched and nothing of Windows is
    /// replaced – Explorer keeps running (desktop, taskbar), Control Panel, Recycle Bin and other special folders
    /// still open in it. Switching off removes exactly what was added, so Explorer is the default again.
    /// </summary>
    public static class DefaultFileManager
    {
        private const string Verb = "WinXplorer";
        private static readonly string[] Classes = { "Directory", "Drive" };
        // the "File Explorer" item Win+E starts
        private const string WinE = @"CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}";
        // "This PC" (desktop icon, Start, shell:MyComputerFolder): open it in WinXplorer too
        private const string ThisPc = @"CLSID\{20D04FE0-3AEA-1069-A2D8-08002B30309D}";

        public static void Apply(bool on)
        {
            try { if (on) Enable(); else Disable(); } catch { }
        }

        /// <summary>Is WinXplorer currently registered as the folder opener (by this or another copy)?</summary>
        public static bool IsActive()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\shell");
                return k?.GetValue(null) as string == Verb;
            }
            catch { return false; }
        }

        private static void Enable()
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || exe.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase)) return;
            using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
            Enable(classes, exe);
            Notify();
        }

        /// <summary>Writes the keys under <paramref name="classes"/> (HKCU\Software\Classes; a scratch key in tests).</summary>
        internal static void Enable(RegistryKey classes, string exe)
        {
            foreach (var c in Classes)
            {
                using (var shell = classes.CreateSubKey(c + @"\shell"))
                    shell.SetValue(null, Verb);   // the default action: what double-click does
                using var v = classes.CreateSubKey($@"{c}\shell\{Verb}");
                v.SetValue(null, Loc.T("OpenInWinXplorer"));
                v.SetValue("Icon", $"\"{exe}\",0");
                using var cmd = v.CreateSubKey("command");
                cmd.SetValue(null, $"\"{exe}\" \"%1\"");
            }
            using (var k = classes.CreateSubKey(WinE + @"\shell\opennewwindow\command"))
            {
                k.SetValue(null, $"\"{exe}\"");
                k.SetValue("DelegateExecute", "");   // empty: use the command above instead of Explorer's handler
            }
            using (var k = classes.CreateSubKey(ThisPc + @"\shell")) k.SetValue(null, "open");   // make "open" the default verb
            using (var k = classes.CreateSubKey(ThisPc + @"\shell\open\command"))
            {
                k.SetValue(null, $"\"{exe}\" \"::thispc\"");
                k.SetValue("DelegateExecute", "");
            }
        }

        private static void Disable()
        {
            using var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", true);
            if (classes == null) return;
            Disable(classes);
            Notify();
        }

        internal static void Disable(RegistryKey classes)
        {
            foreach (var c in Classes)
            {
                using (var shell = classes.OpenSubKey(c + @"\shell", true))
                {
                    if (shell == null) continue;
                    if (shell.GetValue(null) as string == Verb) shell.DeleteValue("", false);
                    try { shell.DeleteSubKeyTree(Verb, false); } catch { }
                }
            }
            // Win+E / This PC: only remove what points at WinXplorer (never somebody else's setting), then empty parent keys
            foreach (var (clsid, verb) in new[] { (WinE, "opennewwindow"), (ThisPc, "open") })
            {
                bool ours;
                using (var k = classes.OpenSubKey($@"{clsid}\shell\{verb}\command"))
                    ours = k?.GetValue(null) is string s && s.Contains("WinXplorer", StringComparison.OrdinalIgnoreCase);
                if (!ours) continue;
                try { classes.DeleteSubKeyTree($@"{clsid}\shell\{verb}", false); } catch { }
                try { using var sk = classes.OpenSubKey(clsid + @"\shell", true); if (sk?.GetValue(null) as string == verb) sk.DeleteValue("", false); } catch { }
                foreach (var parent in new[] { clsid + @"\shell", clsid })
                {
                    using var p = classes.OpenSubKey(parent);
                    if (p != null && p.SubKeyCount == 0 && p.ValueCount == 0) { p.Close(); try { classes.DeleteSubKey(parent, false); } catch { } }
                }
            }
        }

        private static void Notify() => Native.SHChangeNotify(0x08000000 /*SHCNE_ASSOCCHANGED*/, 0, IntPtr.Zero, IntPtr.Zero);

        private static class Native
        {
            [System.Runtime.InteropServices.DllImport("shell32.dll")]
            public static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
        }
    }
}
