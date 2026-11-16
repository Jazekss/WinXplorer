using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WinXplorer.Interop;
using WinXplorer.Models;
using WinXplorer.Services;

namespace WinXplorer.ViewModels
{
    public sealed class PaneViewModel : ObservableObject, IDisposable
    {
        public static event Action<PaneViewModel> AnySelectionChanged;

        public TabViewModel Tab { get; }

        private readonly List<HistEntry> _back = new(), _forward = new();
        private List<FileItem> _all = new();
        private CancellationTokenSource _cts;
        private FileSystemWatcher _watcher;
        private readonly DispatcherTimer _refreshTimer;

        /// <summary>Ask the view to select items (by full path) and optionally scroll to the first one.</summary>
        public event Action<IList<string>, bool> RequestSelect;
        public event Action RequestRename;
        public event Action RequestFocus;
        /// <summary>"address" or "search": ask the view to focus that box.</summary>
        public event Action<string> RequestUi;
        public void FocusAddress() => RequestUi?.Invoke("address");
        public void FocusSearch() => RequestUi?.Invoke("search");
        public void EditFolderView() => RequestUi?.Invoke("folderview");
        /// <summary>Set by Navigate when it opens a view.wxv: the pane shows the view even if it is switched off.</summary>
        public bool ForceView { get; set; }

        public PaneViewModel(TabViewModel tab, string path)
        {
            Tab = tab;
            _viewMode = AppState.Settings.ViewMode;
            _iconSize = AppState.Settings.IconSize;
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); Refresh(); };
            Navigate(path ?? FileSystem.ThisPC, false);
        }

        // ---------------- state ----------------
        private string _path;
        public string Path { get => _path; private set { if (Set(ref _path, value)) { ApplyFolderView(value); OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(IsThisPC)); OnPropertyChanged(nameof(SearchPlaceholder)); } } }

        // ---------------- per-folder view: layout, icon size, sort and grouping are remembered for each folder ----------------
        private static Dictionary<string, FolderView> FolderViews
        {
            get
            {
                var s = AppState.Settings;
                if (s.FolderViews == null || s.FolderViews.Comparer != StringComparer.OrdinalIgnoreCase)
                    s.FolderViews = s.FolderViews == null ? new(StringComparer.OrdinalIgnoreCase) : new(s.FolderViews, StringComparer.OrdinalIgnoreCase);
                return s.FolderViews;
            }
        }

        private void ApplyFolderView(string path)
        {
            var s = AppState.Settings;
            var fv = FindView(path);
            _viewMode = fv?.View ?? s.ViewMode;
            _iconSize = fv?.IconSize > 0 ? fv.IconSize : s.IconSize;
            _sortBy = fv?.SortBy ?? s.DefaultSortBy ?? "name";
            _sortDesc = fv?.SortDesc ?? s.DefaultSortDesc;
            _groupByDate = fv?.GroupByDate ?? s.DefaultGroupByDate;
            AppState.CurrentIconSize = _iconSize;
            foreach (var n in new[] { nameof(ViewMode), nameof(IsDetails), nameof(IsIcons), nameof(IconSize), nameof(TileWidth), nameof(TileHeight), nameof(SortBy), nameof(SortDesc), nameof(GroupByDate) })
                OnPropertyChanged(n);
        }

        /// <summary>The folder's own view, or one a parent folder hands down to its subfolders.</summary>
        private static FolderView FindView(string path)
        {
            if (string.IsNullOrEmpty(path) || path == FileSystem.ThisPC) return null;
            var d = FolderViews;
            if (d.TryGetValue(path, out var own)) return own;
            for (var p = SafeParent(path); !string.IsNullOrEmpty(p); p = SafeParent(p))
                if (d.TryGetValue(p, out var up) && up.Subfolders) return up;
            return null;
        }

        private static string SafeParent(string p)
        {
            try { return p.Length <= 3 ? null : System.IO.Path.GetDirectoryName(p.TrimEnd('\\')) is { Length: > 0 } x ? x : null; }
            catch { return null; }
        }

        /// <summary>The parent folder whose view this folder follows (null = its own or the default).</summary>
        public bool ViewForSubfolders => _path != null && FolderViews.TryGetValue(_path, out var fv) && fv.Subfolders;

        /// <summary>View › Use for subfolders too: this view for every subfolder (their own remembered views are dropped). Again: off.</summary>
        public void ToggleViewForSubfolders()
        {
            if (string.IsNullOrEmpty(_path) || IsThisPC) return;
            var d = FolderViews;
            bool on = !ViewForSubfolders;
            d.TryGetValue(_path, out var old);
            d[_path] = new FolderView { View = _viewMode, IconSize = _iconSize, SortBy = _sortBy, SortDesc = _sortDesc, GroupByDate = _groupByDate, Columns = old?.Columns, Subfolders = on };
            if (on)
            {
                var prefix = _path.TrimEnd('\\') + "\\";
                foreach (var k in d.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList()) d.Remove(k);
            }
            AppState.SaveSoon();
        }

        private void SaveFolderView()
        {
            if (string.IsNullOrEmpty(_path)) return;
            var d = FolderViews;
            if (!d.ContainsKey(_path) && d.Count >= 1000) d.Remove(d.Keys.First());
            d.TryGetValue(_path, out var old);
            d[_path] = new FolderView { View = _viewMode, IconSize = _iconSize, SortBy = _sortBy, SortDesc = _sortDesc, GroupByDate = _groupByDate, Columns = old?.Columns, Subfolders = old?.Subfolders == true };
            AppState.SaveSoon();
        }

        /// <summary>Column widths the user set for this folder's details view (null = automatic).</summary>
        public Dictionary<string, double> ColumnWidths => FindView(_path)?.Columns;

        /// <summary>Remembers the details view's column widths for this folder only.</summary>
        public void SaveColumnWidths(Dictionary<string, double> widths)
        {
            if (string.IsNullOrEmpty(_path) || IsThisPC) return;
            var d = FolderViews;
            if (!d.TryGetValue(_path, out var fv))
            {
                if (d.Count >= 1000) d.Remove(d.Keys.First());
                fv = d[_path] = new FolderView { View = _viewMode, IconSize = _iconSize, SortBy = _sortBy, SortDesc = _sortDesc, GroupByDate = _groupByDate };
            }
            fv.Columns = widths;
            AppState.SaveSoon();
        }

        /// <summary>View › Apply to all folders: this folder's view becomes the default and every remembered one is dropped.</summary>
        public void ApplyViewToAllFolders()
        {
            var s = AppState.Settings;
            s.ViewMode = _viewMode; s.IconSize = _iconSize; s.DefaultSortBy = _sortBy; s.DefaultSortDesc = _sortDesc; s.DefaultGroupByDate = _groupByDate;
            s.FolderViews = null;
            AppState.Save();
        }

        private bool _groupByDate;
        /// <summary>Details view split into Today / Yesterday / Earlier this week … (by date modified).</summary>
        public bool GroupByDate { get => _groupByDate; set { if (Set(ref _groupByDate, value)) { ApplyView(true); SaveFolderView(); } } }
        public string Title => FileSystem.DisplayName(Path ?? "");
        public bool IsThisPC => Path == FileSystem.ThisPC;
        /// <summary>Showing the inside of an archive (read-only).</summary>
        public bool InArchive { get; private set; }

        private ObservableCollection<FileItem> _items = new();
        public ObservableCollection<FileItem> Items
        {
            get => _items;
            private set { if (Set(ref _items, value)) FolderSizes.Fill(this, value); }
        }

        private bool _isLoading;
        public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }

        private string _error;
        public string Error { get => _error; private set { Set(ref _error, value); OnPropertyChanged(nameof(HasError)); } }
        public bool HasError => !string.IsNullOrEmpty(_error);

        private bool _isActive;
        public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }

        private string _viewMode;
        public string ViewMode
        {
            get => _viewMode;
            set { if (Set(ref _viewMode, value)) { SaveFolderView(); OnPropertyChanged(nameof(IsDetails)); OnPropertyChanged(nameof(IsIcons)); } }
        }
        public bool IsDetails => _viewMode != "icons";
        public bool IsIcons => _viewMode == "icons";

        private int _iconSize;
        public int IconSize
        {
            get => _iconSize;
            set
            {
                value = Math.Clamp(value, 32, 384);
                if (Set(ref _iconSize, value))
                {
                    AppState.CurrentIconSize = value;
                    SaveFolderView();
                    OnPropertyChanged(nameof(TileWidth)); OnPropertyChanged(nameof(TileHeight));
                    ThumbnailService.ClearQueue();
                    foreach (var i in _items) i.SetThumbnail(null);
                }
            }
        }
        public double RowHeight => AppState.Settings.CompactRows ? 24 : 30;
        public void NotifyRowHeight() { OnPropertyChanged(nameof(RowHeight)); foreach (var i in _items) i.RefreshDisplay(); }
        public double TileWidth => Math.Max(_iconSize + 14, 84);
        public double TileHeight => _iconSize + 42;

        public bool CanGoBack => _back.Count > 0;
        public bool CanGoForward => _forward.Count > 0;
        public bool CanGoUp => !IsThisPC;

        private string _sortBy = "name";
        public string SortBy { get => _sortBy; set { if (Set(ref _sortBy, value)) { ApplyView(true); SaveFolderView(); } } }
        private bool _sortDesc;
        public bool SortDesc { get => _sortDesc; set { if (Set(ref _sortDesc, value)) { ApplyView(true); SaveFolderView(); } } }

        public void ToggleSort(string column)
        {
            if (_sortBy == column) SortDesc = !SortDesc;
            else { _sortDesc = column is "modified" or "size"; SortBy = column; OnPropertyChanged(nameof(SortDesc)); }
        }

        // ---------------- search ----------------
        private string _search = "";
        public string SearchText
        {
            get => _search;
            set
            {
                if (Set(ref _search, value ?? ""))
                {
                    // with the drive index ready, typing searches all subfolders at once (like the Start menu)
                    if (!IsThisPC && _search.Trim().Length >= 2 && SearchIndex.IsReady(Path)) { SearchRecursive(); return; }
                    if (_isRecursive) { _isRecursive = false; _searchInfo = null; OnPropertyChanged(nameof(IsRecursiveSearch)); _cts?.Cancel(); _ = LoadAsync(null); }
                    else ApplyView(true);
                }
            }
        }
        private bool _isRecursive;
        public bool IsRecursiveSearch => _isRecursive;
        public string SearchPlaceholder => Loc.T("SearchIn") + " " + Title;

        private const int MaxIndexedResults = 5000;
        private string _searchInfo;
        private readonly System.Diagnostics.Stopwatch _searchSw = new();
        private string _searchSummary;
        /// <summary>Address bar while searching subfolders: "folder • 123 results • 0.4 s".</summary>
        public string SearchSummary { get => _searchSummary; private set => Set(ref _searchSummary, value); }

        public void SearchRecursive()
        {
            if (string.IsNullOrWhiteSpace(_search) || IsThisPC) return;
            _cts?.Cancel();
            var cts = _cts = new CancellationTokenSource();
            _isRecursive = true; OnPropertyChanged(nameof(IsRecursiveSearch));
            var results = new ObservableCollection<FileItem>();
            Items = results;
            IsLoading = true;
            var root = Path; var q = _search.Trim(); var hidden = AppState.Settings.ShowHidden;
            var disp = Application.Current.Dispatcher;
            _searchInfo = null;
            _searchSw.Restart();
            UpdateStatus();
            Task.Run(() =>
            {
                // 1) the index: memory only, milliseconds
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var hits = SearchIndex.Search(root, q, hidden, MaxIndexedResults, cts.Token, out bool truncated);
                if (cts.IsCancellationRequested) return;
                if (hits != null)
                {
                    long ms = sw.ElapsedMilliseconds;
                    disp.BeginInvoke(() =>
                    {
                        if (cts.IsCancellationRequested) return;
                        Items = new ObservableCollection<FileItem>(hits);
                        IsLoading = false;
                        _searchInfo = Loc.F("IndexResults", ms) + (truncated ? "  ·  " + Loc.F("IndexTruncated", MaxIndexedResults.ToString("N0")) : "");
                        UpdateStatus();
                    });
                    return;
                }
                // 2) no index for this drive yet: walk the folders
                FileSystem.Search(root, q, hidden, batch => disp.BeginInvoke(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    foreach (var f in batch) results.Add(f);
                    UpdateStatus();
                }), cts.Token);
                disp.BeginInvoke(() => { if (!cts.IsCancellationRequested) { IsLoading = false; UpdateStatus(); } });
            });
        }

        // ---------------- selection / status ----------------
        private IList<FileItem> _selected = Array.Empty<FileItem>();
        public IList<FileItem> Selected
        {
            get => _selected;
            set { _selected = value ?? Array.Empty<FileItem>(); UpdateStatus(); AnySelectionChanged?.Invoke(this); }
        }
        public FileItem FirstSelected => _selected.Count > 0 ? _selected[0] : null;
        public string[] SelectedPaths => _selected.Where(i => !i.IsDrive || true).Select(i => i.FullPath).ToArray();

        private string _status = "";
        public string StatusText { get => _status; private set => Set(ref _status, value); }

        private void UpdateStatus()
        {
            var s = Loc.Items(_items.Count);
            if (_selected.Count > 0)
            {
                s += "   |   " + Loc.Selected(_selected.Count);
                long size = _selected.Where(i => !i.IsDirectory).Sum(i => i.Size);
                if (size > 0) s += "  " + FileItem.FormatSize(size);
            }
            if (_isRecursive) s += "   |   " + (_searchInfo ?? (IsLoading ? Loc.T("Searching") : Loc.T("SearchDone")));
            StatusText = s;
            if (_isRecursive)
            {
                if (!IsLoading) _searchSw.Stop();
                var t = _searchSw.Elapsed.TotalSeconds;
                SearchSummary = Title + "  •  " + Loc.F("SearchResults", _items.Count.ToString("N0")) + (IsLoading ? "…" : "") + "  •  "
                    + (t < 1 ? $"{_searchSw.ElapsedMilliseconds} ms" : $"{t:0.0} s");
            }
            else SearchSummary = null;
        }

        // ---------------- navigation ----------------
        public void Navigate(string target, bool addHistory = true, string selectPath = null)
        {
            // "" is This PC itself (breadcrumb root); only typed text goes through Resolve
            var path = target == FileSystem.ThisPC ? FileSystem.ThisPC : FileSystem.Resolve(target, Path);
            if (path == null) return;
            if (path != FileSystem.ThisPC)
            {
                bool archive = Archives.Split(path) is { } sp && (sp.inner.Length > 0 || AppState.Settings.OpenArchives);
                if (!archive && FileSystem.FileExists(path) == true)
                {
                    if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && Shell.ResolveShortcut(path) is string t && Directory.Exists(t))
                        path = t;
                    else { selectPath = path; path = System.IO.Path.GetDirectoryName(path); }
                }
                // a location that doesn't answer (offline network drive) is opened anyway; LoadAsync shows the error
                if (!archive && FileSystem.DirectoryExists(path) == false)
                {
                    if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || path.StartsWith("::")) { FileOps.OpenInExplorer(path); return; }
                    // "cmd", "ps", "terminal", "code ." … typed in the address bar: run it in this folder (like Explorer)
                    if (FileOps.RunFromAddressBar(target, InArchive || IsThisPC ? null : Path)) return;
                    // a restored tab / history entry whose folder was deleted meanwhile: open what is left of the path
                    if ((_path == null || !addHistory) && FolderGone(path) is string up) { Navigate(up, addHistory, null); return; }
                    MessageBox.Show(Application.Current.MainWindow, Loc.T("PathNotFound") + "\n" + path, "WinXplorer", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (path.Length > 3) path = path.TrimEnd('\\');
            }
            if (addHistory && _path != null && !string.Equals(path, _path, StringComparison.OrdinalIgnoreCase))
            {
                _back.Add(Snapshot());
                if (_back.Count > 200) _back.RemoveAt(0);
                _forward.Clear();
            }
            var previous = _path;
            // view.wxv opened (double-click, from Explorer): show the view even when it is switched off
            ForceView = selectPath != null && selectPath.EndsWith("\\" + WinXplorer.Services.FolderViews.FileName, StringComparison.OrdinalIgnoreCase);
            Path = path;
            FolderUsage.Record(path);
            InArchive = Archives.Split(path) != null; OnPropertyChanged(nameof(InArchive));
            _search = ""; OnPropertyChanged(nameof(SearchText));
            _isRecursive = false; OnPropertyChanged(nameof(IsRecursiveSearch));
            NotifyNav();
            // going up: select the folder we came from
            if (selectPath == null && previous != null && FileSystem.GetParent(previous) is string par && string.Equals(par, path, StringComparison.OrdinalIgnoreCase))
                selectPath = previous;
            _ = LoadAsync(selectPath == null ? null : new[] { selectPath }, scrollTo: true);
        }

        /// <summary>A history entry: the folder and, if a subfolder search was showing, its text and results.</summary>
        private sealed record HistEntry(string Path, string Search, List<FileItem> Results);

        private HistEntry Snapshot() => _isRecursive && !string.IsNullOrEmpty(_search)
            ? new HistEntry(_path, _search, _items.ToList())
            : new HistEntry(_path, null, null);

        private void NotifyNav()
        {
            OnPropertyChanged(nameof(CanGoBack)); OnPropertyChanged(nameof(CanGoForward)); OnPropertyChanged(nameof(CanGoUp));
        }

        /// <summary>Shows a history entry: a folder, or the search results that were open there (instantly, from memory).</summary>
        private void Restore(HistEntry h, IList<string> select, bool scroll)
        {
            Path = h.Path; NotifyNav();
            InArchive = Archives.Split(h.Path) != null; OnPropertyChanged(nameof(InArchive));
            if (h.Search != null)
            {
                _cts?.Cancel();
                _search = h.Search; OnPropertyChanged(nameof(SearchText));
                _isRecursive = true; OnPropertyChanged(nameof(IsRecursiveSearch));
                Error = null;
                IsLoading = false;
                Items = new ObservableCollection<FileItem>(h.Results);
                UpdateStatus();
                if (select != null) RequestSelect?.Invoke(select, scroll);
                return;
            }
            _search = ""; OnPropertyChanged(nameof(SearchText));
            if (_isRecursive) { _isRecursive = false; OnPropertyChanged(nameof(IsRecursiveSearch)); }
            _ = LoadAsync(select, scroll);
        }

        public void GoBack()
        {
            if (_back.Count == 0) return;
            var h = _back[^1]; _back.RemoveAt(_back.Count - 1);
            var prev = _path;
            _forward.Add(Snapshot());
            Restore(h, new[] { prev }, true);
        }

        public void GoForward()
        {
            if (_forward.Count == 0) return;
            var h = _forward[^1]; _forward.RemoveAt(_forward.Count - 1);
            _back.Add(Snapshot());
            Restore(h, null, false);
        }

        public void GoUp()
        {
            if (IsThisPC) return;
            Navigate(FileSystem.GetParent(Path));
        }

        public IReadOnlyList<string> BackHistory => _back.Select(h => h.Path).ToList();

        /// <summary>After F5 / Refresh has reloaded the list (the view gives a little "done" bounce).</summary>
        public event Action Refreshed;

        public async void Refresh()
        {
            if (_isRecursive) { SearchRecursive(); return; }
            try { await LoadAsync(_selected.Select(s => s.FullPath).ToList(), false); } catch { return; }
            Refreshed?.Invoke();
        }

        private const int LoadTimeoutMs = 15000;

        private string _loadedPath;

        /// <summary>
        /// Lists the folder. A folder seen recently is shown at once from <see cref="ListingCache"/> and then checked on disk;
        /// differences are merged into the visible list in place, so scroll position and selection stay where they were.
        /// </summary>
        private async Task LoadAsync(IList<string> select, bool scrollTo = false)
        {
            _cts?.Cancel();
            var cts = _cts = new CancellationTokenSource();
            var path = Path;
            var hidden = AppState.Settings.ShowHidden;
            bool refresh = string.Equals(_loadedPath, path, StringComparison.OrdinalIgnoreCase) && Error == null && _all.Count > 0;
            bool fromCache = false;

            void SelectAfterLoad()
            {
                if (select != null && select.Count > 0) RequestSelect?.Invoke(select, scrollTo);
                else if (scrollTo || select == null) RequestSelect?.Invoke(Array.Empty<string>(), true);
            }

            if (!refresh && ListingCache.TryGet(path, hidden, out var cached))
            {
                _all = cached;
                Error = null;
                ApplyView(false);
                IsLoading = false;
                _loadedPath = path;
                fromCache = true;
                Watch(path);
                SelectAfterLoad();
            }
            else if (!refresh) _loadedPath = null;

            // progress bar only for slow folders; "done" stops a late timer from switching it on after loading finished
            bool done = false;
            _ = Task.Delay(fromCache ? 400 : 120).ContinueWith(_ => Application.Current?.Dispatcher.BeginInvoke(() => { if (!done && _cts == cts && !cts.IsCancellationRequested) IsLoading = true; }));
            List<FileItem> list = null;
            string error = null;
            try
            {
                // own thread: a hung network share must not block thread-pool work (thumbnails, other panes)
                bool inArchive = InArchive;
                var work = Task.Factory.StartNew(() => path == FileSystem.ThisPC ? FileSystem.Drives() : inArchive ? Archives.List(path, cts.Token) : FileSystem.List(path, hidden, cts.Token),
                    cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                var first = await Task.WhenAny(work, Task.Delay(LoadTimeoutMs, cts.Token).ContinueWith(_ => { }));
                if (first == work) list = await work;
                else if (!cts.IsCancellationRequested) error = Loc.T("NotResponding");
            }
            catch (Exception ex) { error = ex.Message; }
            done = true;
            if (cts.IsCancellationRequested) return;
            IsLoading = false;

            if (error != null)
            {
                // the folder itself is gone (deleted / moved elsewhere): step back to the nearest folder that still exists, like Explorer
                if (FolderGone(path) is string up) { ListingCache.Remove(path); _watcher?.Dispose(); _watcher = null; Navigate(up, false, null); return; }
                ListingCache.Remove(path);
                _all = new List<FileItem>();
                _loadedPath = null;
                Error = error;
                ApplyView(false);
                _watcher?.Dispose(); _watcher = null;
                return;
            }

            ListingCache.Put(path, hidden, list);
            if (refresh || fromCache)
            {
                // keep what the user sees: only add / remove / update the rows that changed
                var keep = refresh && select != null ? select : _selected.Select(s => s.FullPath).ToList();
                _all = list;
                bool changed = MergeView();
                // a refresh that asked for a specific item (new folder, rename) selects and shows it
                if (keep.Count > 0 && (changed || refresh && scrollTo)) RequestSelect?.Invoke(keep, refresh && scrollTo);
                _loadedPath = path;
                return;
            }
            _all = list;
            Error = null;
            ApplyView(false);
            _loadedPath = path;
            Watch(path);
            SelectAfterLoad();
        }

        private FileItem[] ComputeView()
        {
            IEnumerable<FileItem> q = _all;
            if (!string.IsNullOrEmpty(_search))
            {
                var m = NameQuery.Matcher(_search);
                q = q.Where(i => m(i.Name));
            }
            var arr = q.ToArray();
            Array.Sort(arr, Compare);
            return arr;
        }

        private void ApplyView(bool keepSelection)
        {
            if (_isRecursive) return;
            var keep = keepSelection ? _selected.Select(s => s.FullPath).ToList() : null;
            Items = new ObservableCollection<FileItem>(ComputeView());
            UpdateStatus();
            if (keep != null && keep.Count > 0) RequestSelect?.Invoke(keep, false);
        }

        private static bool Same(FileItem a, FileItem b) =>
            a.Size == b.Size && a.Modified == b.Modified && a.Attributes == b.Attributes && a.TotalSize == b.TotalSize && a.Name == b.Name;

        /// <summary>Brings Items in line with _all without replacing the collection. Returns true if anything changed.</summary>
        private bool MergeView()
        {
            if (_isRecursive) return false;
            var target = ComputeView();
            var items = _items;
            var want = new Dictionary<string, FileItem>(target.Length, StringComparer.OrdinalIgnoreCase);
            foreach (var t in target) want[t.FullPath] = t;
            bool changed = false;
            for (int k = items.Count - 1; k >= 0; k--)
                if (!want.ContainsKey(items[k].FullPath)) { items.RemoveAt(k); changed = true; }
            for (int j = 0; j < target.Length; j++)
            {
                var t = target[j];
                if (j < items.Count && string.Equals(items[j].FullPath, t.FullPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (!Same(items[j], t)) { items[j] = t; changed = true; }
                    continue;
                }
                // moved (sort key changed) or new
                int at = -1;
                for (int k = j + 1; k < items.Count; k++)
                    if (string.Equals(items[k].FullPath, t.FullPath, StringComparison.OrdinalIgnoreCase)) { at = k; break; }
                if (at >= 0) items.RemoveAt(at);
                items.Insert(j, t);
                changed = true;
            }
            while (items.Count > target.Length) { items.RemoveAt(items.Count - 1); changed = true; }
            if (changed) UpdateStatus();
            return changed;
        }
        private int Compare(FileItem a, FileItem b)
        {
            if (_groupByDate && !IsThisPC)
            {
                // groups in date order (newest first, or oldest first when sorted by date ascending), then the chosen sort inside
                int g = DateGroups.Rank(a.Modified).CompareTo(DateGroups.Rank(b.Modified));
                if (g != 0) return _sortBy == "modified" && !_sortDesc ? -g : g;
            }
            if (a.IsDrive && b.IsDrive && _sortBy == "name") return string.Compare(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase);
            if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
            int r = _sortBy switch
            {
                "modified" => a.Modified.CompareTo(b.Modified),
                "size" => a.Size.CompareTo(b.Size),
                "type" => string.Compare(a.TypeName, b.TypeName, StringComparison.CurrentCultureIgnoreCase),
                _ => Native.StrCmpLogicalW(a.Name, b.Name),
            };
            if (r != 0) return _sortDesc ? -r : r;
            return Native.StrCmpLogicalW(a.Name, b.Name);
        }

        /// <summary>When <paramref name="path"/> no longer exists: the closest parent that does (This PC at the end); otherwise null.</summary>
        private string FolderGone(string path)
        {
            if (InArchive || path == FileSystem.ThisPC || string.IsNullOrEmpty(path)) return null;
            if (FileSystem.DirectoryExists(path, 1500) != false) return null;
            var p = FileSystem.GetParent(path);
            while (p != null && p != FileSystem.ThisPC && FileSystem.DirectoryExists(p, 1500) != true) p = FileSystem.GetParent(p);
            return p ?? FileSystem.ThisPC;
        }

        private FileSystemWatcher _parentWatcher;

        /// <summary>
        /// Watches the open folder's own name in its parent: deleted or moved away → go up (the folder's handle is let go,
        /// so the delete isn't held up); renamed → follow it to the new name.
        /// </summary>
        private void WatchSelf(string path)
        {
            _parentWatcher?.Dispose(); _parentWatcher = null;
            var parent = FileSystem.GetParent(path);
            if (InArchive || parent == null || parent == FileSystem.ThisPC) return;
            try
            {
                var w = new FileSystemWatcher(parent, System.IO.Path.GetFileName(path.TrimEnd('\\')))
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.DirectoryName,
                };
                w.Deleted += (_, _) => Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (_parentWatcher != w || !string.Equals(Path, path, StringComparison.OrdinalIgnoreCase)) return;
                    _watcher?.Dispose(); _watcher = null;
                    _refreshTimer.Stop(); _refreshTimer.Start();
                });
                w.Renamed += (_, e) => Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (_parentWatcher != w || !string.Equals(Path, path, StringComparison.OrdinalIgnoreCase)) return;
                    _watcher?.Dispose(); _watcher = null;
                    if (Directory.Exists(e.FullPath)) Navigate(e.FullPath, false); else { _refreshTimer.Stop(); _refreshTimer.Start(); }
                });
                w.EnableRaisingEvents = true;
                _parentWatcher = w;
            }
            catch { }
        }

        // ---------------- watcher ----------------
        private void Watch(string path)
        {
            if (_watcher != null && string.Equals(_watcher.Path, path, StringComparison.OrdinalIgnoreCase)) return;
            _watcher?.Dispose(); _watcher = null;
            if (path == FileSystem.ThisPC) return;
            WatchSelf(path);
            try
            {
                _watcher = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = false,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.Attributes,
                };
                FileSystemEventHandler h = (_, _) => Application.Current?.Dispatcher.BeginInvoke(() => { _refreshTimer.Stop(); _refreshTimer.Start(); });
                _watcher.Created += h; _watcher.Deleted += h; _watcher.Changed += h;
                _watcher.Renamed += (s, e) => h(s, e);
                _watcher.Error += (_, _) => Application.Current?.Dispatcher.BeginInvoke(() => { _refreshTimer.Stop(); _refreshTimer.Start(); });
                _watcher.EnableRaisingEvents = true;
            }
            catch { _watcher = null; }
        }

        // ---------------- commands used by the view ----------------
        /// <summary>Archives are browsed read-only: say so instead of failing.</summary>
        public bool ReadOnlyHere()
        {
            if (!InArchive) return false;
            Notify.Show(Loc.T("ArchiveReadOnly"));
            return true;
        }

        public void NewFolder()
        {
            if (ReadOnlyHere()) return;
            if (IsThisPC) return;
            try
            {
                var p = FileOps.CreateFolder(Path);
                if (p != null) _ = LoadAndRename(p);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "WinXplorer"); }
        }

        public void NewTextFile()
        {
            if (ReadOnlyHere()) return;
            if (IsThisPC) return;
            try { var p = FileOps.CreateTextFile(Path); if (p != null) _ = LoadAndRename(p); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "WinXplorer"); }
        }

        public void NewFile(string ext)
        {
            if (ReadOnlyHere()) return;
            if (IsThisPC) return;
            try { var p = NewFiles.Create(Path, ext); if (p != null) _ = LoadAndRename(p); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "WinXplorer"); }
        }

        private async Task LoadAndRename(string path)
        {
            await LoadAsync(new[] { path }, true);
            PendingRename = path;
            RequestRename?.Invoke();
        }

        /// <summary>A just-created folder/file the view should open the rename box for (selection may not be applied yet).</summary>
        public string PendingRename { get; set; }

        public void Rename() { if (!ReadOnlyHere()) RequestRename?.Invoke(); }
        public void ReloadAndSelect(string path) => _ = LoadAsync(new[] { path }, true);
        public void Focus() => RequestFocus?.Invoke();
        public void SelectAndFocus(string path) { RequestSelect?.Invoke(new[] { path }, true); RequestFocus?.Invoke(); }

        public void Dispose()
        {
            _cts?.Cancel();
            _watcher?.Dispose();
            _parentWatcher?.Dispose();
            _refreshTimer.Stop();
        }
    }
}