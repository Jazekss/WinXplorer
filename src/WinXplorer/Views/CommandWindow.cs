using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using WinXplorer.Models;
using WinXplorer.Services;

namespace WinXplorer
{
    /// <summary>
    /// Making / editing an own context-menu command: the program, what it opens (the selected item, all of them,
    /// a fixed link, or nothing) and extra parameters – with one-click browser flags (private window, new window,
    /// separate profile). Shows the resulting command line below.
    /// </summary>
    public sealed class CommandWindow : Window
    {
        private readonly TextBox _name = new() { Height = 30 };
        private readonly TextBox _exe = new() { Height = 30 };
        private readonly TextBox _args = new() { Height = 30 };
        private readonly TextBox _url = new() { Height = 30, Margin = new Thickness(0, 6, 0, 0) };
        private readonly RadioButton _mFile = new(), _mAll = new(), _mLink = new(), _mNone = new();
        private readonly TextBlock _preview = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0), FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12 };
        private readonly CustomCommand _c;

        /// <summary>True when saved (the command's Label and Command are set).</summary>
        public static bool Edit(Window owner, CustomCommand c)
        {
            var w = new CommandWindow(c) { Owner = owner, WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen };
            return w.ShowDialog() == true;
        }

        private CommandWindow(CustomCommand c)
        {
            _c = c;
            Title = Loc.T("NewCommand");
            Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
            Icon = App.LoadAppIcon();
            SourceInitialized += (_, _) => ThemeManager.ApplyWindowFrame(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            SetResourceReference(BackgroundProperty, "BarBrush");
            SetResourceReference(ForegroundProperty, "FgBrush");
            SetResourceReference(FontFamilyProperty, "UiFont");
            foreach (var t in new[] { _name, _exe, _args, _url }) { t.Style = (Style)FindResource("InputBox"); t.TextChanged += (_, _) => Update(); }
            _preview.SetResourceReference(TextBlock.ForegroundProperty, "FgDimBrush");

            var body = new StackPanel { Margin = new Thickness(18) };
            body.Children.Add(Label(Loc.T("CommandName"), 0));
            body.Children.Add(_name);

            body.Children.Add(Label(Loc.T("CmdProgram"), 12));
            var exeRow = new DockPanel();
            var choose = new Button { Content = Loc.T("CmdChoose"), Style = (Style)FindResource("SubtleBtn"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
            choose.Click += (_, _) =>
            {
                var app = AppPickerWindow.Pick(this);
                if (app == null) return;
                _exe.Text = CustomCommands.Split(app.Command).exe;
                if (string.IsNullOrWhiteSpace(_name.Text)) _name.Text = Loc.F("OpenWithName", app.Name);
            };
            DockPanel.SetDock(choose, Dock.Right);
            exeRow.Children.Add(choose);
            exeRow.Children.Add(_exe);
            body.Children.Add(exeRow);

            body.Children.Add(Label(Loc.T("CmdOpens"), 12));
            var modes = new WrapPanel();
            foreach (var (rb, key) in new[] { (_mFile, "CmdOpenItem"), (_mAll, "CmdOpenAll"), (_mLink, "CmdOpenLink"), (_mNone, "CmdOpenNothing") })
            {
                rb.Content = Loc.T(key); rb.GroupName = "cmdmode"; rb.Style = (Style)FindResource("Chip"); rb.Margin = new Thickness(0, 0, 6, 6);
                rb.Checked += (_, _) => { _url.Visibility = _mLink.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; Update(); };
                modes.Children.Add(rb);
            }
            body.Children.Add(modes);
            _url.Visibility = Visibility.Collapsed;
            _url.ToolTip = "http://localhost/";
            body.Children.Add(_url);

            body.Children.Add(Label(Loc.T("CmdParams"), 12));
            body.Children.Add(_args);
            var flags = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            flags.Children.Add(Flag(Loc.T("CmdFlagPrivate"), () => Browser() switch { "opera" or "launcher" => "--private", "msedge" => "--inprivate", "firefox" => "-private-window", _ => "--incognito" }));
            flags.Children.Add(Flag(Loc.T("CmdFlagNewWindow"), () => Browser() == "firefox" ? "-new-window" : "--new-window"));
            flags.Children.Add(Flag(Loc.T("CmdFlagProfile"), () =>
            {
                var dir = $@"%LOCALAPPDATA%\JKapps\WinXplorer\BrowserProfiles\{Safe(_name.Text)}";
                return Browser() == "firefox" ? $"-profile \"{dir}\"" : $"--user-data-dir=\"{dir}\"";
            }));
            body.Children.Add(flags);
            var hint = new TextBlock { Text = Loc.T("CmdFlagsHint"), TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "FgDimBrush");
            body.Children.Add(hint);

            body.Children.Add(_preview);

            var ok = new Button { Content = Loc.T("Save"), Style = (Style)FindResource("AccentBtn"), MinWidth = 96, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            ok.Click += (_, _) => Save();
            var cancel = new Button { Content = Loc.T("Cancel"), Style = (Style)FindResource("SubtleBtn"), MinWidth = 96, IsCancel = true, Padding = new Thickness(12, 4, 12, 4) };
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            btns.Children.Add(ok); btns.Children.Add(cancel);
            body.Children.Add(btns);
            Content = body;

            Load(c);
            Loaded += (_, _) => (string.IsNullOrEmpty(_exe.Text) ? _exe : _name).Focus();
        }

        private TextBlock Label(string text, double top) => new() { Text = text, Margin = new Thickness(0, top, 0, 4), FontWeight = FontWeights.SemiBold };

        private Button Flag(string text, Func<string> flag)
        {
            var b = new Button { Content = "+ " + text, Style = (Style)FindResource("SubtleBtn"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
            b.Click += (_, _) =>
            {
                var f = flag();
                if (!_args.Text.Contains(f.Split('=')[0], StringComparison.OrdinalIgnoreCase)) _args.Text = (_args.Text.Trim() + " " + f).Trim();
            };
            return b;
        }

        private string Browser() => Path.GetFileNameWithoutExtension(Environment.ExpandEnvironmentVariables(_exe.Text.Trim().Trim('"'))).ToLowerInvariant();
        private static string Safe(string s)
        {
            s = string.IsNullOrWhiteSpace(s) ? "Profile" : s.Trim();
            foreach (var ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch, '_');
            return s;
        }

        /// <summary>Splits an existing command line back into the fields.</summary>
        private void Load(CustomCommand c)
        {
            _name.Text = c.Label ?? "";
            var (exe, args) = CustomCommands.Split(c.Command ?? "");
            _exe.Text = exe;
            args = args.Trim();
            string Cut(string token) { var a = args[..^token.Length].TrimEnd(); return a; }
            if (args.EndsWith("\"%1\"")) { _mFile.IsChecked = true; args = Cut("\"%1\""); }
            else if (args.EndsWith("%1")) { _mFile.IsChecked = true; args = Cut("%1"); }
            else if (args.EndsWith("%*")) { _mAll.IsChecked = true; args = Cut("%*"); }
            else if (args.EndsWith(CustomCommands.NoItem)) { _mNone.IsChecked = true; args = Cut(CustomCommands.NoItem); }
            else if (args.EndsWith("\"") && args.LastIndexOf('"', args.Length - 2) is int q and >= 0 && args[(q + 1)..^1].Contains("://"))
            { _mLink.IsChecked = true; _url.Text = args[(q + 1)..^1]; args = args[..q].TrimEnd(); }
            else _mFile.IsChecked = true;   // older commands: the item is added at the end
            _args.Text = args;
            Update();
        }

        private string Build()
        {
            var exe = _exe.Text.Trim().Trim('"');
            var parts = "\"" + exe + "\"";
            if (_args.Text.Trim().Length > 0) parts += " " + _args.Text.Trim();
            if (_mFile.IsChecked == true) parts += " \"%1\"";
            else if (_mAll.IsChecked == true) parts += " %*";
            else if (_mLink.IsChecked == true) parts += " \"" + _url.Text.Trim() + "\"";
            else parts += " " + CustomCommands.NoItem;
            return parts;
        }

        private void Update() => _preview.Text = _exe.Text.Trim().Length == 0 ? "" : Build();

        private void Save()
        {
            if (_exe.Text.Trim().Length == 0) { _exe.Focus(); return; }
            if (_mLink.IsChecked == true && !_url.Text.Contains("://")) { _url.Focus(); return; }
            _c.Command = Build();
            _c.Label = string.IsNullOrWhiteSpace(_name.Text) ? Path.GetFileNameWithoutExtension(_exe.Text.Trim().Trim('"')) : _name.Text.Trim();
            DialogResult = true;
        }
    }
}
