using System;
using System.IO;
using System.Runtime.InteropServices;

namespace WinXplorer.Services
{
    /// <summary>
    /// Windows' own file operation engine (IFileOperation) – what Explorer uses. Where the user's rights aren't enough
    /// (a drive's root such as F:\, Program Files…) it shows Windows' "You'll need to provide administrator permission
    /// – Continue" and does the job elevated, so WinXplorer can do what Explorer can without running as administrator.
    /// Used as the fallback when the direct way is denied.
    /// </summary>
    public static class ShellFileOp
    {
        /// <summary>True when the error means "not allowed" (worth asking Windows to do it elevated).</summary>
        public static bool IsDenied(Exception ex) => ex is UnauthorizedAccessException
            || ex is IOException io && (io.HResult & 0xFFFF) is 5 or 1314;

        public static bool IsDenied(int win32Error) => win32Error is 5 or 1314;   // ACCESS_DENIED, PRIVILEGE_NOT_HELD

        /// <summary>Renames a file or folder. False when it failed or the user said no to the permission prompt.</summary>
        public static bool Rename(string path, string newName) =>
            Run(op => op.RenameItem(Item(path.TrimEnd('\\')), newName, IntPtr.Zero))
            && Exists(Path.Combine(Path.GetDirectoryName(path.TrimEnd('\\')) ?? "", newName));

        /// <summary>Creates an empty folder.</summary>
        public static bool NewFolder(string dir, string name) =>
            Run(op => op.NewItem(Item(dir), 0x10 /* FILE_ATTRIBUTE_DIRECTORY */, name, null, IntPtr.Zero))
            && Directory.Exists(Path.Combine(dir, name));

        /// <summary>Puts a file with this content there (written to a temporary file first, then copied in by Windows).</summary>
        public static bool NewFile(string target, Action<Stream> write)
        {
            var tmpDir = Path.Combine(Path.GetTempPath(), "WinXplorer", "elevated-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Directory.CreateDirectory(tmpDir);
                var tmp = Path.Combine(tmpDir, "file.tmp");
                using (var fs = File.Create(tmp)) write?.Invoke(fs);
                var dir = Path.GetDirectoryName(target)!;
                return Run(op => op.CopyItem(Item(tmp), Item(dir), Path.GetFileName(target), IntPtr.Zero)) && File.Exists(target);
            }
            catch { return false; }
            finally { try { Directory.Delete(tmpDir, true); } catch { } }
        }

        private static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

        private static bool Run(Action<IFileOperation> add)
        {
            IFileOperation op = null;
            try
            {
                op = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_FileOperation)!)!;
                op.SetOperationFlags(FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR | FOFX_SHOWELEVATIONPROMPT);
                var hwnd = FileOps.MainHwnd;
                if (hwnd != IntPtr.Zero) op.SetOwnerWindow(hwnd);
                add(op);
                op.PerformOperations();
                return !op.GetAnyOperationsAborted();
            }
            catch { return false; }   // cancelled at the permission prompt, or Windows couldn't either
            finally { if (op != null) Marshal.FinalReleaseComObject(op); }
        }

        private static IShellItem Item(string path)
        {
            var iid = typeof(IShellItem).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item);
            return item;
        }

        // ---------------- interop ----------------
        private static readonly Guid CLSID_FileOperation = new("3ad05575-8857-4850-9277-11b85bdb8e09");
        private const uint FOF_ALLOWUNDO = 0x40, FOF_NOCONFIRMMKDIR = 0x200, FOFX_SHOWELEVATIONPROMPT = 0x00040000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItem item);

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, out IntPtr ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOperation
        {
            uint Advise(IntPtr pfops);
            void Unadvise(uint dwCookie);
            void SetOperationFlags(uint dwOperationFlags);
            void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string pszMessage);
            void SetProgressDialog(IntPtr popd);
            void SetProperties(IntPtr pproparray);
            void SetOwnerWindow(IntPtr hwndOwner);
            void ApplyPropertiesToItem(IShellItem psiItem);
            void ApplyPropertiesToItems(IntPtr punkItems);
            void RenameItem(IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IntPtr pfopsItem);
            void RenameItems(IntPtr pUnkItems, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
            void MoveItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IntPtr pfopsItem);
            void MoveItems(IntPtr punkItems, IShellItem psiDestinationFolder);
            void CopyItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszCopyName, IntPtr pfopsItem);
            void CopyItems(IntPtr punkItems, IShellItem psiDestinationFolder);
            void DeleteItem(IShellItem psiItem, IntPtr pfopsItem);
            void DeleteItems(IntPtr punkItems);
            void NewItem(IShellItem psiDestinationFolder, uint dwFileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string pszName, [MarshalAs(UnmanagedType.LPWStr)] string pszTemplateName, IntPtr pfopsItem);
            void PerformOperations();
            [return: MarshalAs(UnmanagedType.Bool)] bool GetAnyOperationsAborted();
        }
    }
}
