// LinkInputDialog.xaml.cs
//
// Part of mde (MarkDown インラインエディタ).
// A small modal dialog for entering/editing a hyperlink's URL, shown from the editor's right-click
// "文字装飾 → リンクにする…" menu item and the "リンクを編集…" link context menu item.

using System.Windows;

namespace mde
{
    /// <summary>Prompts the user for a URL, pre-filled when editing an existing link.</summary>
    public partial class LinkInputDialog : Window
    {
        /// <summary>The entered URL after the dialog closes with OK (trimmed of whitespace).</summary>
        public string Url { get; private set; } = "";

        /// <summary>Creates the dialog, optionally pre-filled with an existing URL to edit.</summary>
        /// <param name="initialUrl">URL to show pre-filled, or empty for a new link.</param>
        public LinkInputDialog(string initialUrl = "")
        {
            InitializeComponent();
            UrlBox.Text = initialUrl;
            UrlBox.Focus();
            UrlBox.SelectAll();
        }

        /// <summary>Captures the entered URL and closes the dialog with a positive result.</summary>
        /// <param name="sender">The OK button.</param>
        /// <param name="e">Click event.</param>
        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            Url = UrlBox.Text.Trim();
            DialogResult = true;
        }

        /// <summary>Closes the dialog without applying any changes.</summary>
        /// <param name="sender">The Cancel button.</param>
        /// <param name="e">Click event.</param>
        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
