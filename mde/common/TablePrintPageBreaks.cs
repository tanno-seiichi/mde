// TablePrintPageBreaks.cs
//
// mde (Markdown インラインエディタ) の一部。
// PDF出力の時に、表の行がページの境目で上下に分かれないよう、分かれる行の前で改ページする。
// また、表の途中から始まるページの先頭に、表の見出しの行を表示する（または、ページの先頭の行の上に線を引く）。

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
    /// 表が複数のページにまたがる時は、2ページ目以降の先頭にも見出しの行を表示する（何の列か分かるように。表の上の線も
    /// 見出しの行の上に引くため、表の始まりと同じ見た目になる）。TableChunks.BreakPageBeforeRowForPrint は、ページの
    /// 先頭に送る行の前に見出しの行の写しを入れるため、ページの先頭が行の始まり（行の途中で分かれていない）で、その行が
    /// 見出しの行でない時も、同じ処理でその行の前で改ページする。
    /// 見出しの行を表示しない設定（メニュー「ファイル」→「表のヘッダ行をページ毎に表示する」の ✓ を外した時）は、代わりに
    /// ページの先頭の行の上に線を引く。表の線は、各セルの右と下、表の上と左に引いているため、行の途中で分かれずに新しい
    /// ページで始まった行には、上の線が無い（線は前のページの最後の行の下にある）。そのため、ページの先頭が行の始まりで、
    /// その行の上に線が無い時も、同じ処理でその行の前で改ページし、上に線を引く。VSCode スタイルは表の外側の線を引かない
    /// ため、この時は上の線も引かない。
    /// 呼ぶのは、ページを書き出す前（印刷を始める前）にすること。書き出しながら（印刷の途中で）文書を変えると、WPF の
    /// 組み版（PTS）が内部の検査でプロセスを止める。
    /// </summary>
    public static class TablePrintPageBreaks
    {
        /// <summary>
        /// ページを先頭から順に組み立て、ページの境目で分かれる表の行の前で改ページする。表の途中から始まるページの先頭
        /// には、見出しの行を表示する（a_repeatHeaderFlg が false なら、ページの先頭の行の上に線を引く）。書き出しが
        /// 終わったら、戻り値の処理を呼んで元に戻すこと。ページの大きさは、呼ぶ前に a_paginator へ設定しておくこと。
        /// </summary>
        /// <param name="a_doc">文書。</param>
        /// <param name="a_paginator">文書のページ分けの仕組み（FlowDocument の DocumentPaginator）。</param>
        /// <param name="a_repeatHeaderFlg">表の途中から始まるページの先頭に、見出しの行を表示するなら true。</param>
        /// <param name="a_progress">進み具合を知らせる処理（調べ終わったページの数と、文書の中で調べ終わった位置の割合 0〜1）。</param>
        /// <returns>元に戻す処理。</returns>
        public static System.Action Apply(FlowDocument a_doc, DynamicDocumentPaginator a_paginator, bool a_repeatHeaderFlg,
            System.Action<int, double> a_progress)
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
                Scan(a_doc, a_paginator, a_repeatHeaderFlg, a_progress, restores);
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
        private static void Scan(FlowDocument a_doc, DynamicDocumentPaginator a_paginator, bool a_repeatHeaderFlg,
            System.Action<int, double> a_progress, List<System.Action> a_restores)
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
                TableRow row = FindRowAtPageStart(position);
                if (null == row || brokenRows.Contains(row))
                {
                    continue;
                }
                bool splitFlg = HasContentBefore(row, position);
                if (!splitFlg && (a_repeatHeaderFlg ? IsHeaderRow(row) : HasTopLine(row)))
                {
                    continue; // 表が見出しの行から始まるページ・上に線のある行から始まるページ（そのままでよい）
                }
                // ページの先頭が行の途中（行の一部が前のページにある）：その行の前で改ページし、見出しの行を入れて（または
                // 上に線を引いて）、同じページを調べ直す（一度改ページした行がまだ分かれる場合は、1ページに入りきらない行の
                // ため、そのままにする）。
                // ページの先頭が行の始まり（見出しの行以外。線を引く設定では、上に線の無い行）：その行の前で改ページし、
                // 見出しの行を入れる（または上に線を引く）。行はもとからこのページの先頭にあるため、調べ直さない。見出しの
                // 行・線の分だけ下へずれて、ページの最後の行が分かれた場合は、次のページを調べる時に直す
                brokenRows.Add(row);
                System.Action restore = TableChunks.BreakPageBeforeRowForPrint(row, a_repeatHeaderFlg);
                if (null != restore)
                {
                    a_restores.Add(restore);
                    if (splitFlg)
                    {
                        pageNumber--;
                    }
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

        /// <summary>
        /// ページの先頭の位置にある表の行を返す。位置が行の中に無くても、表・行グループ・行の始まりの直前（ページが表の
        /// かたまりの始まりから始まる場合等）なら、その後ろの最初の行を返す。
        /// </summary>
        /// <param name="a_position">ページの先頭の位置。</param>
        /// <returns>行。表の行が無ければ null。</returns>
        private static TableRow FindRowAtPageStart(TextPointer a_position)
        {
            TableRow row = FindRow(a_position);
            if (null != row)
            {
                return row;
            }
            for (TextPointer pointer = a_position;
                null != pointer && TextPointerContext.ElementStart == pointer.GetPointerContext(LogicalDirection.Forward);
                pointer = pointer.GetNextContextPosition(LogicalDirection.Forward))
            {
                DependencyObject element = pointer.GetAdjacentElement(LogicalDirection.Forward);
                if (element is TableRow found)
                {
                    return found;
                }
                if (!(element is Table) && !(element is TableRowGroup))
                {
                    return null; // 表ではないブロック（段落・箇条書き等）から始まるページ
                }
            }
            return null;
        }

        /// <summary>
        /// 行が表の見出しの行（またはその写し）か。見出しの行が分からない行も、見出しの行とみなす（見出しの行を入れない）。
        /// </summary>
        /// <param name="a_row">行。</param>
        /// <returns>見出しの行なら true。</returns>
        private static bool IsHeaderRow(TableRow a_row)
        {
            TableRow header = TableChunks.GetHeaderRow(a_row);
            return null == header || ReferenceEquals(header, a_row);
        }

        /// <summary>
        /// 行の上に線があるか（表の先頭の行等、かたまり（TableChunks）の最初の行で、かたまりの上に線がある場合）。
        /// VSCode スタイルは表の外側の線を引かないため、上の線が無くても、あるとみなす（線を足さない）。
        /// </summary>
        /// <param name="a_row">行。</param>
        /// <returns>線があれば（線を足さなくてよければ）true。</returns>
        private static bool HasTopLine(TableRow a_row)
        {
            if (BlockStyles.VsCodeStyleFlg)
            {
                return true;
            }
            Table chunk = TableChunks.GetChunkOfRow(a_row);
            if (null == chunk)
            {
                return true;
            }
            List<TableRow> rows = TableChunks.GetRows(new[] { chunk });
            return 0 < rows.Count && ReferenceEquals(rows[0], a_row) && 0 < chunk.BorderThickness.Top;
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
