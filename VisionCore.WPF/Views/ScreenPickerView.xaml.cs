using System.Windows;
using System.Windows.Controls;
using VisionCore.WPF.ViewModels;
using UserControl = System.Windows.Controls.UserControl;

namespace VisionCore.WPF.Views
{
    public partial class ScreenPickerView : Window
    {
        public ScreenPickerViewModel Vm { get; }

        public ScreenPickerView()
        {
            InitializeComponent();
            Vm          = new ScreenPickerViewModel();
            DataContext = Vm;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            Vm.ConfirmCommand.Execute(null);
            DialogResult = true;
            Close();
        }
    }
}
