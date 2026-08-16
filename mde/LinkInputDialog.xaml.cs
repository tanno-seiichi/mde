// LinkInputDialog.xaml.cs
//
// Part of mde (MarkDown インラインエディタ).
// A small modal dialog for entering/editing a hyperlink's URL, shown from the m_editor's right-click
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
        /// <param name="a_initialUrl">URL to show pre-filled, or empty for a new link.</param>
        public LinkInputDialog(string a_initialUrl = "")
        {
            InitializeComponent();
            m_urlBox.Text = a_initialUrl;
            m_urlBox.Focus();
            m_urlBox.SelectAll();
        }

        /// <summary>Captures the entered URL and closes the dialog with a positive result.</summary>
        /// <param name="a_sender">The OK button.</param>
        /// <param name="a_args">Click event.</param>
        private void OkClick(object a_sender, RoutedEventArgs a_args)
        {
            Url = m_urlBox.Text.Trim();
            DialogResult = true;
        }

        /// <summary>Closes the dialog without applying any changes.</summary>
        /// <param name="a_sender">The Cancel button.</param>
        /// <param name="a_args">Click event.</param>
        private void CancelClick(object a_sender, RoutedEventArgs a_args)
        {
            DialogResult = false;
        }
    }
}
