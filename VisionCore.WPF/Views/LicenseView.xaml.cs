using System.Windows;
using VisionCore.Core.Models;
using VisionCore.WPF.ViewModels;

namespace VisionCore.WPF.Views
{
    public partial class LicenseView : Window
    {
        public LicenseView()
        {
            InitializeComponent();
        }

        public LicenseView(LicenseViewModel vm) : this()
        {
            DataContext = vm;
            vm.LicenseActivated += (_, _) => DialogResult = true;
            vm.Cancelled        += (_, _) => DialogResult = false;
        }
    }
}
