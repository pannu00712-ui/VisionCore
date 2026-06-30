using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using VisionCore.Core.Models;
using VisionCore.Core.Services;
using Timer = System.Threading.Timer;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;

namespace VisionCore.WPF.ViewModels
{
    // ══════════════════════════════════════════════════════════════════════════
    // LogEntryViewModel — display wrapper for a single log entry
    // ══════════════════════════════════════════════════════════════════════════

    public sealed class LogEntryViewModel : ViewModelBase
    {
        public DateTime Timestamp { get; }
        public string   Level     { get; }
        public string   Source    { get; }
        public string   Message   { get; }

        /// <summary>Colour key resolved by the XAML DataTrigger / converter.</summary>
        public string LevelKey => Level.ToUpperInvariant() switch
        {
            "VERBOSE" or "TRACE" => "Verbose",
            "DEBUG"              => "Debug",
            "INFORMATION"        => "Info",
            "WARNING"            => "Warning",
            "ERROR"              => "Error",
            "FATAL" or "CRITICAL"=> "Fatal",
            _                   => "Info",
        };

        /// <summary>Single-letter badge shown in the Level column.</summary>
        public string LevelBadge => Level.ToUpperInvariant() switch
        {
            "VERBOSE" or "TRACE" => "V",
            "DEBUG"              => "D",
            "INFORMATION"        => "I",
            "WARNING"            => "W",
            "ERROR"              => "E",
            "FATAL" or "CRITICAL"=> "F",
            _                   => "?",
        };

        public string FormattedTimestamp => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
        public string FullText           => $"[{FormattedTimestamp}] [{Level}] [{Source}] {Message}";

        public LogEntryViewModel(LogEntry entry)
        {
            Timestamp = entry.Timestamp;
            Level     = entry.Level    ?? "Information";
            Source    = entry.SourceContext ?? string.Empty;
            Message   = entry.Message  ?? string.Empty;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // LogsViewModel
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Backs the Logs page / panel.
    ///
    /// Features:
    ///   • Polls <see cref="RestApiService.GetLogs"/> every <see cref="PollInterval"/>
    ///     and merges new entries into the observable collection (no duplicates).
    ///   • <see cref="ICollectionView"/> filter supports simultaneous level + text search.
    ///   • Auto-scroll toggle — if enabled, newly added entries scroll into view via
    ///     a weak-event approach; the View wires up <see cref="ScrollRequested"/>.
    ///   • Export command writes the currently filtered view to a .log file.
    ///   • Clear command trims the local buffer (does not affect the service ring buffer).
    /// </summary>
    public sealed class LogsViewModel : ViewModelBase, IDisposable
    {
        // ── Services ──────────────────────────────────────────────────────────
        private readonly ILogger<LogsViewModel> _logger;
        private readonly RestApiService          _api;

        // ── Polling ───────────────────────────────────────────────────────────
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
        private Timer?  _pollTimer;
        private bool    _isDisposed;
        private DateTime _lastSeenTimestamp = DateTime.MinValue;

        // ── Collections ───────────────────────────────────────────────────────
        private readonly ObservableCollection<LogEntryViewModel> _allEntries = new();

        /// <summary>Filtered, sorted view — bind the ListView/DataGrid ItemsSource to this.</summary>
        public CollectionView Entries { get; }

        // ── Filter ────────────────────────────────────────────────────────────
        private string _searchText    = string.Empty;
        private string _levelFilter   = "All";
        private string _sourceFilter  = string.Empty;

        public string SearchText
        {
            get => _searchText;
            set { Set(ref _searchText, value); Entries.Refresh(); }
        }

        public string LevelFilter
        {
            get => _levelFilter;
            set { Set(ref _levelFilter, value); Entries.Refresh(); }
        }

        public string SourceFilter
        {
            get => _sourceFilter;
            set { Set(ref _sourceFilter, value); Entries.Refresh(); }
        }

        public string[] LevelFilterOptions { get; } =
        {
            "All", "Verbose", "Debug", "Information", "Warning", "Error", "Fatal"
        };

        // ── UX state ──────────────────────────────────────────────────────────
        private bool   _autoScroll   = true;
        private bool   _isPaused;
        private int    _totalCount;
        private int    _filteredCount;
        private string _statusMessage = string.Empty;

        public bool   AutoScroll    { get => _autoScroll;    set => Set(ref _autoScroll,    value); }
        public bool   IsPaused      { get => _isPaused;      set { Set(ref _isPaused, value); UpdatePauseCommand(); } }
        public int    TotalCount    { get => _totalCount;    set => Set(ref _totalCount,    value); }
        public int    FilteredCount { get => _filteredCount; set => Set(ref _filteredCount, value); }
        public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

        /// <summary>
        /// Raised after new entries are added; the View subscribes and calls
        /// <c>listView.ScrollIntoView(e)</c> when <see cref="AutoScroll"/> is true.
        /// </summary>
        public event EventHandler<LogEntryViewModel>? ScrollRequested;

        // ── Commands ──────────────────────────────────────────────────────────
        public ICommand ClearCommand      { get; }
        public ICommand ExportCommand     { get; }
        public ICommand PauseCommand      { get; }
        public ICommand CopyLineCommand   { get; }
        public ICommand ResetFilterCommand{ get; }

        // ── Selected entry ────────────────────────────────────────────────────
        private LogEntryViewModel? _selected;
        public  LogEntryViewModel? Selected
        {
            get => _selected;
            set { Set(ref _selected, value); ((RelayCommand)CopyLineCommand).RaiseCanExecuteChanged(); }
        }

        // ── Constructor ───────────────────────────────────────────────────────

        public LogsViewModel(
            ILogger<LogsViewModel> logger,
            RestApiService          api)
        {
            _logger = logger;
            _api    = api;

            // Build CollectionView over the observable collection
            Entries = (CollectionView)CollectionViewSource.GetDefaultView(_allEntries);
            Entries.Filter = ApplyFilter;
            Entries.SortDescriptions.Add(new SortDescription(nameof(LogEntryViewModel.Timestamp),
                ListSortDirection.Descending));

            ClearCommand       = new RelayCommand(_ => ClearEntries());
            ExportCommand      = new AsyncRelayCommand(async _ => await ExportAsync());
            PauseCommand       = new RelayCommand(_ => IsPaused = !IsPaused);
            CopyLineCommand    = new RelayCommand(
                _ => CopySelected(),
                _ => Selected != null);
            ResetFilterCommand = new RelayCommand(_ => ResetFilters());

            // Track filtered count via the underlying collection
            _allEntries.CollectionChanged += (_, _) =>
                FilteredCount = _allEntries.Count(e => Entries.PassesFilter(e));
        }

        // ── Initialise ────────────────────────────────────────────────────────

        public Task InitialiseAsync()
        {
            // Load the existing ring buffer immediately
            MergeNewEntries();

            _pollTimer = new Timer(
                _ => Application.Current?.Dispatcher.InvokeAsync(MergeNewEntries),
                null,
                PollInterval,
                PollInterval);

            return Task.CompletedTask;
        }

        // ── Polling ───────────────────────────────────────────────────────────

        private void MergeNewEntries()
        {
            if (_isDisposed || IsPaused) return;

            try
            {
                var entries = _api.GetLogs(500);   // get full buffer; we de-dup below

                LogEntryViewModel? newest = null;
                int added = 0;

                // Entries come newest-first from the service; iterate in reverse for chronological order
                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    var entry = entries[i];
                    if (entry.Timestamp <= _lastSeenTimestamp) continue;

                    var vm = new LogEntryViewModel(entry);
                    _allEntries.Add(vm);
                    newest = vm;
                    added++;
                }

                if (added > 0)
                {
                    _lastSeenTimestamp = entries[0].Timestamp;   // newest is at index 0
                    TotalCount = _allEntries.Count;
                    Entries.Refresh();
                    FilteredCount = _allEntries.Count(e => Entries.PassesFilter(e));
                    StatusMessage = $"{TotalCount} entries  ({FilteredCount} shown)";

                    if (AutoScroll && newest != null)
                        ScrollRequested?.Invoke(this, newest);

                    // Trim local collection to 1000 to avoid unbounded memory growth
                    while (_allEntries.Count > 1000)
                        _allEntries.RemoveAt(0);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Log poll tick failed.");
            }
        }

        // ── Filter predicate ──────────────────────────────────────────────────

        private bool ApplyFilter(object item)
        {
            if (item is not LogEntryViewModel e) return false;

            // Level filter
            if (LevelFilter != "All" &&
                !e.Level.StartsWith(LevelFilter, StringComparison.OrdinalIgnoreCase))
                return false;

            // Source filter
            if (!string.IsNullOrWhiteSpace(SourceFilter) &&
                !e.Source.Contains(SourceFilter, StringComparison.OrdinalIgnoreCase))
                return false;

            // Full-text search over message + source
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                var q = SearchText.Trim();
                if (!e.Message.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !e.Source.Contains(q,  StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        // ── Commands impl ─────────────────────────────────────────────────────

        private void ClearEntries()
        {
            _allEntries.Clear();
            _lastSeenTimestamp = DateTime.MinValue;
            TotalCount         = 0;
            FilteredCount      = 0;
            StatusMessage      = "Log buffer cleared.";
        }

        private async Task ExportAsync()
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"VisionCore_logs_{DateTime.Now:yyyyMMdd_HHmmss}.log");

            try
            {
                var lines = _allEntries
                    .Where(e => Entries.PassesFilter(e))
                    .OrderBy(e => e.Timestamp)
                    .Select(e => e.FullText);

                await File.WriteAllLinesAsync(path, lines);
                StatusMessage = $"Exported to {path}";
                _logger.LogInformation("Logs exported to {Path}.", path);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Export failed: {ex.Message}";
                _logger.LogError(ex, "Log export failed.");
            }
        }

        private void CopySelected()
        {
            if (Selected != null)
            {
                Clipboard.SetText(Selected.FullText);
                StatusMessage = "Copied to clipboard.";
            }
        }

        private void ResetFilters()
        {
            SearchText   = string.Empty;
            LevelFilter  = "All";
            SourceFilter = string.Empty;
        }

        private void UpdatePauseCommand()
        {
            StatusMessage = IsPaused ? "Live updates paused." : "Live updates resumed.";
        }

        // ── IDisposable ───────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _pollTimer?.Dispose();
        }
    }
}
