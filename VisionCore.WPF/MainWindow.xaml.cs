using System;
using System.Windows;
using System.Windows.Controls;
using VisionCore.WPF.ViewModels;
using Button = System.Windows.Controls.Button;

namespace VisionCore.WPF
{
    public partial class MainWindow : Window
    {
        // ── ViewModels injected via DI ────────────────────────────────────────
        private readonly DashboardViewModel _dashboardVm;
        private readonly LogsViewModel      _logsVm;
        private readonly SettingsViewModel  _settingsVm;
        private readonly AuditLogViewModel  _auditLogVm;

        public MainWindow(
            DashboardViewModel dashboardVm,
            LogsViewModel      logsVm,
            SettingsViewModel  settingsVm,
            AuditLogViewModel  auditLogVm)
        {
            InitializeComponent();

            _dashboardVm = dashboardVm;
            _logsVm      = logsVm;
            _settingsVm  = settingsVm;
            _auditLogVm  = auditLogVm;

            // MainWindow DataContext → DashboardViewModel so the status-bar
            // aggregates (ActiveCameras, AppUptime, etc.) bind directly.
            DataContext = _dashboardVm;

            // Each page has its own DataContext
            PageDashboard.DataContext = _dashboardVm;
            PageLogs.DataContext      = _logsVm;
            PageSettings.DataContext  = _settingsVm;
            PageAuditLog.DataContext  = _auditLogVm;

            // Wire log auto-scroll: LogsViewModel raises ScrollRequested, the
            // View's ListView is owned by LogsView code-behind — so we forward
            // the event to it via a public method.
            _logsVm.ScrollRequested += (_, entry) => PageLogs.ScrollToEntry(entry);

            // Set assembly version in sidebar
            var asm = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = asm != null ? $"v{asm.Major}.{asm.Minor}.{asm.Build}" : "v1.0.0";
        }

        // ── Loaded ────────────────────────────────────────────────────────────

        protected override async void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);

            // Initialise all VMs after the window is visible so the UI is
            // responsive immediately and async work does not block startup.
            await _dashboardVm.InitialiseAsync();
            await _logsVm.InitialiseAsync();
            await _settingsVm.InitialiseAsync();
            await _auditLogVm.InitialiseAsync();
        }

        // ── Nav switching ─────────────────────────────────────────────────────

        private void NavDashboard_Click(object sender, RoutedEventArgs e) =>
            ShowPage(PageDashboard, NavDashboard);

        private void NavLogs_Click(object sender, RoutedEventArgs e) =>
            ShowPage(PageLogs, NavLogs);

        private void NavSettings_Click(object sender, RoutedEventArgs e) =>
            ShowPage(PageSettings, NavSettings);

        private void NavAuditLog_Click(object sender, RoutedEventArgs e) =>
            ShowPage(PageAuditLog, NavAuditLog);

        private void ShowPage(FrameworkElement page, Button activeNav)
        {
            // Hide all pages
            PageDashboard.Visibility = Visibility.Collapsed;
            PageLogs.Visibility      = Visibility.Collapsed;
            PageSettings.Visibility  = Visibility.Collapsed;
            PageAuditLog.Visibility  = Visibility.Collapsed;

            // Reset all nav buttons to inactive style
            NavDashboard.Style = (Style)FindResource("NavItem");
            NavLogs.Style      = (Style)FindResource("NavItem");
            NavSettings.Style  = (Style)FindResource("NavItem");
            NavAuditLog.Style  = (Style)FindResource("NavItem");

            // Show requested page and highlight nav item
            page.Visibility  = Visibility.Visible;
            activeNav.Style  = (Style)FindResource("NavItem.Active");
        }

        // ── Clean shutdown ────────────────────────────────────────────────────

        protected override void OnClosed(EventArgs e)
        {
            _dashboardVm.Dispose();
            _logsVm.Dispose();
            _settingsVm.Dispose();
            base.OnClosed(e);
        }
    }
}
