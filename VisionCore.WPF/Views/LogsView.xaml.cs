using System.Windows.Controls;
using VisionCore.WPF.ViewModels;
using UserControl = System.Windows.Controls.UserControl;

namespace VisionCore.WPF.Views
{
    public partial class LogsView : UserControl
    {
        public LogsView() => InitializeComponent();

        /// <summary>
        /// Called by MainWindow when LogsViewModel raises ScrollRequested.
        /// Scrolls the ListView to the given entry if auto-scroll is on.
        /// </summary>
        public void ScrollToEntry(LogEntryViewModel entry)
        {
            if (entry == null) return;
            LogListView.ScrollIntoView(entry);
        }
    }
}
