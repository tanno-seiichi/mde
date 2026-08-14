using System.Windows;

namespace mde
{
    public partial class LinkInputDialog : Window
    {
        public string Url { get; private set; } = "";

        public LinkInputDialog(string initialUrl = "")
        {
            InitializeComponent();
            UrlBox.Text = initialUrl;
            UrlBox.Focus();
            UrlBox.SelectAll();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            Url = UrlBox.Text.Trim();
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
