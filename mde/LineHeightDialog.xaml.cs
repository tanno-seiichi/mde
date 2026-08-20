// LineHeightDialog.xaml.cs
//
// mde (MarkDown インラインエディタ) の一部。
// エディタの行間（Paragraphの行の高さ）を設定するための小さなモーダルダイアログ。
// スライダーと数値入力を同期させ、値が変わるたびにプレビュー用のコールバックを呼ぶことで、
// ダイアログを開いたまま実際の見た目を確認しながら調整できるようにしている。

using System;
using System.Windows;

namespace mde
{
    /// <summary>行間の値を編集し、変更のたびにライブプレビュー用のコールバックを呼ぶダイアログ。</summary>
    public partial class LineHeightDialog : Window
    {
        private const double DefaultLineHeight = 26;
        private readonly double m_originalValue;
        private readonly Action<double> m_onPreview;
        private bool m_updatingFlg;

        /// <summary>OKで閉じた場合に確定した行間の値。</summary>
        public double LineHeightValue { get; private set; }

        /// <summary>
        /// ダイアログを作成する。
        /// </summary>
        /// <param name="a_initialValue">現在の行間の値（ダイアログを開いた時点の値）。</param>
        /// <param name="a_onPreview">値が変わるたびに呼ばれる、ライブプレビュー用のコールバック。</param>
        public LineHeightDialog(double a_initialValue, Action<double> a_onPreview)
        {
            InitializeComponent();
            m_originalValue = a_initialValue;
            m_onPreview = a_onPreview;

            // Minimum/Maximumの設定に伴う暗黙のValueChanged発火（InitializeComponent実行中に
            // 既定値0が範囲外になることで起こる）が、まだ準備の整っていないこのコンストラクタの
            // 処理と衝突しないよう、初期値の設定が終わってからイベントハンドラを登録する。
            m_updatingFlg = true;
            m_slider.Value = a_initialValue;
            m_valueBox.Text = a_initialValue.ToString("0");
            m_updatingFlg = false;

            m_slider.ValueChanged += SliderValueChanged;
            m_valueBox.TextChanged += ValueBoxTextChanged;
        }

        /// <summary>スライダー・テキストボックス・呼び出し元のプレビューを、二重更新を避けながら
        /// すべて指定した値に合わせる。</summary>
        /// <param name="a_value">設定したい行間の値。</param>
        private void SetValue(double a_value)
        {
            m_updatingFlg = true;
            m_slider.Value = a_value;
            m_valueBox.Text = a_value.ToString("0");
            m_updatingFlg = false;
            m_onPreview?.Invoke(a_value);
        }

        /// <summary>スライダーの値が変わった時に、テキストボックス・プレビューへ反映する。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        private void SliderValueChanged(object a_sender, RoutedPropertyChangedEventArgs<double> a_args)
        {
            if (m_updatingFlg)
            {
                return;
            }
            SetValue(Math.Round(a_args.NewValue));
        }

        /// <summary>テキストボックスの値が変わった時に、スライダー・プレビューへ反映する。
        /// 数値として解釈できない入力中は、確定するまで何もしない。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        private void ValueBoxTextChanged(object a_sender, System.Windows.Controls.TextChangedEventArgs a_args)
        {
            if (m_updatingFlg)
            {
                return;
            }
            if (double.TryParse(m_valueBox.Text, out double value))
            {
                value = Math.Max(m_slider.Minimum, Math.Min(m_slider.Maximum, value));
                m_updatingFlg = true;
                m_slider.Value = value;
                m_updatingFlg = false;
                m_onPreview?.Invoke(value);
            }
        }

        /// <summary>行間を標準値に戻す。</summary>
        /// <param name="a_sender">「既定値に戻す」ボタン。</param>
        /// <param name="a_args">Click event.</param>
        private void ResetClick(object a_sender, RoutedEventArgs a_args)
        {
            SetValue(DefaultLineHeight);
        }

        /// <summary>現在の値を確定し、ダイアログを閉じる。</summary>
        /// <param name="a_sender">OKボタン。</param>
        /// <param name="a_args">Click event.</param>
        private void OkClick(object a_sender, RoutedEventArgs a_args)
        {
            LineHeightValue = m_slider.Value;
            DialogResult = true;
        }

        /// <summary>プレビュー中の変更を元の値へ戻してから、ダイアログを閉じる。</summary>
        /// <param name="a_sender">キャンセルボタン。</param>
        /// <param name="a_args">Click event.</param>
        private void CancelClick(object a_sender, RoutedEventArgs a_args)
        {
            m_onPreview?.Invoke(m_originalValue);
            DialogResult = false;
        }
    }
}
