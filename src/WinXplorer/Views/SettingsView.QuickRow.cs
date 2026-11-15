using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinXplorer.Services;

namespace WinXplorer.Views
{
    /// <summary>Settings › Context menu: the icon row on the menu's first line (which commands, in what order, how big).</summary>
    public partial class SettingsView
    {
        private StackPanel _rowEditor;

        private bool RowForItems => _menuTarget != "background";

        private List<string> CurrentRow => MenuCatalog.Row(S, RowForItems);

        private void SaveRow(List<string> row)
        {
            if (RowForItems) S.ItemQuickRow = row; else S.BackgroundQuickRow = row;
            Save();
            FillRowEditor();
        }

        /// <summary>Card placeholder; filled (again) whenever the edited menu or the row changes.</summary>
        private void AddRowEditor()
        {
            _rowEditor = new StackPanel();
            _page.Children.Add(CardBorder(_rowEditor));
        }

        private void FillRowEditor()
        {
            if (_rowEditor == null) return;
            _rowEditor.Children.Clear();
            var row = CurrentRow;
            int max = MenuCatalog.RowMax(S.QuickRowSize);

            var title = new DockPanel();
            var count = Dim($"{row.Count} / {max}");
            DockPanel.SetDock(count, Dock.Right);
            title.Children.Add(count);
            title.Children.Add(Tb(Loc.T("QuickRow"), 13));
            _rowEditor.Children.Add(title);
            _rowEditor.Children.Add(Dim(Loc.T("QuickRowDesc")).With(t => t.Margin = new Thickness(0, 2, 0, 10)));

            // size
            var sizes = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            foreach (var (key, label) in new[] { ("small", "QuickRowSmall"), ("medium", "QuickRowMedium"), ("large", "QuickRowLarge") })
            {
                var k = key;
                sizes.Children.Add(Chip("rowsize", Loc.T(label) + $"  ({MenuCatalog.RowMax(k)})", (S.QuickRowSize ?? "small") == k, () =>
                {
                    if (S.QuickRowSize == k) return;
                    var keep = CurrentRow;   // read before the size changes: a smaller size cuts the row
                    S.QuickRowSize = k;
                    SaveRow(keep.Take(MenuCatalog.RowMax(k)).ToList());
                }));
            }
            _rowEditor.Children.Add(sizes);

            // preview of the row as it will look in the menu, each icon with ◀ ▶ ×
            var current = new WrapPanel();
            for (int i = 0; i < row.Count; i++)
            {
                int index = i;
                var id = row[i];
                var chip = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(6, 3, 2, 3), Margin = new Thickness(0, 0, 6, 6) };
                chip.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
                chip.SetResourceReference(Border.BackgroundProperty, "SelectedBrush");
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(RowIcon(id, 16));
                sp.Children.Add(Tb(MenuCatalog.RowLabel(id), 12).With(t => { t.Margin = new Thickness(6, 0, 4, 0); t.VerticalAlignment = VerticalAlignment.Center; t.TextWrapping = TextWrapping.NoWrap; }));
                var left = SmallBtn("", Loc.T("MoveLeft"), () => { var r = CurrentRow; (r[index - 1], r[index]) = (r[index], r[index - 1]); SaveRow(r); });
                left.IsEnabled = index > 0;
                var right = SmallBtn("", Loc.T("MoveRight"), () => { var r = CurrentRow; (r[index + 1], r[index]) = (r[index], r[index + 1]); SaveRow(r); });
                right.IsEnabled = index < row.Count - 1;
                sp.Children.Add(left); sp.Children.Add(right);
                sp.Children.Add(SmallBtn("", Loc.T("Remove"), () => { var r = CurrentRow; r.RemoveAt(index); SaveRow(r); }));
                chip.Child = sp;
                current.Children.Add(chip);
            }
            if (row.Count == 0) current.Children.Add(Dim(Loc.T("QuickRowEmpty")).With(t => t.Margin = new Thickness(0, 0, 0, 6)));
            _rowEditor.Children.Add(current);

            // what can be added: commands for this menu, then a new file of each type from Settings › New files
            _rowEditor.Children.Add(Dim(Loc.T("QuickRowAdd")).With(t => t.Margin = new Thickness(0, 8, 0, 6)));
            var avail = new WrapPanel();
            var candidates = MenuCatalog.Commands.Select(c => c.Id)
                .Concat(NewFiles.Enabled.Select(e => MenuCatalog.NewPrefix + e))
                .Concat(CustomCommands.All.Select(c => c.Id))
                .Where(id => MenuCatalog.CanBeInRow(id, RowForItems) && !row.Contains(id));
            foreach (var id in candidates)
            {
                var x = id;
                var b = new Button { Style = (Style)FindResource("SubtleBtn"), Margin = new Thickness(0, 0, 6, 6), IsEnabled = row.Count < max };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(RowIcon(id, 14));
                sp.Children.Add(new TextBlock { Text = MenuCatalog.RowLabel(id), Margin = new Thickness(6, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
                var plus = new TextBlock { Text = "", Style = (Style)FindResource("Glyph"), FontSize = 10 };
                plus.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
                sp.Children.Add(plus);
                b.Content = sp;
                b.Click += (_, _) => { var r = CurrentRow; if (r.Count < max) { r.Add(x); SaveRow(r); } };
                avail.Children.Add(b);
            }
            _rowEditor.Children.Add(avail);

            var reset = Btn(Loc.T("Reset"), () => { if (RowForItems) S.ItemQuickRow = null; else S.BackgroundQuickRow = null; Save(); FillRowEditor(); }, "");
            reset.HorizontalAlignment = HorizontalAlignment.Left;
            reset.Margin = new Thickness(0, 6, 0, 0);
            _rowEditor.Children.Add(reset);
        }

        private FrameworkElement RowIcon(string id, double size)
        {
            if (id.StartsWith(MenuCatalog.NewPrefix))
                return new Image { Source = FileIcons.Small(id[MenuCatalog.NewPrefix.Length..]), Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
            if (id.StartsWith(CustomCommands.Prefix))
                return CustomCommands.Get(id) is { } c && CustomCommands.IconOf(c) is { } ico
                    ? new Image { Source = ico, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center }
                    : new TextBlock { Text = "\uE756", Style = (Style)FindResource("Glyph"), FontSize = size - 2, Width = size };
            return new TextBlock { Text = MenuCatalog.Get(id)?.Glyph ?? "", Style = (Style)FindResource("Glyph"), FontSize = size - 2, Width = size };
        }
    }
}
