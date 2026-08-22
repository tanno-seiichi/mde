// PdfMarginDialog.xaml.cs
//
// mde (MarkDown インラインエディタ) の一部。
// PDF書き出し時の、上下左右の余白（px）を編集するための小さなモーダルダイアログ。
// メニュー「ファイル」→「PDFの余白を設定…」から開く。

using System;
using System.Windows;

namespace mde
{
    /// <summary>PDF書き出し時の、上下左右の余白（px）を入力させ、範囲内へ丸めて返すダイアログ。</summary>
    public partial class PdfMarginDialog : Window
    {
        private const double DefaultTop = 64;
        private const double DefaultBottom = 64;
        private const double DefaultLeft = 80;
        private const double DefaultRight = 80;
        private const double MinMargin = 0;
        private const double MaxMargin = 300;

        /// <summary>OKで閉じた場合に確定した、上の余白（px）。</summary>
        public double MarginTop { get; private set; }

        /// <summary>OKで閉じた場合に確定した、下の余白（px）。</summary>
        public double MarginBottom { get; private set; }

        /// <summary>OKで閉じた場合に確定した、左の余白（px）。</summary>
        public double MarginLeft { get; private set; }

        /// <summary>OKで閉じた場合に確定した、右の余白（px）。</summary>
        public double MarginRight { get; private set; }

        /// <summary>
        /// ダイアログを作成する。各入力欄には、現在設定されている余白の値を初期表示する。
        /// </summary>
        /// <param name="a_top">現在の上余白（px）。</param>
        /// <param name="a_bottom">現在の下余白（px）。</param>
        /// <param name="a_left">現在の左余白（px）。</param>
        /// <param name="a_right">現在の右余白（px）。</param>
        public PdfMarginDialog(double a_top, double a_bottom, double a_left, double a_right)
        {
            InitializeComponent();
            m_topBox.Text = a_top.ToString("0");
            m_bottomBox.Text = a_bottom.ToString("0");
            m_leftBox.Text = a_left.ToString("0");
            m_rightBox.Text = a_right.ToString("0");
            m_topBox.Focus();
            m_topBox.SelectAll();
        }

        /// <summary>入力文字列を数値として解釈し、0〜300pxの範囲に丸める。数値として解釈できない
        /// 場合は、渡された既定値を使う。</summary>
        /// <param name="a_text">TextBoxの入力文字列。</param>
        /// <param name="a_defaultValue">解釈できなかった場合に使う既定値。</param>
        /// <returns>0〜300の範囲に収めた余白の値。</returns>
        private static double ParseMargin(string a_text, double a_defaultValue)
        {
            if (!double.TryParse(a_text, out double value))
            {
                value = a_defaultValue;
            }
            return Math.Max(MinMargin, Math.Min(MaxMargin, value));
        }

        /// <summary>4つの入力欄の値をそれぞれ検証・確定し、ダイアログを閉じる。</summary>
        /// <param name="a_sender">OKボタン。</param>
        /// <param name="a_args">Click event.</param>
        private void OkClick(object a_sender, RoutedEventArgs a_args)
        {
            MarginTop = ParseMargin(m_topBox.Text, DefaultTop);
            MarginBottom = ParseMargin(m_bottomBox.Text, DefaultBottom);
            MarginLeft = ParseMargin(m_leftBox.Text, DefaultLeft);
            MarginRight = ParseMargin(m_rightBox.Text, DefaultRight);
            DialogResult = true;
        }

        /// <summary>4つの入力欄を、いずれも既定値の表示に戻す（OKを押すまでは確定しない）。</summary>
        /// <param name="a_sender">「既定値に戻す」ボタン。</param>
        /// <param name="a_args">Click event.</param>
        private void ResetClick(object a_sender, RoutedEventArgs a_args)
        {
            m_topBox.Text = DefaultTop.ToString("0");
            m_bottomBox.Text = DefaultBottom.ToString("0");
            m_leftBox.Text = DefaultLeft.ToString("0");
            m_rightBox.Text = DefaultRight.ToString("0");
        }

        /// <summary>入力内容を破棄して、ダイアログを閉じる。</summary>
        /// <param name="a_sender">キャンセルボタン。</param>
        /// <param name="a_args">Click event.</param>
        private void CancelClick(object a_sender, RoutedEventArgs a_args)
        {
            DialogResult = false;
        }
    }
}
