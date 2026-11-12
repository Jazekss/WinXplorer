using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shell;
using WinXplorer.Services;

namespace WinXplorer
{
    /// <summary>
    /// "Compress to ZIP": asks for the name (and an optional password – that one needs 7-Zip), then shows the
    /// progress in a small window of its own (with taskbar progress when minimized), with Stop (asks first).
    /// When it is done and the folder is still on screen, the window closes and the archive gets selected;
    /// otherwise it offers to open the folder in a new window.
    /// </summary>
    public sealed class ZipWindow : Window
    {
        private readonly string[] _paths;
        private readonly string _dir;
        private readonly Func<string, bool> _showIfVisible;
        private readonly TextBox _name = new() { Height = 30 };
        private readonly CheckBox _usePwd = new() { Margin = new Thickness(0, 12, 0, 6) };
        private readonly PasswordBox _pwd = new() { Height = 30, IsEnabled = false, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 0, 6, 0) };
        private readonly StackPanel _body = new() { Margin = new Thickness(18) };
        private readonly ProgressBar _bar = new() { Height = 6, Maximum = 1000, Margin = new Thickness(0, 10, 0, 6) };
        private readonly TextBlock _status = new() { TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _pct = new() { FontSize = 22, FontWeight = FontWeights.SemiBold };
        private CancellationTokenSource _cts;
        private string _zip;
        private bool _running;

        public static string SevenZip
        {
            get
            {
                foreach (var p in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)") })
                    if (p != null && File.Exists(Path.Combine(p, "7-Zip", "7z.exe"))) return Path.Combine(p, "7-Zip", "7z.exe");
                return null;
            }
        }

        /// <param name="showIfVisible">Called with the finished archive: true when its folder is on screen (and it was selected there).</param>
        public static void Start(Window owner, string[] paths, Func<string, bool> showIfVisible)
        {
            if (paths == null || paths.Length == 0) return;
            new ZipWindow(paths, showIfVisible) { Owner = null, WindowStartupLocation = WindowStartupLocation.Manual }.Place(owner).Show();
        }

        private ZipWindow Place(Window owner)
        {
            if (owner != null) { Left = owner.Left + (owner.ActualWidth - Width) / 2; Top = owner.Top + (owner.ActualHeight - 240) / 2; }
            return this;
        }

        private ZipWindow(string[] paths, Func<string, bool> showIfVisible)
        {
            _paths = paths;
            _showIfVisible = showIfVisible;
            _dir = Path.GetDirectoryName(paths[0].TrimEnd('\\')) ?? "";
            Title = "WinXplorer – ZIP";
            Width = 440; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.CanMinimize; ShowInTaskbar = true;
            Icon = App.LoadAppIcon();
            TaskbarItemInfo = new TaskbarItemInfo();
            SourceInitialized += (_, _) => ThemeManager.ApplyWindowFrame(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            SetResourceReference(BackgroundProperty, "BarBrush");
            SetResourceReference(ForegroundProperty, "FgBrush");
            SetResourceReference(FontFamilyProperty, "UiFont");
            Content = _body;
            Closing += (_, e) =>
            {
                if (_running && !ConfirmStop()) e.Cancel = true;
            };
            BuildSetup();
        }

        private void BuildSetup()
        {
            _body.Children.Clear();
            var baseName = _paths.Length == 1 ? Path.GetFileNameWithoutExtension(_paths[0].TrimEnd('\\')) : Path.GetFileName(_dir.TrimEnd('\\'));
            if (string.IsNullOrEmpty(baseName)) baseName = "Archive";
            _body.Children.Add(new TextBlock { Text = Loc.T("ZipName"), Margin = new Thickness(0, 0, 0, 4) });
            _name.Style = (Style)FindResource("InputBox");
            _name.Text = Path.GetFileNameWithoutExtension(FileOps.UniqueName(_dir, baseName, ".zip"));
            _body.Children.Add(_name);
            var has7z = SevenZip != null;
            _usePwd.Content = Loc.T("ZipPassword") + (has7z ? "" : "  (" + Loc.T("Needs7Zip") + ")");
            _usePwd.IsEnabled = has7z;
            _usePwd.Click += (_, _) => { _pwd.IsEnabled = _usePwd.IsChecked == true; if (_pwd.IsEnabled) _pwd.Focus(); };
            _body.Children.Add(_usePwd);
            _pwd.SetResourceReference(BackgroundProperty, "InputBrush");
            _pwd.SetResourceReference(ForegroundProperty, "FgBrush");
            _body.Children.Add(_pwd);
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            var ok = new Button { Content = Loc.T("ZipCreate"), Style = (Style)FindResource("AccentBtn"), MinWidth = 96, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancel = new Button { Content = Loc.T("Cancel"), Style = (Style)FindResource("SubtleBtn"), MinWidth = 96, IsCancel = true, Padding = new Thickness(12, 4, 12, 4) };
            ok.Click += (_, _) => Begin();
            cancel.Click += (_, _) => Close();
            row.Children.Add(ok); row.Children.Add(cancel);
            _body.Children.Add(row);
            Loaded += (_, _) => { _name.Focus(); _name.SelectAll(); };
        }

        private void Begin()
        {
            var n = _name.Text.Trim();
            if (n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
            if (n.Length == 0 || n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { _name.Focus(); return; }
            _zip = Path.Combine(_dir, n + ".zip");
            if (File.Exists(_zip) && MessageBox.Show(this, Loc.F("ZipExists", n + ".zip"), "WinXplorer", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var pwd = _usePwd.IsChecked == true ? _pwd.Password : null;
            if (pwd != null && pwd.Length == 0) { _pwd.Focus(); return; }

            _body.Children.Clear();
            _status.Text = Loc.F("Compressing", n + ".zip");
            _body.Children.Add(_pct);
            _body.Children.Add(_status);
            _body.Children.Add(_bar);
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var min = new Button { Content = Loc.T("Minimize"), Style = (Style)FindResource("SubtleBtn"), MinWidth = 96, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4) };
            var stop = new Button { Content = Loc.T("Stop"), Style = (Style)FindResource("SubtleBtn"), MinWidth = 96, Padding = new Thickness(12, 4, 12, 4) };
            min.Click += (_, _) => WindowState = WindowState.Minimized;
            stop.Click += (_, _) => { if (ConfirmStop()) Close(); };
            row.Children.Add(min); row.Children.Add(stop);
            _body.Children.Add(row);
            TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Normal;

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _running = true;
            var zip = _zip;
            long lastTick = 0;
            void Report(double f)
            {
                var now = Environment.TickCount64;
                if (now - lastTick < 100 && f < 1) return;
                lastTick = now;
                Dispatcher.BeginInvoke(() =>
                {
                    _bar.Value = f * 1000;
                    _pct.Text = $"{f * 100:0} %";
                    TaskbarItemInfo.ProgressValue = f;
                });
            }
            new Thread(() =>
            {
                string err = null;
                try
                {
                    if (pwd != null) SevenZipCreate(zip, _paths, pwd, Report, ct);
                    else Archives.CreateZip(zip, _paths, (done, total) => Report(total > 0 ? (double)done / total : 0), ct);
                }
                catch (OperationCanceledException) { err = ""; }
                catch (Exception ex) { err = ex.Message; }
                if (err != null) { try { File.Delete(zip); File.Delete(zip + ".part"); } catch { } }
                Dispatcher.BeginInvoke(() => Finished(err));
            }) { IsBackground = true, Name = "Zip" }.Start();
        }

        private bool ConfirmStop()
        {
            if (!_running) return true;
            if (MessageBox.Show(this, Loc.T("ZipStopAsk"), "WinXplorer", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return false;
            _cts?.Cancel();
            _running = false;
            return true;
        }

        private void Finished(string err)
        {
            _running = false;
            if (err == "") return;   // stopped: the window is closing
            TaskbarItemInfo.ProgressState = err == null ? TaskbarItemProgressState.None : TaskbarItemProgressState.Error;
            if (err != null)
            {
                MessageBox.Show(this, err, "WinXplorer", MessageBoxButton.OK, MessageBoxImage.Warning);
                Close();
                return;
            }
            UndoService.Created(_zip);
            if (_showIfVisible?.Invoke(_zip) == true) { Close(); return; }
            // the user moved on: say it's ready and offer the folder
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            _body.Children.Clear();
            _body.Children.Add(new TextBlock { Text = Loc.T("ZipDone"), FontSize = 16, FontWeight = FontWeights.SemiBold });
            _body.Children.Add(new TextBlock { Text = Path.GetFileName(_zip), Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var open = new Button { Content = Loc.T("ZipOpenFolder"), Style = (Style)FindResource("AccentBtn"), Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4), IsDefault = true };
            var close = new Button { Content = Loc.T("Close"), Style = (Style)FindResource("SubtleBtn"), MinWidth = 96, IsCancel = true, Padding = new Thickness(12, 4, 12, 4) };
            var zip = _zip;
            open.Click += (_, _) =>
            {
                var dir = Path.GetDirectoryName(zip);
                if (!MainWindow.ActivateExisting(dir)) MainWindow.OpenNewWindow(dir);
                Close();
            };
            close.Click += (_, _) => Close();
            row.Children.Add(open); row.Children.Add(close);
            _body.Children.Add(row);
            Activate();
        }

        /// <summary>Password-protected zip (AES-256) through 7-Zip's command line, reading its percentage.</summary>
        private static void SevenZipCreate(string zip, string[] paths, string pwd, Action<double> report, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(SevenZip) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(paths[0].TrimEnd('\\')) };
            foreach (var a in new[] { "a", "-tzip", "-mem=AES256", "-p" + pwd, "-bsp1", "-bso0", "-y", zip }) psi.ArgumentList.Add(a);
            foreach (var p in paths) psi.ArgumentList.Add(Path.GetFileName(p.TrimEnd('\\')) is { Length: > 0 } f ? f : p);
            using var proc = Process.Start(psi)!;
            using var reg = ct.Register(() => { try { proc.Kill(true); } catch { } });
            var sb = new System.Text.StringBuilder();
            int c;
            while ((c = proc.StandardOutput.Read()) >= 0)
            {
                if (c == '%')
                {
                    var s = sb.ToString().Trim();
                    var i = s.LastIndexOfAny(new[] { ' ', '\b', '\r' });
                    if (int.TryParse(i >= 0 ? s[(i + 1)..] : s, out var pct)) report(pct / 100.0);
                    sb.Clear();
                }
                else sb.Append((char)c);
                if (sb.Length > 200) sb.Remove(0, 100);
            }
            proc.WaitForExit();
            ct.ThrowIfCancellationRequested();
            if (proc.ExitCode > 1) throw new IOException(proc.StandardError.ReadToEnd().Trim());
            report(1);
        }
    }
}
