// ZoomManager.cs
//
// mde (Markdown インラインエディタ) の一部。
// エディタの表示倍率（ズーム）の管理を担当するクラス。ツールバーの「－」「100%」「＋」
// ボタン、Ctrl+マウスホイールでのズーム操作、および現在の倍率に応じたエディタ・
// ソースエディタ・ツールバー表示の更新をまとめて扱う。
// MainWindow.xaml.csの「ズーム」区画をそのまま抽出したもの。XAMLのClick=／
// PreviewMouseWheel=は元のメソッド名（MainWindow側の薄いラッパー）を参照し続けるため、
// このクラス自身はXAMLから直接参照されない。
// MainWindow本体への参照は持たない。エディタ・ソースエディタ・ズーム倍率表示ボタンは、
// InitializeComponent()で一度構築されたあとは差し替わらないため、delegateではなく
// オブジェクト参照をそのままコンストラクタで受け取る。

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using mde.common;

namespace mde.manager
{
    /// <summary>
    /// エディタのズーム倍率を管理する。現在の倍率はZoomLevelプロパティで読み取れる
    /// （設定の保存時にMainWindowから参照される）。
    /// </summary>
    public class ZoomManager
    {
        private readonly RichTextBox m_editor;
        private readonly TextBox m_sourceEditor;
        private readonly Button m_zoomLabelBtn;

        /// <summary>選べるズーム倍率（小さい順。拡大・縮小はこの中の隣の倍率に移る）。
        /// 【任意の倍率にしない理由】倍率によっては（80%・60% 等）、WPF 標準のカーソル（幅 1px）も一緒に縮小されて
        /// 画面のピクセルの境目に来る位置（行の先頭等）で消えてしまうため、カーソルが消えないことを確かめた倍率だけにする。</summary>
        public static readonly double[] ZOOM_LEVELS = { 0.48, 0.57, 0.69, 0.83, 1.0, 1.2, 1.44, 1.72, 2.07, 2.48 };

        /// <summary>ズーム倍率の下限・上限（ZOOM_LEVELS の最小・最大。メニュー「表示」の拡大・縮小を選べるかの判定にも使う）。</summary>
        public const double MIN_ZOOM_LEVEL = 0.48;
        public const double MAX_ZOOM_LEVEL = 2.48;

        /// <summary>エディタに適用中のズーム倍率（1.0が100%）。</summary>
        private double m_zoomLevel;

        /// <summary>現在のズーム倍率（1.0が100%）。設定の保存時に参照される。</summary>
        public double ZoomLevel => m_zoomLevel;

        /// <param name="a_editor">ズーム対象のエディタ（RichTextBox）。</param>
        /// <param name="a_sourceEditor">ズーム対象のソースエディタ（TextBox）。</param>
        /// <param name="a_zoomLabelBtn">現在の倍率（パーセント）を表示するツールバーのボタン。</param>
        /// <param name="a_initialZoomLevel">起動時の初期倍率（設定ファイルから読み込んだ値）。
        /// この時点では見た目には反映されない。実際にエディタへ適用するには、他の初期化が
        /// 一通り終わったあとにApplyCurrentZoomLevelを呼ぶ必要がある（MainWindowのコンストラクタ
        /// 参照）。</param>
        public ZoomManager(RichTextBox a_editor, TextBox a_sourceEditor, Button a_zoomLabelBtn, double a_initialZoomLevel)
        {
            this.m_editor = a_editor;
            this.m_sourceEditor = a_sourceEditor;
            this.m_zoomLabelBtn = a_zoomLabelBtn;
            this.m_zoomLevel = a_initialZoomLevel;
        }

        /// <summary>起動時、初期倍率（コンストラクタで渡された値）をエディタへ実際に適用する。</summary>
        public void ApplyCurrentZoomLevel() => SetZoom(m_zoomLevel);

        public void ZoomInClick(object a_sender, RoutedEventArgs a_args) => ChangeZoom(NextZoomLevel(true));
        public void ZoomOutClick(object a_sender, RoutedEventArgs a_args) => ChangeZoom(NextZoomLevel(false));
        public void ZoomResetClick(object a_sender, RoutedEventArgs a_args) => ChangeZoom(1.0);

        /// <summary>利用者の操作で倍率を変える時に、倍率を変える処理（引数）を包んで実行する処理（MainWindow が設定する。
        /// 倍率を変えても編集ペインの一番上に見えている行が動かないよう、変える前後でスクロール位置を合わせる）。
        /// null なら倍率を変えるだけ。</summary>
        public Action<Action> ZoomChangeWrapper { get; set; }

        /// <summary>利用者の操作で倍率を変えた時に、今の倍率（引数。1.0が100%）を知らせる先（MainWindow が設定する。
        /// ウィンドウの中央に倍率を少しの間表示する）。null なら知らせない。</summary>
        public Action<double> ZoomLevelShown { get; set; }

        /// <summary>利用者の操作（拡大・縮小・100%に戻す・Ctrl+ホイール）で倍率を変え（ZoomChangeWrapper 経由）、
        /// 今の倍率を知らせる（ZoomLevelShown）。倍率が変わらない時（もう一番大きい・小さい時等）は、知らせるだけ。</summary>
        /// <param name="a_value">新しいズーム倍率（1.0が100%）。</param>
        private void ChangeZoom(double a_value)
        {
            if (SnapZoomLevel(a_value) != m_zoomLevel)
            {
                if (null == ZoomChangeWrapper)
                {
                    SetZoom(a_value);
                }
                else
                {
                    ZoomChangeWrapper(() => SetZoom(a_value));
                }
            }
            ZoomLevelShown?.Invoke(m_zoomLevel);
        }

        /// <summary>Ctrl+ホイールでエディタをズームする（スクロールの代わり）。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        public void EditorPreviewMouseWheel(object a_sender, MouseWheelEventArgs a_args)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                a_args.Handled = true;
                ChangeZoom(NextZoomLevel(a_args.Delta > 0));
            }
        }

        /// <summary>今の倍率の隣の倍率（ZOOM_LEVELS の中で、今より1つ大きい・小さいもの）を返す。端なら今の倍率のまま。</summary>
        /// <param name="a_zoomInFlg">true なら拡大（1つ大きい倍率）、false なら縮小（1つ小さい倍率）。</param>
        /// <returns>隣の倍率。</returns>
        private double NextZoomLevel(bool a_zoomInFlg)
        {
            int index = Array.IndexOf(ZOOM_LEVELS, SnapZoomLevel(m_zoomLevel));
            index = Math.Max(0, Math.Min(ZOOM_LEVELS.Length - 1, index + (a_zoomInFlg ? 1 : -1)));
            return ZOOM_LEVELS[index];
        }

        /// <summary>倍率を ZOOM_LEVELS の中で一番近いものにそろえる（以前の版で保存した 80% 等の倍率も、選べる倍率にする）。</summary>
        /// <param name="a_value">倍率（1.0が100%）。</param>
        /// <returns>ZOOM_LEVELS の中で一番近い倍率。</returns>
        private static double SnapZoomLevel(double a_value)
        {
            double nearest = 1.0;
            double nearestDiff = double.MaxValue;
            foreach (double level in ZOOM_LEVELS)
            {
                double diff = Math.Abs(level - a_value);
                if (diff < nearestDiff)
                {
                    nearest = level;
                    nearestDiff = diff;
                }
            }
            return nearest;
        }

        /// <summary>新しいズーム倍率を適用し、ツールバーのパーセント表示を更新する。</summary>
        /// <param name="a_value">新しいズーム倍率（1.0が100%）。ZOOM_LEVELS の中で一番近いものにそろえる。</param>
        public void SetZoom(double a_value)
        {
            m_zoomLevel = SnapZoomLevel(a_value);
            m_editor.LayoutTransform = new ScaleTransform(m_zoomLevel, m_zoomLevel);
            m_sourceEditor.FontSize = BlockStyles.BodyFontSize * m_zoomLevel;
            m_zoomLabelBtn.Content = Math.Round(m_zoomLevel * 100) + "%";
        }
    }
}
