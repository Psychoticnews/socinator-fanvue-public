using FanvueDominatorUI.ViewModels.Engage;
using System.Windows;
using System.Windows.Controls;

namespace FanvueDominatorUI.Views.Engage
{
    public partial class MassMessagesControl : UserControl
    {
        public MassMessagesControl()
        {
            InitializeComponent();
            Unloaded += OnUnloaded;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as MassMessagesViewModel;
            if (vm != null) { vm.Cancel(); }
        }
    }
}
