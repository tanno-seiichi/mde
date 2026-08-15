// TableSizeDialog.xaml.cs
//
// Part of mde (MarkDown インラインエディタ).
// A small modal dialog for choosing a new table's row/column count, shown from the editor's
// right-click "表を挿入" (insert table) menu item.

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
            RowsBox.Focus();
            RowsBox.SelectAll();
        }

        /// <summary>Validates and clamps the entered values, then closes the dialog with a positive
        /// result.</summary>
        /// <param name="sender">The OK button.</param>
        /// <param name="e">Click event.</param>
        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(RowsBox.Text, out int r) || r < 1) r = 1;
            if (!int.TryParse(ColsBox.Text, out int c) || c < 1) c = 1;
            if (r > 50) r = 50;
            if (c > 20) c = 20;

            Rows = r;
            Columns = c;
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
