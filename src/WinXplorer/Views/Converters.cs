using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using WinXplorer.Services;

namespace WinXplorer.Views
{
    /// <summary>{l:T Key} – localized string.</summary>
    public sealed class T : MarkupExtension
    {
        public string Key { get; set; }
        public T() { }
        public T(string key) { Key = key; }
        public override object ProvideValue(IServiceProvider sp) => Loc.T(Key);
    }

    /// <summary>File panes for normal tabs, the settings page for the Settings tab.</summary>
    public sealed class TabContentSelector : System.Windows.Controls.DataTemplateSelector
    {
        public DataTemplate Panes { get; set; }
        public DataTemplate Settings { get; set; }
        public override DataTemplate SelectTemplate(object item, DependencyObject container) =>
            item is ViewModels.TabViewModel { IsSettings: true } ? Settings : Panes;
    }

    public sealed class PathIconConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c)
        {
            var path = value as string;
            if (string.IsNullOrEmpty(path)) return null;
            try { return Directory.Exists(path) ? IconService.GetPathIcon(path) : IconService.FolderIcon; } catch { return null; }
        }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }

    public sealed class InverseBoolToVis : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c) => value is true ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }

    public sealed class NullToVis : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c)
        {
            bool isNull = value == null || (value is string s && s.Length == 0);
            bool invert = p as string == "invert";
            return isNull ^ invert ? Visibility.Collapsed : Visibility.Visible;
        }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }

    /// <summary>Fraction (0..1) × parameter width.</summary>
    public sealed class FractionToWidth : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c)
        {
            double f = value is double d ? Math.Clamp(d, 0, 1) : 0;
            double w = double.Parse((string)p, CultureInfo.InvariantCulture);
            return f * w;
        }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }
}
