using System.Windows;

namespace mde
{
    public partial class TableSizeDialog : Window
    {
        public int Rows { get; private set; } = 3;
        public int Columns { get; private set; } = 3;

        public TableSizeDialog()
        {
            InitializeComponent();
            RowsBox.Focus();
            RowsBox.SelectAll();
        }

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

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
