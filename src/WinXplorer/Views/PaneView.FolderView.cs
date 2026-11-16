using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using WinXplorer.Services;

namespace WinXplorer.Views
{
    /// <summary>Folders with a view.wxv: their page (or the view's settings) is shown over the file list.</summary>
    public partial class PaneView
    {
        /// <summary>The folder the user chose to see plainly ("Open as folder") – until they leave it.</summary>
        private string _plainPath;

        private bool ViewShowing => ViewHost.Visibility == Visibility.Visible;

        private void HideFolderView()
        {
            ViewHost.Content = null;
            ViewHost.Visibility = Visibility.Collapsed;
        }

        /// <summary>Called when the folder changes: shows its view when it has one (and it is on), else the file list.</summary>
        private void UpdateFolderView(bool force = false, bool edit = false)
        {
            if (_vm == null || _vm.IsThisPC || _vm.InArchive || string.IsNullOrEmpty(_vm.Path)) { HideFolderView(); return; }
            var path = _vm.Path;
            if (_plainPath != null && !string.Equals(_plainPath, path, StringComparison.OrdinalIgnoreCase)) _plainPath = null;
            var file = FolderViews.FileOf(path);
            if (file == null && !edit) { HideFolderView(); return; }
            file ??= Path.Combine(path, FolderViews.FileName);
            var v = File.Exists(file) ? FolderViews.Load(file) : null;
            if (v == null)
            {
                // a new / empty view.wxv: set it up when asked to, otherwise the folder is shown as usual
                if (force || edit) _ = ShowFolderEditorNew(path, file);
                else HideFolderView();
                return;
            }
            if (edit) ShowFolderEditor(path, file, v, false);
            else if (force || (v.Enabled && _plainPath == null)) ShowFolderPage(path, file, v);
            else HideFolderView();
        }

        private void ShowFolderPage(string path, string file, WxvView v)
        {
            _plainPath = null;
            var page = new FolderViewControl(path, file, v);
            page.OpenFolder += (p, newTab) => { if (newTab) MainWindow.Instance.OpenInNewTab(p); else _vm.Navigate(p); };
            page.OpenPlain += () => { _plainPath = path; HideFolderView(); FocusList(); };
            page.OpenEditor += () => ShowFolderEditor(path, file, v, false);
            ViewHost.Content = page;
            ViewHost.Visibility = Visibility.Visible;
        }

        private void ShowFolderEditor(string path, string file, WxvView v, bool isNew)
        {
            var ed = new FolderViewEditor(path, file, v, isNew);
            ed.Saved += () =>
            {
                var saved = FolderViews.Load(file);
                if (saved != null) ShowFolderPage(path, file, saved); else HideFolderView();
                _vm.Refresh();
            };
            ed.Cancelled += () =>
            {
                var cur = File.Exists(file) ? FolderViews.Load(file) : null;
                if (cur != null && !isNew) ShowFolderPage(path, file, cur);
                else { _plainPath = path; HideFolderView(); }
            };
            ViewHost.Content = ed;
            ViewHost.Visibility = Visibility.Visible;
        }

        /// <summary>A view that isn't set up yet: look at the folder first (projects? categories? pictures?), then the settings.</summary>
        private async Task ShowFolderEditorNew(string path, string file)
        {
            ViewHost.Content = new System.Windows.Controls.TextBlock { Text = Loc.T("WxvChecking"), Margin = new Thickness(36, 30, 0, 0), FontSize = 14 }
                .With(t => t.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "FgDimBrush"));
            ViewHost.Visibility = Visibility.Visible;
            var guess = await Task.Run(() => FolderViews.Detect(path));
            if (!string.Equals(_vm?.Path, path, StringComparison.OrdinalIgnoreCase)) return;   // the user moved on meanwhile
            ShowFolderEditor(path, file, guess, true);
        }

        /// <summary>New › Folder view: makes view.wxv here (empty) and opens its settings.</summary>
        public static bool CreateViewFile(string folder)
        {
            var file = Path.Combine(folder, FolderViews.FileName);
            if (File.Exists(file)) return true;
            return FileOps.WriteNew(file, _ => { });
        }
    }
}
