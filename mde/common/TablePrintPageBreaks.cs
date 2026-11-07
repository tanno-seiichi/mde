// TablePrintPageBreaks.cs
//
// mde (Markdown インラインエディタ) の一部。
// PDF出力の時に、表の行がページの境目で上下に分かれないよう、分かれる行の前で改ページする。

using System.Collections.Generic;
using System.Windows;
using System.Windows.Documents;

namespace mde.common
{
    /// <summary>
    /// PDF出力の時に、表の行がページの境目で上下に分かれないようにする。
    /// WPF は、表の行がページの最後に入りきらない時、その行を列ごとに別々に分けて、残りを次のページへ送る。そのため、
    /// 前のページで書き終わった列は次のページで空のセルになり（行の中の段落の数が列によって違うと、前のページ側にも
    /// 空のセルができる）、セルが歯抜けに見えていた。WPF には行を分けない設定が無いため、ページを順に組み立てながら、
    /// ページの先頭が行の途中になっていたら、その行の前で改ページする（TableChunks.BreakPageBeforeRowForPrint）。
    /// 1ページに入りきらない高さの行は、改ページしても分かれるため、そのままにする。
    /// 呼ぶのは、ページを書き出す前（印刷を始める前）にすること。書き出しながら（印刷の途中で）文書を変えると、WPF の
    /// 組み版（PTS）が内部の検査でプロセスを止める。
    /// </summary>
    public static class TablePrintPageBreaks
    {
        /// <summary>
        /// ページを先頭から順に組み立て、ページの境目で分かれる表の行の前で改ページする。書き出しが終わったら、戻り値の
        /// 処理を呼んで元に戻すこと。ページの大きさは、呼ぶ前に a_paginator へ設定しておくこと。
        /// </summary>
        /// <param name="a_doc">文書。</param>
        /// <param name="a_paginator">文書のページ分けの仕組み（FlowDocument の DocumentPaginator）。</param>
        /// <param name="a_progress">進み具合を知らせる処理（調べ終わったページの数と、文書の中で調べ終わった位置の割合 0〜1）。</param>
        /// <returns>元に戻す処理。</returns>
        public static System.Action Apply(FlowDocument a_doc, DynamicDocumentPaginator a_paginator, System.Action<int, double> a_progress)
        {
            var restores = new List<System.Action>();
            System.Action restoreAll = () =>
            {
                for (int i = restores.Count - 1; 0 <= i; i--)
                {
                    restores[i]();
                }
            };
            try
            {
                Scan(a_doc, a_paginator, a_progress, restores);
            }
            catch
            {
                // 途中で失敗した場合も、それまでに改ページした行を元に戻してから知らせる
                restoreAll();
                throw;
            }
            return restoreAll;
        }

        /// <summary>Apply の本体。改ページした行を元に戻す処理を a_restores へ足していく。</summary>
        private static void Scan(FlowDocument a_doc, DynamicDocumentPaginator a_paginator, System.Action<int, double> a_progress,
            List<System.Action> a_restores)
        {
            var brokenRows = new HashSet<TableRow>();
            int docLength = System.Math.Max(1, a_doc.ContentStart.GetOffsetToPosition(a_doc.ContentEnd));
            // 1ページ目の先頭は、前のページが無いため調べない
            for (int pageNumber = 1; ; pageNumber++)
            {
                DocumentPage page = a_paginator.GetPage(pageNumber);
                if (null == page || DocumentPage.Missing == page)
                {
                    break;
                }
                TextPointer position = GetPageStart(a_paginator, page);
                // ページは、文書を変える前に破棄する（組み立てたページを残したまま文書を変えると、WPF の組み版（PTS）が
                // 内部の検査で止まることがある）
                page.Dispose();
                if (null == position)
                {
                    continue;
                }
                a_progress?.Invoke(pageNumber, System.Math.Min(1.0, (double)a_doc.ContentStart.GetOffsetToPosition(position) / docLength));
                TableRow row = FindRow(position);
                if (null == row || brokenRows.Contains(row) || !HasContentBefore(row, position))
                {
                    continue;
                }
                // ページの先頭が行の途中（行の一部が前のページにある）：その行の前で改ページして、同じページを調べ直す
                // （一度改ページした行がまだ分かれる場合は、1ページに入りきらない行のため、そのままにする）
                brokenRows.Add(row);
                System.Action restore = TableChunks.BreakPageBeforeRowForPrint(row);
                if (null != restore)
                {
                    a_restores.Add(restore);
                    pageNumber--;
                }
            }
        }

        /// <summary>ITextView（WPF の内部の型。ページに載っている文書の範囲を返す）。</summary>
        private static readonly System.Type s_textViewType = typeof(FlowDocument).Assembly.GetType("System.Windows.Documents.ITextView");

        /// <summary>
        /// ページの先頭（ページに載っている文書の範囲のうち、最も前の位置）を返す。DynamicDocumentPaginator.GetPagePosition は、
        /// ページの範囲の一覧（ITextView.TextSegments）の最初の範囲の始まりを返すが、表の行が分かれたページでは、一覧が文書の
        /// 順に並んでいないことがある（前のページから続く列の残りより、次の行の方が先に並ぶ）。その場合、ページの先頭が次の行の
        /// 始まりと判断され、分かれた行を見逃していたため、すべての範囲の始まりのうち最も前の位置を使う。範囲の一覧が取れない
        /// 場合は、GetPagePosition の位置を使う。
        /// </summary>
        /// <param name="a_paginator">文書のページ分けの仕組み。</param>
        /// <param name="a_page">ページ。</param>
        /// <returns>ページの先頭の位置。分からなければ null。</returns>
        private static TextPointer GetPageStart(DynamicDocumentPaginator a_paginator, DocumentPage a_page)
        {
            TextPointer start = a_paginator.GetPagePosition(a_page) as TextPointer;
            try
            {
                if (null != s_textViewType && a_page is System.IServiceProvider provider &&
                    provider.GetService(s_textViewType) is object textView &&
                    s_textViewType.GetProperty("TextSegments")?.GetValue(textView) is System.Collections.IEnumerable segments)
                {
                    foreach (object segment in segments)
                    {
                        if (segment?.GetType().GetProperty("Start", System.Reflection.BindingFlags.Instance |
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(segment) is TextPointer segmentStart &&
                            (null == start || 0 > segmentStart.CompareTo(start)))
                        {
                            start = segmentStart;
                        }
                    }
                }
            }
            catch
            {
                // 範囲の一覧が取れない場合は、GetPagePosition の位置を使う
            }
            return start;
        }

        /// <summary>位置を含む表の行を返す。</summary>
        /// <param name="a_position">位置。</param>
        /// <returns>行。表の中でなければ null。</returns>
        private static TableRow FindRow(TextPointer a_position)
        {
            for (DependencyObject node = a_position.Parent; null != node; node = (node as TextElement)?.Parent)
            {
                if (node is TableRow row)
                {
                    return row;
                }
            }
            return null;
        }

        /// <summary>
        /// 行の中の、位置より前に中身（文字・画像等、または書き終わったセル・段落）があるか（＝行の一部が前のページに
        /// あるか）。位置より前が、行・セル・段落の始まりだけなら、行はそのページから始まっている。
        /// </summary>
        /// <param name="a_row">行。</param>
        /// <param name="a_position">ページの先頭の位置。</param>
        /// <returns>中身があれば true。</returns>
        private static bool HasContentBefore(TableRow a_row, TextPointer a_position)
        {
            TextPointer pointer = a_row.ContentStart;
            while (null != pointer && 0 > pointer.CompareTo(a_position))
            {
                switch (pointer.GetPointerContext(LogicalDirection.Forward))
                {
                    case TextPointerContext.Text:
                    case TextPointerContext.EmbeddedElement:
                    case TextPointerContext.ElementEnd:
                        return true;
                    case TextPointerContext.None:
                        return false;
                }
                pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
            }
            return false;
        }
    }
}
