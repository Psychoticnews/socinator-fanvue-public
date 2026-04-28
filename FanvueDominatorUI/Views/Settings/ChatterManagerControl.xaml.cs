using FanvueDominatorUI.ViewModels.Settings;
using System.Windows;
using System.Windows.Controls;

namespace FanvueDominatorUI.Views.Settings
{
    public partial class ChatterManagerControl : UserControl
    {
        public ChatterManagerControl()
        {
            InitializeComponent();
            Unloaded += OnUnloaded;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as ChatterManagerViewModel;
            if (vm != null) { vm.Cancel(); }
        }
    }
}
