using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace WinXplorer.Services
{
    /// <summary>
    /// The programs Windows recommends for a file type (the same ones "Open with" lists) – the standard menu shows them
    /// for pictures, video and music, so e.g. AIMP is right there for an .mp3.
    /// </summary>
    public static class OpenWithApps
    {
        public sealed class App
        {
            public string Name { get; init; }
            public ImageSource Icon { get; init; }
            internal IAssocHandler Handler { get; init; }
        }

        /// <summary>Kinds that get their apps in the standard menu.</summary>
        public static bool AppliesTo(string kind) => kind is "image" or "video" or "audio";

        private static readonly Dictionary<string, List<App>> _cache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Up to <paramref name="max"/> recommended apps for this extension (read once per type, UI thread).</summary>
        public static List<App> For(string ext, int max = 4)
        {
            if (string.IsNullOrEmpty(ext)) return new();
            if (_cache.TryGetValue(ext, out var cached)) return cached;
            var list = new List<App>();
            try
            {
                if (SHAssocEnumHandlers(ext, 1 /* ASSOC_FILTER_RECOMMENDED */, out var en) == 0 && en != null)
                {
                    while (list.Count < max && en.Next(1, out var h, out var got) == 0 && got == 1 && h != null)
                    {
                        string ui = null, name = null;
                        try { h.GetUIName(out ui); } catch { }
                        try { h.GetName(out name); } catch { }
                        if (string.IsNullOrWhiteSpace(ui) || list.Any(a => a.Name == ui)) continue;
                        list.Add(new App { Name = ui, Icon = IconFor(name), Handler = h });
                    }
                }
            }
            catch { }
            _cache[ext] = list;
            return list;
        }

        private static ImageSource IconFor(string name)
        {
            try
            {
                if (string.IsNullOrEmpty(name)) return null;
                if (File.Exists(name)) return IconService.GetPathIcon(name);
                // a Windows (Store) app: its id
                return Interop.Shell.GetThumbnail(AppCatalog.AppsFolder + name, 16, Interop.Shell.SIIGBF_ICONONLY);
            }
            catch { return null; }
        }

        /// <summary>Opens the files with that app (Windows starts it, like "Open with" does).</summary>
        public static void Open(App app, IList<string> paths)
        {
            var pidls = new List<IntPtr>();
            try
            {
                foreach (var p in paths)
                    if (SHParseDisplayName(p, IntPtr.Zero, out var pidl, 0, out _) == 0) pidls.Add(pidl);
                if (pidls.Count == 0) return;
                if (SHCreateShellItemArrayFromIDLists((uint)pidls.Count, pidls.ToArray(), out var arr) != 0 || arr == null) return;
                var bhid = BHID_DataObject; var iid = IID_IDataObject;
                arr.BindToHandler(IntPtr.Zero, ref bhid, ref iid, out var obj);
                app.Handler.Invoke((System.Runtime.InteropServices.ComTypes.IDataObject)obj);
            }
            catch (Exception ex) { Notify.Show(app.Name + ": " + ex.Message); }
            finally { foreach (var p in pidls) Marshal.FreeCoTaskMem(p); }
        }

        // ---------------- interop ----------------
        private static Guid BHID_DataObject = new("B8C0BD9F-ED24-455C-83E6-D5390C4FE8C4");
        private static Guid IID_IDataObject = new("0000010e-0000-0000-C000-000000000046");

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHAssocEnumHandlers(string pszExtra, int afFilter, out IEnumAssocHandlers ppEnumHandler);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHParseDisplayName(string name, IntPtr pbc, out IntPtr pidl, uint sfgaoIn, out uint psfgaoOut);

        [DllImport("shell32.dll")]
        private static extern int SHCreateShellItemArrayFromIDLists(uint cidl, IntPtr[] rgpidl, out IShellItemArray ppsiItemArray);

        [ComImport, Guid("973810ae-9599-4b88-9e4d-6ee98c9552da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IEnumAssocHandlers
        {
            [PreserveSig] int Next(uint celt, out IAssocHandler rgelt, out uint pceltFetched);
        }

        [ComImport, Guid("F04061AC-1659-4a3f-A954-775AA57FC083"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAssocHandler
        {
            void GetName([MarshalAs(UnmanagedType.LPWStr)] out string ppsz);
            void GetUIName([MarshalAs(UnmanagedType.LPWStr)] out string ppsz);
            void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] out string ppszPath, out int pIndex);
            [PreserveSig] int IsRecommended();
            void MakeDefault([MarshalAs(UnmanagedType.LPWStr)] string pszDescription);
            void Invoke(System.Runtime.InteropServices.ComTypes.IDataObject pdo);
            void CreateInvoker(System.Runtime.InteropServices.ComTypes.IDataObject pdo, out IntPtr ppInvoker);
        }

        [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IShellItemArray
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppvOut);
        }
    }
}
