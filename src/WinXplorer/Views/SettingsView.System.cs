using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WinXplorer.Interop;
using WinXplorer.Services;

namespace WinXplorer.Views
{
    /// <summary>
    /// Settings › System check: what WinXplorer uses from Windows, checked live on this PC – a green tick or what
    /// to add (with a button to the right Settings / Store page). Answers "it plays elsewhere but not here".
    /// </summary>
    public partial class SettingsView
    {
        private void BuildSystem()
        {
            _page.Children.Add(Dim(Loc.T("SysIntro")).With(t => t.Margin = new Thickness(0, 0, 0, 6)));
            var packages = InstalledPackages();
            bool Pkg(string prefix) => packages.Any(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            void Store(string id) => FileTypes.OpenStore(id);
            void Optional() => FileOps.OpenUrl("ms-settings:optionalfeatures");

            Header(Loc.T("SysWindows"));
            var (product, edition, build) = WindowsInfo();
            bool nEdition = edition != null && (edition.EndsWith("N") || edition.EndsWith("NN"));
            Check("", product + (build != null ? "  ·  " + Loc.T("SysBuild") + " " + build : ""), nEdition ? Loc.T("SysEditionN") : Loc.T("SysWindowsOk"), !nEdition, true, null, null);

            Header(Loc.T("SysMedia"));
            bool mf = File.Exists(Path.Combine(sys, "mfplat.dll"));
            Check("", "Media Foundation", mf ? Loc.T("SysMfOk") : Loc.T("MediaNoFeaturePack"), mf, true, Loc.T("OpenOptionalFeatures"), Optional);
            bool wmp = FileTypes.MediaPlayerAvailable();
            Check("", "Windows Media Player Legacy", wmp ? Loc.T("SysWmpOk") : Loc.T("MediaNoWmp"), wmp, true, Loc.T("OpenOptionalFeatures"), Optional);
            Check("", "HEVC Video Extensions (H.265)", Loc.T("SysHevc"), Pkg("Microsoft.HEVCVideoExtension"), false, Loc.T("GetCodec"), () => Store("9N4WGH0Z6VHQ"));
            Check("", "AV1 Video Extension", Loc.T("SysAv1"), Pkg("Microsoft.AV1VideoExtension"), false, Loc.T("GetCodec"), () => Store("9MVZQVXJBQ9V"));
            Check("", "Web Media Extensions / VP9", Loc.T("SysWebMedia"), Pkg("Microsoft.WebMediaExtensions") || Pkg("Microsoft.VP9VideoExtensions"), false, Loc.T("GetCodec"), () => Store("9N5TDP8VCMHS"));

            Header(Loc.T("SysPictures"));
            bool heif = NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, "libheif.dll"), out var h);
            if (heif) NativeLibrary.Free(h);
            Check("", "HEIC / HEIF / AVIF", heif ? Loc.T("SysHeifOk") : Loc.T("SysHeifMissing"), heif, true, null, null);
            Check("", "WebP Image Extensions", Loc.T("SysWebp"), Pkg("Microsoft.WebpImageExtension"), false, Loc.T("GetCodec"), () => Store("9PG2DK419DRG"));
            Check("", "Raw Image Extension", Loc.T("SysRaw"), Pkg("Microsoft.RawImageExtension"), false, Loc.T("GetCodec"), () => Store("9NCTDW2W1BH8"));

            Header(Loc.T("SysDocuments"));
            Check("", "PDF", Loc.T("SysPdfOk"), true, true, null, null);
            bool pdfHandler = PreviewHandlerHost.HandlerFor(".pdf") != null;
            Check("", Loc.T("SysPdfPages"), pdfHandler ? Loc.T("SysPdfPagesOk") : Loc.T("SysPdfPagesMissing"), pdfHandler, false, null, null);
            bool word = PreviewHandlerHost.HandlerFor(".docx") != null, excel = PreviewHandlerHost.HandlerFor(".xlsx") != null;
            Check("", "Word / Excel / PowerPoint", word && excel ? Loc.T("SysOfficeOk") : Loc.T("SysOfficeMissing"), word && excel, false, null, null);

            Header(Loc.T("SysIntegration"));
            Check("", Loc.T("DefaultFM"), DefaultFileManager.IsActive() ? Loc.T("SysOn") : Loc.T("SysOff"), DefaultFileManager.IsActive(), false, null, null);
        }

        /// <summary>One check: icon, name, explanation; on the right ✓ / ⚠ (required) / – (optional) and an optional fix button.</summary>
        private void Check(string glyph, string title, string desc, bool ok, bool required, string fixText, Action fix)
        {
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (!ok && fix != null) right.Children.Add(Btn(fixText, fix));
            var badge = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 3, 10, 3), VerticalAlignment = VerticalAlignment.Center };
            var t = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold };
            if (ok)
            {
                t.Text = "✓  " + Loc.T("SysOk");
                badge.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 0x10, 0x89, 0x3E));
                t.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2E, 0xA0, 0x5A));
            }
            else if (required)
            {
                t.Text = "⚠  " + Loc.T("SysMissing");
                badge.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 0xE8, 0x11, 0x23));
                t.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0x45, 0x50));
            }
            else
            {
                t.Text = Loc.T("SysOptional");
                badge.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
                t.SetResourceReference(TextBlock.ForegroundProperty, "FgDimBrush");
            }
            badge.Child = t;
            right.Children.Add(badge);
            Card(glyph, title, desc, right);
        }

        /// <summary>Store apps installed for this user (package family names), e.g. Microsoft.HEVCVideoExtension.</summary>
        private static List<string> InstalledPackages()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
                return k?.GetSubKeyNames().ToList() ?? new List<string>();
            }
            catch { return new List<string>(); }
        }

        private static (string product, string edition, string build) WindowsInfo()
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                var product = k?.GetValue("ProductName") as string ?? "Windows";
                var build = k?.GetValue("CurrentBuild") as string;
                // the registry still says "Windows 10" on Windows 11
                if (int.TryParse(build, out var b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
                var display = k?.GetValue("DisplayVersion") as string;
                return (product + (display != null ? " " + display : ""), k?.GetValue("EditionID") as string, build);
            }
            catch { return ("Windows", null, null); }
        }
    }
}
