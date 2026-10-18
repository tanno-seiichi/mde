// LinkHoverUnderline.cs
//
// mde (Markdown インラインエディタ) の一部。
// VSCodeスタイルの時、リンクにマウスを乗せている間だけ、リンクの文字に下線を表示する（mdew の VSCodeスタイルと同じ。
// VSCodeスタイルのリンクは、ふだんは下線なし。BlockStyles.LinkTextDecorations 参照）。

using mde.common;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace mde.editor
{
    /// <summary>
    /// リンクにマウスを乗せている間だけ下線を表示する。
    /// 下線は、リンクの Run の TextDecorations を変えるのではなく、エディタの上に重ねた描画（Adorner）で引く。
    /// 文書の中の要素の値を変えると、RichTextBox の「元に戻す」の履歴に記録されてしまい（マウスを動かすたびに
    /// 履歴が増え、Ctrl+Z で編集ではなく下線の表示が戻ってしまう）、未保存の印も付いてしまうため。
    /// 下線の位置は、文字の位置（TextPointer.GetCharacterRect）とフォントの寸法から求める。スクロール・編集・
    /// サイズの変更をしたら、いったん消す（次にマウスを動かした時に引き直す）。
    /// </summary>
    public class LinkHoverUnderline
    {
        private readonly RichTextBox m_editor;
        private UnderlineAdorner m_adorner;
        private Run m_hoverRun;
        /// <summary>今引いている下線の、各行の文字の範囲（マウスがリンクの上から外れたかの判定に使う）。</summary>
        private readonly List<Rect> m_hoverLineRects = new List<Rect>();
        private bool m_enabledFlg;

        /// <param name="a_editor">対象のエディタ。</param>
        public LinkHoverUnderline(RichTextBox a_editor)
        {
            m_editor = a_editor;
            m_editor.MouseMove += EditorMouseMove;
            m_editor.MouseLeave += (s, e) => Clear();
            m_editor.TextChanged += (s, e) => Clear();
            m_editor.SizeChanged += (s, e) => Clear();
            m_editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((s, e) => Clear()));
        }

        /// <summary>マウスを乗せた時に下線を表示するか（VSCodeスタイルの時だけ true。MainWindow.ApplyDocStyle が設定する）。</summary>
        public bool Enabled
        {
            get => m_enabledFlg;
            set
            {
                m_enabledFlg = value;
                if (!value)
                {
                    Clear();
                }
            }
        }

        /// <summary>マウスを動かした時：マウスの下のリンクに下線を引く（リンクから外れたら消す）。</summary>
        /// <param name="a_sender">エディタ。</param>
        /// <param name="a_args">マウスのイベントの引数。</param>
        private void EditorMouseMove(object a_sender, MouseEventArgs a_args)
        {
            if (!m_enabledFlg)
            {
                return;
            }
            Point point = a_args.GetPosition(m_editor);
            if (null != m_hoverRun && IsOverLines(point))
            {
                return;
            }
            Run run = null;
            List<Rect> lineRects = null;
            TextPointer position = m_editor.GetPositionFromPoint(point, false);
            if (null != position && position.Parent is Run candidate && candidate.Tag is LinkInfo)
            {
                lineRects = MeasureLines(candidate);
                if (IsOver(lineRects, point))
                {
                    run = candidate;
                }
            }
            if (null == run)
            {
                Clear();
                return;
            }
            m_hoverRun = run;
            m_hoverLineRects.Clear();
            m_hoverLineRects.AddRange(lineRects);
            Show(run, lineRects);
        }

        /// <summary>a_point が、今下線を引いているリンクの文字の上か。</summary>
        /// <param name="a_point">エディタの座標。</param>
        /// <returns>リンクの文字の上なら true。</returns>
        private bool IsOverLines(Point a_point) => IsOver(m_hoverLineRects, a_point);

        private static bool IsOver(List<Rect> a_lineRects, Point a_point)
        {
            foreach (Rect r in a_lineRects)
            {
                if (r.Contains(a_point))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>リンクの Run の文字が並んでいる範囲を、行ごとに求める（行の途中で折り返している場合は複数）。</summary>
        /// <param name="a_run">リンクの Run。</param>
        /// <returns>行ごとの範囲（エディタの座標。高さは文字の位置の高さ）。</returns>
        private static List<Rect> MeasureLines(Run a_run)
        {
            var lines = new List<Rect>();
            TextPointer end = a_run.ContentEnd;
            TextPointer p = a_run.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
            while (null != p && p.CompareTo(end) < 0)
            {
                TextPointer next = p.GetNextInsertionPosition(LogicalDirection.Forward);
                if (null == next || next.CompareTo(end) > 0)
                {
                    next = end;
                }
                Rect start = p.GetCharacterRect(LogicalDirection.Forward);
                Rect stop = next.GetCharacterRect(LogicalDirection.Backward);
                if (start.IsEmpty || stop.IsEmpty)
                {
                    break;
                }
                // 文字の右端（次の位置の、手前の文字側の端）。行が変わった場合は、文字の左端で代用する
                double right = Math.Abs(stop.Top - start.Top) < 0.5 ? stop.Left : start.Left;
                int last = lines.Count - 1;
                if (0 <= last && Math.Abs(lines[last].Top - start.Top) < 0.5)
                {
                    Rect line = lines[last];
                    double left = Math.Min(line.Left, start.Left);
                    lines[last] = new Rect(left, line.Top, Math.Max(line.Right, right) - left, line.Height);
                }
                else
                {
                    lines.Add(new Rect(Math.Min(start.Left, right), start.Top, Math.Abs(right - start.Left), start.Height));
                }
                if (0 == next.CompareTo(end))
                {
                    break;
                }
                p = next;
            }
            return lines;
        }

        /// <summary>下線を表示する。</summary>
        /// <param name="a_run">リンクの Run（文字の色・フォントを使う）。</param>
        /// <param name="a_lineRects">行ごとの文字の範囲（MeasureLines）。</param>
        private void Show(Run a_run, List<Rect> a_lineRects)
        {
            AdornerLayer layer = AdornerLayer.GetAdornerLayer(m_editor);
            if (null == layer)
            {
                return;
            }
            if (null == m_adorner)
            {
                m_adorner = new UnderlineAdorner(m_editor);
                layer.Add(m_adorner);
            }
            // 下線の位置：文字の基準線（ベースライン）の少し下（フォントが決めている下線の位置）
            FontFamily fontFamily = a_run.FontFamily;
            double fontSize = a_run.FontSize;
            var typeface = new Typeface(fontFamily, a_run.FontStyle, a_run.FontWeight, a_run.FontStretch);
            double cellHeight = fontFamily.LineSpacing * fontSize;
            double underlineOffset = -typeface.UnderlinePosition * fontSize;
            if (underlineOffset <= 0)
            {
                underlineOffset = 0.1 * fontSize;
            }
            m_adorner.Rects.Clear();
            foreach (Rect r in a_lineRects)
            {
                // GetCharacterRect の高さが文字の高さなら、上端から基準線までは Baseline、行の高さ（LineHeight を指定した
                // 段落）なら、行の高さを同じ比率で分けた位置を基準線とする
                double baseline = Math.Abs(r.Height - cellHeight) < 1.5
                    ? r.Top + fontFamily.Baseline * fontSize
                    : r.Top + r.Height * (fontFamily.Baseline / fontFamily.LineSpacing);
                double y = Math.Round(baseline + underlineOffset);
                m_adorner.Rects.Add(new Rect(r.Left, y, r.Width, 1));
            }
            m_adorner.Brush = a_run.Foreground;
            m_adorner.InvalidateVisual();
        }

        /// <summary>下線を消す。</summary>
        private void Clear()
        {
            m_hoverRun = null;
            m_hoverLineRects.Clear();
            if (null != m_adorner && 0 < m_adorner.Rects.Count)
            {
                m_adorner.Rects.Clear();
                m_adorner.InvalidateVisual();
            }
        }

        /// <summary>エディタの上に下線を描く Adorner。マウスの操作は受け取らない。</summary>
        private class UnderlineAdorner : Adorner
        {
            public readonly List<Rect> Rects = new List<Rect>();
            public Brush Brush;

            public UnderlineAdorner(UIElement a_adornedElement) : base(a_adornedElement)
            {
                IsHitTestVisible = false;
                SnapsToDevicePixels = true;
            }

            protected override void OnRender(DrawingContext a_drawingContext)
            {
                if (null == Brush)
                {
                    return;
                }
                foreach (Rect r in Rects)
                {
                    a_drawingContext.DrawRectangle(Brush, null, r);
                }
            }
        }
    }
}
