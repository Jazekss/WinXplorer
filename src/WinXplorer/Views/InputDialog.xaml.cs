using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using WinXplorer.Services;

namespace WinXplorer.Views
{
    public partial class InputDialog : Window
    {
        private InputDialog() { InitializeComponent(); }

        /// <summary>Returns the entered text, or null when cancelled.</summary>
        public static string Ask(Window owner, string title, string prompt, string initial, int selectLength = -1)
        {
            var d = new InputDialog { Title = title };
            if (owner != null && owner.IsVisible) d.Owner = owner;
            d.Prompt.Text = prompt;
            d.Input.Text = initial ?? "";
            d.SourceInitialized += (_, _) => ThemeManager.ApplyWindowFrame(new WindowInteropHelper(d).Handle);
            d.Loaded += (_, _) =>
            {
                d.Input.Focus();
                if (selectLength >= 0) d.Input.Select(0, Math.Min(selectLength, d.Input.Text.Length));
                else d.Input.SelectAll();
            };
            return d.ShowDialog() == true ? d.Input.Text : null;
        }

        /// <summary>Themed yes/no question (Enter = OK, Esc = Cancel).</summary>
        public static bool Confirm(Window owner, string title, string message, string okText = null)
        {
            var d = new InputDialog { Title = title };
            if (owner != null && owner.IsVisible) d.Owner = owner;
            d.Prompt.Text = message;
            d.Prompt.Margin = new Thickness(0);
            d.Input.Visibility = Visibility.Collapsed;
            if (okText != null) d.OkBtn.Content = okText;
            d.SourceInitialized += (_, _) => ThemeManager.ApplyWindowFrame(new WindowInteropHelper(d).Handle);
            d.Loaded += (_, _) => d.OkBtn.Focus();
            return d.ShowDialog() == true;
        }

        private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

        private void Input_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { DialogResult = true; e.Handled = true; }
        }
    }
}
