// TableSizeDialog.xaml.cs
//
// Part of mde (MarkDown インラインエディタ).
// A small modal dialog for choosing a new m_table's row/column count, shown from the m_editor's
// right-click "表を挿入" (insert m_table) menu item.

using System.Windows;

namespace mde
{
    /// <summary>Prompts the user for a row count and column count, clamping both to sane bounds.</summary>
    public partial class TableSizeDialog : Window
    {
        /// <summary>Chosen row count (including the header row) after the dialog closes with OK.</summary>
        public int Rows { get; private set; } = 3;

        /// <summary>Chosen column count after the dialog closes with OK.</summary>
        public int Columns { get; private set; } = 3;

        /// <summary>Creates the dialog with default 3x3 values and focuses the row-count field.</summary>
        public TableSizeDialog()
        {
            InitializeComponent();
            m_rowsBox.Focus();
            m_rowsBox.SelectAll();
        }

        /// <summary>Validates and clamps the entered values, then closes the dialog with a positive
        /// result.</summary>
        /// <param name="a_sender">The OK button.</param>
        /// <param name="a_args">Click event.</param>
        private void OkClick(object a_sender, RoutedEventArgs a_args)
        {
            if (!int.TryParse(m_rowsBox.Text, out int r) || r < 1) r = 1;
            if (!int.TryParse(m_colsBox.Text, out int c) || c < 1) c = 1;
            if (r > 50) r = 50;
            if (c > 20) c = 20;

            Rows = r;
            Columns = c;
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
