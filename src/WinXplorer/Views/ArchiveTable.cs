using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using WinXplorer.Models;
using WinXplorer.Services;

namespace WinXplorer.Views
{
    /// <summary>One line of the archive contents table (public: it is bound from XAML templates).</summary>
    public sealed class ArchiveRow
    {
        public string Number { get; init; }
        public string Name { get; init; }
        public bool IsDir { get; init; }
        public Thickness Indent { get; init; }
        public string Glyph => IsDir ? "" : Name == "…" ? "" : "";
        public double GlyphOpacity => IsDir ? 1 : 0.45;
        public FontWeight Weight => IsDir ? FontWeights.SemiBold : FontWeights.Normal;
        public string SizeText { get; init; }
        public string Ext { get; init; }
        public string DateText { get; init; }
    }

    /// <summary>
    /// What's inside an archive, as a table: row number, name (folders as a tree, contents indented), size,
    /// extension, date and time. Narrow previews show number, name and size only.
    /// </summary>
    public static class ArchiveTable
    {
        private sealed class Node
        {
            public string Name;
            public bool Dir;
            public long Size;
            public DateTime Mod;
            public readonly Dictionary<string, Node> Kids = new(StringComparer.OrdinalIgnoreCase);
        }

        public static List<ArchiveRow> Rows(List<(string path, long size, DateTime mod, bool dir)> entries, bool truncated)
        {
            var root = new Node { Dir = true };
            foreach (var e in entries)
            {
                var parts = e.path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                var n = root;
                for (int i = 0; i < parts.Length; i++)
                {
                    bool last = i == parts.Length - 1;
                    if (!n.Kids.TryGetValue(parts[i], out var k)) n.Kids[parts[i]] = k = new Node { Name = parts[i], Dir = !last || e.dir };
                    if (last)
                    {
                        k.Dir |= e.dir;
                        if (!e.dir) k.Size = e.size;
                        if (e.mod != DateTime.MinValue) k.Mod = e.mod;
                    }
                    n = k;
                }
            }
            Total(root);

            var rows = new List<ArchiveRow>();
            void Walk(Node n, int depth)
            {
                foreach (var k in n.Kids.Values.OrderBy(x => x.Dir ? 0 : 1).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    rows.Add(new ArchiveRow
                    {
                        Number = (rows.Count + 1).ToString(),
                        Name = k.Name,
                        IsDir = k.Dir,
                        Indent = new Thickness(6 + depth * 16, 0, 4, 0),
                        SizeText = k.Dir && k.Size == 0 ? "" : FileItem.FormatSize(k.Size),
                        Ext = k.Dir ? "" : Path.GetExtension(k.Name).TrimStart('.').ToLowerInvariant(),
                        DateText = k.Mod == DateTime.MinValue ? "" : k.Mod.ToString("g"),
                    });
                    if (k.Dir) Walk(k, depth + 1);
                }
            }
            Walk(root, 0);
            if (truncated) rows.Add(new ArchiveRow { Name = "…", Indent = new Thickness(6, 0, 4, 0) });
            return rows;
        }

        /// <summary>Folder size = everything inside; folder date = newest file when the archive has none for it.</summary>
        private static void Total(Node n)
        {
            if (!n.Dir) return;
            long sum = 0; DateTime newest = DateTime.MinValue;
            foreach (var k in n.Kids.Values)
            {
                Total(k);
                sum += k.Size;
                if (k.Mod > newest) newest = k.Mod;
            }
            n.Size = sum;
            if (n.Mod == DateTime.MinValue) n.Mod = newest;
        }

        private const string Ns = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";

        private static string Cols(bool wide) => wide
            ? "<ColumnDefinition Width='44'/><ColumnDefinition Width='*' MinWidth='120'/><ColumnDefinition Width='78'/><ColumnDefinition Width='62'/><ColumnDefinition Width='128'/>"
            : "<ColumnDefinition Width='38'/><ColumnDefinition Width='*'/><ColumnDefinition Width='74'/>";

        private static string Cell(int col, string content, bool lastCol) =>
            $"<Border Grid.Column='{col}' BorderBrush='{{DynamicResource BorderBrush}}' BorderThickness='0,0,{(lastCol ? 0 : 1)},0'>{content}</Border>";

        private static string Text(string binding, string align = "Left", string brush = "FgBrush", string extra = "") =>
            $"<TextBlock Text='{binding}' HorizontalAlignment='{align}' VerticalAlignment='Center' Margin='6,0,6,0' TextTrimming='CharacterEllipsis' Foreground='{{DynamicResource {brush}}}' {extra}/>";

        private static DataTemplate RowTemplate(bool wide)
        {
            var name = "<DockPanel Margin='{Binding Indent}' VerticalAlignment='Center'>"
                + "<TextBlock DockPanel.Dock='Left' Text='{Binding Glyph}' FontFamily='{DynamicResource IconFont}' FontSize='12' Margin='0,0,6,0' VerticalAlignment='Center' Opacity='{Binding GlyphOpacity}' Foreground='{DynamicResource AccentTextBrush}'/>"
                + "<TextBlock Text='{Binding Name}' FontWeight='{Binding Weight}' TextTrimming='CharacterEllipsis' VerticalAlignment='Center' Foreground='{DynamicResource FgBrush}'/></DockPanel>";
            var cells = Cell(0, Text("{Binding Number}", "Right", "FgFaintBrush", "FontSize='11'"), false)
                + Cell(1, name, false)
                + Cell(2, Text("{Binding SizeText}", "Right", "FgDimBrush"), !wide);
            if (wide)
                cells += Cell(3, Text("{Binding Ext}", "Left", "FgDimBrush"), false)
                       + Cell(4, Text("{Binding DateText}", "Left", "FgDimBrush"), true);
            var xaml = $"<DataTemplate {Ns}><Grid Height='22' TextElement.FontSize='12'><Grid.ColumnDefinitions>{Cols(wide)}</Grid.ColumnDefinitions>{cells}</Grid></DataTemplate>";
            return (DataTemplate)XamlReader.Parse(xaml);
        }

        private static Grid Header(bool wide)
        {
            string H(string text, string align = "Left") => Text(System.Security.SecurityElement.Escape(text), align, "FgDimBrush", "FontWeight='SemiBold'");
            var cells = Cell(0, H("#", "Right"), false) + Cell(1, H(Loc.T("ColName")), false) + Cell(2, H(Loc.T("ColSize"), "Right"), !wide);
            if (wide) cells += Cell(3, H(Loc.T("ColExt")), false) + Cell(4, H(Loc.T("ColModified")), true);
            return (Grid)XamlReader.Parse($"<Grid {Ns} Height='26' TextElement.FontSize='12'><Grid.ColumnDefinitions>{Cols(wide)}</Grid.ColumnDefinitions>{cells}</Grid>");
        }

        private const string ListXaml = @"
<ListBox xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
         Background='Transparent' BorderThickness='0' AlternationCount='2' Focusable='True'
         ScrollViewer.HorizontalScrollBarVisibility='Disabled' ScrollViewer.CanContentScroll='True'
         VirtualizingPanel.IsVirtualizing='True' VirtualizingPanel.VirtualizationMode='Recycling'>
  <ListBox.ItemContainerStyle>
    <Style TargetType='ListBoxItem'>
      <Setter Property='Padding' Value='0'/>
      <Setter Property='HorizontalContentAlignment' Value='Stretch'/>
      <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='ListBoxItem'>
            <Border x:Name='bd' Background='Transparent' BorderBrush='{DynamicResource BorderBrush}' BorderThickness='0,0,0,1'><ContentPresenter/></Border>
            <ControlTemplate.Triggers>
              <Trigger Property='ItemsControl.AlternationIndex' Value='1'><Setter TargetName='bd' Property='Background' Value='{DynamicResource HoverBrush}'/></Trigger>
              <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='{DynamicResource PressedBrush}'/></Trigger>
              <Trigger Property='IsSelected' Value='True'><Setter TargetName='bd' Property='Background' Value='{DynamicResource SelectedBrush}'/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
  </ListBox.ItemContainerStyle>
</ListBox>";

        /// <summary>The table, switching between the wide (5 columns) and narrow (3 columns) layout as its width changes.</summary>
        public static FrameworkElement Build(List<ArchiveRow> rows)
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var head = new Border { BorderThickness = new Thickness(0, 0, 0, 1) };
            head.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            head.SetResourceReference(Border.BackgroundProperty, "BarBrush");
            root.Children.Add(head);
            var list = (ListBox)XamlReader.Parse(ListXaml);
            list.ItemsSource = rows;
            Grid.SetRow(list, 1);
            root.Children.Add(list);

            bool? wide = null;
            void Layout(double width)
            {
                bool w = width >= 460;
                if (wide == w) return;
                wide = w;
                head.Child = Header(w);
                list.ItemTemplate = RowTemplate(w);
            }
            Layout(0);
            root.SizeChanged += (_, e) => Layout(e.NewSize.Width);
            return root;
        }
    }
}
