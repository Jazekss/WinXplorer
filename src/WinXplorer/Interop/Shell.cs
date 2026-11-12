using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinXplorer.Interop
{
    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string name, out uint eaten, out IntPtr ppidl, ref uint attrs);
        [PreserveSig] int EnumObjects(IntPtr hwnd, int flags, out IntPtr ppenum);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid, IntPtr reserved, out IntPtr ppv);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint flags, IntPtr pName);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IntPtr ppidlOut);
    }

    [ComImport, Guid("000214e4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint iMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr reserved, IntPtr pszName, uint cchMax);
    }

    [ComImport, Guid("000214f4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint iMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr reserved, IntPtr pszName, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint iMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr reserved, IntPtr pszName, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr result);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        [MarshalAs(UnmanagedType.LPStr)] public string lpParameters;
        [MarshalAs(UnmanagedType.LPStr)] public string lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.LPStr)] public string lpTitle;
        public IntPtr lpVerbW;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpParametersW;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpDirectoryW;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpTitleW;
        public Native.POINT ptInvoke;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE { public int cx, cy; }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    internal class CShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellLinkW
    {
        [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
    }

    internal static class Shell
    {
        public static readonly Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
        public static readonly Guid IID_IContextMenu = new("000214e4-0000-0000-c000-000000000046");
        public static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl, uint sfgaoIn, out uint psfgaoOut);

        [DllImport("shell32.dll")]
        public static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr ppidlLast);

        [DllImport("shell32.dll")]
        public static extern int SHGetDesktopFolder(out IntPtr ppshf);

        [DllImport("shell32.dll")]
        public static extern IntPtr ILFindLastID(IntPtr pidl);

        [DllImport("ole32.dll")]
        public static extern void CoTaskMemFree(IntPtr pv);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        public static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        // ---------- thumbnails ----------
        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight; public ushort biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int c, out BITMAP bm);
        [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        public const int SIIGBF_RESIZETOFIT = 0, SIIGBF_BIGGERSIZEOK = 1, SIIGBF_ICONONLY = 4, SIIGBF_THUMBNAILONLY = 8;

        /// <summary>Gets the shell thumbnail (or icon) for a path. Works with every codec/thumbnail handler installed in Windows.</summary>
        public static BitmapSource GetThumbnail(string path, int size, int flags = SIIGBF_BIGGERSIZEOK)
        {
            IShellItemImageFactory f = null;
            IntPtr hbm = IntPtr.Zero;
            try
            {
                var iid = IID_IShellItemImageFactory;
                SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out f);
                if (f.GetImage(new SIZE { cx = size, cy = size }, flags, out hbm) != 0 || hbm == IntPtr.Zero) return null;
                var src = HBitmapToSource(hbm, out bool hasAlpha);
                // Some folder thumbnails (system folders with a content preview) come back without an alpha
                // channel and would render on an opaque white square: fall back to the plain folder icon.
                if (!hasAlpha && (flags & SIIGBF_ICONONLY) == 0 && System.IO.Directory.Exists(path))
                {
                    Native.DeleteObject(hbm); hbm = IntPtr.Zero;
                    if (f.GetImage(new SIZE { cx = size, cy = size }, flags | SIIGBF_ICONONLY, out hbm) != 0 || hbm == IntPtr.Zero) return src;
                    return HBitmapToSource(hbm, out _) ?? src;
                }
                return src;
            }
            catch { return null; }
            finally
            {
                if (hbm != IntPtr.Zero) Native.DeleteObject(hbm);
                if (f != null) Marshal.ReleaseComObject(f);
            }
        }

        /// <summary>
        /// Windows hands out many menu icons as black monochrome glyphs (drawn for light menus). In the dark theme
        /// those are recoloured light so they stay visible; coloured icons are left alone.
        /// </summary>
        // ---- video orientation (phones store "rotate 90°" as metadata; WPF's player ignores it) ----
        [StructLayout(LayoutKind.Sequential)] private struct PROPERTYKEY { public Guid fmtid; public uint pid; }
        [StructLayout(LayoutKind.Sequential)] private struct PROPVARIANT { public ushort vt; public ushort r1, r2, r3; public IntPtr p1; public IntPtr p2; }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
            [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bindCtx, int flags, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
        [DllImport("propsys.dll", CharSet = CharSet.Unicode)] private static extern int PSGetPropertyKeyFromName(string name, out PROPERTYKEY key);
        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT pv);

        /// <summary>Clockwise rotation (0 / 90 / 180 / 270) a video asks for in its metadata (System.Video.Orientation).</summary>
        public static int GetVideoOrientation(string path)
        {
            IPropertyStore store = null;
            try
            {
                if (PSGetPropertyKeyFromName("System.Video.Orientation", out var key) < 0) return 0;
                var iid = typeof(IPropertyStore).GUID;
                if (SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref iid, out store) < 0 || store == null) return 0;
                if (store.GetValue(ref key, out var v) < 0) return 0;
                int deg = v.vt is 19 or 18 or 3 or 2 ? (int)(v.p1.ToInt64() & 0xFFFFFFFF) : 0;   // VT_UI4 / VT_UI2 / VT_I4 / VT_I2
                PropVariantClear(ref v);
                return deg is 90 or 180 or 270 ? deg : 0;
            }
            catch { return 0; }
            finally { if (store != null) Marshal.ReleaseComObject(store); }
        }

        internal static BitmapSource ForTheme(BitmapSource src)
        {
            if (src == null || !Services.ThemeManager.IsDark) return src;
            try
            {
                var f = new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0);
                int w = f.PixelWidth, h = f.PixelHeight;
                var px = new byte[w * h * 4];
                f.CopyPixels(px, w * 4, 0);
                int visible = 0;
                for (int i = 0; i < px.Length; i += 4)
                {
                    byte a = px[i + 3];
                    if (a < 40) continue;
                    visible++;
                    // un-premultiply; anything bright or saturated means a coloured icon
                    int b = px[i] * 255 / a, g = px[i + 1] * 255 / a, r = px[i + 2] * 255 / a;
                    int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                    if (max > 110 || max - min > 40) return src;
                }
                if (visible == 0) return src;
                const int light = 0xE6;
                for (int i = 0; i < px.Length; i += 4)
                {
                    byte a = px[i + 3];
                    px[i] = px[i + 1] = px[i + 2] = (byte)(light * a / 255);
                }
                var res = BitmapSource.Create(w, h, src.DpiX, src.DpiY, PixelFormats.Pbgra32, null, px, w * 4);
                res.Freeze();
                return res;
            }
            catch { return src; }
        }

        internal static BitmapSource HBitmapToSource(IntPtr hbm, out bool alpha)
        {
            alpha = false;
            GetObject(hbm, Marshal.SizeOf<BITMAP>(), out var bm);
            int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
            if (w <= 0 || h <= 0) return null;
            var bmi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var px = new byte[w * h * 4];
            var hdc = GetDC(IntPtr.Zero);
            GetDIBits(hdc, hbm, 0, (uint)h, px, ref bmi, 0);
            ReleaseDC(IntPtr.Zero, hdc);
            bool straight = false;
            for (int i = 3; i < px.Length; i += 4)
            {
                byte a = px[i];
                if (a != 0) alpha = true;
                if (px[i - 1] > a || px[i - 2] > a || px[i - 3] > a) straight = true;
            }
            // an all-zero alpha channel means "opaque, no alpha"; colour above alpha means non-premultiplied
            var fmt = !alpha ? PixelFormats.Bgr32 : straight ? PixelFormats.Bgra32 : PixelFormats.Pbgra32;
            var src = BitmapSource.Create(w, h, 96, 96, fmt, null, px, w * 4);
            src.Freeze();
            return src;
        }

        public static string ResolveShortcut(string lnk)
        {
            try
            {
                var link = (IShellLinkW)new CShellLink();
                ((IPersistFile)link).Load(lnk, 0);
                var sb = new StringBuilder(1024);
                link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                Marshal.ReleaseComObject(link);
                return sb.Length > 0 ? sb.ToString() : null;
            }
            catch { return null; }
        }
    }

    /// <summary>One item read out of the Windows context menu.</summary>
    internal sealed class ShellMenuItem
    {
        public string Text;
        public uint Id;
        public string Verb;
        public bool IsSeparator, IsDisabled;
        public ImageSource Icon;
        public List<ShellMenuItem> Children;
        /// <summary>Stable key used to remember the item in settings (verb if it has one, otherwise the text).</summary>
        public string Key => !string.IsNullOrEmpty(Verb) ? "verb:" + Verb.ToLowerInvariant() : "text:" + Text?.ToLowerInvariant();
    }

    /// <summary>
    /// The Windows context menu for some items, built but not shown, so that its entries can be listed
    /// and invoked from WinXplorer's own menu. Dispose when the menu is closed.
    /// </summary>
    internal sealed class ShellMenu : IDisposable
    {
        private const uint CMF_NORMAL = 0, CMF_EXPLORE = 4, CMF_CANRENAME = 0x10, CMF_EXTENDEDVERBS = 0x100;
        private const uint CMIC_MASK_UNICODE = 0x4000, CMIC_MASK_PTINVOKE = 0x20000000;
        private const uint FirstId = 1, LastId = 0x7EFF;

        private IContextMenu _cm;
        private IShellFolder _parent;
        private readonly List<IntPtr> _pidls = new();
        private IntPtr _hMenu;
        private string _folder;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MENUITEMINFO
        {
            public int cbSize; public uint fMask, fType, fState, wID; public IntPtr hSubMenu, hbmpChecked, hbmpUnchecked, dwItemData;
            public IntPtr dwTypeData; public uint cch; public IntPtr hbmpItem;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMenuItemInfoW(IntPtr hMenu, uint item, bool byPos, ref MENUITEMINFO mii);
        private const uint MIIM_STATE = 1, MIIM_ID = 2, MIIM_SUBMENU = 4, MIIM_STRING = 0x40, MIIM_BITMAP = 0x80, MIIM_FTYPE = 0x100;
        private const uint MFT_SEPARATOR = 0x800, MFS_DISABLED = 3;

        public static ShellMenu Create(IntPtr hwnd, string[] paths, string folder, bool extended)
        {
            var m = new ShellMenu { _folder = folder };
            try
            {
                Guid iidSF = Shell.IID_IShellFolder, iidCM = Shell.IID_IContextMenu;
                IntPtr cmPtr;
                if (paths != null && paths.Length > 0)
                {
                    foreach (var p in paths)
                        if (Shell.SHParseDisplayName(p, IntPtr.Zero, out var pidl, 0, out _) == 0 && pidl != IntPtr.Zero) m._pidls.Add(pidl);
                    if (m._pidls.Count == 0) { m.Dispose(); return null; }
                    if (Shell.SHBindToParent(m._pidls[0], ref iidSF, out var pSF, out _) != 0) { m.Dispose(); return null; }
                    m._parent = (IShellFolder)Marshal.GetObjectForIUnknown(pSF);
                    Marshal.Release(pSF);
                    var children = m._pidls.Select(Shell.ILFindLastID).ToArray();
                    if (m._parent.GetUIObjectOf(hwnd, (uint)children.Length, children, ref iidCM, IntPtr.Zero, out cmPtr) != 0) { m.Dispose(); return null; }
                }
                else
                {
                    if (Shell.SHParseDisplayName(folder, IntPtr.Zero, out var pidl, 0, out _) != 0) { m.Dispose(); return null; }
                    m._pidls.Add(pidl);
                    Shell.SHGetDesktopFolder(out var pDesk);
                    var desktop = (IShellFolder)Marshal.GetObjectForIUnknown(pDesk);
                    Marshal.Release(pDesk);
                    if (desktop.BindToObject(pidl, IntPtr.Zero, ref iidSF, out var pSF) == 0)
                    {
                        m._parent = (IShellFolder)Marshal.GetObjectForIUnknown(pSF);
                        Marshal.Release(pSF);
                        Marshal.ReleaseComObject(desktop);
                    }
                    else m._parent = desktop;
                    if (m._parent.CreateViewObject(hwnd, ref iidCM, out cmPtr) != 0) { m.Dispose(); return null; }
                }
                m._cm = (IContextMenu)Marshal.GetObjectForIUnknown(cmPtr);
                Marshal.Release(cmPtr);
                m._hMenu = Native.CreatePopupMenu();
                m._cm.QueryContextMenu(m._hMenu, 0, FirstId, LastId, CMF_NORMAL | CMF_EXPLORE | CMF_CANRENAME | (extended ? CMF_EXTENDEDVERBS : 0));
                return m;
            }
            catch { m.Dispose(); return null; }
        }

        /// <summary>Reads the menu (and its submenus) into a tree.</summary>
        public List<ShellMenuItem> ReadItems(int maxDepth = 2) => Read(_hMenu, maxDepth);

        private List<ShellMenuItem> Read(IntPtr hMenu, int depth)
        {
            var list = new List<ShellMenuItem>();
            int n = Native.GetMenuItemCount(hMenu);
            var buf = Marshal.AllocHGlobal(1024);
            try
            {
                for (uint i = 0; i < n; i++)
                {
                    var mii = new MENUITEMINFO { cbSize = Marshal.SizeOf<MENUITEMINFO>(), fMask = MIIM_FTYPE | MIIM_STATE | MIIM_ID | MIIM_SUBMENU | MIIM_STRING | MIIM_BITMAP, dwTypeData = buf, cch = 500 };
                    if (!GetMenuItemInfoW(hMenu, i, true, ref mii)) continue;
                    if ((mii.fType & MFT_SEPARATOR) != 0) { list.Add(new ShellMenuItem { IsSeparator = true }); continue; }
                    var text = mii.cch > 0 ? Marshal.PtrToStringUni(buf, (int)mii.cch) : null;
                    text = CleanText(text);
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    var item = new ShellMenuItem { Text = text, Id = mii.wID, IsDisabled = (mii.fState & MFS_DISABLED) != 0 };
                    if (mii.hSubMenu == IntPtr.Zero && mii.wID >= FirstId && mii.wID <= LastId) item.Verb = Verb(mii.wID);
                    if ((long)mii.hbmpItem > 11) { try { item.Icon = Shell.ForTheme(Shell.HBitmapToSource(mii.hbmpItem, out _)); } catch { } }
                    if (mii.hSubMenu != IntPtr.Zero && depth > 0)
                    {
                        // dynamic submenus (Send to, Open with…) are filled when they are about to open
                        try
                        {
                            if (_cm is IContextMenu3 c3) c3.HandleMenuMsg2(0x0117, mii.hSubMenu, (IntPtr)i, out _);
                            else if (_cm is IContextMenu2 c2) c2.HandleMenuMsg(0x0117, mii.hSubMenu, (IntPtr)i);
                        }
                        catch { }
                        item.Children = Read(mii.hSubMenu, depth - 1);
                    }
                    else if (mii.hSubMenu != IntPtr.Zero) item.Children = new List<ShellMenuItem>();
                    list.Add(item);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            // trim separators at the edges / duplicates
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i].IsSeparator && (i == 0 || i == list.Count - 1 || list[i - 1].IsSeparator)) list.RemoveAt(i);
            return list;
        }

        private static string CleanText(string s)
        {
            if (s == null) return null;
            int tab = s.IndexOf('\t');
            if (tab >= 0) s = s[..tab];
            return s.Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&").Trim();
        }

        public string Verb(uint id)
        {
            var buf = Marshal.AllocHGlobal(1024);
            try { return _cm.GetCommandString((UIntPtr)(id - FirstId), 4 /*GCS_VERBW*/, IntPtr.Zero, buf, 512) == 0 ? Marshal.PtrToStringUni(buf) : null; }
            catch { return null; }
            finally { Marshal.FreeHGlobal(buf); }
        }

        public void Invoke(IntPtr hwnd, uint id, int x, int y)
        {
            try
            {
                var info = new CMINVOKECOMMANDINFOEX
                {
                    cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                    fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE,
                    hwnd = hwnd,
                    lpVerb = (IntPtr)(id - FirstId),
                    lpVerbW = (IntPtr)(id - FirstId),
                    lpDirectoryW = _folder,
                    nShow = 1,
                    ptInvoke = new Native.POINT { X = x, Y = y },
                };
                _cm.InvokeCommand(ref info);
            }
            catch { }
        }

        public void Dispose()
        {
            if (_hMenu != IntPtr.Zero) { Native.DestroyMenu(_hMenu); _hMenu = IntPtr.Zero; }
            if (_cm != null) { try { Marshal.ReleaseComObject(_cm); } catch { } _cm = null; }
            if (_parent != null) { try { Marshal.ReleaseComObject(_parent); } catch { } _parent = null; }
            foreach (var p in _pidls) Shell.CoTaskMemFree(p);
            _pidls.Clear();
        }
    }

    /// <summary>Hosts the real Windows Explorer context menu (with all shell extensions: 7-Zip, Open with, Send to...).</summary>
    internal static class ShellContextMenu
    {
        public sealed record CustomItem(string Text, Action Action);

        private static IContextMenu2 _cm2;
        private static IContextMenu3 _cm3;

        private const uint CMF_NORMAL = 0, CMF_EXPLORE = 4, CMF_CANRENAME = 0x10, CMF_EXTENDEDVERBS = 0x100;
        private const uint CMIC_MASK_UNICODE = 0x4000, CMIC_MASK_PTINVOKE = 0x20000000,
            CMIC_MASK_SHIFT_DOWN = 0x10000000, CMIC_MASK_CONTROL_DOWN = 0x40000000;
        private const uint GCS_VERBW = 4;
        private const uint FirstId = 1, LastId = 0x7EFF, CustomBase = 0x7F00;

        public static IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (_cm2 == null && _cm3 == null) return IntPtr.Zero;
            switch (msg)
            {
                case 0x0117: // WM_INITMENUPOPUP
                case 0x002B: // WM_DRAWITEM
                case 0x002C: // WM_MEASUREITEM
                case 0x0120: // WM_MENUCHAR
                    try
                    {
                        if (_cm3 != null && _cm3.HandleMenuMsg2((uint)msg, wParam, lParam, out var res) == 0)
                        { handled = true; return res; }
                        if (_cm2 != null && msg != 0x0120 && _cm2.HandleMenuMsg((uint)msg, wParam, lParam) == 0)
                        { handled = true; return msg == 0x0117 ? IntPtr.Zero : new IntPtr(1); }
                    }
                    catch { }
                    break;
            }
            return IntPtr.Zero;
        }

        /// <param name="paths">Selected items (all must share one parent), or null for folder background.</param>
        /// <param name="interceptVerb">Return true to handle a canonical verb (e.g. "rename", "open") yourself.</param>
        public static void Show(IntPtr hwnd, string[] paths, string folder, IList<CustomItem> custom,
            Func<string, bool> interceptVerb, int x, int y, bool extended)
        {
            var pidls = new List<IntPtr>();
            IntPtr cmPtr = IntPtr.Zero, hMenu = IntPtr.Zero;
            IShellFolder parent = null;
            IContextMenu cm = null;
            try
            {
                Guid iidSF = Shell.IID_IShellFolder, iidCM = Shell.IID_IContextMenu;
                if (paths != null && paths.Length > 0)
                {
                    foreach (var p in paths)
                        if (Shell.SHParseDisplayName(p, IntPtr.Zero, out var pidl, 0, out _) == 0 && pidl != IntPtr.Zero)
                            pidls.Add(pidl);
                    if (pidls.Count == 0) return;
                    if (Shell.SHBindToParent(pidls[0], ref iidSF, out var pSF, out _) != 0) return;
                    parent = (IShellFolder)Marshal.GetObjectForIUnknown(pSF);
                    Marshal.Release(pSF);
                    var children = pidls.Select(Shell.ILFindLastID).ToArray();
                    if (parent.GetUIObjectOf(hwnd, (uint)children.Length, children, ref iidCM, IntPtr.Zero, out cmPtr) != 0) return;
                }
                else
                {
                    if (Shell.SHParseDisplayName(folder, IntPtr.Zero, out var pidl, 0, out _) != 0) return;
                    pidls.Add(pidl);
                    Shell.SHGetDesktopFolder(out var pDesk);
                    var desktop = (IShellFolder)Marshal.GetObjectForIUnknown(pDesk);
                    Marshal.Release(pDesk);
                    if (desktop.BindToObject(pidl, IntPtr.Zero, ref iidSF, out var pSF) == 0)
                    {
                        parent = (IShellFolder)Marshal.GetObjectForIUnknown(pSF);
                        Marshal.Release(pSF);
                        Marshal.ReleaseComObject(desktop);
                    }
                    else parent = desktop;
                    if (parent.CreateViewObject(hwnd, ref iidCM, out cmPtr) != 0) return;
                }

                cm = (IContextMenu)Marshal.GetObjectForIUnknown(cmPtr);
                Marshal.Release(cmPtr); cmPtr = IntPtr.Zero;
                _cm2 = cm as IContextMenu2;
                _cm3 = cm as IContextMenu3;

                hMenu = Native.CreatePopupMenu();
                uint flags = CMF_NORMAL | CMF_EXPLORE | CMF_CANRENAME | (extended ? CMF_EXTENDEDVERBS : 0);
                cm.QueryContextMenu(hMenu, 0, FirstId, LastId, flags);

                if (custom != null && custom.Count > 0)
                {
                    uint pos = 0;
                    for (int i = 0; i < custom.Count; i++)
                    {
                        if (custom[i] == null) Native.InsertMenuW(hMenu, pos++, Native.MF_BYPOSITION | Native.MF_SEPARATOR, UIntPtr.Zero, null);
                        else Native.InsertMenuW(hMenu, pos++, Native.MF_BYPOSITION | Native.MF_STRING, (UIntPtr)(CustomBase + (uint)i), custom[i].Text);
                    }
                    Native.InsertMenuW(hMenu, pos, Native.MF_BYPOSITION | Native.MF_SEPARATOR, UIntPtr.Zero, null);
                }

                uint cmd = Native.TrackPopupMenuEx(hMenu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON, x, y, hwnd, IntPtr.Zero);
                if (cmd == 0) return;

                if (cmd >= CustomBase)
                {
                    custom[(int)(cmd - CustomBase)]?.Action?.Invoke();
                    return;
                }

                string verb = null;
                var buf = Marshal.AllocHGlobal(1024);
                try
                {
                    if (cm.GetCommandString((UIntPtr)(cmd - FirstId), GCS_VERBW, IntPtr.Zero, buf, 512) == 0)
                        verb = Marshal.PtrToStringUni(buf);
                }
                catch { }
                finally { Marshal.FreeHGlobal(buf); }

                if (verb != null && interceptVerb != null && interceptVerb(verb)) return;

                var info = new CMINVOKECOMMANDINFOEX
                {
                    cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                    fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE
                            | (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift) ? CMIC_MASK_SHIFT_DOWN : 0)
                            | (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control) ? CMIC_MASK_CONTROL_DOWN : 0),
                    hwnd = hwnd,
                    lpVerb = (IntPtr)(cmd - FirstId),
                    lpVerbW = (IntPtr)(cmd - FirstId),
                    lpDirectoryW = folder,
                    nShow = 1,
                    ptInvoke = new Native.POINT { X = x, Y = y },
                };
                cm.InvokeCommand(ref info);
            }
            catch { }
            finally
            {
                _cm2 = null; _cm3 = null;
                if (hMenu != IntPtr.Zero) Native.DestroyMenu(hMenu);
                if (cm != null) Marshal.ReleaseComObject(cm);
                if (parent != null) Marshal.ReleaseComObject(parent);
                foreach (var p in pidls) Shell.CoTaskMemFree(p);
            }
        }
    }
}
