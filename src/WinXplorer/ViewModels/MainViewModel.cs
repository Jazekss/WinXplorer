using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using WinXplorer.Models;
using WinXplorer.Services;

namespace WinXplorer.ViewModels
{
    public sealed class MainViewModel : ObservableObject
    {
        public ObservableCollection<TabViewModel> Tabs { get; } = new();
        public SidebarViewModel Sidebar { get; } = new();

        private TabViewModel _selectedTab;
        public TabViewModel SelectedTab
        {
            get => _selectedTab;
            set
            {
                if (value == null || _selectedTab == value) return;
                if (_selectedTab != null) _selectedTab.IsSelected = false;
                _selectedTab = value;
                _selectedTab.IsSelected = true;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ActivePane));
                OnPropertyChanged(nameof(IsSettingsActive));
                PaneViewModelSelectionBridge.Raise(value.Active);
            }
        }

        /// <summary>The pane commands act on; null while the Settings tab is shown.</summary>
        public PaneViewModel ActivePane => _selectedTab?.Active;
        public bool IsSettingsActive => _selectedTab?.IsSettings == true;

        public IEnumerable<PaneViewModel> AllPanes =>
            Tabs.Where(t => !t.IsSettings).SelectMany(t => t.Secondary != null ? new[] { t.Primary, t.Secondary } : new[] { t.Primary });

        public TabViewModel NewTab(string path = null, bool select = true, int index = -1, SavedTab saved = null)
        {
            var t = new TabViewModel(path ?? FileSystem.ThisPC, saved);
            Insert(t, index, select);
            return t;
        }

        private void Insert(TabViewModel t, int index, bool select)
        {
            if (index < 0 || index > Tabs.Count) Tabs.Add(t); else Tabs.Insert(index, t);
            if (select || SelectedTab == null) SelectedTab = t;
        }

        public void OpenSettings()
        {
            var existing = Tabs.FirstOrDefault(t => t.IsSettings);
            if (existing != null) { SelectedTab = existing; return; }
            Insert(TabViewModel.CreateSettings(), Tabs.IndexOf(SelectedTab) + 1, true);
        }

        public void CloseTab(TabViewModel t)
        {
            int i = Tabs.IndexOf(t);
            if (i < 0) return;
            if (Tabs.Count == 1)
            {
                if (t.IsSettings) { NewTab(FileSystem.ThisPC); }
                else { System.Windows.Application.Current.MainWindow?.Close(); return; }
            }
            Tabs.Remove(t);
            t.Dispose();
            if (SelectedTab == t) { _selectedTab = null; SelectedTab = Tabs[System.Math.Clamp(i, 0, Tabs.Count - 1)]; }
        }

        public void CycleTab(int dir)
        {
            if (Tabs.Count < 2) return;
            int i = (Tabs.IndexOf(SelectedTab) + dir + Tabs.Count) % Tabs.Count;
            SelectedTab = Tabs[i];
        }

        public void SaveSession()
        {
            var tabs = Tabs.Where(t => !t.IsSettings).ToList();
            AppState.Settings.Tabs = tabs.Select(t => t.Save()).ToList();
            AppState.Settings.SelectedTab = System.Math.Max(0, tabs.IndexOf(SelectedTab));
        }
    }
}
