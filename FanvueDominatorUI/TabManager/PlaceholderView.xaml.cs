using System.Windows.Controls;

namespace FanvueDominatorUI.TabManager
{
    /// <summary>
    /// Placeholder view for template tabs.
    /// Displays a message indicating the feature is coming soon.
    /// </summary>
    public partial class PlaceholderView : UserControl
    {
        public PlaceholderView()
        {
            InitializeComponent();
        }

        public PlaceholderView(string message)
        {
            InitializeComponent();
            MessageText.Text = message;
        }
    }
}
