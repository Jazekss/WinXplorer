using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using WinXplorer.Models;
using WinXplorer.Services;

namespace WinXplorer.Views
{
    /// <summary>
    /// Renames many files at once, like editing text: one name per line, the current names alongside.
    /// Column selection (Alt+drag, middle-button drag or Alt+Shift+arrows) edits the same spot on every line.
    /// Lines whose name would clash are pink and the rename can't be confirmed until they are fixed.
    /// </summary>
    public sealed class MultiRenameWindow : Window
    {
        private readonly List<FileItem> _items;
        private readonly TextEditor _old, _new;
        private readonly CheckBox _ext, _regex;
        private readonly TextBox _find, _replace;
        private readonly TextBlock _status;
        private readonly Button _ok;
        private readonly LineMarks _marks = new();
        private readonly Dictionary<string, HashSet<string>> _folderNames = new(StringComparer.OrdinalIgnoreCase);
        private bool _showExt;

        /// <summary>What was renamed (old full path, new full path) after OK.</summary>
        public List<(string from, string to)> Result { get; private set; }

        public static List<(string from, string to)> Show(Window owner, IEnumerable<FileItem> items)
        {
            var list = items.Where(i => !i.IsDrive).ToList();
            if (list.Count == 0) return null;
            var w = new MultiRenameWindow(list) { Owner = owner };
            return w.ShowDialog() == true ? w.Result : null;
        }

        private MultiRenameWindow(List<FileItem> items)
        {
            _items = items;
            Title = Loc.T("Rename") + " · " + Loc.Items(items.Count);
            Width = 900; Height = Math.Clamp(220 + items.Count * 18, 420, 760);
            MinWidth = 560; MinHeight = 320;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            UseLayoutRounding = true;
            SourceInitialized += (_, _) => ThemeManager.ApplyWindowFrame(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            SetResourceReference(BackgroundProperty, "BarBrush");
            SetResourceReference(ForegroundProperty, "FgBrush");
            SetResourceReference(FontFamilyProperty, "UiFont");
            FontSize = 12;

            var root = new Grid { Margin = new Thickness(16, 12, 16, 14) };
            for (int i = 0; i < 5; i++) root.RowDefinitions.Add(new RowDefinition { Height = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

            // hint
            var hint = new TextBlock { Text = Loc.T("RenameManyHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "FgDimBrush");
            root.Children.Add(hint);

            // tools: find / replace, extensions
            var tools = new DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = false };
            Grid.SetRow(tools, 1);
            _find = Box(180, Loc.T("Find"));
            _replace = Box(180, Loc.T("ReplaceWith"));
            var replaceBtn = new Button { Content = Loc.T("ReplaceAll"), Style = (Style)FindResource("SubtleBtn"), Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(12, 0, 12, 0), Height = 30 };
            replaceBtn.Click += (_, _) => ReplaceAll();
            _regex = new CheckBox { Content = ".*", ToolTip = Loc.T("UseRegex"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            _regex.SetResourceReference(ForegroundProperty, "FgBrush");
            var findLbl = new TextBlock { Text = Loc.T("Find"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            tools.Children.Add(findLbl);
            tools.Children.Add(_find);
            tools.Children.Add(new TextBlock { Text = "→", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) });
            tools.Children.Add(_replace);
            tools.Children.Add(_regex);
            tools.Children.Add(replaceBtn);
            var reset = new Button { Content = Loc.T("Reset"), Style = (Style)FindResource("SubtleBtn"), Padding = new Thickness(12, 0, 12, 0), Height = 30, Margin = new Thickness(8, 0, 0, 0) };
            reset.Click += (_, _) => { _new.Document.Text = string.Join("\n", _items.Select(Shown)); };
            DockPanel.SetDock(reset, Dock.Right);
            _ext = new CheckBox { Content = Loc.T("EditExtensions"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            _ext.SetResourceReference(ForegroundProperty, "FgBrush");
            _ext.Checked += (_, _) => ToggleExt(true);
            _ext.Unchecked += (_, _) => ToggleExt(false);
            DockPanel.SetDock(_ext, Dock.Right);
            tools.Children.Add(reset);
            tools.Children.Add(_ext);
            root.Children.Add(tools);

            // column titles
            var heads = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            Grid.SetRow(heads, 2);
            heads.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            heads.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            heads.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            var h1 = new TextBlock { Text = Loc.T("CurrentName") }; h1.SetResourceReference(TextBlock.ForegroundProperty, "FgDimBrush");
            var h2 = new TextBlock { Text = Loc.T("NewName"), FontWeight = FontWeights.SemiBold }; Grid.SetColumn(h2, 2);
            heads.Children.Add(h1); heads.Children.Add(h2);
            root.Children.Add(heads);

            // editors
            var eds = new Grid();
            Grid.SetRow(eds, 3);
            eds.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            eds.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            eds.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            _old = Editor(true);
            _new = Editor(false);
            var b1 = Frame(_old); var b2 = Frame(_new);
            Grid.SetColumn(b2, 2);
            eds.Children.Add(b1); eds.Children.Add(b2);
            root.Children.Add(eds);
            _old.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            _old.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
            _new.TextArea.TextView.ScrollOffsetChanged += (_, _) => _old.ScrollToVerticalOffset(_new.VerticalOffset);
            _old.TextArea.TextView.ScrollOffsetChanged += (_, _) => { if (Math.Abs(_old.VerticalOffset - _new.VerticalOffset) > 0.5) _new.ScrollToVerticalOffset(_old.VerticalOffset); };
            _new.TextArea.TextView.BackgroundRenderers.Add(_marks);
            _new.TextChanged += (_, _) => Validate();
            _new.TextArea.Caret.PositionChanged += (_, _) => _old.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
            _old.TextArea.TextView.BackgroundRenderers.Add(new CaretLineMark(_new));
            ColumnDrag(_new.TextArea);

            // status + buttons
            var bottom = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            Grid.SetRow(bottom, 4);
            var btns = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(btns, Dock.Right);
            _ok = new Button { Content = Loc.T("Rename"), Style = (Style)FindResource("AccentBtn"), Margin = new Thickness(0, 0, 8, 0), MinWidth = 96 };
            _ok.Click += (_, _) => Apply();
            var cancel = new Button { Content = Loc.T("Cancel"), Style = (Style)FindResource("SubtleBtn"), IsCancel = true, MinWidth = 80 };
            btns.Children.Add(_ok); btns.Children.Add(cancel);
            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 12, 0) };
            bottom.Children.Add(btns);
            bottom.Children.Add(_status);
            root.Children.Add(bottom);
            Content = root;

            // folder contents for the "already exists" check
            foreach (var dir in _items.Select(Dir).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try { foreach (var e in Directory.EnumerateFileSystemEntries(dir)) set.Add(Path.GetFileName(e)); } catch { }
                foreach (var it in _items.Where(i => string.Equals(Dir(i), dir, StringComparison.OrdinalIgnoreCase))) set.Remove(it.Name);
                _folderNames[dir] = set;
            }

            _old.Document.Text = string.Join("\n", _items.Select(Shown));
            _new.Document.Text = _old.Document.Text;
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { Apply(); e.Handled = true; }
                else if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
            };
            Loaded += (_, _) => { _new.Focus(); _new.TextArea.Caret.Offset = _new.Document.GetLineByNumber(1).EndOffset; };
        }

        private TextBox Box(double width, string tip)
        {
            var t = new TextBox { Width = width, Height = 30, Style = (Style)FindResource("InputBox"), ToolTip = tip, VerticalContentAlignment = VerticalAlignment.Center };
            t.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ReplaceAll(); e.Handled = true; } };
            return t;
        }

        private TextEditor Editor(bool readOnly)
        {
            var ed = new TextEditor
            {
                IsReadOnly = readOnly,
                ShowLineNumbers = !readOnly,
                FontFamily = (FontFamily)FindResource("MonoFont"),
                FontSize = 13,
                Background = Brushes.Transparent,
                Padding = new Thickness(4, 2, 4, 2),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                WordWrap = false,
            };
            ed.SetResourceReference(ForegroundProperty, readOnly ? "FgDimBrush" : "FgBrush");
            ed.SetResourceReference(TextEditor.LineNumbersForegroundProperty, "FgFaintBrush");
            ed.Options.EnableHyperlinks = false;
            ed.Options.EnableEmailHyperlinks = false;
            ed.Options.EnableRectangularSelection = true;
            ed.Options.AllowScrollBelowDocument = false;
            ed.Options.ShowSpaces = false;
            StrongSelection(ed.TextArea);
            return ed;
        }

        /// <summary>A selection that stays obvious on a dark screen in sunlight: solid accent, white text, a border.</summary>
        public static void StrongSelection(TextArea area)
        {
            var a = Application.Current.TryFindResource("AccentColor") is Color c ? c : Color.FromRgb(0x00, 0x78, 0xD4);
            area.SelectionCornerRadius = 1;
            area.SelectionBrush = new SolidColorBrush(Color.FromArgb(ThemeManager.IsDark ? (byte)0xC0 : (byte)0x90, a.R, a.G, a.B));
            area.SelectionBorder = new Pen(new SolidColorBrush(ThemeManager.IsDark ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) : a), 1);
            area.SelectionForeground = Brushes.White;
        }

        private static Border Frame(TextEditor ed)
        {
            var b = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = ed, Padding = new Thickness(1) };
            b.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            b.SetResourceReference(Border.BackgroundProperty, "ContentBrush");
            return b;
        }

        /// <summary>Middle mouse button: drag a column (rectangular) selection, like in code editors.</summary>
        private static void ColumnDrag(TextArea area)
        {
            TextViewPosition? start = null;
            area.PreviewMouseDown += (_, e) =>
            {
                if (e.ChangedButton != MouseButton.Middle) return;
                var p = Pos(area, e);
                if (p == null) return;
                start = p;
                area.Focus();
                area.Caret.Position = p.Value;
                area.ClearSelection();
                area.CaptureMouse();
                e.Handled = true;
            };
            area.PreviewMouseMove += (_, e) =>
            {
                if (start == null || e.MiddleButton != MouseButtonState.Pressed) return;
                var p = Pos(area, e);
                if (p == null) return;
                area.Selection = new RectangleSelection(area, start.Value, p.Value);
                area.Caret.Position = p.Value;
                e.Handled = true;
            };
            area.PreviewMouseUp += (_, e) =>
            {
                if (e.ChangedButton != MouseButton.Middle || start == null) return;
                start = null;
                area.ReleaseMouseCapture();
                e.Handled = true;
            };
        }

        private static TextViewPosition? Pos(TextArea area, MouseEventArgs e)
        {
            var tv = area.TextView;
            var pt = e.GetPosition(tv);
            pt.X = Math.Max(0, pt.X); pt.Y = Math.Clamp(pt.Y, 0, Math.Max(0, tv.ActualHeight - 1));
            pt += tv.ScrollOffset;
            // virtual space past the end of short lines, so a column can span lines of different length
            var line = tv.GetVisualLineFromVisualTop(pt.Y);
            if (line == null) return null;
            int col = line.GetVisualColumn(pt, true);
            int docLine = line.FirstDocumentLine.LineNumber;
            return new TextViewPosition(docLine, Math.Max(1, line.GetRelativeOffset(Math.Min(col, line.VisualLength)) + 1), col);
        }

        // ---------- names ----------
        private static string Dir(FileItem i) => Path.GetDirectoryName(i.FullPath.TrimEnd('\\')) ?? "";
        private static string Ext(FileItem i) => i.IsDirectory ? "" : Path.GetExtension(i.Name);
        private string Shown(FileItem i) => _showExt || i.IsDirectory ? i.Name : Path.GetFileNameWithoutExtension(i.Name);
        private string Final(int i, string line)
        {
            line = line.Trim();
            return _showExt || _items[i].IsDirectory ? line : line + Ext(_items[i]);
        }

        private List<string> Lines()
        {
            var lines = _new.Document.Text.Replace("\r", "").Split('\n').ToList();
            // a newline after the last name is fine
            while (lines.Count > _items.Count && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return lines;
        }

        private void ToggleExt(bool on)
        {
            var lines = Lines();
            bool fits = lines.Count == _items.Count;
            for (int i = 0; fits && i < lines.Count; i++)
            {
                var it = _items[i];
                if (it.IsDirectory) continue;
                var ext = Ext(it);
                if (on) lines[i] += ext;
                else if (lines[i].EndsWith(ext, StringComparison.OrdinalIgnoreCase)) lines[i] = lines[i][..^ext.Length];
            }
            _showExt = on;
            _old.Document.Text = string.Join("\n", _items.Select(Shown));
            if (fits) _new.Document.Text = string.Join("\n", lines);
            Validate();
        }

        private void ReplaceAll()
        {
            var find = _find.Text;
            if (string.IsNullOrEmpty(find)) return;
            var lines = Lines();
            try
            {
                Regex rx = _regex.IsChecked == true ? new Regex(find, RegexOptions.CultureInvariant) : null;
                for (int i = 0; i < lines.Count; i++)
                    lines[i] = rx != null ? rx.Replace(lines[i], _replace.Text) : lines[i].Replace(find, _replace.Text, StringComparison.Ordinal);
            }
            catch (ArgumentException ex) { _status.Text = ex.Message; return; }
            // one undo step in the editor
            _new.Document.Replace(0, _new.Document.TextLength, string.Join("\n", lines));
        }

        private static readonly char[] Bad = Path.GetInvalidFileNameChars();

        private List<(string from, string to)> Validate()
        {
            var lines = Lines();
            _marks.Bad.Clear(); _marks.Changed.Clear();
            string first = null;
            var pairs = new List<(string from, string to)>();
            if (lines.Count != _items.Count)
            {
                first = Loc.F("RenLineCount", lines.Count.ToString(), _items.Count.ToString());
                for (int i = _items.Count; i < lines.Count; i++) _marks.Bad.Add(i + 1);
            }
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int n = Math.Min(lines.Count, _items.Count);
            int changed = 0;
            for (int i = 0; i < n; i++)
            {
                var it = _items[i];
                var name = Final(i, lines[i]);
                var dir = Dir(it);
                string problem = null;
                if (string.IsNullOrWhiteSpace(lines[i]) || name.IndexOfAny(Bad) >= 0 || name.EndsWith('.') || name.Length > 255)
                    problem = Loc.F("RenInvalid", (i + 1).ToString());
                else if (seen.TryGetValue(dir + "\\" + name, out var other))
                    problem = Loc.F("RenDup", (i + 1).ToString(), (other + 1).ToString());
                else if (_folderNames.TryGetValue(dir, out var names) && names.Contains(name))
                    problem = Loc.F("RenExists", (i + 1).ToString(), name);
                if (problem == null) seen[dir + "\\" + name] = i;
                if (problem != null) { _marks.Bad.Add(i + 1); first ??= problem; }
                if (!string.Equals(name, it.Name, StringComparison.Ordinal))
                {
                    changed++;
                    _marks.Changed.Add(i + 1);
                    pairs.Add((it.FullPath.TrimEnd('\\'), Path.Combine(dir, name)));
                }
            }
            _new.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
            bool ok = first == null && changed > 0;
            _ok.IsEnabled = ok;
            _ok.Opacity = ok ? 1 : 0.45;
            _status.Text = first ?? Loc.F("RenChanged", changed.ToString(), _items.Count.ToString());
            if (first != null) _status.Foreground = new SolidColorBrush(ThemeManager.IsDark ? Color.FromRgb(0xFF, 0x99, 0xA4) : Color.FromRgb(0xC4, 0x2B, 0x1C));
            else _status.SetResourceReference(TextBlock.ForegroundProperty, "FgDimBrush");
            return ok ? pairs : null;
        }

        private void Apply()
        {
            var pairs = Validate();
            if (pairs == null) return;
            var errors = new List<string>();
            Result = FileOps.RenameBatch(pairs, errors);
            if (errors.Count > 0)
                MessageBox.Show(this, Loc.T("RenFailed") + "\n\n" + string.Join("\n", errors.Take(12)) + (errors.Count > 12 ? "\n…" : ""), "WinXplorer", MessageBoxButton.OK, MessageBoxImage.Warning);
            else if (Result.Count > 1) Notify.Show(Loc.F("RenamedMany", Loc.Items(Result.Count)));
            DialogResult = true;
        }

        /// <summary>Pink lines = name clashes or invalid names; light accent = changed.</summary>
        private sealed class LineMarks : IBackgroundRenderer
        {
            public readonly HashSet<int> Bad = new(), Changed = new();
            public KnownLayer Layer => KnownLayer.Background;

            public void Draw(TextView tv, DrawingContext dc)
            {
                if (!tv.VisualLinesValid) return;
                var bad = new SolidColorBrush(ThemeManager.IsDark ? Color.FromArgb(0x66, 0xC4, 0x2B, 0x55) : Color.FromArgb(0x55, 0xFF, 0x6F, 0x91));
                var chg = new SolidColorBrush(Color.FromArgb(ThemeManager.IsDark ? (byte)0x26 : (byte)0x1C, 0x00, 0x78, 0xD4));
                foreach (var vl in tv.VisualLines)
                {
                    int ln = vl.FirstDocumentLine.LineNumber;
                    var brush = Bad.Contains(ln) ? bad : Changed.Contains(ln) ? chg : null;
                    if (brush == null) continue;
                    dc.DrawRectangle(brush, null, new Rect(0, vl.VisualTop - tv.ScrollOffset.Y, tv.ActualWidth + tv.ScrollOffset.X, vl.Height));
                }
            }
        }

        /// <summary>Highlights, in the current-names column, the line the caret is on in the new-names column.</summary>
        private sealed class CaretLineMark : IBackgroundRenderer
        {
            private readonly TextEditor _src;
            public CaretLineMark(TextEditor src) => _src = src;
            public KnownLayer Layer => KnownLayer.Background;

            public void Draw(TextView tv, DrawingContext dc)
            {
                if (!tv.VisualLinesValid) return;
                int ln = _src.TextArea.Caret.Line;
                var brush = new SolidColorBrush(Color.FromArgb(0x22, 0x80, 0x80, 0x80));
                foreach (var vl in tv.VisualLines)
                    if (vl.FirstDocumentLine.LineNumber == ln)
                        dc.DrawRectangle(brush, null, new Rect(0, vl.VisualTop - tv.ScrollOffset.Y, tv.ActualWidth, vl.Height));
            }
        }
    }
}
