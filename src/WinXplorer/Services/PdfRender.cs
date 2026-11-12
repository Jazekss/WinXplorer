using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media.Imaging;

namespace WinXplorer.Services
{
    /// <summary>
    /// Renders the first page of a PDF – and of an Illustrator file, which is a PDF inside – with Windows' own
    /// PDF engine (Windows.Data.Pdf), called through raw WinRT interop so no extra SDK has to be shipped.
    /// </summary>
    public static unsafe class PdfRender
    {
        private static readonly Guid IID_IPdfDocumentStatics = new("433a0b5f-c007-4788-90f2-08143d922599");
        private static readonly Guid IID_IPdfDocument = new("ac7ebedd-80fa-4089-846e-81b77ff5a86c");
        private static readonly Guid IID_IPdfPage = new("9db4b0c8-5320-4cfc-ad76-493fdad0e594");
        private static readonly Guid IID_IPdfPageRenderOptions = new("3c98056f-b7cf-4c29-9a04-52d90267f425");
        private static readonly Guid IID_IRandomAccessStream = new("905a0fe1-bc53-11df-8c49-001e4fc686da");
        private static readonly Guid IID_IAsyncInfo = new("00000036-0000-0000-c000-000000000046");

        [DllImport("combase.dll")] private static extern int RoInitialize(int type);
        [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr classId, in Guid iid, out IntPtr factory);
        [DllImport("combase.dll")] private static extern int RoActivateInstance(IntPtr classId, out IntPtr instance);
        [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string s, int len, out IntPtr hstring);
        [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr hstring);
        [DllImport("shcore.dll")] private static extern int CreateRandomAccessStreamOverStream(IntPtr stream, int options, in Guid iid, out IntPtr ras);
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] private static extern int SHCreateStreamOnFileEx(string file, uint mode, uint attrs, bool create, IntPtr template, out IntPtr stream);
        [DllImport("ole32.dll")] private static extern int CreateStreamOnHGlobal(IntPtr hglobal, bool deleteOnRelease, out IntPtr stream);
        [DllImport("ole32.dll")] private static extern int GetHGlobalFromStream(IntPtr stream, out IntPtr hglobal);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr h);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr h);

        private static void* Slot(IntPtr obj, int i) => (*(void***)obj)[i];
        private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
        private static void Release(ref IntPtr p) { if (p != IntPtr.Zero) { Marshal.Release(p); p = IntPtr.Zero; } }

        /// <summary>Waits for an IAsyncOperation / IAsyncAction and returns its result pointer (IntPtr.Zero for an action).</summary>
        private static IntPtr Await(IntPtr op, bool hasResult, int timeoutMs = 20000)
        {
            Check(Marshal.QueryInterface(op, in IID_IAsyncInfo, out var info));
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (true)
                {
                    int status;
                    Check(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(info, 7))(info, &status));   // get_Status
                    if (status == 1) break;                                   // Completed
                    if (status >= 2)                                          // Canceled / Error
                    {
                        int err;
                        ((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(info, 8))(info, &err);         // get_ErrorCode
                        Check(err < 0 ? err : unchecked((int)0x80004004));
                    }
                    if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException();
                    Thread.Sleep(5);
                }
                IntPtr result = IntPtr.Zero;
                // IAsyncOperation<T>.GetResults / IAsyncAction.GetResults: slot 8 (after IInspectable + Completed get/put)
                if (hasResult) Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(op, 8))(op, &result));
                else Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(op, 8))(op));
                return result;
            }
            finally { Marshal.Release(info); }
        }

        /// <summary>First page as a bitmap whose longer side is about <paramref name="longSide"/> pixels; null when it can't be read.</summary>
        public static BitmapSource FirstPage(string path, int longSide = 4096)
        {
            // WinRT async calls complete through the apartment: an STA thread that just waits would never see them
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.MTA)
            {
                BitmapSource r = null;
                var t = new Thread(() => r = FirstPage(path, longSide)) { IsBackground = true };
                t.SetApartmentState(ApartmentState.MTA);
                t.Start(); t.Join();
                return r;
            }
            RoInitialize(1); // MTA
            IntPtr cls = IntPtr.Zero, statics = IntPtr.Zero, inFile = IntPtr.Zero, inRas = IntPtr.Zero, op = IntPtr.Zero, doc = IntPtr.Zero, docI = IntPtr.Zero,
                   page = IntPtr.Zero, optCls = IntPtr.Zero, optObj = IntPtr.Zero, opt = IntPtr.Zero, outStm = IntPtr.Zero, outRas = IntPtr.Zero, act = IntPtr.Zero;
            try
            {
                Check(WindowsCreateString("Windows.Data.Pdf.PdfDocument", "Windows.Data.Pdf.PdfDocument".Length, out cls));
                Check(RoGetActivationFactory(cls, in IID_IPdfDocumentStatics, out statics));
                Check(SHCreateStreamOnFileEx(path, 0x40 /*STGM_READ | STGM_SHARE_DENY_NONE*/, 0, false, IntPtr.Zero, out inFile));
                Check(CreateRandomAccessStreamOverStream(inFile, 0, in IID_IRandomAccessStream, out inRas));
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(statics, 8))(statics, inRas, &op));   // LoadFromStreamAsync(stream)
                doc = Await(op, true);
                Check(Marshal.QueryInterface(doc, in IID_IPdfDocument, out docI));
                Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(docI, 6))(docI, 0, &page));            // GetPage(0)
                IntPtr pageI;
                Check(Marshal.QueryInterface(page, in IID_IPdfPage, out pageI));
                Release(ref page); page = pageI;
                float* size = stackalloc float[2];
                Check(((delegate* unmanaged[Stdcall]<IntPtr, float*, int>)Slot(page, 10))(page, size));                    // get_Size (DIPs)
                double w = Math.Max(1, size[0]), h = Math.Max(1, size[1]);
                double scale = longSide / Math.Max(w, h);
                uint dw = (uint)Math.Max(1, Math.Round(w * scale)), dh = (uint)Math.Max(1, Math.Round(h * scale));

                Check(WindowsCreateString("Windows.Data.Pdf.PdfPageRenderOptions", "Windows.Data.Pdf.PdfPageRenderOptions".Length, out optCls));
                Check(RoActivateInstance(optCls, out optObj));
                Check(Marshal.QueryInterface(optObj, in IID_IPdfPageRenderOptions, out opt));
                Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(opt, 9))(opt, dw));                          // put_DestinationWidth
                Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(opt, 11))(opt, dh));                         // put_DestinationHeight

                Check(CreateStreamOnHGlobal(IntPtr.Zero, true, out outStm));
                Check(CreateRandomAccessStreamOverStream(outStm, 0, in IID_IRandomAccessStream, out outRas));
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, int>)Slot(page, 7))(page, outRas, opt, &act)); // RenderToStreamAsync(out, options)
                Await(act, false, 60000);

                Check(GetHGlobalFromStream(outStm, out var hg));
                var bytes = ReadStream(outStm, hg);
                using var ms = new MemoryStream(bytes);
                var frame = BitmapFrame.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                // a plain pixel copy: the decoder frame is tied to this short-lived thread
                var bmp = new WriteableBitmap(frame);
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
            finally
            {
                Release(ref act); Release(ref outRas); Release(ref outStm); Release(ref opt); Release(ref optObj);
                Release(ref page); Release(ref docI); Release(ref doc); Release(ref op); Release(ref inRas); Release(ref inFile); Release(ref statics);
                if (cls != IntPtr.Zero) WindowsDeleteString(cls);
                if (optCls != IntPtr.Zero) WindowsDeleteString(optCls);
            }
        }

        /// <summary>The bytes written to an HGLOBAL stream (its size comes from IStream::Seek to the end).</summary>
        private static byte[] ReadStream(IntPtr stm, IntPtr hg)
        {
            long end;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, long, int, long*, int>)Slot(stm, 5))(stm, 0, 2 /*STREAM_SEEK_END*/, &end)); // IStream::Seek
            var bytes = new byte[end];
            var p = GlobalLock(hg);
            try { Marshal.Copy(p, bytes, 0, (int)end); } finally { GlobalUnlock(hg); }
            return bytes;
        }
    }
}
