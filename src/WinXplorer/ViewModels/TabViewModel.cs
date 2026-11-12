using System.ComponentModel;
using WinXplorer.Models;
using WinXplorer.Services;

namespace WinXplorer.ViewModels
{
    public sealed class TabViewModel : ObservableObject
    {
        /// <summary>The Settings page shown as a tab (has no file panes).</summary>
        public bool IsSettings { get; }

        public PaneViewModel Primary { get; }

        private PaneViewModel _secondary;
        public PaneViewModel Secondary { get => _secondary; private set => Set(ref _secondary, value); }

        private TabViewModel() { IsSettings = true; }
        public static TabViewModel CreateSettings() => new();

        public TabViewModel(string path, SavedTab saved = null)
        {
            Primary = new PaneViewModel(this, saved?.Left ?? path);
            Primary.PropertyChanged += PaneChanged;
            _active = Primary;
            Primary.IsActive = true;
            if (saved != null && saved.Split != "none")
            {
                _split = saved.Split;
                Secondary = new PaneViewModel(this, saved.Right ?? Primary.Path);
                Secondary.PropertyChanged += PaneChanged;
            }
        }

        private void PaneChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PaneViewModel.Title) && sender == Active) OnPropertyChanged(nameof(Title));
        }

        /// <summary>none | vertical (left/right) | horizontal (top/bottom)</summary>
        private string _split = "none";
        public string Split
        {
            get => _split;
            set
            {
                if (IsSettings || !Set(ref _split, value)) return;
                if (value != "none" && Secondary == null)
                {
                    Secondary = new PaneViewModel(this, Primary.Path);
                    Secondary.PropertyChanged += PaneChanged;
                }
                if (value == "none") Active = Primary;
                OnPropertyChanged(nameof(IsSplit));
            }
        }
        public bool IsSplit => _split != "none";

        private PaneViewModel _active;
        public PaneViewModel Active
        {
            get => _active;
            set
            {
                if (value == null || _active == value) return;
                _active.IsActive = false;
                _active = value;
                _active.IsActive = true;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Title));
                PaneViewModelSelectionBridge.Raise(value);
            }
        }

        public PaneViewModel Other => !IsSplit ? null : (_active == Primary ? Secondary : Primary);

        public string Title => IsSettings ? Loc.T("Settings") : _active.Title;

        private bool _isSelected;
        public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

        public SavedTab Save() => new() { Left = Primary?.Path, Right = Secondary?.Path, Split = _split };

        public void Dispose()
        {
            Primary?.Dispose();
            Secondary?.Dispose();
        }
    }

    internal static class PaneViewModelSelectionBridge
    {
        public static event System.Action<PaneViewModel> ActivePaneChanged;
        public static void Raise(PaneViewModel p) => ActivePaneChanged?.Invoke(p);
    }
}
