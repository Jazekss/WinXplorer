using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinXplorer.Services;

namespace WinXplorer.Views
{
    /// <summary>Settings › Shortcuts &amp; help: every keyboard shortcut (editable) and short how-tos.</summary>
    public partial class SettingsView
    {
        /// <summary>A help row: either a remappable command (Cmd) or a fixed mouse/keyboard hint (Keys + What).</summary>
        private readonly record struct HelpRow(string Cmd, string Keys = null, string What = null);

        private static HelpRow K(string cmd) => new(cmd);
        private static HelpRow Fixed(string keys, string what) => new(null, keys, what);

        private static readonly (string group, string glyph, HelpRow[] rows)[] Shortcuts =
        {
            ("HkNavigation", "", new[]
            {
                K("Back"), K("Forward"), K("Up"), K("Address"), K("Search"), K("Refresh"), K("OtherPane"),
                Fixed("🖱 Middle click", "HkMiddleClick"),
            }),
            ("HkTabs", "", new[]
            {
                K("NewTab"), K("CloseTab"), K("NextTab"), K("PrevTab"),
                Fixed("Ctrl + 1 … 9", "HkTabN"),
                K("NewWindow"), K("Preview"), K("Settings"),
            }),
            ("HkFiles", "", new[]
            {
                K("Open"), K("QuickView"), K("Rename"), K("Delete"), K("DeletePerm"),
                K("Copy"), K("Cut"), K("Paste"), K("CopyPath"), K("NewFolder"), K("Undo"),
                Fixed("Ctrl + A", "HkSelectAll"),
                K("Properties"), K("Menu"),
                Fixed("Ctrl + 🖱 wheel", "HkIconSize"),
                Fixed("🖱 Drag in empty space", "HkMarquee"),
            }),
            ("HkViewer", "", new[]
            {
                K("Viewer.Prev"), K("Viewer.Next"), K("Viewer.First"), K("Viewer.Last"),
                K("Viewer.ZoomIn"), K("Viewer.ZoomOut"),
                Fixed("🖱 wheel", "HkZoom"),
                K("Viewer.Fit"), K("Viewer.Actual"), K("Viewer.RotateRight"), K("Viewer.RotateLeft"),
                K("Viewer.Crop"), K("Viewer.Copy"), K("Viewer.Open"), K("Viewer.Delete"), K("Viewer.Maximize"), K("Viewer.Close"),
            }),
            ("HkQuick", "", new[]
            {
                K("Quick.Prev"), K("Quick.Next"), K("Quick.First"), K("Quick.Last"), K("Quick.Open"), K("Quick.Close"),
            }),
            ("HkRenameMany", "", new[]
            {
                Fixed("Alt + 🖱 drag  /  🖱 middle drag", "HkColumn"),
                Fixed("Alt + Shift + arrows", "HkColumnKeys"),
                Fixed("Ctrl + Enter", "HkApplyRename"),
                Fixed("Esc", "HkCancel"),
            }),
        };

        private static readonly (string glyph, string title, string text)[] Tips =
        {
            ("", "TipSearchT", "TipSearch"),
            ("", "TipArchivesT", "TipArchives"),
            ("", "TipRenameT", "TipRename"),
            ("", "TipQuickT", "TipQuick"),
            ("", "TipCropT", "TipCrop"),
            ("", "TipSplitT", "TipSplit"),
        };

        /// <summary>Redraws one command row (after a change); filled while the help page is built.</summary>
        private readonly Dictionary<string, Action<string>> _hkRows = new();
        private Button _hkResetAll;

        private void BuildHelp()
        {
            _hkRows.Clear();
            _page.Children.Add(Dim(Loc.T("HelpIntro")));
            Header(Loc.T("HkTitle"));

            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = true };
            _hkResetAll = Btn(Loc.T("HkResetAll"), () =>
            {
                Hotkeys.ResetAll();
                foreach (var r in _hkRows.Values) r(null);
                UpdateResetAll();
            }, "");
            DockPanel.SetDock(_hkResetAll, Dock.Right);
            _hkResetAll.Margin = new Thickness(12, 0, 0, 0);
            top.Children.Add(_hkResetAll);
            var hint = Dim(Loc.T("HkEditHint"));
            hint.VerticalAlignment = VerticalAlignment.Center;
            top.Children.Add(hint);
            _page.Children.Add(top);
            UpdateResetAll();

            foreach (var (group, glyph, rows) in Shortcuts)
            {
                var list = new StackPanel();
                foreach (var row in rows)
                    list.Children.Add(row.Cmd != null ? CommandRow(row.Cmd) : FixedRow(row.Keys, row.What));
                WideCard(glyph, Loc.T(group), null, list);
            }

            Header(Loc.T("HelpHowTo"));
            foreach (var (glyph, title, text) in Tips) Card(glyph, Loc.T(title), Loc.T(text), null);
        }

        private void UpdateResetAll()
        {
            if (_hkResetAll != null) _hkResetAll.IsEnabled = !Hotkeys.AllDefault;
        }

        private static Grid RowGrid()
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            return g;
        }

        private static FrameworkElement FixedRow(string keysText, string what)
        {
            var g = RowGrid();
            var keys = new WrapPanel { Margin = new Thickness(2, 3, 12, 3), VerticalAlignment = VerticalAlignment.Center };
            foreach (var part in keysText.Split("  /  "))
            {
                if (keys.Children.Count > 0) keys.Children.Add(Slash());
                keys.Children.Add(Keycap(part.Replace("🖱 ", "").Replace("Middle click", Loc.T("HkMouseMiddle")).Replace("Drag in empty space", Loc.T("HkMouseDrag"))
                    .Replace("middle drag", Loc.T("HkMouseMiddleDrag")).Replace("wheel", Loc.T("HkMouseWheel")).Replace("drag", Loc.T("HkMouseDragShort")).Replace("arrows", Loc.T("HkArrows"))));
            }
            g.Children.Add(keys);
            var t = Tb(Loc.T(what), 12);
            t.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(t, 1); g.Children.Add(t);
            return g;
        }

        private static TextBlock Slash() => Dim("/", 12).With(t => { t.Margin = new Thickness(5, 0, 5, 0); t.VerticalAlignment = VerticalAlignment.Center; });

        /// <summary>A remappable command: its keycaps (click = change, right-click = remove), + to add one, ↺ to reset.</summary>
        private FrameworkElement CommandRow(string id)
        {
            var cmd = Hotkeys.Find(id);
            var g = RowGrid();

            void Fill(string message)
            {
                g.Children.Clear();
                var keys = new WrapPanel { Margin = new Thickness(0, 2, 12, 2), VerticalAlignment = VerticalAlignment.Center };
                var gestures = Hotkeys.Get(id);
                for (int i = 0; i < gestures.Count; i++)
                {
                    if (i > 0) keys.Children.Add(Slash());
                    int index = i;
                    var b = KeycapButton(gestures[i].Display(), Loc.T("HkKeyTip"));
                    b.Click += (_, _) => StartCapture(keys, b, id, index, Fill);
                    b.MouseRightButtonUp += (_, e) => { e.Handled = true; Hotkeys.Remove(id, index); Fill(null); UpdateResetAll(); };
                    keys.Children.Add(b);
                }
                if (gestures.Count == 0) keys.Children.Add(Dim(Loc.T("HkNone"), 12).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(2, 0, 6, 0); }));
                var add = SmallBtn("", Loc.T("HkAdd"), () => { });
                add.Margin = new Thickness(4, 0, 0, 0);
                add.Opacity = 0.7;
                add.Click += (_, _) => StartCapture(keys, add, id, -1, Fill);
                keys.Children.Add(add);
                g.Children.Add(keys);

                var what = Tb(Loc.T(cmd.Label), 12);
                what.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(what, 1); g.Children.Add(what);

                if (!Hotkeys.IsDefault(id))
                {
                    var reset = SmallBtn("", Loc.T("HkReset") + "  (" + string.Join("  /  ", cmd.Defaults.Select(Gesture.Parse).Where(x => x != null).Select(x => x.Value.Display())) + ")",
                        () => { Hotkeys.Reset(id); Fill(null); UpdateResetAll(); });
                    reset.VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetColumn(reset, 2); g.Children.Add(reset);
                }

                if (!string.IsNullOrEmpty(message))
                {
                    var warn = Tb("⚠  " + message, 11, "AccentTextBrush");
                    warn.Margin = new Thickness(0, 0, 0, 4);
                    Grid.SetRow(warn, 1); Grid.SetColumnSpan(warn, 3); g.Children.Add(warn);
                }
            }

            Fill(null);
            _hkRows[id] = Fill;
            return g;
        }

        /// <summary>Puts a "press keys" box in place of <paramref name="anchor"/> and waits for a combination.</summary>
        private void StartCapture(WrapPanel keys, UIElement anchor, string id, int index, Action<string> refill)
        {
            int pos = keys.Children.IndexOf(anchor);
            if (pos < 0) return;
            var text = new TextBlock { Text = Loc.T("HkPress"), FontSize = 12, FontStyle = FontStyles.Italic };
            text.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            text.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
            var box = new Border
            {
                CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1.5), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 1, 0, 1),
                Focusable = true, FocusVisualStyle = null, Child = text, MinWidth = 90,
            };
            box.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            box.SetResourceReference(Border.BackgroundProperty, "InputBrush");
            keys.Children.RemoveAt(pos);
            keys.Children.Insert(pos, box);

            bool done = false;
            void End(string message)
            {
                if (done) return;
                done = true;
                Hotkeys.Capturing = false;
                refill(message);
            }

            box.PreviewKeyDown += (_, e) =>
            {
                e.Handled = true;
                var g = Hotkeys.FromEvent(e);
                if (g == null)
                {
                    // only modifiers so far: show them while the user is still pressing
                    var m = Keyboard.Modifiers;
                    var parts = new List<string>();
                    if (m.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
                    if (m.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
                    if (m.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
                    text.Text = parts.Count > 0 ? string.Join(" + ", parts) + " + …" : Loc.T("HkPress");
                    return;
                }
                if (g.Value.Key == Key.Escape && g.Value.Mods == ModifierKeys.None) { End(null); return; }
                var clashes = Hotkeys.Assign(id, index, g.Value);
                End(clashes.Count > 0 ? string.Format(Loc.T("HkTaken"), g.Value.Display(), string.Join(", ", clashes.Select(c => "\"" + Loc.T(c.Label) + "\""))) : null);
                foreach (var c in clashes)
                    if (_hkRows.TryGetValue(c.Id, out var r)) r(null);
                UpdateResetAll();
            };
            box.PreviewKeyUp += (_, e) =>
            {
                e.Handled = true;
                if (!done && Keyboard.Modifiers == ModifierKeys.None) text.Text = Loc.T("HkPress");
            };
            box.LostKeyboardFocus += (_, _) => End(null);

            Hotkeys.Capturing = true;
            box.Dispatcher.BeginInvoke(new Action(() => { if (!done && !Keyboard.Focus(box).Equals(box)) End(null); }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private Button KeycapButton(string text, string tip)
        {
            var b = new Button { Style = (Style)FindResource("IconBtn"), Padding = new Thickness(2), MinWidth = 0, MinHeight = 0, ToolTip = tip, Content = Keycap(text) };
            return b;
        }

        private static Border Keycap(string text)
        {
            var b = new Border { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1, 1, 1, 2), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 1, 0, 1) };
            b.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            b.SetResourceReference(Border.BackgroundProperty, "ContentBrush");
            var t = new TextBlock { Text = text, FontSize = 12 };
            t.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            t.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
            b.Child = t;
            return b;
        }
    }

    internal static class FluentExt
    {
        public static T With<T>(this T o, System.Action<T> a) { a(o); return o; }
    }
}
