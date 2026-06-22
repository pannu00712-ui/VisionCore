using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using VisionCore.Core.Models;
using VisionCore.Core.Services;

namespace VisionCore.WPF.ViewModels
{
    // ══════════════════════════════════════════════════════════════════════════
    // OverlayRowViewModel — editable row in the overlays list
    // ══════════════════════════════════════════════════════════════════════════

    public sealed class OverlayRowViewModel : ViewModelBase
    {
        private OverlayType _type = OverlayType.Timestamp;
        private string      _text      = string.Empty;
        private string      _logoPath  = string.Empty;
        private int         _x;
        private int         _y;
        private int         _fontSize  = 16;
        private string      _color     = "white";

        public OverlayType Type     { get => _type;     set => Set(ref _type,     value); }
        public string      Text     { get => _text;     set => Set(ref _text,     value); }
        public string      LogoPath { get => _logoPath; set => Set(ref _logoPath, value); }
        public int         X        { get => _x;        set => Set(ref _x,        value); }
        public int         Y        { get => _y;        set => Set(ref _y,        value); }
        public int         FontSize { get => _fontSize; set => Set(ref _fontSize, value); }
        public string      Color    { get => _color;    set => Set(ref _color,    value); }

        public static OverlayRowViewModel FromModel(OverlayConfig o) => new()
        {
            Type     = o.Type,
            Text     = o.Text     ?? string.Empty,
            LogoPath = o.LogoPath ?? string.Empty,
            X        = o.X,
            Y        = o.Y,
            FontSize = o.FontSize,
            Color    = o.Color    ?? "white",
        };

        public OverlayConfig ToModel() => new()
        {
            Type     = Type,
            Text     = Text,
            LogoPath = LogoPath,
            X        = X,
            Y        = Y,
            FontSize = FontSize,
            Color    = Color,
        };
    }

    // ══════════════════════════════════════════════════════════════════════════
    // CameraEditViewModel
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Backs the Add / Edit Camera dialog.
    ///
    /// Responsibilities:
    ///   • Loads an existing <see cref="CameraConfig"/> into bindable properties (Edit mode)
    ///     or initialises sane defaults (Add mode)
    ///   • Runs <see cref="GpuDetector"/> on open and pre-selects the recommended accelerator
    ///   • Validates all required fields; exposes <see cref="ValidationErrors"/> for inline error display
    ///   • Builds and saves a <see cref="CameraConfig"/> via <see cref="SettingsService"/> on Save
    ///   • Exposes <see cref="SavedCamera"/> so the caller can immediately start the stream
    /// </summary>
    public sealed class CameraEditViewModel : ViewModelBase, IDataErrorInfo
    {
        // ── Services ──────────────────────────────────────────────────────────
        private readonly ILogger<CameraEditViewModel> _logger;
        private readonly SettingsService              _settings;
        private readonly GpuDetector                  _gpuDetector;

        // ── Mode ──────────────────────────────────────────────────────────────
        public bool IsEditMode { get; private set; }
        public string Title    => IsEditMode ? "Edit Camera" : "Add Camera";

        // ── Result ────────────────────────────────────────────────────────────
        /// <summary>Set after a successful Save; null if cancelled or not yet saved.</summary>
        public CameraConfig? SavedCamera { get; private set; }
        public bool          DialogResult { get; private set; }

        // ── GPU detection ─────────────────────────────────────────────────────
        private bool   _isDetectingGpu;
        private string _gpuSummary = "Detecting…";
        public  bool   IsDetectingGpu { get => _isDetectingGpu; set => Set(ref _isDetectingGpu, value); }
        public  string GpuSummary     { get => _gpuSummary;     set => Set(ref _gpuSummary,     value); }

        // ── Identity ──────────────────────────────────────────────────────────
        private Guid   _id   = Guid.NewGuid();
        private string _name = string.Empty;
        public  string Name  { get => _name; set { Set(ref _name, value); ValidateAll(); } }

        // ── Source ────────────────────────────────────────────────────────────
        private CameraSource _source = CameraSource.Screen;
        private string       _webcamDeviceId = string.Empty;
        private string       _windowTitle    = string.Empty;
        private string       _inputUrl       = string.Empty;
        private string       _rtspPath       = string.Empty;
        private int          _rtspPort       = 8554;
        private ScreenRegion? _region;

        public CameraSource Source         { get => _source;        set { Set(ref _source,        value); OnPropertyChanged(nameof(IsWebcamVisible)); OnPropertyChanged(nameof(IsRegionVisible)); OnPropertyChanged(nameof(IsAppWindowVisible)); OnPropertyChanged(nameof(IsExternalSourceVisible)); } }
        public string       WebcamDeviceId { get => _webcamDeviceId; set => Set(ref _webcamDeviceId, value); }
        public string       RtspPath       { get => _rtspPath;       set { Set(ref _rtspPath, value); ValidateAll(); } }
        public int          RtspPort       { get => _rtspPort;       set => Set(ref _rtspPort, value); }
        public ScreenRegion? Region        { get => _region;         set { Set(ref _region, value); OnPropertyChanged(nameof(RegionSummary)); } }

        public bool IsWebcamVisible    => Source is CameraSource.Webcam or CameraSource.Combined;
        public bool IsRegionVisible    => Source is CameraSource.Screen or CameraSource.Combined;
        public bool IsAppWindowVisible    => Source is CameraSource.AppWindow;
        public bool IsExternalSourceVisible => Source is CameraSource.ExternalRtsp or CameraSource.ExternalHttp;

        public string WindowTitle { get => _windowTitle; set => Set(ref _windowTitle, value); }
        public string InputUrl    { get => _inputUrl;    set => Set(ref _inputUrl,    value); }

        public string RegionSummary => Region != null
            ? $"Monitor {Region.MonitorIndex}  •  {Region.X},{Region.Y}  {Region.Width}×{Region.Height}"
            : "Full primary monitor (default)";

        // ── Video ─────────────────────────────────────────────────────────────
        private VideoCodec _codec      = VideoCodec.H264;
        private Resolution _resolution = Resolution.R1080p;
        private int        _frameRate  = 30;
        private int        _bitrate    = 4000;
        private GpuAccel   _gpuAccel   = GpuAccel.None;

        public VideoCodec Codec      { get => _codec;      set => Set(ref _codec,      value); }
        public Resolution Resolution { get => _resolution; set => Set(ref _resolution, value); }
        public int        FrameRate  { get => _frameRate;  set { Set(ref _frameRate,  value); ValidateAll(); } }
        public int        Bitrate    { get => _bitrate;    set { Set(ref _bitrate,    value); ValidateAll(); } }
        public GpuAccel   GpuAccel   { get => _gpuAccel;  set => Set(ref _gpuAccel,  value); }

        // ── Audio ─────────────────────────────────────────────────────────────
        private AudioSource _audioSource   = AudioSource.None;
        private string      _micDeviceId   = string.Empty;
        private int         _audioBitrate  = 128;

        public AudioSource AudioSource   { get => _audioSource;  set { Set(ref _audioSource,  value); OnPropertyChanged(nameof(IsAudioConfigVisible)); } }
        public string      MicDeviceId   { get => _micDeviceId;  set => Set(ref _micDeviceId,  value); }
        public int         AudioBitrate  { get => _audioBitrate; set => Set(ref _audioBitrate, value); }
        public bool        IsAudioConfigVisible => AudioSource != AudioSource.None;

        // ── ONVIF ─────────────────────────────────────────────────────────────
        private bool _onvifEnabled = true;
        private int  _onvifPort    = 8080;

        public bool OnvifEnabled { get => _onvifEnabled; set => Set(ref _onvifEnabled, value); }
        public int  OnvifPort    { get => _onvifPort;    set { Set(ref _onvifPort, value); ValidateAll(); } }

        // ── Motion detection ──────────────────────────────────────────────────
        private bool   _motionDetection = false;
        private int    _motionThreshold = 25;
        private double _motionRatio     = 0.02;

        public bool   MotionDetection { get => _motionDetection; set => Set(ref _motionDetection, value); }
        public int    MotionThreshold { get => _motionThreshold; set => Set(ref _motionThreshold, value); }
        public double MotionRatio     { get => _motionRatio;     set => Set(ref _motionRatio,     value); }

        // ── Auth ──────────────────────────────────────────────────────────────
        private string _username = string.Empty;
        private string _password = string.Empty;

        public string Username { get => _username; set => Set(ref _username, value); }
        public string Password { get => _password; set => Set(ref _password, value); }

        // ── Overlays ──────────────────────────────────────────────────────────
        public ObservableCollection<OverlayRowViewModel> Overlays { get; } = new();

        // ── Combo sources (for XAML binding) ─────────────────────────────────
        public IReadOnlyList<CameraSource>  SourceOptions     { get; } = Enum.GetValues<CameraSource>().ToList();
        public IReadOnlyList<VideoCodec>    CodecOptions      { get; } = Enum.GetValues<VideoCodec>().ToList();
        public IReadOnlyList<Resolution>    ResolutionOptions { get; } = Enum.GetValues<Resolution>().ToList();
        public IReadOnlyList<GpuAccel>      GpuAccelOptions   { get; } = Enum.GetValues<GpuAccel>().ToList();
        public IReadOnlyList<AudioSource>   AudioOptions      { get; } = Enum.GetValues<AudioSource>().ToList();
        public IReadOnlyList<OverlayType>   OverlayTypeOptions{ get; } = Enum.GetValues<OverlayType>().ToList();

        // ── Validation ────────────────────────────────────────────────────────
        private readonly Dictionary<string, string> _errors = new();
        public  IReadOnlyDictionary<string, string> ValidationErrors => _errors;
        public  bool IsValid => _errors.Count == 0;

        // IDataErrorInfo — consumed by WPF binding validation
        public string Error => string.Join("; ", _errors.Values);
        public string this[string column] => _errors.TryGetValue(column, out var e) ? e : string.Empty;

        // ── Status ────────────────────────────────────────────────────────────
        private bool   _isBusy;
        private string _busyMessage = string.Empty;
        public  bool   IsBusy       { get => _isBusy;       set => Set(ref _isBusy,       value); }
        public  string BusyMessage  { get => _busyMessage;  set => Set(ref _busyMessage,  value); }

        // ── Commands ──────────────────────────────────────────────────────────
        public ICommand SaveCommand          { get; }
        public ICommand CancelCommand        { get; }
        public ICommand DetectGpuCommand     { get; }
        public ICommand AddOverlayCommand    { get; }
        public ICommand RemoveOverlayCommand { get; }
        public ICommand AutoFillPathCommand  { get; }
        public ICommand PickScreenCommand    { get; }

        /// <summary>Raised when the dialog should close (Save or Cancel).</summary>
        public event EventHandler<bool>? RequestClose;

        // ── Constructor ───────────────────────────────────────────────────────

        public CameraEditViewModel(
            ILogger<CameraEditViewModel> logger,
            SettingsService              settings,
            GpuDetector                  gpuDetector,
            CameraConfig?                existingCamera = null)
        {
            _logger      = logger;
            _settings    = settings;
            _gpuDetector = gpuDetector;
            IsEditMode   = existingCamera != null;

            if (existingCamera != null)
                LoadFromModel(existingCamera);
            else
                AutoFillPath();   // derive a safe default path from a new GUID

            SaveCommand = new AsyncRelayCommand(
                async _ => await SaveAsync(),
                _ => IsValid && !IsBusy);

            CancelCommand        = new RelayCommand(_ => RequestClose?.Invoke(this, false));
            DetectGpuCommand     = new AsyncRelayCommand(async _ => await DetectGpuAsync());
            AddOverlayCommand    = new RelayCommand(_ => Overlays.Add(new OverlayRowViewModel()));
            RemoveOverlayCommand = new RelayCommand(
                p => { if (p is OverlayRowViewModel o) Overlays.Remove(o); },
                p => p is OverlayRowViewModel);
            AutoFillPathCommand  = new RelayCommand(_ => AutoFillPath());
            PickScreenCommand    = new RelayCommand(_ => PickScreen());

            ValidateAll();
        }

        // ── Initialise (called from dialog Loaded event) ──────────────────────

        public async Task InitialiseAsync()
        {
            await DetectGpuAsync();
        }

        // ── GPU detection ─────────────────────────────────────────────────────

        private async Task DetectGpuAsync()
        {
            IsDetectingGpu = true;
            GpuSummary     = "Detecting GPU…";
            try
            {
                var result  = await _gpuDetector.DetectAsync();
                GpuSummary  = result.Summary;

                // Only auto-set on Add; preserve the user's choice on Edit
                if (!IsEditMode)
                    GpuAccel = await _gpuDetector.RecommendAccelAsync();
            }
            catch (Exception ex)
            {
                GpuSummary = "GPU detection failed.";
                _logger.LogWarning(ex, "GPU detection failed in CameraEditViewModel.");
            }
            finally
            {
                IsDetectingGpu = false;
            }
        }

        // ── Save ──────────────────────────────────────────────────────────────

        private async Task SaveAsync()
        {
            ValidateAll();
            if (!IsValid) return;

            IsBusy      = true;
            BusyMessage = "Saving…";

            try
            {
                var model = BuildModel();
                await _settings.SaveCameraAsync(model);
                SavedCamera  = model;
                DialogResult = true;
                RequestClose?.Invoke(this, true);
                _logger.LogInformation("Camera '{Name}' saved (Edit={Edit}).", model.Name, IsEditMode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save camera '{Name}'.", Name);
                BusyMessage = $"Save failed: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ── Model mapping ─────────────────────────────────────────────────────

        /// <summary>Load an existing config for editing (called by DashboardViewModel).</summary>
        public void LoadFromConfig(CameraConfig c)
        {
            IsEditMode = true;
            LoadFromModel(c);
        }

        private void LoadFromModel(CameraConfig c)
        {
            _id             = c.Id;
            Name            = c.Name;
            Source          = c.Source;
            WebcamDeviceId  = c.WebcamDeviceId ?? string.Empty;
            WindowTitle     = c.WindowTitle ?? string.Empty;
            InputUrl        = c.InputUrl ?? string.Empty;
            RtspPath        = c.RtspPath;
            RtspPort        = c.RtspPort;
            Region          = c.Region;
            Codec           = c.Codec;
            Resolution      = c.Resolution;
            FrameRate       = c.FrameRate;
            Bitrate         = c.Bitrate;
            GpuAccel        = c.GpuAccel;
            AudioSource     = c.AudioSource;
            MicDeviceId     = c.MicDeviceId   ?? string.Empty;
            AudioBitrate    = c.AudioBitrate;
            OnvifEnabled    = c.OnvifEnabled;
            OnvifPort       = c.OnvifPort;
            MotionDetection = c.MotionDetection;
            Username        = c.Username       ?? string.Empty;
            Password        = c.Password       ?? string.Empty;

            Overlays.Clear();
            foreach (var o in c.Overlays)
                Overlays.Add(OverlayRowViewModel.FromModel(o));
        }

        private CameraConfig BuildModel() => new()
        {
            Id              = _id,
            Name            = Name.Trim(),
            Source          = Source,
            WebcamDeviceId  = string.IsNullOrEmpty(WebcamDeviceId) ? null : WebcamDeviceId,
            WindowTitle     = string.IsNullOrEmpty(WindowTitle) ? null : WindowTitle,
            InputUrl        = string.IsNullOrEmpty(InputUrl) ? null : InputUrl,
            RtspPath        = RtspPath.Trim().TrimStart('/'),
            RtspPort        = RtspPort,
            Region          = Region,
            Codec           = Codec,
            Resolution      = Resolution,
            FrameRate       = FrameRate,
            Bitrate         = Bitrate,
            GpuAccel        = GpuAccel,
            AudioSource     = AudioSource,
            MicDeviceId     = string.IsNullOrEmpty(MicDeviceId) ? null : MicDeviceId,
            AudioBitrate    = AudioBitrate,
            OnvifEnabled    = OnvifEnabled,
            OnvifPort       = OnvifPort,
            MotionDetection = MotionDetection,
            Username        = string.IsNullOrEmpty(Username) ? null : Username,
            Password        = string.IsNullOrEmpty(Password) ? null : Password,
            Overlays        = Overlays.Select(o => o.ToModel()).ToList(),
            Enabled         = true,
        };

        // ── Helpers ───────────────────────────────────────────────────────────

        private void AutoFillPath()
        {
            if (!string.IsNullOrEmpty(Name))
                RtspPath = Name.ToLowerInvariant()
                    .Replace(" ", "-")
                    .Replace("_", "-");
            else
                RtspPath = $"cam-{_id.ToString("N")[..8]}";
        }

        private void PickScreen()
        {
            var picker = new Views.ScreenPickerView();
            // Pre-populate with existing region if any
            if (Region != null)
            {
                picker.Vm.RegionX = Region.X;
                picker.Vm.RegionY = Region.Y;
                picker.Vm.RegionW = Region.Width;
                picker.Vm.RegionH = Region.Height;
            }

            if (picker.ShowDialog() == true && picker.Vm.SelectedRegion != null)
                Region = picker.Vm.SelectedRegion;
        }



        private void ValidateAll()
        {
            _errors.Clear();

            if (string.IsNullOrWhiteSpace(Name))
                _errors[nameof(Name)] = "Camera name is required.";

            if (string.IsNullOrWhiteSpace(RtspPath))
                _errors[nameof(RtspPath)] = "RTSP path is required.";
            else if (RtspPath.Contains(' '))
                _errors[nameof(RtspPath)] = "RTSP path must not contain spaces.";

            if (FrameRate is < 1 or > 120)
                _errors[nameof(FrameRate)] = "Frame rate must be between 1 and 120.";

            if (Bitrate is < 100 or > 100_000)
                _errors[nameof(Bitrate)] = "Bitrate must be between 100 and 100 000 kbps.";

            if (OnvifPort is < 1024 or > 65535)
                _errors[nameof(OnvifPort)] = "ONVIF port must be in range 1024–65535.";

            OnPropertyChanged(nameof(IsValid));
            OnPropertyChanged(nameof(Error));
            ((AsyncRelayCommand)SaveCommand).RaiseCanExecuteChanged();
        }
    }
}
