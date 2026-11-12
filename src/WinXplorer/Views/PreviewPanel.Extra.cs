using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using WinXplorer.Interop;
using WinXplorer.Services;

namespace WinXplorer.Views
{
    /// <summary>Lays its children out in one column, or in two (first half left, second half right).</summary>
    public sealed class InfoColumns : Panel
    {
        public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(nameof(Columns), typeof(int), typeof(InfoColumns),
            new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsMeasure));
        public int Columns { get => (int)GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }
        private const double Gap = 24;

        /// <summary>Columns = 0: as many as fit (one per ~260 px, at most 4) – a wide panel needs less scrolling.</summary>
        private int ColsFor(double width)
        {
            int c = Columns > 0 ? Columns : double.IsInfinity(width) ? 1 : (int)((width + Gap) / (260 + Gap));
            return Math.Clamp(c, 1, Math.Max(1, Math.Min(4, InternalChildren.Count)));
        }

        protected override Size MeasureOverride(Size avail)
        {
            int cols = ColsFor(avail.Width);
            double w = double.IsInfinity(avail.Width) ? double.PositiveInfinity : Math.Max(0, (avail.Width - Gap * (cols - 1)) / cols);
            int per = (InternalChildren.Count + cols - 1) / cols;
            var h = new double[cols];
            double maxW = 0;
            for (int i = 0; i < InternalChildren.Count; i++)
            {
                var c = InternalChildren[i];
                c.Measure(new Size(w, double.PositiveInfinity));
                h[Math.Min(cols - 1, i / Math.Max(1, per))] += c.DesiredSize.Height;
                maxW = Math.Max(maxW, c.DesiredSize.Width);
            }
            return new Size(double.IsInfinity(w) ? maxW * cols : avail.Width, h.Length == 0 ? 0 : h.Max());
        }

        protected override Size ArrangeOverride(Size size)
        {
            int cols = ColsFor(size.Width);
            double w = Math.Max(0, (size.Width - Gap * (cols - 1)) / cols);
            int per = (InternalChildren.Count + cols - 1) / cols;
            var y = new double[cols];
            for (int i = 0; i < InternalChildren.Count; i++)
            {
                var c = InternalChildren[i];
                int col = Math.Min(cols - 1, i / Math.Max(1, per));
                double hh = c.DesiredSize.Height;
                c.Arrange(new Rect(col * (w + Gap), y[col], w, hh));
                y[col] += hh;
            }
            return size;
        }
    }

    public partial class PreviewPanel
    {
        private static int LongestLine(string text)
        {
            int best = 0, start = 0;
            for (int i = 0; i <= text.Length; i++)
                if (i == text.Length || text[i] == '\n') { best = Math.Max(best, i - start); start = i + 1; }
            return best;
        }

        private static string PrettyJson(string text)
        {
            try
            {
                using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            }
            catch { return null; }
        }

        // ---------------- archives: what's inside ----------------
        private async Task<bool> ShowArchive(string path, CancellationToken ct)
        {
            ShowMessage(Loc.T("Loading"), "");
            try
            {
                var s = await Task.Run(() => Archives.Summary(path, 20000), ct);
                if (ct.IsCancellationRequested) return true;
                if (_quick)
                {
                    var rows = await Task.Run(() => ArchiveTable.Rows(s.entries, s.files + s.folders > s.entries.Count), ct);
                    if (ct.IsCancellationRequested) return true;
                    ArchiveFrame.Child = ArchiveTable.Build(rows);
                    ShowOnly(ArchiveFrame);
                }
                else SetImage(BigIcon(), false);   // side panel: icon + what's inside in numbers; Space shows the table
                int at = 0;
                _info.Insert(at++, new InfoRow { Label = Loc.T("Contains"), Value = Loc.FilesFolders(s.files, s.folders) });
                _info.Insert(at++, new InfoRow { Label = Loc.T("Unpacked"), Value = FileItemSize(s.total) + (s.total > 0 && s.packed > 0 ? $"  ({Math.Round(100.0 * s.packed / s.total)}%)" : "") });
                if (s.encrypted) _info.Insert(at++, new InfoRow { Label = Loc.T("Encrypted"), Value = Loc.T("ArchivePassword") });
                AddAction("", Loc.T("OpenArchive"), () => _pane?.Navigate(path));
                AddAction("", Loc.T("ExtractHere"), () => FileOps.ExtractArchive(path, null, d => _pane?.ReloadAndSelect(d)));
                return true;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) return true;
                _info.Insert(0, new InfoRow { Label = Loc.T("Preview"), Value = ex.Message });
                return false;
            }
        }

        private static string FileItemSize(long b) => Models.FileItem.FormatSize(b);

        // ---------------- programs: details, never the bytes ----------------
        private async Task ShowProgram(string path, CancellationToken ct)
        {
            SetImage(await ImageLoader.RunSta(() => Shell.GetThumbnail(path, 256)), false);
            var info = await Task.Run(() =>
            {
                var rows = new System.Collections.Generic.List<(string, string)>();
                try
                {
                    var v = FileVersionInfo.GetVersionInfo(path);
                    rows.Add((Loc.T("Description"), v.FileDescription));
                    rows.Add((Loc.T("Product"), v.ProductName));
                    rows.Add((Loc.T("Company"), v.CompanyName));
                    rows.Add((Loc.T("FileVersion"), v.FileVersion));
                    if (v.ProductVersion != v.FileVersion) rows.Add((Loc.T("ProductVersion"), v.ProductVersion));
                    rows.Add((Loc.T("Copyright"), v.LegalCopyright));
                    var orig = v.OriginalFilename?.EndsWith(".mui", StringComparison.OrdinalIgnoreCase) == true ? v.OriginalFilename[..^4] : v.OriginalFilename;
                    if (!string.Equals(orig, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)) rows.Add((Loc.T("OriginalName"), orig));
                }
                catch { }
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var pe = new PEReader(fs);
                    var h = pe.PEHeaders;
                    string arch = h.CoffHeader.Machine switch
                    {
                        Machine.Amd64 => "x64", Machine.I386 => "x86", Machine.Arm64 => "ARM64", Machine.Arm or Machine.ArmThumb2 => "ARM", _ => h.CoffHeader.Machine.ToString(),
                    };
                    var kind = new StringBuilder(arch);
                    if (h.CorHeader != null)
                    {
                        bool anyCpu = (h.CorHeader.Flags & System.Reflection.PortableExecutable.CorFlags.ILOnly) != 0 && h.CoffHeader.Machine == Machine.I386
                                      && (h.CorHeader.Flags & System.Reflection.PortableExecutable.CorFlags.Requires32Bit) == 0;
                        kind.Clear().Append(anyCpu ? "Any CPU" : arch).Append(" · .NET");
                    }
                    if (h.PEHeader != null)
                    {
                        kind.Append(" · ").Append(h.PEHeader.Subsystem switch
                        {
                            Subsystem.WindowsGui => Loc.T("SubsystemGui"),
                            Subsystem.WindowsCui => Loc.T("SubsystemConsole"),
                            Subsystem.Native => Loc.T("SubsystemDriver"),
                            Subsystem.EfiApplication or Subsystem.EfiBootServiceDriver or Subsystem.EfiRuntimeDriver => "EFI",
                            _ => h.PEHeader.Subsystem.ToString(),
                        });
                        if ((h.CoffHeader.Characteristics & Characteristics.Dll) != 0) kind.Append(" · DLL");
                    }
                    rows.Insert(0, (Loc.T("Program"), kind.ToString()));
                    var built = DateTimeOffset.FromUnixTimeSeconds((uint)h.CoffHeader.TimeDateStamp).LocalDateTime;
                    // Windows' own files carry a hash here instead of a date
                    if (built.Year > 1995 && built <= DateTime.Now) rows.Add((Loc.T("Built"), built.ToString("G")));
                }
                catch { rows.Insert(0, (Loc.T("Program"), Loc.T("NotPe"))); }
                try
                {
#pragma warning disable SYSLIB0057
                    var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
                    var subject = new X509Certificate2(cert).GetNameInfo(X509NameType.SimpleName, false);
                    rows.Add((Loc.T("SignedBy"), subject));
                }
                catch { } // unsigned, or signed through a Windows catalog
                return rows;
            }, ct);
            if (ct.IsCancellationRequested) return;
            int at = 0;
            foreach (var (l, v) in info)
                if (!string.IsNullOrWhiteSpace(v)) _info.Insert(at++, new InfoRow { Label = l, Value = v.Trim() });
        }
    }
}
