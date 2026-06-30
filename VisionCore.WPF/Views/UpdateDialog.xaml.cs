using System.Windows;
using VisionCore.WPF.ViewModels;

namespace VisionCore.WPF.Views
{
    public partial class UpdateDialog : Window
    {
        public UpdateDialog(UpdateViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
            vm.RequestClose += (_, _) => Close();

            Resources.Add("BoolToVis", new System.Windows.Controls.BooleanToVisibilityConverter());
        }
    }
}
