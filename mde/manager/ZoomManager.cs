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

        public void ZoomInClick(object a_sender, RoutedEventArgs a_args) => SetZoom(m_zoomLevel + 0.1);
        public void ZoomOutClick(object a_sender, RoutedEventArgs a_args) => SetZoom(m_zoomLevel - 0.1);
        public void ZoomResetClick(object a_sender, RoutedEventArgs a_args) => SetZoom(1.0);

        /// <summary>Ctrl+ホイールでエディタをズームする（スクロールの代わり）。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        public void EditorPreviewMouseWheel(object a_sender, MouseWheelEventArgs a_args)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                a_args.Handled = true;
                SetZoom(m_zoomLevel + (a_args.Delta > 0 ? 0.1 : -0.1));
            }
        }

        /// <summary>新しいズーム倍率を適用し、ツールバーのパーセント表示を更新する。</summary>
        /// <param name="a_value">新しいズーム倍率（1.0が100%）。妥当な範囲に丸められる。</param>
        public void SetZoom(double a_value)
        {
            m_zoomLevel = Math.Max(0.5, Math.Min(2.5, Math.Round(a_value, 2)));
            m_editor.LayoutTransform = new ScaleTransform(m_zoomLevel, m_zoomLevel);
            m_sourceEditor.FontSize = 16 * m_zoomLevel;
            m_zoomLabelBtn.Content = Math.Round(m_zoomLevel * 100) + "%";
        }
    }
}
