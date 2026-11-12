using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WinXplorer.Interop
{
    /// <summary>
    /// Shows a file with the Windows preview handler registered for its type – the same previews as Explorer's
    /// preview pane (PDF, Word, Excel, PowerPoint, Outlook mail, fonts...). Nothing is bundled: if Windows or an
    /// installed app has no previewer for the type, <see cref="Failed"/> fires and the caller shows something else.
    /// Handlers run out of process (prevhost.exe) where possible, so a crashing one can't take WinXplorer down.
    /// </summary>
    internal sealed class PreviewHandlerHost : HwndHost
    {
        private const string HandlerKey = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";

        [ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPreviewHandler
        {
            void SetWindow(IntPtr hwnd, ref RECT rect);
            void SetRect(ref RECT rect);
            void DoPreview();
            void Unload();
            void SetFocus();
            void QueryFocus(out IntPtr phwnd);
            [PreserveSig] int TranslateAccelerator(IntPtr pmsg);
        }

        [ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IInitializeWithStream { void Initialize(IStream pstream, uint grfMode); }

        [ComImport, Guid("7f73be3f-fb79-493c-a6c7-7ee14e245841"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IInitializeWithItem { void Initialize([MarshalAs(UnmanagedType.Interface)] object psi, uint grfMode); }

        [ComImport, Guid("b7d14566-0509-4cce-a71f-0a554233bd9b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IInitializeWithFile { void Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode); }

        [ComImport, Guid("196bf9a5-b346-4ef0-aa1e-5dcdb76768b1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPreviewHandlerVisuals
        {
            void SetBackgroundColor(uint color);
            void SetFont(IntPtr plf);
            void SetTextColor(uint color);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [ComImport, Guid("fc4801a3-2ba9-11cf-a229-00aa003d7352"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IObjectWithSite
        {
            void SetSite([MarshalAs(UnmanagedType.IUnknown)] object site);
            void GetSite(ref Guid riid, out IntPtr ppv);
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct ACCEL { public byte fVirt; public ushort key, cmd; }

        [DllImport("user32.dll")] private static extern IntPtr CreateAcceleratorTable([In] ACCEL[] accels, int count);
        [DllImport("user32.dll")] private static extern bool DestroyAcceleratorTable(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        /// <summary>
        /// Our side of the previewer: it hands keys it doesn't use (we ask for Space and Esc) back to us, so
        /// Space still closes the quick view while e.g. Word's previewer has the keyboard.
        /// </summary>
        [ComVisible(true), Guid("fec87aaf-35f9-447a-adb7-20234491401a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IPreviewHandlerFrame
        {
            [PreserveSig] int GetWindowContext(IntPtr info);
            [PreserveSig] int TranslateAccelerator(ref MSG msg);
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        private sealed class Frame : IPreviewHandlerFrame
        {
            private readonly IntPtr _accel;
            private readonly Action<System.Windows.Input.Key> _key;
            public Frame(Action<System.Windows.Input.Key> key)
            {
                _key = key;
                const byte FVIRTKEY = 1;
                _accel = CreateAcceleratorTable(new[] { new ACCEL { fVirt = FVIRTKEY, key = 0x20, cmd = 1 }, new ACCEL { fVirt = FVIRTKEY, key = 0x1B, cmd = 2 } }, 2);
            }
            public int GetWindowContext(IntPtr info)
            {
                if (info == IntPtr.Zero) return unchecked((int)0x80004003);
                Marshal.WriteIntPtr(info, _accel);
                Marshal.WriteInt32(info, IntPtr.Size, 2);
                return 0;
            }
            public int TranslateAccelerator(ref MSG msg)
            {
                const uint WM_KEYDOWN = 0x100;
                if (msg.message == WM_KEYDOWN && (msg.wParam == (IntPtr)0x20 || msg.wParam == (IntPtr)0x1B))
                {
                    var k = msg.wParam == (IntPtr)0x20 ? System.Windows.Input.Key.Space : System.Windows.Input.Key.Escape;
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => _key(k));
                    return 0;
                }
                return 1; // S_FALSE: not ours
            }
            public void Free() { if (_accel != IntPtr.Zero) DestroyAcceleratorTable(_accel); }
        }

        private Frame _frame;
        /// <summary>Space or Esc pressed while the previewer had the keyboard.</summary>
        public event Action<System.Windows.Input.Key> KeyPressed;

        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int SHCreateStreamOnFileEx(string file, uint mode, uint attrs, bool create, IntPtr template, out IStream stream);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr bc, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object item);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(int exStyle, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);

        private const uint CLSCTX_INPROC_SERVER = 1, CLSCTX_LOCAL_SERVER = 4;
        private const uint STGM_READ = 0, STGM_SHARE_DENY_NONE = 0x40;

        private readonly Guid _clsid;
        private readonly string _path;
        private readonly Color _back, _text;
        private IPreviewHandler _handler;
        private IStream _stream;
        private IntPtr _hwnd;
        private bool _started, _disposed;

        public event Action Failed;

        public PreviewHandlerHost(Guid clsid, string path, Color back, Color text)
        {
            _clsid = clsid; _path = path; _back = back; _text = text;
        }

        /// <summary>The previewer registered for an extension (".pdf"), or null.</summary>
        public static Guid? HandlerFor(string ext)
        {
            if (string.IsNullOrEmpty(ext)) return null;
            try
            {
                string Read(string key)
                {
                    using var k = Registry.ClassesRoot.OpenSubKey(key + @"\ShellEx\" + HandlerKey);
                    return k?.GetValue(null) as string;
                }
                var v = Read(ext);
                if (v == null)
                {
                    using var e = Registry.ClassesRoot.OpenSubKey(ext);
                    if (e?.GetValue(null) is string progId && progId.Length > 0) v = Read(progId);
                }
                v ??= Read(@"SystemFileAssociations\" + ext);
                if (v == null)
                {
                    using var e = Registry.ClassesRoot.OpenSubKey(ext);
                    if (e?.GetValue("PerceivedType") is string pt && pt.Length > 0) v = Read(@"SystemFileAssociations\" + pt);
                }
                return Guid.TryParse(v, out var g) ? g : null;
            }
            catch { return null; }
        }

        protected override HandleRef BuildWindowCore(HandleRef parent)
        {
            const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000, WS_CLIPSIBLINGS = 0x04000000;
            _hwnd = CreateWindowExW(0, "Static", "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS, 0, 0, 1, 1, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            // start after layout: the out-of-process call pumps messages and must not run inside a layout pass
            Dispatcher.BeginInvoke(Start, DispatcherPriority.Background);
            return new HandleRef(this, _hwnd);
        }

        private RECT Bounds()
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            return new RECT { Right = Math.Max(1, (int)(ActualWidth * dpi.DpiScaleX)), Bottom = Math.Max(1, (int)(ActualHeight * dpi.DpiScaleY)) };
        }

        private void Start()
        {
            if (_started || _disposed || _hwnd == IntPtr.Zero) return;
            _started = true;
            try
            {
                var iid = typeof(IPreviewHandler).GUID;
                var clsid = _clsid;
                object obj;
                if (CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_LOCAL_SERVER, ref iid, out obj) != 0 &&
                    CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out obj) != 0)
                    throw new COMException("no handler");
                _handler = (IPreviewHandler)obj;

                bool ok = false;
                if (obj is IInitializeWithStream ws && SHCreateStreamOnFileEx(_path, STGM_READ | STGM_SHARE_DENY_NONE, 0, false, IntPtr.Zero, out _stream) == 0)
                {
                    try { ws.Initialize(_stream, STGM_READ); ok = true; } catch { }
                }
                if (!ok && obj is IInitializeWithItem wi)
                {
                    var siid = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");
                    if (SHCreateItemFromParsingName(_path, IntPtr.Zero, ref siid, out var item) == 0)
                        try { wi.Initialize(item, STGM_READ); ok = true; } catch { }
                }
                if (!ok && obj is IInitializeWithFile wf)
                {
                    try { wf.Initialize(_path, STGM_READ); ok = true; } catch { }
                }
                if (!ok) throw new COMException("can't initialize");

                if (obj is IPreviewHandlerVisuals vis)
                {
                    try
                    {
                        vis.SetBackgroundColor((uint)(_back.R | _back.G << 8 | _back.B << 16));
                        vis.SetTextColor((uint)(_text.R | _text.G << 8 | _text.B << 16));
                    }
                    catch { }
                }
                if (obj is IObjectWithSite site)
                {
                    _frame = new Frame(k => KeyPressed?.Invoke(k));
                    try { site.SetSite(_frame); } catch { }
                }
                var r = Bounds();
                _handler.SetWindow(_hwnd, ref r);
                _handler.DoPreview();
                // previewers like Word's take the keyboard when they open: give it back so Space / arrows keep working
                var top = GetAncestor(_hwnd, 2 /*GA_ROOT*/);
                SetFocus(top);
                Dispatcher.BeginInvoke(() => { if (!_disposed) SetFocus(top); }, DispatcherPriority.ApplicationIdle);
            }
            catch
            {
                Release();
                if (!_disposed) Failed?.Invoke();
            }
        }

        protected override void OnWindowPositionChanged(Rect rc)
        {
            base.OnWindowPositionChanged(rc);
            if (_handler == null) return;
            var r = new RECT { Right = Math.Max(1, (int)rc.Width), Bottom = Math.Max(1, (int)rc.Height) };
            try { _handler.SetRect(ref r); } catch { }
        }

        private void Release()
        {
            if (_handler != null)
            {
                if (_handler is IObjectWithSite site) try { site.SetSite(null); } catch { }
                try { _handler.Unload(); } catch { }
                try { Marshal.FinalReleaseComObject(_handler); } catch { }
                _handler = null;
            }
            _frame?.Free();
            _frame = null;
            if (_stream != null)
            {
                try { Marshal.FinalReleaseComObject(_stream); } catch { }
                _stream = null;
            }
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            _disposed = true;
            Release();
            DestroyWindow(hwnd.Handle);
            _hwnd = IntPtr.Zero;
        }

        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            Release();
            base.Dispose(disposing);
        }
    }
}
