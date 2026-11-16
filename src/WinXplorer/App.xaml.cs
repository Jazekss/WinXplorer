using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinXplorer.Services;

namespace WinXplorer
{
    /// <summary>
    /// Command line: WinXplorer.exe [folder | ::thispc] [--pos X,Y] [--split vertical|horizontal --right folder] [--settings]
    /// </summary>
    public sealed class StartOptions
    {
        private const string ThisPcToken = "::thispc";
        public string Path;           // null = restore the saved session
        public System.Windows.Point? ScreenPos;
        public string Split, Right;
        public bool OpenSettings;
        /// <summary>Window rectangle (WPF units) and maximized state, from a restored session.</summary>
        public System.Windows.Rect? Bounds;
        public bool Maximized;

        public static StartOptions Parse(string[] args)
        {
            var o = new StartOptions();
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                switch (a)
                {
                    case "--pos" when i + 1 < args.Length:
                        var p = args[++i].Split(',');
                        if (p.Length == 2 && double.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)
                            && double.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y))
                            o.ScreenPos = new System.Windows.Point(x, y);
                        break;
                    case "--split" when i + 1 < args.Length: o.Split = args[++i]; break;
                    case "--right" when i + 1 < args.Length: o.Right = Unwrap(args[++i]); break;
                    case "--settings": o.OpenSettings = true; break;
                    case "--bounds" when i + 1 < args.Length:
                        var b = args[++i].Split(',');
                        var inv = System.Globalization.CultureInfo.InvariantCulture;
                        if (b.Length >= 4 && double.TryParse(b[0], System.Globalization.NumberStyles.Float, inv, out var bl) && double.TryParse(b[1], System.Globalization.NumberStyles.Float, inv, out var bt)
                            && double.TryParse(b[2], System.Globalization.NumberStyles.Float, inv, out var bw) && double.TryParse(b[3], System.Globalization.NumberStyles.Float, inv, out var bh) && bw > 100 && bh > 100)
                            o.Bounds = new System.Windows.Rect(bl, bt, bw, bh);
                        o.Maximized = b.Length >= 5 && b[4] == "max";
                        break;
                    default:
                        if (!a.StartsWith("--") && o.Path == null) o.Path = Unwrap(a);
                        break;
                }
            }
            return o;
        }

        private static string Unwrap(string a) => string.Equals(a, ThisPcToken, StringComparison.OrdinalIgnoreCase) ? Services.FileSystem.ThisPC : a;

        private static string Quote(string path)
        {
            if (string.IsNullOrEmpty(path)) return "\"" + ThisPcToken + "\"";
            var t = path.TrimEnd('\\');
            if (t.EndsWith(':')) t += @"\\"; // "C:\\" – a single backslash would escape the closing quote
            return "\"" + t + "\"";
        }

        public static string Build(string path, System.Windows.Point? pos = null, string split = null, string right = null)
        {
            var s = Quote(path);
            if (pos is System.Windows.Point p) s += FormattableString.Invariant($" --pos {p.X:0},{p.Y:0}");
            if (!string.IsNullOrEmpty(split) && split != "none") s += " --split " + split + " --right " + Quote(right);
            return s;
        }
    }

    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Never show "There is no disk in the drive" system dialogs while browsing.
            Interop.Native.SetErrorMode(0x0001 | 0x8000);

            // background index crawl (started by the file manager; exits when done, so its memory is freed)
            if (e.Args.Length > 0 && e.Args[0] == "--build-index")
            {
                SearchIndex.BuildMain(e.Args);
                Shutdown();
                return;
            }

            if (e.Args.Length == 1 && e.Args[0] == "--warmup")
            {
                // installer: read every DLL of the app once (antivirus scans and the disk cache), then quit
                var dir = AppContext.BaseDirectory;
                System.Threading.Tasks.Parallel.ForEach(System.IO.Directory.EnumerateFiles(dir, "*.dll"), f =>
                {
                    try { System.Reflection.AssemblyName.GetAssemblyName(f); } catch { }
                    try { using var s = System.IO.File.OpenRead(f); var b = new byte[1 << 16]; while (s.Read(b, 0, b.Length) > 0) { } } catch { }
                });
                foreach (var f in System.IO.Directory.EnumerateFiles(dir, "*.dll"))
                    try { System.Reflection.Assembly.LoadFrom(f); } catch { }
                Shutdown();
                return;
            }

            if (e.Args.Length == 1 && e.Args[0] == "--restore-windows")
            {
                AppState.Load();
                WinXplorer.MainWindow.RestoreWindows();
                Shutdown();
                return;
            }

            if (e.Args.Length == 2 && e.Args[0] == "--export-icon")
            {
                ExportIcon(e.Args[1]);
                Shutdown();
                return;
            }

            base.OnStartup(e);
            AppState.Load();
            Loc.Init(AppState.Settings.Language);
            // the uninstaller: give folders back to Windows Explorer and drop the archive menu entries
            if (e.Args.Length == 1 && e.Args[0] == "--unregister")
            {
                DefaultFileManager.Apply(false);
                Associations.Apply(false);
                Associations.ApplyViewFiles(false);
                Shutdown();
                return;
            }
            ThemeManager.Init();

            DispatcherUnhandledException += (_, ex) =>
            {
                ex.Handled = MainWindow?.IsLoaded == true;
                if (!ex.Handled) MessageBox.Show(ex.Exception.ToString(), "WinXplorer");
                try { Directory.CreateDirectory(AppState.DataDir); File.AppendAllText(Path.Combine(AppState.DataDir, "error.log"), $"{DateTime.Now:u} {ex.Exception}\n\n"); } catch { }
            };

            FileIcons.Archive();   // the archive icon is drawn once here (WPF drawing wants the UI thread)
            var opt = StartOptions.Parse(e.Args);
            if (opt.Path != null && opt.Split == null && opt.ScreenPos == null && opt.Bounds == null && !opt.OpenSettings && WinXplorer.MainWindow.ActivateExisting(opt.Path))
            {
                Shutdown();
                return;
            }
            var win = new MainWindow(opt);
            MainWindow = win;
            win.Show();
            SearchIndex.Start();
            ExplorerWatch.Start();
            new System.Threading.Thread(() =>
            {
                Associations.Apply(AppState.Settings.ArchiveOpenWith);
                Associations.ApplyViewFiles(true);   // view.wxv opens in WinXplorer, with its own icon
                // keeps the folder association pointing at this exe (it may have moved or been updated)
                if (AppState.Settings.DefaultFileManager) DefaultFileManager.Apply(true);
                Archives.CleanTemp();
            }) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal }.Start();
            // Windows switched light/dark: the archive icon in Explorer's menu follows
            ThemeManager.ThemeChanged += () =>
            {
                if (AppState.Settings.ArchiveOpenWith)
                    new System.Threading.Thread(() => Associations.Apply(true)) { IsBackground = true }.Start();
            };
        }

        /// <summary>Loads the app icon (SVG) as an ImageSource.</summary>
        public static ImageSource LoadAppIcon() => LoadSvgResource("WinXplorer.svg");

        /// <summary>An SVG compiled into the app (Assets/…) as a drawing.</summary>
        public static ImageSource LoadSvgResource(string name)
        {
            try
            {
                var info = GetResourceStream(new Uri("pack://application:,,,/WinXplorer;component/Assets/" + name));
                if (info == null) return null;
                var settings = new SharpVectors.Renderers.Wpf.WpfDrawingSettings { IncludeRuntime = false };
                using var reader = new SharpVectors.Converters.FileSvgReader(settings);
                var d = reader.Read(info.Stream);
                var img = new DrawingImage(d);
                img.Freeze();
                return img;
            }
            catch { return null; }
        }

        /// <summary>A PNG compiled into the app (Assets/…).</summary>
        public static ImageSource LoadPngResource(string name)
        {
            try
            {
                var bi = new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri("pack://application:,,,/WinXplorer;component/Assets/" + name);
                bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch { return null; }
        }

        /// <summary>Renders the SVG logo into a multi-size .ico (used at build time for the exe icon).</summary>
        private static void ExportIcon(string path) => WriteIco(LoadAppIcon(), path);

        /// <summary>Writes a drawing as a multi-size .ico (16 … 256 px PNG frames). Needs an STA thread.</summary>
        public static void WriteIco(ImageSource img, string path)
        {
            if (img == null) return;
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
            var pngs = sizes.Select(s =>
            {
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    double scale = Math.Min(s / img.Width, s / img.Height);
                    double w = img.Width * scale, h = img.Height * scale;
                    dc.DrawImage(img, new Rect((s - w) / 2, (s - h) / 2, w, h));
                }
                var rtb = new RenderTargetBitmap(s, s, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using var ms = new MemoryStream();
                enc.Save(ms);
                return ms.ToArray();
            }).ToArray();

            using var fs = File.Create(path);
            using var bw = new BinaryWriter(fs);
            bw.Write((short)0); bw.Write((short)1); bw.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                bw.Write((byte)0); bw.Write((byte)0);
                bw.Write((short)1); bw.Write((short)32);
                bw.Write(pngs[i].Length); bw.Write(offset);
                offset += pngs[i].Length;
            }
            foreach (var p in pngs) bw.Write(p);
        }
    }
}
