// ColumnWidthDialog.xaml.cs
//
// Part of mde (Markdown インラインエディタ).
// A small modal dialog for adjusting each column's width (expressed as a plain number that
// corresponds to the separator row's dash count), shown from the m_editor's right-click
// "列幅を調整…" table menu item. The number of rows in this dialog is not fixed (it depends
// on how many columns the target table has), so the input rows are built dynamically in the
// constructor rather than declared statically in XAML (unlike TableSizeDialog/PdfMarginDialog,
// which always have a fixed number of fields).

using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace mde
{
    /// <summary>各列の幅（区切り行のダッシュ数に相当する数値）を一覧で入力させるダイアログ。</summary>
    public partial class ColumnWidthDialog : Window
    {
        private readonly List<TextBox> m_boxes = new List<TextBox>();

        /// <summary>コンストラクタでの入力欄・「自動計算」チェックボックスの初期値設定中に、
        /// TextChangedイベントで「自動計算」を意図せずオフにしてしまわないための抑制フラグ。</summary>
        private bool m_suppressAutoUncheckFlg;

        /// <summary>ダイアログがOKで閉じた後の、列ごとの値（区切り行のダッシュ数）。
        /// 入力順は、コンストラクタに渡した列の順序と一致する。「自動計算」がオンのまま
        /// OKされた場合は、すべて既定値（BlockStyles.TABLE_COLUMN_DEFAULT_DASH_COUNT）になる
        /// （呼び出し側で「未調整の表に戻す」操作として扱われる。TableEditor.ApplyColumnWidths・
        /// BlockStyles.ApplyEffectiveColumnWidths参照）。</summary>
        public List<int> DashCounts { get; private set; }

        /// <summary>
        /// 表の列ごとのラベル（見出しセルの文字列など）と、現在の値（ダッシュ数）、
        /// 「自動計算」チェックボックスの初期状態を受け取り、列数分の入力欄を動的に組み立てる。
        /// </summary>
        /// <param name="a_labels">各列のラベル（見出しセルの文字列。空なら「列N」を使う）。</param>
        /// <param name="a_currentDashCounts">各列の現在の値（ダッシュ数）。a_labelsと同じ順序・
        /// 同じ件数であること。</param>
        /// <param name="a_isAutoCalculated">「自動計算」チェックボックスの初期状態
        /// （BlockStyles.BuildColumnWidthDialogInfo参照）。</param>
        public ColumnWidthDialog(List<string> a_labels, List<int> a_currentDashCounts, bool a_isAutoCalculated)
        {
            InitializeComponent();

            m_suppressAutoUncheckFlg = true;

            var labelBrush = (Brush)FindResource("InkSoftBrush");
            for (int i = 0; i < a_labels.Count; i++)
            {
                string label = string.IsNullOrWhiteSpace(a_labels[i]) ? "列" + (i + 1) : a_labels[i];
                var labelBlock = new TextBlock
                {
                    Text = label,
                    Margin = new Thickness(0, 0 == i ? 0 : 10, 0, 4),
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = labelBrush
                };
                var box = new TextBox
                {
                    Text = a_currentDashCounts[i].ToString(),
                    Padding = new Thickness(6, 4, 6, 4)
                };
                box.TextChanged += ColumnWidthBoxTextChanged;
                m_columnsPanel.Children.Add(labelBlock);
                m_columnsPanel.Children.Add(box);
                m_boxes.Add(box);
            }

            m_autoCalculateCheckBox.IsChecked = a_isAutoCalculated;
            m_suppressAutoUncheckFlg = false;

            if (m_boxes.Count > 0)
            {
                m_boxes[0].Focus();
                m_boxes[0].SelectAll();
            }
        }

        /// <summary>入力欄がユーザーの手で変更されたら、「自動計算」のチェックを自動的に外す
        /// （コンストラクタでの初期値設定中はm_suppressAutoUncheckFlgで抑制する）。</summary>
        /// <param name="a_sender">変更された入力欄。</param>
        /// <param name="a_args">TextChangedイベント。</param>
        private void ColumnWidthBoxTextChanged(object a_sender, TextChangedEventArgs a_args)
        {
            if (!m_suppressAutoUncheckFlg)
            {
                m_autoCalculateCheckBox.IsChecked = false;
            }
        }

        /// <summary>「自動計算」がオンならDashCountsをすべて既定値にし、オフなら各入力欄の値を
        /// 検証・下限3・上限60でクランプしてDashCountsへ格納する（下限・上限はBlockStyles.
        /// TABLE_COLUMN_MIN_DASH_COUNT・TABLE_COLUMN_MAX_DASH_COUNTと一致させてある）。
        /// いずれの場合もOKで閉じる。</summary>
        /// <param name="a_sender">OKボタン。</param>
        /// <param name="a_args">Clickイベント。</param>
        private void OkClick(object a_sender, RoutedEventArgs a_args)
        {
            if (true == m_autoCalculateCheckBox.IsChecked)
            {
                DashCounts = Enumerable.Repeat(BlockStyles.TABLE_COLUMN_DEFAULT_DASH_COUNT, m_boxes.Count).ToList();
                DialogResult = true;
                return;
            }

            var result = new List<int>();
            foreach (var box in m_boxes)
            {
                if (!int.TryParse(box.Text, out int v) || v < BlockStyles.TABLE_COLUMN_MIN_DASH_COUNT)
                {
                    v = BlockStyles.TABLE_COLUMN_MIN_DASH_COUNT;
                }
                if (v > BlockStyles.TABLE_COLUMN_MAX_DASH_COUNT)
                {
                    v = BlockStyles.TABLE_COLUMN_MAX_DASH_COUNT;
                }
                result.Add(v);
            }
            DashCounts = result;
            DialogResult = true;
        }

        /// <summary>変更を適用せずにダイアログを閉じる。</summary>
        /// <param name="a_sender">キャンセルボタン。</param>
        /// <param name="a_args">Clickイベント。</param>
        private void CancelClick(object a_sender, RoutedEventArgs a_args)
        {
            DialogResult = false;
        }
    }
}
