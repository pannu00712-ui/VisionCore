using System.Windows.Controls;
using VisionCore.WPF.ViewModels;
using UserControl = System.Windows.Controls.UserControl;

namespace VisionCore.WPF.Views
{
    public partial class DashboardView : UserControl
    {
        public DashboardView() => InitializeComponent();

        private DashboardViewModel? Vm => DataContext as DashboardViewModel;

        private void DataGrid_MouseDoubleClick(object sender,
            System.Windows.Input.MouseButtonEventArgs e)
        {
            var vm = Vm;
            if (vm?.EditCameraCommand.CanExecute(null) == true)
                vm.EditCameraCommand.Execute(null);
        }
    }
}
