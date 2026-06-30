using System.Windows;
using VisionCore.WPF.ViewModels;

namespace VisionCore.WPF.Views
{
    public partial class CameraEditView : Window
    {
        private readonly CameraEditViewModel _vm;

        public CameraEditView(CameraEditViewModel vm)
        {
            InitializeComponent();
            _vm         = vm;
            DataContext = vm;

            // Wire password box — PasswordBox cannot two-way bind in XAML for
            // security reasons, so we sync in code-behind instead.
            PwdBox.Password          = vm.Password;
            PwdBox.PasswordChanged  += (_, _) => vm.Password = PwdBox.Password;

            // VM raises RequestClose(bool result) when Save or Cancel is clicked
            vm.RequestClose += (_, saved) =>
            {
                DialogResult = saved;
                Close();
            };
        }

        protected override async void OnContentRendered(System.EventArgs e)
        {
            base.OnContentRendered(e);
            await _vm.InitialiseAsync();
        }
    }
}
