using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using WinXplorer.ViewModels;

namespace WinXplorer.Views
{
    public partial class TabView : UserControl
    {
        private TabViewModel _tab;

        public TabView()
        {
            InitializeComponent();
            DataContextChanged += (_, _) => Attach(DataContext as TabViewModel);
        }

        private void Attach(TabViewModel tab)
        {
            if (_tab != null) _tab.PropertyChanged -= TabChanged;
            _tab = tab;
            if (tab == null) return;
            tab.PropertyChanged += TabChanged;
            P1.DataContext = tab.Primary;
            Layout();
        }

        private void TabChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(TabViewModel.Split) or nameof(TabViewModel.Secondary)) Layout();
        }

        private void Layout()
        {
            Host.RowDefinitions.Clear();
            Host.ColumnDefinitions.Clear();
            foreach (UIElement c in Host.Children) { Grid.SetRow(c, 0); Grid.SetColumn(c, 0); }

            var split = _tab.Split;
            if (split == "none" || _tab.Secondary == null)
            {
                Splitter.Visibility = Visibility.Collapsed;
                P2.Visibility = Visibility.Collapsed;
                P1.Margin = new Thickness(0);
                return;
            }
            P2.DataContext = _tab.Secondary;
            P2.Visibility = Visibility.Visible;
            Splitter.Visibility = Visibility.Visible;
            if (split == "vertical")
            {
                Host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 150 });
                Host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
                Host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 150 });
                Grid.SetColumn(Splitter, 1); Grid.SetColumn(P2, 2);
                Splitter.ResizeDirection = GridResizeDirection.Columns;
            }
            else
            {
                Host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 });
                Host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(5) });
                Host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 });
                Grid.SetRow(Splitter, 1); Grid.SetRow(P2, 2);
                Splitter.ResizeDirection = GridResizeDirection.Rows;
            }
        }
    }
}
