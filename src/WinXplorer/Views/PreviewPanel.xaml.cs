using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Highlighting;
using WinXplorer.Interop;
using WinXplorer.Models;
using WinXplorer.Services;
using WinXplorer.ViewModels;

namespace WinXplorer.Views
{
    public sealed class InfoRow : ObservableObject
    {
        public string Label { get; init; }
        private string _value;
        public string Value { get => _value; set => Set(ref _value, value); }
    }

    public partial class PreviewPanel : UserControl
    {
        private readonly ObservableCollection<InfoRow> _info = new();

        /// <summary>Hover popup: every file shows only the short facts – size, contents, format, dimensions, length, dates
        /// (and the folder for search results) – in two columns that fit without scrolling; no action buttons.</summary>
        public bool HoverMode
        {
            get => _hoverMode;
            set
            {
                _hoverMode = value;
                if (!value) return;
                Actions.Visibility = Visibility.Collapsed;
                InfoColumnCount = 2;
                Details.MaxHeight = 96;
                Details.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            }
        }
        private bool _hoverMode;
        private bool _hoverFilter, _hoverLocation;

        private void FilterHoverInfo()
        {
            if (!_hoverFilter) return;
            var keep = new[] { Loc.T("Size"), Loc.T("Contains"), Loc.T("Format"), Loc.T("Dimensions"), Loc.T("Duration"), Loc.T("Created"), Loc.T("ColModified"), _hoverLocation ? Loc.T("Location") : null };
            for (int i = _info.Count - 1; i >= 0; i--)
                if (Array.IndexOf(keep, _info[i].Label) < 0) _info.RemoveAt(i);
            var size = _info.FirstOrDefault(r => r.Label == Loc.T("Size"));
            if (size != null && size.Value.Contains("  (")) size.Value = size.Value[..size.Value.IndexOf("  (", StringComparison.Ordinal)];
        }
        private CancellationTokenSource _cts;
        private FileItem _item;
        private PaneViewModel _pane;
        private readonly DispatcherTimer _mediaTimer;
        private bool _playing;
        private const long MaxTextBytes = 2 * 1024 * 1024;
        private readonly Brush _checker;

        private void SetImage(ImageSource img, bool checker)
        {
            ResetZoom();
            Img.Source = img;
            Img.StretchDirection = img is DrawingImage ? StretchDirection.Both : StretchDirection.DownOnly;
            ImageFrame.Background = checker ? _checker : Brushes.Transparent;
            ShowOnly(ImageFrame);
        }

        public PreviewPanel()
        {
            InitializeComponent();
            InfoList.ItemsSource = _info;
            _checker = ImageFrame.Background;
            _mediaTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _mediaTimer.Tick += (_, _) => UpdateSeek();
            Code.Options.EnableHyperlinks = false;
            Code.Options.EnableEmailHyperlinks = false;
            ThemeManager.ThemeChanged += () => { if (_item != null && CodeFrame.Visibility == Visibility.Visible) Show(_item, _pane); };
            SetupZoom();
        }

        // ---------------- image zoom (wheel, at the cursor) and pan (drag) ----------------
        private Matrix _zoom = Matrix.Identity;
        private Point _panLast;
        private bool _panning, _panned;

        private void ResetZoom()
        {
            _zoom = Matrix.Identity;
            Img.RenderTransform = Transform.Identity;
            ImageFrame.Cursor = Cursors.Hand;
        }

        private void SetupZoom()
        {
            ImageFrame.PreviewMouseWheel += (_, e) =>
            {
                if (Img.Source == null) return;
                e.Handled = true;
                double f = e.Delta > 0 ? 1.25 : 1 / 1.25;
                double scale = _zoom.M11 * f;
                if (scale <= 1.001) { ResetZoom(); return; }
                if (scale > 40) return;
                var p = e.GetPosition(Img);           // in the picture's own (untransformed) coordinates
                _zoom.ScaleAtPrepend(f, f, p.X, p.Y);
                Img.RenderTransform = new MatrixTransform(_zoom);
                ImageFrame.Cursor = Cursors.SizeAll;
            };
            ImageFrame.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2 && _item?.FullPath is { } ip && File.Exists(ip)
                    && Path.GetExtension(ip).ToLowerInvariant() is not (".svg" or ".svgz" or ".heic" or ".heif" or ".hif" or ".avif"))
                {
                    // double click: the system's default picture viewer (formats it can't show stay here)
                    e.Handled = true;
                    FileOps.Open(ip);
                    return;
                }
                _panned = false;
                if (_zoom.IsIdentity) return;
                _panning = true;
                _panLast = e.GetPosition(ImageFrame);
                ImageFrame.CaptureMouse();
            };
            ImageFrame.PreviewMouseMove += (_, e) =>
            {
                if (!_panning || e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(ImageFrame);
                var d = p - _panLast;
                if (!_panned && Math.Abs(d.X) < 3 && Math.Abs(d.Y) < 3) return;
                _panned = true;
                _panLast = p;
                _zoom.Translate(d.X, d.Y);
                Img.RenderTransform = new MatrixTransform(_zoom);
            };
            ImageFrame.PreviewMouseLeftButtonUp += (_, _) =>
            {
                if (!_panning) return;
                _panning = false;
                ImageFrame.ReleaseMouseCapture();
            };
        }

        private PreviewHandlerHost _handlerHost;

        /// <summary>Quick view window: more room for the content – no header (the window title has the name), details in two columns.</summary>
        private bool _quick;

        public bool QuickView
        {
            set
            {
                _quick = value;
                Details.MaxHeight = value ? 150 : double.PositiveInfinity;
                Header.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
                InfoColumnCount = 0;   // auto: as many columns as the width allows
                Host.Margin = value ? new Thickness(12, 8, 12, 8) : new Thickness(12, 4, 12, 8);
            }
        }

        public static readonly DependencyProperty InfoColumnCountProperty =
            DependencyProperty.Register(nameof(InfoColumnCount), typeof(int), typeof(PreviewPanel), new PropertyMetadata(0));
        public int InfoColumnCount { get => (int)GetValue(InfoColumnCountProperty); set => SetValue(InfoColumnCountProperty, value); }

        /// <summary>Raised when the Windows previewer (Word, PDF...) had the keyboard and Space / Esc was pressed in it.</summary>
        public event Action<System.Windows.Input.Key> HandlerKey;

        private void DropHandler()
        {
            var h = _handlerHost;
            if (h == null) return;
            _handlerHost = null;
            HandlerFrame.Child = null;
            try { h.Dispose(); } catch { }
        }

        public void Clear()
        {
            _cts?.Cancel();
            StopMedia();
            DropHandler();
            Img.Source = null;
            Code.Document.Text = "";
            ImageFrame.Visibility = CodeFrame.Visibility = ArchiveFrame.Visibility = MediaHost.Visibility = HandlerFrame.Visibility = Visibility.Collapsed;
            ArchiveFrame.Child = null;
            MessagePanel.Visibility = Visibility.Visible;
            Message.Text = Loc.T("SelectToPreview");
            MessageGlyph.Text = "";
            _hoverFilter = false; _info.CollectionChanged -= OnHoverInfoChanged; _info.Clear();
            Actions.Children.Clear();
            HeaderIcon.Source = null;
            HeaderName.Text = "";
            HeaderType.Text = "";
            _item = null;
        }

        private void ShowOnly(UIElement el)
        {
            ImageFrame.Visibility = el == ImageFrame ? Visibility.Visible : Visibility.Collapsed;
            CodeFrame.Visibility = el == CodeFrame ? Visibility.Visible : Visibility.Collapsed;
            ArchiveFrame.Visibility = el == ArchiveFrame ? Visibility.Visible : Visibility.Collapsed;
            if (el != ArchiveFrame) ArchiveFrame.Child = null;
            MediaHost.Visibility = el == MediaHost ? Visibility.Visible : Visibility.Collapsed;
            HandlerFrame.Visibility = el == HandlerFrame ? Visibility.Visible : Visibility.Collapsed;
            if (el != HandlerFrame) DropHandler();
            MessagePanel.Visibility = el == MessagePanel ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowMessage(string text, string glyph = "")
        {
            Message.Text = text;
            MessageGlyph.Text = glyph;
            ShowOnly(MessagePanel);
        }

        private void Add(string label, string value)
        {
            if (!string.IsNullOrWhiteSpace(value)) _info.Add(new InfoRow { Label = label, Value = value });
        }

        private InfoRow AddRow(string label, string value)
        {
            var r = new InfoRow { Label = label, Value = value };
            _info.Add(r);
            return r;
        }

        private void AddAction(string glyph, string text, Action a)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = glyph, Style = (Style)FindResource("Glyph"), FontSize = 13 });
            sp.Children.Add(new TextBlock { Text = text, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var b = new Button { Content = sp, Style = (Style)FindResource("IconBtn"), Margin = new Thickness(0, 0, 2, 2) };
            b.Click += (_, _) => a();
            Actions.Children.Add(b);
        }

        public async void Show(FileItem item, PaneViewModel pane)
        {
            _cts?.Cancel();
            var cts = _cts = new CancellationTokenSource();
            var ct = cts.Token;
            StopMedia();
            DropHandler();
            Img.Source = null;
            _hoverFilter = false; _info.CollectionChanged -= OnHoverInfoChanged; _info.Clear();
            Actions.Children.Clear();
            _item = item;
            _pane = pane;
            if (HoverMode)
            {
                _hoverFilter = true;
                _hoverLocation = pane?.IsRecursiveSearch == true;
                _info.CollectionChanged += OnHoverInfoChanged;
            }

            if (item == null)
            {
                if (pane == null || pane.IsThisPC) { HeaderIcon.Source = null; HeaderName.Text = pane?.Title ?? ""; HeaderType.Text = ""; ShowMessage(Loc.T("SelectToPreview")); return; }
                // nothing selected: describe the current folder
                item = new FileItem { Name = pane.Title, FullPath = pane.Path, IsDirectory = true };
                try { var di = new DirectoryInfo(pane.Path); item = new FileItem { Name = pane.Title, FullPath = pane.Path, IsDirectory = true, Modified = di.LastWriteTime, Created = di.CreationTime, Attributes = (uint)di.Attributes }; } catch { }
            }

            if (item.InArchive && !item.IsDirectory)
            {
                // a file inside an archive: preview the extracted copy (small files only – it's extracted to show it)
                if (item.Size > 64L * 1024 * 1024 || Archives.Split(item.FullPath) is { } sp && await Task.Run(() => Archives.IsSolid(sp.archive))) { HeaderIcon.Source = item.Icon; HeaderName.Text = item.Name; HeaderType.Text = item.TypeName; ShowMessage(Loc.T("ArchiveBigFile"), "\uE7B8"); return; }
                var inner = item.FullPath;
                var real = await Task.Run(() => { try { return Archives.Materialize(new[] { inner }).FirstOrDefault(); } catch { return null; } });
                if (ct.IsCancellationRequested) return;
                var fi = real == null ? null : FileSystem.Stat(real, Path.GetDirectoryName(real));
                if (fi == null) { ShowMessage(Loc.T("NoCodec"), "\uE7B8"); return; }
                item = fi;
            }
            else if (item.InArchive)
            {
                HeaderIcon.Source = item.Icon; HeaderName.Text = item.Name; HeaderType.Text = item.TypeName;
                ShowMessage(Loc.T("ArchiveFolder"), "\uE8B7");
                return;
            }
            HeaderIcon.Source = IconService.GetPathIcon(item.FullPath, true) ?? item.Icon;
            HeaderName.Text = item.Name;
            HeaderType.Text = item.TypeName;
            var path = item.FullPath;

            if (item.IsDirectory)
            {
                ShowFolder(item, ct);
                return;
            }

            // ---- common file info ----
            Add(Loc.T("Size"), $"{FileItem.FormatSize(item.Size)}  ({item.Size:N0} {Loc.T("Bytes")})");
            Add(Loc.T("ColModified"), Fmt(item.Modified));
            Add(Loc.T("Created"), Fmt(item.Created));
            try { Add(Loc.T("Accessed"), Fmt(File.GetLastAccessTime(path))); } catch { }
            Add(Loc.T("Attributes"), AttrText(item.Attributes));
            Add(Loc.T("Location"), Path.GetDirectoryName(path));

            AddAction("", Loc.T("Open"), () => FileOps.Open(path));
            AddAction("", Loc.T("OpenWith"), () => FileOps.OpenWith(path));
            AddAction("", Loc.T("CopyPath"), () => FileOps.SetClipboardText(path));
            AddAction("", Loc.T("Properties"), () => FileOps.ShowProperties(path));
            AddAction("", Loc.T("Hashes"), () => ComputeHashes(path, ct));

            var kind = FileTypes.Kind(path);
            if (_hoverFilter) FilterHoverInfo();
            if (kind == PreviewKind.None && item.Size < MaxTextBytes * 4 && FileTypes.MayBeText(path) && FileTypes.LooksLikeText(path)) kind = PreviewKind.Text;
            if (kind == PreviewKind.None && FileTypes.Executable.Contains(item.Extension)) { await ShowProgram(path, ct); return; }
            if (kind == PreviewKind.None && FileTypes.Archive.Contains(item.Extension) && await ShowArchive(path, ct)) return;

            switch (kind)
            {
                case PreviewKind.Image:
                case PreviewKind.Svg:
                    await ShowImage(path, ct);
                    break;
                case PreviewKind.Video:
                case PreviewKind.Audio:
                    ShowMedia(path, kind == PreviewKind.Audio);
                    break;
                case PreviewKind.Text:
                    await ShowText(path, item.Size, ct);
                    break;
                default:
                    await ShowOther(path, ct);
                    break;
            }
        }

        private void OnHoverInfoChanged(object s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (!_hoverFilter) { _info.CollectionChanged -= OnHoverInfoChanged; return; }
            Dispatcher.BeginInvoke(FilterHoverInfo);
        }

        private static string Fmt(DateTime d) => d == DateTime.MinValue ? "" : d.ToString("G");

        private static string AttrText(uint a)
        {
            var l = new List<string>();
            if ((a & 0x1) != 0) l.Add(Loc.T("AttrReadOnly"));
            if ((a & 0x2) != 0) l.Add(Loc.T("AttrHidden"));
            if ((a & 0x4) != 0) l.Add(Loc.T("AttrSystem"));
            if ((a & 0x20) != 0) l.Add(Loc.T("AttrArchive"));
            if ((a & 0x800) != 0) l.Add(Loc.T("AttrCompressed"));
            if ((a & 0x4000) != 0) l.Add(Loc.T("AttrEncrypted"));
            if ((a & 0x400) != 0) l.Add(Loc.T("AttrLink"));
            if ((a & 0x400000) != 0 || (a & 0x1000) != 0) l.Add(Loc.T("AttrOnline"));
            return string.Join(", ", l);
        }

        // ---------------- folders ----------------
        /// <summary>Folders show their contents as a table (quick view and hover preview).</summary>
        public bool ShowFolderContents { get; set; }

        private async Task ShowFolderList(string path, CancellationToken ct)
        {
            var rows = await Task.Run(() =>
            {
                var entries = new List<(string path, long size, DateTime mod, bool dir)>();
                bool more = false;
                try
                {
                    foreach (var i in FileSystem.List(path, AppState.Settings.ShowHidden, ct))
                    {
                        if (entries.Count >= 5000) { more = true; break; }
                        entries.Add((i.Name, i.Size, i.Modified, i.IsDirectory));
                    }
                }
                catch { }
                return ArchiveTable.Rows(entries, more);
            });
            if (ct.IsCancellationRequested) return;
            if (rows.Count == 0) { ShowMessage(Loc.T("EmptyFolder"), "\uE8B7"); return; }
            ArchiveFrame.Child = ArchiveTable.Build(rows);
            ShowOnly(ArchiveFrame);
        }

        private async void ShowFolder(FileItem item, CancellationToken ct)
        {
            var path = item.FullPath;
            SetImage(Shell.GetThumbnail(path, 256), false);
            // quick view (Space on a folder): what's inside, without opening it
            if (_quick || ShowFolderContents) _ = ShowFolderList(path, ct);

            if (item.IsDrive)
            {
                try
                {
                    var d = new DriveInfo(path);
                    if (d.IsReady)
                    {
                        Add(Loc.T("Free"), FileItem.FormatSize(d.AvailableFreeSpace));
                        Add(Loc.T("Used"), FileItem.FormatSize(d.TotalSize - d.TotalFreeSpace));
                        Add(Loc.T("Capacity"), FileItem.FormatSize(d.TotalSize));
                        Add(Loc.T("FileSystem"), d.DriveFormat);
                        Add(Loc.T("Label"), d.VolumeLabel);
                    }
                }
                catch { }
            }
            var contents = AddRow(Loc.T("Contains"), Loc.T("Counting"));
            if (!item.IsDrive)
            {
                Add(Loc.T("ColModified"), Fmt(item.Modified));
                Add(Loc.T("Created"), Fmt(item.Created));
                Add(Loc.T("Attributes"), AttrText(item.Attributes));
            }
            Add(Loc.T("Path"), path);

            var sizeRow = (InfoRow)null;
            AddAction("", Loc.T("CalcSize"), () =>
            {
                sizeRow ??= AddRow(Loc.T("Size"), Loc.T("Counting"));
                _ = CalcSize(path, sizeRow, ct);
            });
            AddAction("", Loc.T("Terminal"), () => FileOps.OpenTerminal(path));
            AddAction("", Loc.T("CopyPath"), () => FileOps.SetClipboardText(path));
            AddAction("", Loc.T("Properties"), () => FileOps.ShowProperties(path));

            try
            {
                var (files, dirs) = await Task.Run(() =>
                {
                    int f = 0, d = 0;
                    foreach (var i in FileSystem.List(path, AppState.Settings.ShowHidden, ct)) { if (i.IsDirectory) d++; else f++; }
                    return (f, d);
                }, ct);
                if (!ct.IsCancellationRequested) contents.Value = Loc.FilesFolders(files, dirs);
            }
            catch (Exception ex) { if (!ct.IsCancellationRequested) contents.Value = ex.Message; }
        }

        private static async Task CalcSize(string path, InfoRow row, CancellationToken ct)
        {
            long total = 0; int files = 0, dirs = 0;
            var last = Environment.TickCount64;
            var disp = Application.Current.Dispatcher;
            await Task.Run(() =>
            {
                var stack = new Stack<string>();
                stack.Push(path);
                while (stack.Count > 0 && !ct.IsCancellationRequested)
                {
                    var d = stack.Pop();
                    List<FileItem> list;
                    try { list = FileSystem.List(d, true, ct); } catch { continue; }
                    foreach (var i in list)
                    {
                        if (i.IsDirectory) { dirs++; if ((i.Attributes & 0x400) == 0) stack.Push(i.FullPath); }
                        else { files++; total += i.Size; }
                    }
                    if (Environment.TickCount64 - last > 200)
                    {
                        last = Environment.TickCount64;
                        long t = total; int f = files, dd = dirs;
                        disp.BeginInvoke(() => row.Value = $"{FileItem.FormatSize(t)} … ({Loc.FilesFolders(f, dd)})");
                    }
                }
            }, ct).ContinueWith(_ => { });
            if (!ct.IsCancellationRequested) row.Value = $"{FileItem.FormatSize(total)}  ({total:N0} {Loc.T("Bytes")})\n{Loc.FilesFolders(files, dirs)}";
        }

        // ---------------- images ----------------
        private async Task ShowImage(string path, CancellationToken ct)
        {
            ShowMessage(Loc.T("Loading"), "");
            try
            {
                var (img, info) = await ImageLoader.RunSta(() => ImageLoader.Load(path, 1600));
                if (ct.IsCancellationRequested) return;
                if (img == null)
                {
                    ShowMessage(Loc.T("NoCodec"), "");
                    // this PC lacks Microsoft's free decoder: one click to its Store page
                    if (FileTypes.MissingCodec(path) is { } codec)
                    {
                        Message.Text = Loc.F("CodecMissing", Loc.T(codec.nameKey));
                        for (int i = 0; i < codec.storeIds.Length; i++)
                        {
                            var sid = codec.storeIds[i];
                            AddAction("", Loc.T("GetCodec") + (codec.storeIds.Length > 1 ? $" ({i + 1})" : ""), () => FileTypes.OpenStore(sid));
                        }
                    }
                    return;
                }
                SetImage(img, true);
                int at = 0;
                void Ins(string l, string v) { if (!string.IsNullOrWhiteSpace(v)) _info.Insert(at++, new InfoRow { Label = l, Value = v }); }
                if (info.Width > 0) Ins(Loc.T("Dimensions"), $"{info.Width} × {info.Height} px" + (info.Width * (long)info.Height > 1_000_000 ? $"  ({info.Width * (long)info.Height / 1e6:0.#} MP)" : ""));
                Ins(Loc.T("Format"), info.Format + (info.FromShell ? " (" + Loc.T("ShellPreview") + ")" : ""));
                if (info.Bits > 0) Ins(Loc.T("BitDepth"), info.Bits + " bit");
                if (info.DpiX > 0 && Math.Abs(info.DpiX - 96) > 0.5) Ins("DPI", info.DpiX.ToString("0"));
                if (info.Frames > 1) Ins(Loc.T("Frames"), info.Frames.ToString());
                Ins(Loc.T("Camera"), info.Camera);
                Ins(Loc.T("DateTaken"), info.DateTaken);
                Ins(Loc.T("Exposure"), info.Exposure);
            }
            catch (Exception ex) { if (!ct.IsCancellationRequested) ShowMessage(ex.Message, ""); }
        }

        private void ImageFrame_Click(object sender, MouseButtonEventArgs e)
        {
            if (_panned) { _panned = false; return; }   // that was a drag to move the zoomed picture
            if (_item != null && !_item.IsDirectory && _pane != null) MainWindow.Instance.ShowViewer(_pane, _item);
        }

        // ---------------- other files: shell thumbnail or hex ----------------
        private async Task ShowOther(string path, CancellationToken ct)
        {
            // a Windows previewer for this type (PDF, Word, Excel, PowerPoint, mail...)?
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var clsid = PreviewHandlerHost.HandlerFor(ext);
            // PDF: Windows' own engine draws the first page sharp (zoomable); the side panel always uses it,
            // quick view only when no PDF previewer is installed (that one shows every page)
            if (ext == ".pdf" && (!_quick || clsid == null) && await ShowPdf(path, ct)) return;
            if (ct.IsCancellationRequested) return;
            // the side panel shows documents as their thumbnail or icon next to the details – Space opens the full preview
            if (!_quick) { await ShowThumbOrIcon(path, ct); return; }
            // Word documents on a PC without Office: their text
            if (clsid == null && ext is ".docx" or ".docm" && await ShowDocxText(path, ct)) return;
            // Illustrator files are PDF inside: the PDF previewer shows them in full quality
            if (clsid == null && ext is ".ai" or ".ait") clsid = PreviewHandlerHost.HandlerFor(".pdf");
            if (clsid != null)
            {
                Color C(string key, Color fallback) => (TryFindResource(key) as SolidColorBrush)?.Color ?? fallback;
                var h = new PreviewHandlerHost(clsid.Value, path, C("ContentBrush", Colors.White), C("FgBrush", Colors.Black));
                h.KeyPressed += k => HandlerKey?.Invoke(k);
                h.Failed += () =>
                {
                    if (_handlerHost != h || ct.IsCancellationRequested) return;
                    DropHandler();
                    _ = ext is ".docx" or ".docm" ? DocxOrThumb(path, ct) : ShowThumbOrHex(path, ct);
                };
                _handlerHost = h;
                HandlerFrame.Child = h;
                ShowOnly(HandlerFrame);
                return;
            }
            await ShowThumbOrHex(path, ct);
        }

        private async Task DocxOrThumb(string path, CancellationToken ct)
        {
            if (!await ShowDocxText(path, ct) && !ct.IsCancellationRequested) await ShowThumbOrHex(path, ct);
        }

        /// <summary>Side panel: the file's thumbnail if Windows has a real one, otherwise its big icon (never a hex dump).</summary>
        private async Task ShowThumbOrIcon(string path, CancellationToken ct)
        {
            var thumb = await ImageLoader.RunSta(() => (ImageSource)Shell.GetThumbnail(path, 512, Shell.SIIGBF_THUMBNAILONLY | Shell.SIIGBF_BIGGERSIZEOK));
            if (ct.IsCancellationRequested) return;
            SetImage(thumb ?? BigIcon(), false);
        }

        private ImageSource BigIcon() => _item == null ? null : IconService.GetLargeIcon(_item, 256) ?? _item.Icon;

        private async Task<bool> ShowPdf(string path, CancellationToken ct)
        {
            var page = await Task.Run(() => PdfRender.FirstPage(path, _quick ? 2400 : 1800));
            if (page == null || ct.IsCancellationRequested) return false;
            SetImage(page, false);
            return true;
        }

        /// <summary>The text of a .docx (it's a zip with word/document.xml) – paragraphs, tabs and line breaks.</summary>
        private async Task<bool> ShowDocxText(string path, CancellationToken ct)
        {
            var text = await Task.Run(() =>
            {
                try
                {
                    using var zip = System.IO.Compression.ZipFile.OpenRead(path);
                    var entry = zip.GetEntry("word/document.xml");
                    if (entry == null) return null;
                    using var s = entry.Open();
                    var doc = System.Xml.Linq.XDocument.Load(s);
                    System.Xml.Linq.XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
                    var sb = new StringBuilder();
                    foreach (var p in doc.Descendants(w + "p"))
                    {
                        foreach (var n in p.Descendants())
                        {
                            if (n.Name == w + "t") sb.Append(n.Value);
                            else if (n.Name == w + "tab") sb.Append('\t');
                            else if (n.Name == w + "br" || n.Name == w + "cr") sb.Append('\n');
                        }
                        sb.Append('\n');
                        if (sb.Length > 400_000) break;
                    }
                    return sb.ToString();
                }
                catch { return null; }
            });
            if (text == null || ct.IsCancellationRequested) return false;
            Code.SyntaxHighlighting = null;
            Code.WordWrap = true;
            Code.ShowLineNumbers = false;
            Code.Document = new ICSharpCode.AvalonEdit.Document.TextDocument(text);
            ShowOnly(CodeFrame);
            _info.Insert(0, new InfoRow { Label = Loc.T("Preview"), Value = Loc.T("DocTextOnly") });
            return true;
        }

        private async Task ShowThumbOrHex(string path, CancellationToken ct)
        {
            var thumb = await ImageLoader.RunSta(() => (ImageSource)Shell.GetThumbnail(path, 512, Shell.SIIGBF_THUMBNAILONLY | Shell.SIIGBF_BIGGERSIZEOK)
                                                         ?? EmbeddedImages.Load(path, 1600));
            if (ct.IsCancellationRequested) return;
            if (thumb != null) { SetImage(thumb, false); return; }
            // developers: show a hex dump of the first bytes
            try
            {
                var bytes = await Task.Run(() =>
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var b = new byte[Math.Min(4096, fs.Length)];
                    int n = fs.Read(b, 0, b.Length);
                    return b.AsSpan(0, n).ToArray();
                }, ct);
                if (ct.IsCancellationRequested) return;
                if (bytes.Length == 0) { ShowMessage(Loc.T("EmptyFile"), ""); return; }
                var sb = new StringBuilder();
                for (int i = 0; i < bytes.Length; i += 16)
                {
                    sb.Append(i.ToString("X8")).Append("  ");
                    for (int j = 0; j < 16; j++) sb.Append(i + j < bytes.Length ? bytes[i + j].ToString("X2") + " " : "   ");
                    sb.Append(' ');
                    for (int j = 0; j < 16 && i + j < bytes.Length; j++) { var c = (char)bytes[i + j]; sb.Append(c >= 32 && c < 127 ? c : '.'); }
                    sb.Append('\n');
                }
                Code.SyntaxHighlighting = null;
                Code.ShowLineNumbers = false;
                Code.Document.Text = sb.ToString();
                Code.ScrollToHome();
                ShowOnly(CodeFrame);
                _info.Insert(0, new InfoRow { Label = Loc.T("Preview"), Value = Loc.T("HexPreview") });
            }
            catch (Exception ex) { ShowMessage(ex.Message, ""); }
        }

        // ---------------- text / code ----------------
        private async Task ShowText(string path, long size, CancellationToken ct)
        {
            ShowMessage(Loc.T("Loading"), "");
            try
            {
                var (text, enc, truncated) = await Task.Run(() =>
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var sr = new StreamReader(fs, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
                    var buf = new char[MaxTextBytes];
                    int n = sr.ReadBlock(buf, 0, buf.Length);
                    return (new string(buf, 0, n), sr.CurrentEncoding, !sr.EndOfStream);
                }, ct);
                if (ct.IsCancellationRequested) return;
                var ext = Path.GetExtension(path).ToLowerInvariant();
                int longest = LongestLine(text);
                bool formatted = false;
                // minified JSON (one huge line): show it indented – much faster and readable
                if (!truncated && longest > 400 && ext is ".json" or ".geojson" or ".webmanifest" or ".har")
                {
                    var pretty = await Task.Run(() => PrettyJson(text), ct);
                    if (ct.IsCancellationRequested) return;
                    if (pretty != null) { text = pretty; formatted = true; longest = LongestLine(text); }
                }
                var hl = HighlightingManager.Instance.GetDefinitionByExtension(ext);
                if (hl == null)
                {
                    var name = Path.GetFileName(path).ToLowerInvariant();
                    if (ext is ".jsonc" or ".json5") hl = HighlightingManager.Instance.GetDefinitionByExtension(".js");
                    else if (ext is ".ts" or ".tsx" or ".jsx" or ".mjs" or ".cjs" or ".vue" or ".svelte") hl = HighlightingManager.Instance.GetDefinitionByExtension(".js");
                    else if (ext is ".csproj" or ".props" or ".targets" or ".xaml" or ".axaml" or ".svg" or ".resx" or ".nuspec" or ".manifest" or ".config") hl = HighlightingManager.Instance.GetDefinition("XML");
                    else if (ext is ".kt" or ".scala" or ".go" or ".rs" or ".swift" or ".dart") hl = HighlightingManager.Instance.GetDefinition("C#");
                    else if (ext is ".yml" or ".yaml" or ".toml" or ".ini" or ".env") hl = null;
                }
                // very long lines (base64 blobs, minified code) make syntax colouring and selection slow: plain + wrapped
                bool longLines = longest > 5000;
                Code.SyntaxHighlighting = hl == null || longLines ? null : CodeTheme.Adapt(hl);
                Code.WordWrap = longLines;
                Code.ShowLineNumbers = true;
                Code.Document = new ICSharpCode.AvalonEdit.Document.TextDocument(text);
                Code.ScrollToHome();
                ShowOnly(CodeFrame);
                int lines = 1; foreach (var c in text) if (c == '\n') lines++;
                _info.Insert(0, new InfoRow { Label = Loc.T("Lines"), Value = lines.ToString("N0") + (truncated ? " +" : "") });
                _info.Insert(1, new InfoRow { Label = Loc.T("Encoding"), Value = enc.WebName.ToUpperInvariant() + (text.Contains("\r\n") ? "  ·  CRLF" : text.Contains('\n') ? "  ·  LF" : "") });
                if (hl != null) _info.Insert(2, new InfoRow { Label = Loc.T("Language"), Value = hl.Name });
                if (truncated) _info.Insert(0, new InfoRow { Label = Loc.T("Preview"), Value = Loc.T("Truncated") });
                if (formatted) _info.Insert(0, new InfoRow { Label = Loc.T("Preview"), Value = Loc.T("JsonFormatted") });
            }
            catch (Exception ex) { if (!ct.IsCancellationRequested) ShowMessage(ex.Message, ""); }
        }

        // ---------------- media ----------------
        private void ShowMedia(string path, bool audio)
        {
            ShowOnly(MediaHost);
            AudioGlyph.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
            _playing = false;
            PlayGlyph.Text = "";
            SeekFill.Width = 0;
            TimeText.Text = "";
            // hover popup: nothing there can be clicked – no play / volume buttons; while it plays, a plain progress line and the time
            PlayBtn.Visibility = VolPanel.Visibility = _hoverMode ? Visibility.Collapsed : Visibility.Visible;
            SeekBar.Visibility = _hoverMode && !AutoPlay ? Visibility.Hidden : Visibility.Visible;
            SeekBar.IsHitTestVisible = !_hoverMode;
            SeekBar.Cursor = _hoverMode ? null : Cursors.Hand;
            // seeking previews frames for video; for sound only it stops playback from starting at all
            Media.ScrubbingEnabled = !audio;
            Media.LayoutTransform = Transform.Identity;
            _videoRotation = audio ? 0 : Shell.GetVideoOrientation(path);
            try
            {
                Media.Source = new Uri(path);
                Media.IsMuted = _muted;
                Media.Volume = _volume;
                MuteGlyph.Text = _muted ? "\uE74F" : "\uE767";
                Dispatcher.BeginInvoke(UpdateVol, DispatcherPriority.Loaded);
                Media.Play();
                Media.Pause();
            }
            catch (Exception ex) { ShowMessage(ex.Message, ""); }
        }

        private void StopMedia()
        {
            _mediaTimer.Stop();
            try { Media.Stop(); Media.Close(); Media.Source = null; } catch { }
            _playing = false;
        }

        private int _videoRotation;

        /// <summary>Video / audio start playing as soon as they are open (hover preview).</summary>
        public bool AutoPlay { get; set; }

        private void Media_Opened(object sender, RoutedEventArgs e)
        {
            if (AutoPlay && !_playing) Dispatcher.BeginInvoke(new Action(() => { if (!_playing && MediaHost.Visibility == Visibility.Visible) Play_Click(null, null); }));
            // a phone video filmed upright: stored sideways with "rotate 90°" metadata, which the player doesn't apply
            int w = Media.NaturalVideoWidth, h = Media.NaturalVideoHeight;
            bool sideways = _videoRotation is 90 or 270;
            if (_videoRotation == 180 || (sideways && w > h))
                Media.LayoutTransform = new RotateTransform(_videoRotation);
            if (sideways && w > h) (w, h) = (h, w);
            if (AudioGlyph.Visibility != Visibility.Visible && !Media.HasVideo && Media.Source?.LocalPath is { } vp)
            {
                // sound plays but the picture can't be decoded: name the missing codec, with its Store link
                StopMedia();
                var miss = FileTypes.MissingVideoCodec(vp);
                ShowMessage(Loc.T("VideoNoCodec") + (miss is { } m ? "\n" + m.name : ""), "");
                if (miss is { } m2) AddAction("", Loc.T("GetCodec"), () => FileTypes.OpenStore(m2.storeId));
                return;
            }
            if (Media.NaturalDuration.HasTimeSpan)
                _info.Insert(0, new InfoRow { Label = Loc.T("Duration"), Value = Media.NaturalDuration.TimeSpan.ToString(Media.NaturalDuration.TimeSpan.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss") });
            if (w > 0) _info.Insert(1, new InfoRow { Label = Loc.T("Dimensions"), Value = $"{w} × {h} px" });
            UpdateSeek();
        }

        private void Media_Ended(object sender, RoutedEventArgs e)
        {
            Media.Stop(); _playing = false; PlayGlyph.Text = ""; _mediaTimer.Stop(); UpdateSeek();
        }

        private void Media_Failed(object sender, ExceptionRoutedEventArgs e)
        {
            if (ShowMediaSetupHint()) return;
            ShowMessage(Loc.T("NoCodec") + "\n" + e.ErrorException?.Message, "\uE8D6");
        }

        /// <summary>
        /// WPF plays video and audio through Windows Media Player's engine and Media Foundation. Windows "N" editions
        /// have neither (Media Feature Pack), and Windows Media Player Legacy can be removed as an optional feature –
        /// then nothing plays. Tells the user what to add, with a button to the right Settings page.
        /// </summary>
        private bool ShowMediaSetupHint()
        {
            var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string key = !File.Exists(Path.Combine(sys, "mfplat.dll")) ? "MediaNoFeaturePack"
                       : !FileTypes.MediaPlayerAvailable() ? "MediaNoWmp" : null;
            if (key == null) return false;
            ShowMessage(Loc.T(key), "\uE8D6");
            AddAction("\uE713", Loc.T("OpenOptionalFeatures"), () => FileOps.OpenUrl("ms-settings:optionalfeatures"));
            return true;
        }

        private void Play_Click(object sender, RoutedEventArgs e)
        {
            if (_playing) { Media.Pause(); _playing = false; PlayGlyph.Text = ""; _mediaTimer.Stop(); }
            else
            {
                Media.Play(); _playing = true; PlayGlyph.Text = "\uE769"; _mediaTimer.Start();
                var path = Media.Source;
                _ = System.Threading.Tasks.Task.Delay(2500).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
                {
                    if (_playing && Media.Source == path && !Media.NaturalDuration.HasTimeSpan && Media.Position == TimeSpan.Zero)
                        if (!ShowMediaSetupHint()) ShowMessage(Loc.T("MediaUnsupported"), "\uE8D6");
                }));
            }
        }

        private static double _volume = 0.6;
        private static bool _muted;
        private bool _volDrag;

        private void Mute_Click(object sender, RoutedEventArgs e)
        {
            _muted = Media.IsMuted = !Media.IsMuted;
            MuteGlyph.Text = Media.IsMuted ? "\uE74F" : "\uE767";
        }

        private void SetVol(MouseEventArgs e)
        {
            _volume = Math.Clamp(e.GetPosition(VolBar).X / Math.Max(1, VolBar.ActualWidth), 0, 1);
            Media.Volume = _volume;
            if (_muted && _volume > 0) { _muted = Media.IsMuted = false; MuteGlyph.Text = "\uE767"; }
            UpdateVol();
        }

        private void UpdateVol() => VolFill.Width = Math.Max(0, VolBar.ActualWidth * _volume);
        private void Vol_MouseDown(object sender, MouseButtonEventArgs e) { _volDrag = true; VolBar.CaptureMouse(); SetVol(e); e.Handled = true; }
        private void Vol_MouseMove(object sender, MouseEventArgs e) { if (_volDrag) SetVol(e); }
        private void Vol_MouseUp(object sender, MouseButtonEventArgs e) { _volDrag = false; VolBar.ReleaseMouseCapture(); }

        private void UpdateSeek()
        {
            if (!Media.NaturalDuration.HasTimeSpan) return;
            var total = Media.NaturalDuration.TimeSpan;
            var pos = Media.Position;
            SeekFill.Width = total.TotalSeconds > 0 ? SeekBar.ActualWidth * pos.TotalSeconds / total.TotalSeconds : 0;
            string f = total.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss";
            // hover popup without playing: only the length
            TimeText.Text = _hoverMode && !AutoPlay ? total.ToString(f) : pos.ToString(f) + " / " + total.ToString(f);
        }

        private void Seek_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!Media.NaturalDuration.HasTimeSpan || SeekBar.ActualWidth <= 0) return;
            var frac = e.GetPosition(SeekBar).X / SeekBar.ActualWidth;
            Media.Position = TimeSpan.FromSeconds(Media.NaturalDuration.TimeSpan.TotalSeconds * Math.Clamp(frac, 0, 1));
            UpdateSeek();
        }

        // ---------------- hashes ----------------
        private void ComputeHashes(string path, CancellationToken ct)
        {
            if (_info.Any(r => r.Label == "SHA-256")) return;
            var sha = AddRow("SHA-256", Loc.T("Computing"));
            var md5 = AddRow("MD5", Loc.T("Computing"));
            Task.Run(() =>
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20);
                using var s = SHA256.Create();
                using var m = MD5.Create();
                var buf = new byte[1 << 20];
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    if (ct.IsCancellationRequested) return (null, null);
                    s.TransformBlock(buf, 0, n, null, 0);
                    m.TransformBlock(buf, 0, n, null, 0);
                }
                s.TransformFinalBlock(buf, 0, 0);
                m.TransformFinalBlock(buf, 0, 0);
                return (Convert.ToHexString(s.Hash).ToLowerInvariant(), Convert.ToHexString(m.Hash).ToLowerInvariant());
            }, ct).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully && t.Result.Item1 != null) { sha.Value = t.Result.Item1; md5.Value = t.Result.Item2; }
                else if (t.IsFaulted) { sha.Value = t.Exception?.InnerException?.Message; md5.Value = ""; }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    /// <summary>Makes AvalonEdit's built-in (light) syntax colors readable on dark backgrounds.</summary>
    internal static class CodeTheme
    {
        private static readonly Dictionary<IHighlightingDefinition, Dictionary<HighlightingColor, Color?>> _orig = new();

        public static IHighlightingDefinition Adapt(IHighlightingDefinition def)
        {
            if (!_orig.TryGetValue(def, out var orig))
            {
                orig = new Dictionary<HighlightingColor, Color?>();
                foreach (var c in def.NamedHighlightingColors)
                    orig[c] = c.Foreground?.GetColor(null);
                _orig[def] = orig;
            }
            foreach (var (hc, col) in orig)
            {
                if (hc.IsFrozen || col == null) continue;
                var c = col.Value;
                if (ThemeManager.IsDark)
                {
                    double l = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
                    if (l < 0.55)
                    {
                        double t = 0.55 - l + 0.15;
                        c = Color.FromRgb((byte)(c.R + (255 - c.R) * t), (byte)(c.G + (255 - c.G) * t), (byte)(c.B + (255 - c.B) * t));
                    }
                }
                hc.Foreground = new SimpleHighlightingBrush(c);
            }
            return def;
        }
    }
}
