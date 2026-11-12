using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace WinXplorer.Views
{
    /// <summary>Puts popup windows (viewer, quick view) over their owner – on the owner's monitor, never past its edges.</summary>
    internal static class WindowPlacement
    {
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        /// <summary>Work area (without the taskbar) of the monitor the owner is on, in WPF units.</summary>
        public static Rect WorkArea(Window owner)
        {
            try
            {
                var hwnd = owner != null ? new WindowInteropHelper(owner).Handle : IntPtr.Zero;
                if (hwnd != IntPtr.Zero)
                {
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    if (GetMonitorInfo(MonitorFromWindow(hwnd, 2 /*MONITOR_DEFAULTTONEAREST*/), ref mi))
                    {
                        var dpi = VisualTreeHelper.GetDpi(owner);
                        return new Rect(mi.rcWork.Left / dpi.DpiScaleX, mi.rcWork.Top / dpi.DpiScaleY,
                            (mi.rcWork.Right - mi.rcWork.Left) / dpi.DpiScaleX, (mi.rcWork.Bottom - mi.rcWork.Top) / dpi.DpiScaleY);
                    }
                }
            }
            catch { }
            return SystemParameters.WorkArea;
        }

        /// <summary>The owner's rectangle on screen (a maximized window = its monitor's work area).</summary>
        public static Rect OwnerRect(Window owner, Rect area)
        {
            if (owner == null || !owner.IsLoaded || owner.WindowState == WindowState.Maximized) return area;
            var r = new Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight);
            r.Intersect(area);
            return r.IsEmpty || r.Width < 100 ? area : r;
        }

        /// <summary>Sizes <paramref name="w"/> (at most the work area) and centres it over the owner, kept inside the monitor.</summary>
        public static void Center(Window w, Window owner, double width, double height)
        {
            var area = WorkArea(owner);
            var o = OwnerRect(owner, area);
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Width = Math.Max(w.MinWidth, Math.Min(width, area.Width));
            w.Height = Math.Max(w.MinHeight, Math.Min(height, area.Height));
            w.Left = Math.Clamp(o.Left + (o.Width - w.Width) / 2, area.Left, Math.Max(area.Left, area.Right - w.Width));
            w.Top = Math.Clamp(o.Top + (o.Height - w.Height) / 2, area.Top, Math.Max(area.Top, area.Bottom - w.Height));
        }
    }
}
