using FanvueDominatorUI.ViewModels.Engage;
using System.Windows;
using System.Windows.Controls;

namespace FanvueDominatorUI.Views.Engage
{
    public partial class VaultControl : UserControl
    {
        public VaultControl()
        {
            InitializeComponent();
            Unloaded += OnUnloaded;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as VaultViewModel;
            if (vm != null) { vm.Cancel(); }
        }
    }
}
