using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace WinXplorer.Views
{
    /// <summary>Drag in empty space to select with a rectangle (Ctrl keeps the current selection and toggles).</summary>
    public partial class PaneView
    {
        private ListBox _mqList;
        private ScrollViewer _mqScroll;
        private Panel _mqPanel;
        private Point _mqStart;          // in content coordinates (viewport + scroll offset)
        private HashSet<object> _mqBase;
        private bool _mqActive;
        private DispatcherTimer _mqTimer;

        private static T Find<T>(DependencyObject root, Func<T, bool> ok = null) where T : DependencyObject
        {
            var q = new Queue<DependencyObject>();
            q.Enqueue(root);
            while (q.Count > 0)
            {
                var d = q.Dequeue();
                if (d is T t && d != root && (ok == null || ok(t))) return t;
                for (int i = 0, n = VisualTreeHelper.GetChildrenCount(d); i < n; i++) q.Enqueue(VisualTreeHelper.GetChild(d, i));
            }
            return null;
        }

        private bool BeginMarquee(ListBox lb, MouseButtonEventArgs e)
        {
            if (lb == Drives) return false;
            var sv = Find<ScrollViewer>(lb);
            var panel = Find<Panel>(lb, p => p.IsItemsHost);
            if (sv == null || panel == null) return false;
            var p = e.GetPosition(sv);
            if (p.X < 0 || p.Y < 0) return false;
            _mqList = lb; _mqScroll = sv; _mqPanel = panel;
            _mqStart = new Point(p.X + sv.HorizontalOffset, p.Y + sv.VerticalOffset);
            _mqBase = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? lb.SelectedItems.Cast<object>().ToHashSet() : new HashSet<object>();
            _mqActive = false;
            lb.CaptureMouse();
            lb.LostMouseCapture += OnMarqueeLost;
            _mqTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Input, (_, _) => MarqueeTick(), Dispatcher);
            _mqTimer.Start();
            return true;
        }

        private void OnMarqueeLost(object sender, MouseEventArgs e) => EndMarquee();

        private void MarqueeTick()
        {
            if (_mqList == null) return;
            if (Mouse.LeftButton != MouseButtonState.Pressed) { EndMarquee(); return; }
            var sv = _mqScroll;
            var p = Mouse.GetPosition(sv);
            double vh = sv.ActualHeight;
            // close to / past the top or bottom edge: scroll
            if (_mqActive)
            {
                if (p.Y < 8) sv.ScrollToVerticalOffset(sv.VerticalOffset - Math.Clamp(8 - p.Y, 4, 60));
                else if (p.Y > vh - 8) sv.ScrollToVerticalOffset(sv.VerticalOffset + Math.Clamp(p.Y - vh + 8, 4, 60));
            }
            var cur = new Point(Math.Clamp(p.X, 0, Math.Max(0, sv.ActualWidth - 1)) + sv.HorizontalOffset, Math.Clamp(p.Y, 0, Math.Max(0, vh - 1)) + sv.VerticalOffset);
            if (!_mqActive && (cur - _mqStart).Length < 4) return;
            _mqActive = true;
            var rc = new Rect(_mqStart, cur);

            // draw (in viewport coordinates, clipped to the list)
            var view = new Rect(rc.X - sv.HorizontalOffset, rc.Y - sv.VerticalOffset, rc.Width, rc.Height);
            view.Intersect(new Rect(0, 0, Math.Max(1, sv.ActualWidth), Math.Max(1, vh)));
            if (!view.IsEmpty)
            {
                var tl = sv.TranslatePoint(view.TopLeft, MarqueeLayer);
                Canvas.SetLeft(Marquee, tl.X); Canvas.SetTop(Marquee, tl.Y);
                Marquee.Width = Math.Max(1, view.Width); Marquee.Height = Math.Max(1, view.Height);
                Marquee.Visibility = Visibility.Visible;
            }

            // select what it touches (items scrolled out of view keep their state)
            foreach (var c in _mqPanel.Children.OfType<ListBoxItem>())
            {
                if (!c.IsVisible) continue;
                Rect b;
                try { b = c.TransformToAncestor(sv).TransformBounds(new Rect(c.RenderSize)); } catch { continue; }
                b.Offset(sv.HorizontalOffset, sv.VerticalOffset);
                var item = _mqList.ItemContainerGenerator.ItemFromContainer(c);
                if (item == DependencyProperty.UnsetValue) continue;
                bool hit = b.IntersectsWith(rc);
                bool want = _mqBase.Contains(item) ? !hit : hit;
                if (c.IsSelected != want) c.IsSelected = want;
            }
        }

        private void EndMarquee()
        {
            if (_mqList == null) return;
            var lb = _mqList;
            _mqList = null;
            _mqTimer?.Stop();
            lb.LostMouseCapture -= OnMarqueeLost;
            if (lb.IsMouseCaptured) lb.ReleaseMouseCapture();
            Marquee.Visibility = Visibility.Collapsed;
        }
    }
}
