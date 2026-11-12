using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using ICSharpCode.AvalonEdit.Editing;
using WinXplorer.Models;
using WinXplorer.Services;
using WinXplorer.ViewModels;

namespace WinXplorer.Views
{
    /// <summary>
    /// Space on a file that isn't a picture: a window with its content – text and code (selectable, copyable),
    /// video / audio, PDF and Office documents through the Windows previewers, a hex view for the rest.
    /// Space or Esc closes it, ← → (or the buttons) go to the previous / next file.
    /// </summary>
    public sealed class QuickViewWindow : Window
    {
        private readonly List<FileItem> _items;
        private readonly PaneViewModel _pane;
        private readonly PreviewPanel _preview = new() { QuickView = true };
        private readonly TextBlock _count;
        private readonly Button _prev, _next, _prevName, _nextName;
        private int _index;

        public FileItem CurrentItem => _items.Count > 0 ? _items[Math.Clamp(_index, 0, _items.Count - 1)] : null;

        public QuickViewWindow(List<FileItem> items, int index, PaneViewModel pane, Window owner)
        {
            _items = items;
            _pane = pane;
            _index = index;
            Owner = owner;
            ShowInTaskbar = false;
            UseLayoutRounding = true;
            MinWidth = 420; MinHeight = 320;
            SetResourceReference(BackgroundProperty, "SidebarBrush");
            SetResourceReference(ForegroundProperty, "FgBrush");
            SetResourceReference(FontFamilyProperty, "UiFont");
            FontSize = 12;

            // [‹ previous name]  ‹  3 / 7  ›  [next name ›] – the counter exactly in the middle
            var root = new DockPanel();
            var bar = new Grid { Margin = new Thickness(8, 6, 8, 0), Height = 32 };
            DockPanel.SetDock(bar, Dock.Top);
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _prevName = NameBtn(() => Go(-1), HorizontalAlignment.Right);
            _nextName = NameBtn(() => Go(1), HorizontalAlignment.Left);
            _prev = Btn("\uE76B", "←", () => Go(-1));
            _next = Btn("\uE76C", "→", () => Go(1));
            var left = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(_prev, Dock.Right);
            left.Children.Add(_prev); left.Children.Add(_prevName);
            var right = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(_next, Dock.Left);
            right.Children.Add(_next); right.Children.Add(_nextName);
            _count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0), FontWeight = FontWeights.SemiBold, MinWidth = 44, TextAlignment = TextAlignment.Center };
            Grid.SetColumn(_count, 1); Grid.SetColumn(right, 2);
            bar.Children.Add(left); bar.Children.Add(_count); bar.Children.Add(right);
            root.Children.Add(bar);
            root.Children.Add(_preview);
            Content = root;
            _preview.HandlerKey += k => { if (k is Key.Space or Key.Escape) Close(); };

            // ~75% of the file manager, centred over it
            var ob = WindowPlacement.OwnerRect(owner, WindowPlacement.WorkArea(owner));
            WindowPlacement.Center(this, owner, ob.Width * 0.75, ob.Height * 0.8);

            SourceInitialized += (_, _) => { ThemeManager.ApplyWindowFrame(new WindowInteropHelper(this).Handle); InstallKeyHook(); };
            Loaded += (_, _) => { ShowItem(); Activate(); Focus(); };
            PreviewKeyDown += OnKey;
            Closed += (_, _) => { RemoveKeyHook(); _preview.Clear(); };
        }

        // ---- after a click into a Windows previewer (Word, Excel, PDF…) its own window – in another process – has the
        // keyboard, so WPF never sees the keys. A low-level hook catches the close shortcut there (only then). ----
        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] private struct GUITHREADINFO { public int cbSize, flags; public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret; public int l, t, r, b; }
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr pid);
        [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
        [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
        [DllImport("user32.dll")] private static extern short GetKeyState(int vk);
        [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string name);

        private HookProc _hookProc;
        private IntPtr _hook;

        private void InstallKeyHook()
        {
            _hookProc = KeyHook;
            _hook = SetWindowsHookEx(13 /*WH_KEYBOARD_LL*/, _hookProc, GetModuleHandle(null), 0);
        }

        private void RemoveKeyHook()
        {
            if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        }

        private IntPtr KeyHook(int code, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (code >= 0 && (wParam == (IntPtr)0x100 || wParam == (IntPtr)0x104)) // WM_KEYDOWN / WM_SYSKEYDOWN
                {
                    var me = new WindowInteropHelper(this).Handle;
                    if (me != IntPtr.Zero && GetForegroundWindow() == me)
                    {
                        var gti = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
                        // the focus is in a child window that isn't WPF's own: the previewer
                        if (GetGUIThreadInfo(GetWindowThreadProcessId(me, IntPtr.Zero), ref gti) && gti.hwndFocus != me
                            && (gti.hwndFocus == IntPtr.Zero || IsChild(me, gti.hwndFocus)))
                        {
                            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                            var mods = ModifierKeys.None;
                            if (GetKeyState(0x11) < 0) mods |= ModifierKeys.Control;
                            if (GetKeyState(0x10) < 0) mods |= ModifierKeys.Shift;
                            if (GetKeyState(0x12) < 0) mods |= ModifierKeys.Alt;
                            var key = KeyInterop.KeyFromVirtualKey((int)info.vkCode);
                            if (System.Linq.Enumerable.Contains(Hotkeys.Get("Quick.Close"), new Gesture(key, mods)))
                            {
                                Dispatcher.BeginInvoke(new Action(Close));
                                return (IntPtr)1;   // swallowed: the previewer doesn't get it
                            }
                        }
                    }
                }
            }
            catch { }
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        private Button Btn(string glyph, string tip, Action a)
        {
            var b = new Button { Style = (Style)FindResource("IconBtn"), ToolTip = tip, MinWidth = 34, MinHeight = 30, Focusable = false };
            b.Content = new TextBlock { Text = glyph, Style = (Style)FindResource("Glyph"), FontSize = 12 };
            b.Click += (_, _) => a();
            return b;
        }

        private Button NameBtn(Action a, HorizontalAlignment align)
        {
            var t = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, "FgDimBrush");
            var b = new Button { Style = (Style)FindResource("IconBtn"), Content = t, Focusable = false, HorizontalAlignment = align, Padding = new Thickness(8, 0, 8, 0), MinHeight = 30 };
            b.Click += (_, _) => a();
            return b;
        }

        private void Go(int delta)
        {
            int i = Math.Clamp(_index + delta, 0, _items.Count - 1);
            if (i == _index) return;
            _index = i;
            ShowItem();
        }

        private void ShowItem()
        {
            var item = CurrentItem;
            if (item == null) { Close(); return; }
            Title = item.Name;
            _count.Text = _items.Count > 1 ? $"{_index + 1} / {_items.Count}" : "";
            _prev.IsEnabled = _index > 0;
            _next.IsEnabled = _index < _items.Count - 1;
            ((TextBlock)_prevName.Content).Text = _index > 0 ? _items[_index - 1].Name : "";
            ((TextBlock)_nextName.Content).Text = _index < _items.Count - 1 ? _items[_index + 1].Name : "";
            _prevName.Visibility = _index > 0 ? Visibility.Visible : Visibility.Hidden;
            _nextName.Visibility = _index < _items.Count - 1 ? Visibility.Visible : Visibility.Hidden;
            _preview.Show(item, _pane);
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            // inside the text view the arrows move the caret (to select and copy); everywhere else they browse
            bool inText = Keyboard.FocusedElement is TextArea || Keyboard.FocusedElement is TextBox;
            if (Hotkeys.FromEvent(e) is not { } g) return;
            var cmd = Hotkeys.Match(HkScope.Quick, e);
            // in a text field plain keys type; in the read-only text view the arrows, Home/End, PgUp/PgDn and Enter move the caret
            if (cmd == null || (Keyboard.FocusedElement is TextBox && Hotkeys.IsTextKey(g))
                || (inText && !g.HasChordModifier && g.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End
                    or Key.PageUp or Key.PageDown or Key.Return or Key.Back or Key.Delete)) return;
            switch (cmd)
            {
                case "Quick.Close": Close(); break;
                case "Quick.Prev": Go(-1); break;
                case "Quick.Next": Go(1); break;
                case "Quick.First": Go(-_index); break;
                case "Quick.Last": Go(_items.Count); break;
                case "Quick.Open": open(); break;
                default: return;
            }
            e.Handled = true;

            void open() { if (CurrentItem != null) FileOps.Open(CurrentItem.FullPath); }
        }
    }
}
