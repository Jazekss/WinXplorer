using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using WinXplorer.Models;
using WinXplorer.Services;

namespace WinXplorer.ViewModels
{
    public enum DropHint { None, Above, Below, Into }

    public sealed class SidebarItem : ObservableObject
    {
        public SidebarEntryData Data { get; init; }
        public SidebarKind Kind { get; init; }
        public string Path { get; init; }
        public bool IsDrive { get; init; }
        public bool IsSeparator => Kind == SidebarKind.Separator;
        /// <summary>Separator with a title: groups the entries below it.</summary>
        public bool IsHeader => IsSeparator && !string.IsNullOrEmpty(Header);
        public string Header => Data?.Kind == SidebarKind.Frequent ? Loc.T("FrequentFolders") : Data?.Label;
        /// <summary>A folder shown by the "Frequent folders" block (not pinned by the user).</summary>
        public bool IsFrequent => Kind == SidebarKind.Frequent;

        private string _name;
        public string Name { get => _name; set => Set(ref _name, value); }

        private ImageSource _icon;
        public ImageSource Icon { get => _icon; set => Set(ref _icon, value); }

        private string _toolTip;
        public string ToolTip { get => _toolTip; set => Set(ref _toolTip, value); }

        private double _usage = -1;
        public double Usage { get => _usage; set { Set(ref _usage, value); OnPropertyChanged(nameof(HasUsage)); OnPropertyChanged(nameof(UsageAlmostFull)); } }
        public bool HasUsage => _usage >= 0;
        public bool UsageAlmostFull => _usage > 0.9;

        private DropHint _hint;
        public DropHint Hint { get => _hint; set => Set(ref _hint, value); }

        private bool _isCurrent;
        public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }
    }

    public sealed class SidebarViewModel : ObservableObject
    {
        public ObservableCollection<SidebarItem> Items { get; } = new();

        private static System.Collections.Generic.List<SidebarEntryData> Data => AppState.Settings.Sidebar;

        public void Rebuild()
        {
            Items.Clear();
            foreach (var e in Data)
            {
                switch (e.Kind)
                {
                    case SidebarKind.Folder:
                        Items.Add(new SidebarItem
                        {
                            Data = e, Kind = e.Kind, Path = e.Path,
                            Name = string.IsNullOrEmpty(e.Label) ? FileSystem.DisplayName(e.Path) : e.Label,
                            Icon = Directory.Exists(e.Path) ? IconService.GetPathIcon(e.Path) : null,
                            ToolTip = e.Path,
                        });
                        break;
                    case SidebarKind.App:
                        Items.Add(new SidebarItem
                        {
                            Data = e, Kind = e.Kind, Path = e.Path,
                            Name = string.IsNullOrEmpty(e.Label) ? AppName(e.Path) : e.Label,
                            Icon = File.Exists(e.Path) ? IconService.GetPathIconNoOverlay(e.Path) : null,
                            ToolTip = e.Path,
                        });
                        break;
                    case SidebarKind.ThisPC:
                        Items.Add(new SidebarItem { Data = e, Kind = e.Kind, Path = FileSystem.ThisPC, Name = string.IsNullOrEmpty(e.Label) ? Loc.T("ThisPC") : e.Label, ToolTip = Loc.T("ThisPC") });
                        break;
                    case SidebarKind.Separator:
                        Items.Add(new SidebarItem { Data = e, Kind = e.Kind });
                        break;
                    case SidebarKind.Frequent:
                        // a titled group with the most used folders (filled again as you browse)
                        Items.Add(new SidebarItem { Data = e, Kind = SidebarKind.Separator });
                        foreach (var f in FrequentItems(e)) Items.Add(f);
                        break;
                    case SidebarKind.Drives:
                        foreach (var d in DriveInfo.GetDrives())
                        {
                            if (AppState.Settings.HiddenDrives.Contains(d.Name, StringComparer.OrdinalIgnoreCase) || FileSystem.IsEmptyDrive(d)) continue;
                            var item = new SidebarItem { Data = e, Kind = e.Kind, Path = d.Name, IsDrive = true, Name = d.Name.TrimEnd('\\'), ToolTip = d.Name };
                            Items.Add(item);
                            _ = FillDrive(item, d);
                        }
                        break;
                }
            }
            UpdateCurrent(_current);
        }

        private static async Task FillDrive(SidebarItem item, DriveInfo d)
        {
            var r = await Task.Run(() =>
            {
                string name = null; ImageSource icon = null; double usage = -1; string tip = d.Name;
                try { name = IconService.GetDisplayName(d.Name); } catch { }
                try { icon = IconService.GetPathIcon(d.Name); } catch { }
                try
                {
                    if (d.IsReady && d.TotalSize > 0)
                    {
                        usage = 1.0 - (double)d.AvailableFreeSpace / d.TotalSize;
                        tip = $"{d.Name}\n{Loc.Free(FileItem.FormatSize(d.AvailableFreeSpace))} / {FileItem.FormatSize(d.TotalSize)}";
                    }
                }
                catch { }
                return (name, icon, usage, tip);
            });
            if (!string.IsNullOrEmpty(r.name)) item.Name = r.name;
            item.Icon = r.icon;
            item.Usage = r.usage;
            item.ToolTip = r.tip;
        }

        private const int FrequentCount = 6;

        private System.Collections.Generic.IEnumerable<SidebarItem> FrequentItems(SidebarEntryData e)
        {
            var pinned = new System.Collections.Generic.HashSet<string>(
                Data.Where(d => d.Kind == SidebarKind.Folder && d.Path != null).Select(d => d.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var p in FolderUsage.Top(FrequentCount, pinned))
                yield return new SidebarItem
                {
                    Data = e, Kind = SidebarKind.Frequent, Path = p,
                    Name = FileSystem.DisplayName(p), Icon = IconService.GetPathIcon(p), ToolTip = p,
                };
        }

        /// <summary>Updates only the frequent block (after a folder was opened) – the rest of the sidebar stays.</summary>
        public void RefreshFrequent()
        {
            int head = -1;
            for (int i = 0; i < Items.Count; i++)
                if (Items[i].IsSeparator && Items[i].Data?.Kind == SidebarKind.Frequent) { head = i; break; }
            if (head < 0) return;
            var fresh = FrequentItems(Items[head].Data).ToList();
            var old = Items.Skip(head + 1).TakeWhile(i => i.IsFrequent).ToList();
            if (fresh.Select(f => f.Path).SequenceEqual(old.Select(o => o.Path), StringComparer.OrdinalIgnoreCase)) return;
            foreach (var o in old) Items.Remove(o);
            for (int i = 0; i < fresh.Count; i++) Items.Insert(head + 1 + i, fresh[i]);
            UpdateCurrent(_current);
        }

        public bool ShowFrequent
        {
            get => HasKind(SidebarKind.Frequent);
            set
            {
                if (value == ShowFrequent) return;
                if (value) Data.Insert(0, new SidebarEntryData { Kind = SidebarKind.Frequent });
                else Data.RemoveAll(d => d.Kind == SidebarKind.Frequent);
                Commit();
            }
        }

        private string _current;
        public void UpdateCurrent(string path)
        {
            _current = path;
            foreach (var i in Items)
                i.IsCurrent = path != null && i.Kind != SidebarKind.Separator && string.Equals(i.Path?.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        private void Commit() { AppState.Save(); Rebuild(); }

        public void AddFolder(string path, int dataIndex = -1)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
            if (Data.Any(d => d.Kind == SidebarKind.Folder && string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase))) return;
            var e = new SidebarEntryData { Kind = SidebarKind.Folder, Path = path };
            if (dataIndex < 0 || dataIndex > Data.Count) Data.Add(e); else Data.Insert(dataIndex, e);
            Commit();
        }

        public void AddSpecial(SidebarKind kind)
        {
            if (kind != SidebarKind.Separator && Data.Any(d => d.Kind == kind)) return;
            Data.Add(new SidebarEntryData { Kind = kind });
            Commit();
        }

        private static string AppName(string path)
        {
            try
            {
                // exe: the product's own name ("Visual Studio Code"), otherwise the file name without extension
                if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    var d = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription;
                    if (!string.IsNullOrWhiteSpace(d)) return d.Trim();
                }
            }
            catch { }
            return System.IO.Path.GetFileNameWithoutExtension(path);
        }

        /// <summary>Pins a program, shortcut or file; clicking it in the sidebar opens it.</summary>
        public void AddApp(string path, int dataIndex = -1)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            if (Data.Any(d => d.Kind == SidebarKind.App && string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase))) return;
            var e = new SidebarEntryData { Kind = SidebarKind.App, Path = path };
            if (dataIndex < 0 || dataIndex > Data.Count) Data.Add(e); else Data.Insert(dataIndex, e);
            Commit();
        }

        public void AddHeader(string title, int dataIndex = -1)
        {
            var e = new SidebarEntryData { Kind = SidebarKind.Separator, Label = string.IsNullOrWhiteSpace(title) ? null : title.Trim() };
            if (dataIndex < 0 || dataIndex > Data.Count) Data.Add(e); else Data.Insert(dataIndex, e);
            Commit();
        }

        public bool HasKind(SidebarKind kind) => Data.Any(d => d.Kind == kind);

        public void Remove(SidebarItem item)
        {
            if (item.IsDrive)
            {
                // hide only this drive; the drive group stays
                AppState.Settings.HiddenDrives.Add(item.Path);
            }
            else if (item.IsFrequent)
            {
                // a frequent folder: forget it (the block stays)
                FolderUsage.Forget(item.Path);
                RefreshFrequent();
                return;
            }
            else Data.Remove(item.Data);
            Commit();
        }

        public void RemoveDrivesGroup()
        {
            Data.RemoveAll(d => d.Kind == SidebarKind.Drives);
            Commit();
        }

        public void ShowAllDrives()
        {
            AppState.Settings.HiddenDrives.Clear();
            if (!HasKind(SidebarKind.Drives)) Data.Add(new SidebarEntryData { Kind = SidebarKind.Drives });
            Commit();
        }

        public void Rename(SidebarItem item, string label)
        {
            if (item.Data == null || item.IsDrive) return; // (separators: the label becomes the header title)
            item.Data.Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
            Commit();
        }

        public int DataIndexOf(SidebarItem item) => item?.Data == null ? -1 : Data.IndexOf(item.Data);

        public void Move(SidebarItem item, int newDataIndex)
        {
            var e = item.Data;
            int old = Data.IndexOf(e);
            if (old < 0) return;
            Data.RemoveAt(old);
            if (newDataIndex > old) newDataIndex--;
            newDataIndex = Math.Clamp(newDataIndex, 0, Data.Count);
            Data.Insert(newDataIndex, e);
            Commit();
        }

        public void MoveBy(SidebarItem item, int delta)
        {
            int i = Data.IndexOf(item.Data);
            if (i < 0) return;
            int j = Math.Clamp(i + delta, 0, Data.Count - 1);
            if (i == j) return;
            Data.RemoveAt(i);
            Data.Insert(j, item.Data);
            Commit();
        }

        public void Reset()
        {
            AppState.Settings.Sidebar = AppState.DefaultSidebar();
            AppState.Settings.HiddenDrives.Clear();
            Commit();
        }
    }
}
