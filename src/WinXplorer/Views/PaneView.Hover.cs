using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using WinXplorer.Models;
using WinXplorer.Services;

namespace WinXplorer.Views
{
    /// <summary>
    /// Hover preview (Settings › General, off by default): rest the mouse on a file or folder for a second and a
    /// small preview opens beside the pointer – pictures, video / audio (optionally playing at once), documents,
    /// text, archive and folder contents. It closes as soon as the mouse moves to something else, scrolls or clicks.
    /// </summary>
    public partial class PaneView
    {
        private DispatcherTimer _hoverTimer;
        private Popup _hoverPopup;
        private PreviewPanel _hoverPreview;
        private FileItem _hoverItem, _hoverShown;
        private Point _hoverScreen;

        private void SetupHover()
        {
            _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
            _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); ShowHover(); };
            foreach (var lb in new ListBox[] { List, Tiles })
            {
                lb.MouseMove += Hover_MouseMove;
                lb.MouseLeave += (_, _) => HideHover();
                lb.PreviewMouseDown += (_, _) => HideHover();
                lb.PreviewMouseWheel += (_, _) => HideHover();
                lb.PreviewKeyDown += (_, _) => HideHover();
            }
            Unloaded += (_, _) => HideHover();
        }

        private void Hover_MouseMove(object sender, MouseEventArgs e)
        {
            if (!AppState.Settings.HoverPreview || e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed) { HideHover(); return; }
            var item = ItemAt(e.OriginalSource);
            if (item == _hoverItem && (_hoverShown != null || _hoverTimer.IsEnabled)) return;
            HideHover();
            _hoverItem = item;
            if (item == null || item.IsDrive || Window.GetWindow(this)?.IsActive != true) return;
            _hoverScreen = PointToScreen(e.GetPosition(this));
            _hoverTimer.Start();
        }

        private void ShowHover()
        {
            var item = _hoverItem;
            if (item == null || !IsMouseOver || Window.GetWindow(this)?.IsActive != true) return;
            if (_hoverPopup == null)
            {
                _hoverPreview = new PreviewPanel { QuickView = true, ShowFolderContents = true, HoverMode = true };
                var frame = new Border { Width = 480, Height = 380, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = _hoverPreview, Padding = new Thickness(2) };
                frame.SetResourceReference(Border.BackgroundProperty, "PopupBrush");
                frame.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
                // not transparent: Windows' document previewers are real windows and need an ordinary popup window
                _hoverPopup = new Popup { Child = frame, AllowsTransparency = false, Placement = PlacementMode.AbsolutePoint, StaysOpen = true, Focusable = false };
            }
            _hoverPreview.AutoPlay = AppState.Settings.HoverAutoPlay;
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            // beside the pointer; WPF moves a popup back inside the screen when it would stick out
            _hoverPopup.HorizontalOffset = _hoverScreen.X / dpi.DpiScaleX + 18;
            _hoverPopup.VerticalOffset = _hoverScreen.Y / dpi.DpiScaleY + 18;
            _hoverShown = item;
            _hoverPreview.Show(item, _vm);
            _hoverPopup.IsOpen = true;
        }

        private void HideHover()
        {
            _hoverTimer?.Stop();
            _hoverItem = null;
            if (_hoverShown == null) return;
            _hoverShown = null;
            if (_hoverPopup != null) _hoverPopup.IsOpen = false;
            _hoverPreview?.Clear();
        }
    }
}
