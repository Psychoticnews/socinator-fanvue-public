using System.Windows;

namespace FanvueDominatorUI.Views.Engage
{
    public partial class VaultFolderEditDialog : Window
    {
        public string Result { get; private set; }

        public VaultFolderEditDialog(string title, string initialName)
        {
            InitializeComponent();
            Title = title ?? "Folder";
            NameTextBox.Text = initialName ?? string.Empty;
            Loaded += (s, e) =>
            {
                NameTextBox.Focus();
                NameTextBox.SelectAll();
            };
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            Result = NameTextBox.Text == null ? string.Empty : NameTextBox.Text.Trim();
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
