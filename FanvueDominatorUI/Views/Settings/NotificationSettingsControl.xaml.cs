using System.Windows.Controls;
using FanvueDominatorUI.ViewModels.Settings;

namespace FanvueDominatorUI.Views.Settings
{
    public partial class NotificationSettingsControl : UserControl
    {
        public NotificationSettingsControl()
        {
            InitializeComponent();
            DataContext = new NotificationSettingsViewModel();
        }
    }
}
