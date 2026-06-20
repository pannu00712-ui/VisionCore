using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using VisionCore.Core.Models;
using VisionCore.Core.Services;

namespace VisionCore.WPF.ViewModels
{
    /// <summary>
    /// Backs the ScreenPickerView dialog.
    ///
    /// Flow:
    ///   1. User sees a list of monitors with live thumbnails.
    ///   2. Clicks a monitor → full-monitor region is pre-selected.
    ///   3. Optionally edits X / Y / W / H to pick a sub-region.
    ///   4. Clicks "Refresh Preview" to see a live grab of the chosen region.
    ///   5. Clicks OK → dialog closes; caller reads back SelectedRegion.
    /// </summary>
    public sealed class ScreenPickerViewModel : ViewModelBase
    {
        // ── Monitor list item ─────────────────────────────────────────────────

        public sealed class MonitorItem : ViewModelBase
        {
            private BitmapImage? _thumbnail;

            public ScreenCaptureService.MonitorInfo Info { get; }
            public BitmapImage? Thumbnail
            {
                get => _thumbnail;
                set => Set(ref _thumbnail, value);
            }

            public string Label => Info.ToString();

            public MonitorItem(ScreenCaptureService.MonitorInfo info) => Info = info;
        }

        // ── State ─────────────────────────────────────────────────────────────

        private MonitorItem?   _selectedMonitor;
        private BitmapImage?   _regionPreview;
        private bool           _isLoading;
        private int            _regionX;
        private int            _regionY;
        private int            _regionW   = 1920;
        private int            _regionH   = 1080;
        private bool           _fullMonitor = true;

        // ── Properties ────────────────────────────────────────────────────────

        public ObservableCollection<MonitorItem> Monitors { get; } = new();

        public MonitorItem? SelectedMonitor
        {
            get => _selectedMonitor;
            set
            {
                if (Set(ref _selectedMonitor, value) && value != null)
                    ApplyFullMonitor(value.Info);
            }
        }

        public bool FullMonitor
        {
            get => _fullMonitor;
            set
            {
                Set(ref _fullMonitor, value);
                OnPropertyChanged(nameof(IsCustomRegion));
                if (value && _selectedMonitor != null)
                    ApplyFullMonitor(_selectedMonitor.Info);
            }
        }

        public bool IsCustomRegion => !_fullMonitor;

        public int RegionX { get => _regionX; set => Set(ref _regionX, value); }
        public int RegionY { get => _regionY; set => Set(ref _regionY, value); }
        public int RegionW { get => _regionW; set => Set(ref _regionW, value); }
        public int RegionH { get => _regionH; set => Set(ref _regionH, value); }

        public BitmapImage? RegionPreview
        {
            get => _regionPreview;
            private set => Set(ref _regionPreview, value);
        }

        public bool IsLoading
        {
            get => _isLoading;
            private set => Set(ref _isLoading, value);
        }

        /// <summary>Set by the dialog after OK is clicked.</summary>
        public ScreenRegion? SelectedRegion { get; private set; }

        // ── Commands ──────────────────────────────────────────────────────────

        public ICommand RefreshPreviewCommand { get; }
        public ICommand SelectMonitorCommand  { get; }
        public ICommand ConfirmCommand        { get; }

        // ── Constructor ───────────────────────────────────────────────────────

        public ScreenPickerViewModel()
        {
            RefreshPreviewCommand = new RelayCommand(_ => _ = RefreshPreviewAsync());
            SelectMonitorCommand  = new RelayCommand(o => SelectMonitor(o as MonitorItem));
            ConfirmCommand        = new RelayCommand(_ => Confirm());

            LoadMonitors();
        }

        // ── Init ──────────────────────────────────────────────────────────────

        private void LoadMonitors()
        {
            Monitors.Clear();
            var monitors = ScreenCaptureService.GetMonitors();
            foreach (var m in monitors)
                Monitors.Add(new MonitorItem(m));

            if (Monitors.Count > 0)
                SelectedMonitor = Monitors[0];

            // Load thumbnails in background
            _ = LoadThumbnailsAsync();
        }

        private async Task LoadThumbnailsAsync()
        {
            foreach (var item in Monitors)
            {
                var bytes = await Task.Run(() =>
                    ScreenCaptureService.CaptureThumbnail(item.Info.Index, 280));
                if (bytes != null)
                    item.Thumbnail = ToBitmapImage(bytes);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void ApplyFullMonitor(ScreenCaptureService.MonitorInfo info)
        {
            RegionX = info.X;
            RegionY = info.Y;
            RegionW = info.Width;
            RegionH = info.Height;
            _ = RefreshPreviewAsync();
        }

        private void SelectMonitor(MonitorItem? item)
        {
            if (item == null) return;
            SelectedMonitor = item;
        }

        private async Task RefreshPreviewAsync()
        {
            IsLoading = true;
            try
            {
                var region = BuildRegion();
                var bytes  = await Task.Run(() =>
                    ScreenCaptureService.CaptureRegionThumbnail(region, 480));
                RegionPreview = bytes != null ? ToBitmapImage(bytes) : null;
            }
            finally
            {
                IsLoading = false;
            }
        }

        private void Confirm()
        {
            SelectedRegion = BuildRegion();
        }

        private ScreenRegion BuildRegion() => new()
        {
            MonitorIndex = _selectedMonitor?.Info.Index ?? 0,
            X            = RegionX,
            Y            = RegionY,
            Width        = Math.Max(RegionW, 2),
            Height       = Math.Max(RegionH, 2),
        };

        private static BitmapImage ToBitmapImage(byte[] bytes)
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource     = new MemoryStream(bytes);
            bmp.CacheOption      = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
    }

    // ── Relay command helper (if not already shared) ──────────────────────────

    // RelayCommand is already in ViewModelBase.cs — no need to redeclare
}
