using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinXplorer.Services;

namespace WinXplorer
{
    /// <summary>
    /// "Open with an app…": the installed programs and the Windows apps in one searchable list, plus
    /// "Find it myself…" for a program that isn't in the Start menu. Returns the picked app or null.
    /// </summary>
    public sealed class AppPickerWindow : Window
    {
        private readonly TextBox _search = new() { Height = 30, Margin = new Thickness(0, 0, 0, 10) };
        private readonly ListBox _list = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        private readonly TextBlock _status = new() { Margin = new Thickness(4, 12, 0, 12) };
        private List<AppEntry> _all = new();
        private AppEntry _result;

        public static AppEntry Pick(Window owner)
        {
            var w = new AppPickerWindow { Owner = owner, WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen };
            w.ShowDialog();
            return w._result;
        }

        private AppPickerWindow()
        {
            Title = Loc.T("AddOpenWithApp");
            Width = 460; Height = 600; MinWidth = 340; MinHeight = 320;
            ShowInTaskbar = false;
            Icon = App.LoadAppIcon();
            SourceInitialized += (_, _) => ThemeManager.ApplyWindowFrame(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            SetResourceReference(BackgroundProperty, "BarBrush");
            SetResourceReference(ForegroundProperty, "FgBrush");
            SetResourceReference(FontFamilyProperty, "UiFont");

            _search.Style = (Style)FindResource("InputBox");
            _search.TextChanged += (_, _) => Fill();
            _search.PreviewKeyDown += (_, e) => { if (e.Key == Key.Down && _list.Items.Count > 0) { _list.Focus(); FocusFirst(); e.Handled = true; } };
            _status.Text = Loc.T("Loading");
            _status.SetResourceReference(TextBlock.ForegroundProperty, "FgDimBrush");
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
            _list.ItemContainerStyle = (Style)FindResource("EditorItem");   // theme colours (light text in the dark theme)
            _list.SetResourceReference(ForegroundProperty, "FgBrush");
            _list.MouseDoubleClick += (_, e) => { if (ItemFrom(e.OriginalSource) != null) Accept(); };
            _list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Accept(); e.Handled = true; } };

            var browse = new Button { Content = Loc.T("FindMyself"), Style = (Style)FindResource("SubtleBtn"), Padding = new Thickness(12, 4, 12, 4) };
            browse.Click += (_, _) => Browse();
            var ok = new Button { Content = Loc.T("Add"), Style = (Style)FindResource("AccentBtn"), MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            ok.Click += (_, _) => Accept();
            var cancel = new Button { Content = Loc.T("Cancel"), Style = (Style)FindResource("SubtleBtn"), MinWidth = 90, IsCancel = true, Padding = new Thickness(12, 4, 12, 4) };
            var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = false };
            bottom.Children.Add(browse);
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(ok); right.Children.Add(cancel);
            DockPanel.SetDock(right, Dock.Right);
            bottom.Children.Add(right);

            var root = new DockPanel { Margin = new Thickness(16) };
            DockPanel.SetDock(_search, Dock.Top); root.Children.Add(_search);
            DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
            DockPanel.SetDock(_status, Dock.Top); root.Children.Add(_status);
            root.Children.Add(_list);
            Content = root;

            Loaded += async (_, _) =>
            {
                _search.Focus();
                _all = await ImageLoader.RunSta(AppCatalog.Load);
                Fill();
            };
        }

        private void Fill()
        {
            var q = _search.Text.Trim();
            var shown = _all.Where(a => q.Length == 0 || a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
            _list.Items.Clear();
            void Group(string title, IEnumerable<AppEntry> apps)
            {
                var l = apps.ToList();
                if (l.Count == 0) return;
                var h = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, _list.Items.Count == 0 ? 0 : 14, 0, 4) };
                _list.Items.Add(new ListBoxItem { Content = h, IsEnabled = false, Focusable = false });
                foreach (var a in l)
                {
                    var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 3, 2, 3) };
                    sp.Children.Add(new Image { Source = a.Icon, Width = 24, Height = 24, Margin = new Thickness(0, 0, 10, 0) });
                    sp.Children.Add(new TextBlock { Text = a.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                    _list.Items.Add(new ListBoxItem { Content = sp, Tag = a });
                }
            }
            Group(Loc.T("InstalledPrograms"), shown.Where(a => !a.IsStoreApp));
            Group(Loc.T("WindowsApps"), shown.Where(a => a.IsStoreApp));
            _status.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (shown.Count == 0) _status.Text = _all.Count == 0 ? Loc.T("Loading") : Loc.T("NothingFound");
        }

        private void FocusFirst()
        {
            var first = _list.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is AppEntry);
            if (first != null) { _list.SelectedItem = first; first.Focus(); }
        }

        private static AppEntry ItemFrom(object src)
        {
            for (var d = src as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
                if (d is ListBoxItem li) return li.Tag as AppEntry;
            return null;
        }

        private void Accept()
        {
            if ((_list.SelectedItem as ListBoxItem)?.Tag is not AppEntry a) return;
            _result = a;
            DialogResult = true;
        }

        private void Browse()
        {
            var picked = PickerWindow.Files(this, Loc.T("AddOpenWithApp"), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                new[] { new PickerWindow.Filter(Loc.T("Programs"), "*.exe") });
            if (picked == null || picked.Length == 0) return;
            var file = picked[0];
            string name = null;
            try { name = System.Diagnostics.FileVersionInfo.GetVersionInfo(file).FileDescription; } catch { }
            if (string.IsNullOrWhiteSpace(name)) name = System.IO.Path.GetFileNameWithoutExtension(file);
            _result = new AppEntry { Name = name.Trim(), Command = $"\"{file}\" \"%1\"" };
            DialogResult = true;
        }
    }
}
