// TableChunks.cs
//
// mde (Markdown インラインエディタ) の一部。
// 大きな表を、内部では複数の Table（かたまり）に分けて持ち、見た目・保存・編集の操作では1つの表として扱うための処理。

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace mde.common
{
    /// <summary>
    /// 大きな表の2つ目以降のかたまり（Table）の Tag に設定する印（中身を持たない目印。CodeBlockContinuationInfo 等と
    /// 同種）。この印を持つ Table は、直前の Table の続き（同じ表の行の続き）として扱う（TableChunks 参照）。
    /// Tag は元に戻す（Undo）で表が作り直された時も引き継がれる（ローカル値として保存されるため）。
    /// </summary>
    public class TableContinuationInfo
    {
    }

    /// <summary>
    /// 大きな表のかたまり分け。
    /// WPF の RichTextBox は、表のセルの中で1文字打つたびに、その表全体を組み直す（表の行を一部だけ組み直す仕組みが無い）。
    /// 数百行の表では、1文字ごとに 0.4〜0.5 秒止まっていた。そこで、行の多い表は、内部では CHUNK_ROWS 行ずつの複数の
    /// Table（かたまり）に分ける（組み直されるのは、文字を打ったかたまりだけになる）。
    /// ・1つ目のかたまり（先頭）が見出しの行を持ち、2つ目以降のかたまりは Tag に TableContinuationInfo を持つ。
    /// ・見た目は1つの表と同じにする：列の幅はすべてのかたまりで同じ（表全体の内容から計算して、先頭の値を写す）、
    /// 　かたまりの間は余白なし（最後のかたまり以外は下の余白を 0 にする）、続きのかたまりは表の上の線を引かない。
    /// 　罫線は、表全体での行の位置から決める（BlockStyles.ApplyTableCellBorders。かたまりの最後の行の下の線は、
    /// 　そのかたまり自身の下の線として引く）。
    /// ・保存（MarkdownConverter.TableToMarkdown）・コピー・行や列の挿入と削除・キャレットの移動等は、すべてのかたまりの
    /// 　行をまとめて1つの表として扱う（GetRows）。
    /// </summary>
    public static class TableChunks
    {
        /// <summary>1つのかたまりの行数（分ける時の目安）。</summary>
        public const int CHUNK_ROWS = 20;

        /// <summary>この行数（見出しの行を含む）を超える表を、かたまりに分ける。</summary>
        public const int SPLIT_THRESHOLD_ROWS = 40;

        /// <summary>行を足してかたまりがこの行数を超えたら、そのかたまりを分け直す（Rebalance）。</summary>
        public const int MAX_CHUNK_ROWS = 40;

        /// <summary>表のかたまりが、直前の Table の続き（2つ目以降のかたまり）か。印（TableContinuationInfo）が
        /// あっても、直前が表でなければ（先頭のかたまりが消された等）、続きではなく表の先頭として扱う。</summary>
        /// <param name="a_block">調べるブロック。</param>
        /// <returns>続きのかたまりなら true。</returns>
        public static bool IsContinuation(Block a_block)
        {
            return a_block is Table table && table.Tag is TableContinuationInfo && table.PreviousBlock is Table;
        }

        /// <summary>表の先頭のかたまりを返す（a_table が先頭なら a_table 自身）。</summary>
        /// <param name="a_table">表のいずれかのかたまり。</param>
        /// <returns>先頭のかたまり。a_table が null なら null。</returns>
        public static Table GetHead(Table a_table)
        {
            Table table = a_table;
            while (null != table && IsContinuation(table))
            {
                table = (Table)table.PreviousBlock;
            }
            return table;
        }

        /// <summary>表のすべてのかたまりを、先頭から順に返す（分けていない表なら、その表だけ）。</summary>
        /// <param name="a_table">表のいずれかのかたまり。</param>
        /// <returns>かたまりの並び。</returns>
        public static List<Table> GetChunks(Table a_table)
        {
            var chunks = new List<Table>();
            Table head = GetHead(a_table);
            if (null == head)
            {
                return chunks;
            }
            chunks.Add(head);
            Block next = head.NextBlock;
            while (next is Table nextTable && nextTable.Tag is TableContinuationInfo)
            {
                chunks.Add(nextTable);
                next = nextTable.NextBlock;
            }
            return chunks;
        }

        /// <summary>表の最後のかたまりを返す。</summary>
        /// <param name="a_table">表のいずれかのかたまり。</param>
        /// <returns>最後のかたまり。</returns>
        public static Table GetLastChunk(Table a_table)
        {
            Table last = GetHead(a_table);
            while (last?.NextBlock is Table nextTable && nextTable.Tag is TableContinuationInfo)
            {
                last = nextTable;
            }
            return last;
        }

        /// <summary>表のすべてのかたまりの行を、先頭から順に返す（1つの表としての行の並び）。</summary>
        /// <param name="a_table">表のいずれかのかたまり。</param>
        /// <returns>行の並び。</returns>
        public static List<TableRow> GetRows(Table a_table)
        {
            return GetRows(GetChunks(a_table));
        }

        /// <summary>かたまりの並びの行を、先頭から順に返す。</summary>
        /// <param name="a_chunks">かたまりの並び。</param>
        /// <returns>行の並び。</returns>
        public static List<TableRow> GetRows(IEnumerable<Table> a_chunks)
        {
            var rows = new List<TableRow>();
            foreach (Table chunk in a_chunks)
            {
                foreach (TableRowGroup rg in chunk.RowGroups)
                {
                    rows.AddRange(rg.Rows);
                }
            }
            return rows;
        }

        /// <summary>
        /// 行が属する表の見出しの行（表の先頭のかたまりの最初の行）を返す。PDF出力の間に、改ページのための空の段落
        /// （BreakPageBeforeRowForPrint）の後ろへ移したかたまりは、表の先頭として扱われるが、その最初の行には見出しの行の
        /// 写しを入れているため、その写しを返す（見た目は同じ）。
        /// </summary>
        /// <param name="a_row">行。</param>
        /// <returns>見出しの行。表の中の行でなければ null。</returns>
        public static TableRow GetHeaderRow(TableRow a_row)
        {
            Table head = GetHead(GetChunkOfRow(a_row));
            if (null == head)
            {
                return null;
            }
            List<TableRow> rows = GetRows(new[] { head });
            return 0 < rows.Count ? rows[0] : null;
        }

        /// <summary>セルが属する表のかたまり（Table）を返す。</summary>
        /// <param name="a_cell">セル。</param>
        /// <returns>かたまり。表の中のセルでなければ null。</returns>
        public static Table GetChunkOfCell(TableCell a_cell)
        {
            return (a_cell?.Parent as TableRow)?.Parent is TableRowGroup rg ? rg.Parent as Table : null;
        }

        /// <summary>行が属する表のかたまり（Table）を返す。</summary>
        /// <param name="a_row">行。</param>
        /// <returns>かたまり。表の中の行でなければ null。</returns>
        public static Table GetChunkOfRow(TableRow a_row)
        {
            return (a_row?.Parent as TableRowGroup)?.Parent as Table;
        }

        /// <summary>かたまりの行数。</summary>
        /// <param name="a_chunk">かたまり。</param>
        /// <returns>行数。</returns>
        public static int CountRows(Table a_chunk)
        {
            int count = 0;
            foreach (TableRowGroup rg in a_chunk.RowGroups)
            {
                count += rg.Rows.Count;
            }
            return count;
        }

        /// <summary>
        /// 組み立てた表（まだ文書に入れていない、1つの Table）が大きければ、かたまりに分ける。先頭の CHUNK_ROWS 行は
        /// a_table に残し、残りの行を新しい続きのかたまりへ移す。列の幅・罫線・余白も1つの表と同じ見た目になるようにする
        /// （呼び出し側は、列の幅と罫線を a_table に設定し終えてから呼ぶこと。最後のかたまりの下の余白は、文書に入れた後に
        /// ApplyChunkFrame で決まる）。
        /// </summary>
        /// <param name="a_table">組み立てた表。</param>
        /// <returns>かたまりの並び（分けなかった場合は a_table だけ）。この順に文書へ入れること。</returns>
        public static List<Table> SplitIfLarge(Table a_table)
        {
            var chunks = new List<Table> { a_table };
            if (CountRows(a_table) <= SPLIT_THRESHOLD_ROWS)
            {
                return chunks;
            }
            List<TableRowGroup> groups = MoveRowsIntoNewGroups(a_table);
            a_table.RowGroups.Add(groups[0]);
            for (int i = 1; i < groups.Count; i++)
            {
                Table chunk = CreateContinuationChunk(a_table);
                chunk.RowGroups.Add(groups[i]);
                chunks.Add(chunk);
            }
            ApplyBorders(chunks);
            return chunks;
        }

        /// <summary>
        /// 行を足したかたまりが大きくなり過ぎた（MAX_CHUNK_ROWS を超えた）時、そのかたまりを分け直す（表の行を
        /// 足し続けると、1つのかたまりが大きくなって、また入力が遅くなるため）。分けていない小さな表が大きくなった場合も、
        /// ここでかたまりに分ける。文書の中の表に対して呼ぶこと。
        /// </summary>
        /// <param name="a_chunk">行を足したかたまり。</param>
        public static void RebalanceIfLarge(Table a_chunk)
        {
            if (null == a_chunk || !(a_chunk.Parent is FlowDocument doc) || CountRows(a_chunk) <= MAX_CHUNK_ROWS)
            {
                return;
            }
            // 行は、すべて新しい行グループへ入れ直す（行グループの途中の行を取り出さないため。MoveRowsIntoNewGroups 参照）
            List<TableRowGroup> groups = MoveRowsIntoNewGroups(a_chunk);
            a_chunk.RowGroups.Add(groups[0]);
            Block insertAfter = a_chunk;
            for (int i = 1; i < groups.Count; i++)
            {
                Table chunk = CreateContinuationChunk(a_chunk);
                chunk.RowGroups.Add(groups[i]);
                doc.Blocks.InsertAfter(insertAfter, chunk);
                insertAfter = chunk;
            }
            ApplyBorders(GetChunks(a_chunk));
        }

        /// <summary>
        /// かたまりの行が無くなった時の後始末（行の削除の後に呼ぶ）。表のかたまりが他にあれば、空になったかたまりを
        /// 取り除く。先頭のかたまりが空になった場合は、次のかたまりの行を先頭へ移して、次のかたまりを取り除く（先頭の
        /// かたまりには、元の文字の記録・前の空行の数・直前のコメント等が結び付いているため、先頭のかたまり自体は残す）。
        /// </summary>
        /// <param name="a_chunk">行を削除したかたまり。</param>
        /// <returns>後始末をした後の表の先頭のかたまり。表が丸ごと無くなった場合は null。</returns>
        public static Table RemoveIfEmpty(Table a_chunk)
        {
            if (null == a_chunk || !(a_chunk.Parent is FlowDocument doc))
            {
                return a_chunk;
            }
            Table head = GetHead(a_chunk);
            if (0 < CountRows(a_chunk))
            {
                return head;
            }
            var chunks = GetChunks(a_chunk);
            if (1 == chunks.Count)
            {
                doc.Blocks.Remove(a_chunk);
                return null;
            }
            if (ReferenceEquals(a_chunk, head))
            {
                Table next = chunks[1];
                TableRowGroup headGroup = head.RowGroups.FirstOrDefault();
                if (null == headGroup)
                {
                    headGroup = new TableRowGroup();
                    head.RowGroups.Add(headGroup);
                }
                // 行は、行グループの先頭から順に取り出して移す（MoveRowsIntoNewGroups 参照）
                foreach (TableRowGroup source in next.RowGroups.ToList())
                {
                    while (0 < source.Rows.Count)
                    {
                        TableRow row = source.Rows[0];
                        source.Rows.Remove(row);
                        headGroup.Rows.Add(row);
                    }
                }
                doc.Blocks.Remove(next);
            }
            else
            {
                doc.Blocks.Remove(a_chunk);
            }
            ApplyBorders(GetChunks(head));
            return head;
        }

        /// <summary>表のすべてのかたまりを文書から取り除く。</summary>
        /// <param name="a_table">表のいずれかのかたまり。</param>
        public static void RemoveAll(Table a_table)
        {
            foreach (Table chunk in GetChunks(a_table))
            {
                (chunk.Parent as FlowDocument)?.Blocks.Remove(chunk);
            }
        }

        /// <summary>罫線とかたまりの間の見た目を設定し直す（BlockStyles.ApplyTableCellBorders。GitHub スタイルの罫線の色は、
        /// 先頭のかたまりの線の色）。</summary>
        /// <param name="a_chunks">かたまりの並び（先頭から順）。</param>
        public static void ApplyBorders(IList<Table> a_chunks)
        {
            if (null == a_chunks || 0 == a_chunks.Count)
            {
                return;
            }
            BlockStyles.ApplyTableCellBorders(a_chunks, a_chunks[0].BorderBrush ?? BlockStyles.TableCellBorderBrush);
        }

        /// <summary>
        /// かたまりの間の見た目（余白・列の幅）を、1つの表と同じになるよう設定する（罫線は BlockStyles.ApplyTableCellBorders が
        /// 設定し、最後にこれを呼ぶ）。
        /// ・余白：最後のかたまり以外は 0（かたまりの間に隙間を作らない）。最後のかたまりは既定（スタイル）のまま。ただし、
        /// 　WPF は1つの表の後ろに、その表の前のブロックの下の余白を持ち越す（表の下の余白と比べて大きい方を空ける。
        /// 　MarginCollapsingState.CollapseBottomMargin）。かたまりに分けると、それが最後のかたまりまで届かなくなるため、
        /// 　前のブロックの下の余白の方が大きければ、最後のかたまりの下の余白をその値にする（見出しの直後の表等）。
        /// ・列の幅：先頭のかたまりと同じにする（SyncColumns）。
        /// </summary>
        /// <param name="a_chunks">かたまりの並び（先頭から順）。</param>
        public static void ApplyChunkFrame(IList<Table> a_chunks)
        {
            if (null == a_chunks || 0 == a_chunks.Count)
            {
                return;
            }
            for (int i = 0; i < a_chunks.Count - 1; i++)
            {
                SetIfDifferent(a_chunks[i], Block.MarginProperty, new Thickness(0));
            }
            Table last = a_chunks[a_chunks.Count - 1];
            double? lastBottom = ExpectedLastChunkBottomMargin(a_chunks);
            if (lastBottom.HasValue)
            {
                SetIfDifferent(last, Block.MarginProperty, new Thickness(0, 0, 0, lastBottom.Value));
            }
            else if (DependencyProperty.UnsetValue != last.ReadLocalValue(Block.MarginProperty))
            {
                last.ClearValue(Block.MarginProperty);
            }
            SyncColumns(a_chunks);
        }

        /// <summary>最後のかたまりに設定する下の余白（ApplyChunkFrame 参照）。既定（スタイル）のままでよい時は null。</summary>
        /// <param name="a_chunks">かたまりの並び（先頭から順）。</param>
        /// <returns>下の余白。既定のままでよい時は null。</returns>
        private static double? ExpectedLastChunkBottomMargin(IList<Table> a_chunks)
        {
            if (a_chunks.Count < 2 || !(a_chunks[0].Parent is FlowDocument))
            {
                return null;
            }
            double before = MarginCarriedAfter(a_chunks[0].PreviousBlock);
            return before > BlockStyles.ListBottomSpacing + 0.001 ? before : (double?)null;
        }

        /// <summary>
        /// ブロックの後ろに WPF が持ち越す下の余白（次のブロックの上の余白と比べて、大きい方が空く）。段落はその段落の下の
        /// 余白。下に線も Padding も無い表・リストは、中や前から持ち越した余白と自身の下の余白の大きい方（WPF の
        /// MarginCollapsingState と同じ考え方）。
        /// </summary>
        /// <param name="a_block">ブロック（null なら 0）。</param>
        /// <returns>余白（px）。</returns>
        private static double MarginCarriedAfter(Block a_block)
        {
            if (null == a_block)
            {
                return 0;
            }
            double own = Positive(a_block.Margin.Bottom);
            if (0 < a_block.BorderThickness.Bottom || 0 < a_block.Padding.Bottom)
            {
                return own;
            }
            if (a_block is Table table)
            {
                // 分けた表の最後のかたまりは、前のかたまり（下に線がある）から何も持ち越さない
                return IsContinuation(table) ? own : System.Math.Max(own, MarginCarriedAfter(table.PreviousBlock));
            }
            if (a_block is List list && list.ListItems.LastListItem is ListItem item)
            {
                double inner = Positive(item.Margin.Bottom);
                if (0 == item.BorderThickness.Bottom && 0 == item.Padding.Bottom)
                {
                    inner = System.Math.Max(inner, MarginCarriedAfter(item.Blocks.LastBlock));
                }
                return System.Math.Max(own, inner);
            }
            return own;
        }

        /// <summary>正の値ならその値、それ以外（負・NaN）は 0。</summary>
        private static double Positive(double a_value)
        {
            return 0 < a_value ? a_value : 0;
        }

        /// <summary>続きのかたまりの列（数と幅）を、先頭のかたまりと同じにする。</summary>
        /// <param name="a_table">表のいずれかのかたまり。</param>
        public static void SyncColumns(Table a_table)
        {
            SyncColumns(GetChunks(a_table));
        }

        /// <summary>続きのかたまりの列（数と幅）を、先頭のかたまりと同じにする。</summary>
        /// <param name="a_chunks">かたまりの並び（先頭から順）。</param>
        public static void SyncColumns(IList<Table> a_chunks)
        {
            if (null == a_chunks || a_chunks.Count < 2)
            {
                return;
            }
            Table head = a_chunks[0];
            for (int i = 1; i < a_chunks.Count; i++)
            {
                TableColumnCollection columns = a_chunks[i].Columns;
                while (columns.Count > head.Columns.Count)
                {
                    columns.RemoveAt(columns.Count - 1);
                }
                while (columns.Count < head.Columns.Count)
                {
                    columns.Add(new TableColumn());
                }
                for (int c = 0; c < head.Columns.Count; c++)
                {
                    GridLength width = head.Columns[c].Width;
                    if (columns[c].Width != width)
                    {
                        columns[c].Width = width;
                    }
                }
            }
        }

        /// <summary>
        /// 文書の中の分けた表の見た目（余白・外周の線・列の幅）が1つの表と同じになっているかを調べ、ずれていれば直す。
        /// WPF 標準の操作（表の外から表の途中までを選んで削除した等）で先頭や最後のかたまりが丸ごと消えた時や、元に戻す
        /// （Undo）でかたまりが作り直された時（列の情報は元に戻らない）のずれを直すために使う。
        /// </summary>
        /// <param name="a_doc">文書。</param>
        /// <param name="a_columnsOnlyFlg">列の幅だけを直すか（元に戻す・やり直しの後。余白・線は元に戻す側が戻すため）。</param>
        /// <returns>何か直したら true。</returns>
        public static bool RepairDocument(FlowDocument a_doc, bool a_columnsOnlyFlg)
        {
            bool changedFlg = false;
            Block block = a_doc?.Blocks.FirstBlock;
            while (null != block)
            {
                if (!(block is Table head))
                {
                    block = block.NextBlock;
                    continue;
                }
                var chunks = GetChunks(head);
                block = chunks[chunks.Count - 1].NextBlock;
                if (a_columnsOnlyFlg)
                {
                    if (1 < chunks.Count && !ColumnsInSync(chunks))
                    {
                        SyncColumns(chunks);
                        changedFlg = true;
                    }
                    continue;
                }
                if (!IsFrameConsistent(chunks))
                {
                    ApplyBorders(chunks);
                    changedFlg = true;
                }
            }
            return changedFlg;
        }

        /// <summary>かたまりの列の幅が、すべて先頭と同じか。</summary>
        private static bool ColumnsInSync(IList<Table> a_chunks)
        {
            Table head = a_chunks[0];
            for (int i = 1; i < a_chunks.Count; i++)
            {
                TableColumnCollection columns = a_chunks[i].Columns;
                if (columns.Count != head.Columns.Count)
                {
                    return false;
                }
                for (int c = 0; c < columns.Count; c++)
                {
                    if (columns[c].Width != head.Columns[c].Width)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>かたまりの間の見た目（余白・外周の線・列の幅）が、ApplyBorders の結果と同じか。</summary>
        private static bool IsFrameConsistent(IList<Table> a_chunks)
        {
            Table head = a_chunks[0];
            if (!BlockStyles.VsCodeStyleFlg && 0 == head.BorderThickness.Top)
            {
                return false; // 先頭のかたまりが消えて、続きのかたまりが先頭になった（上の線が無い）
            }
            for (int i = 0; i < a_chunks.Count; i++)
            {
                Table chunk = a_chunks[i];
                bool lastFlg = i == a_chunks.Count - 1;
                if (0 < i && 0 != chunk.BorderThickness.Top)
                {
                    return false;
                }
                // 最後のかたまり以外は、かたまり自身に下の線がある。最後のかたまりには無い
                if (lastFlg ? 0 != chunk.BorderThickness.Bottom : 0 == chunk.BorderThickness.Bottom)
                {
                    return false;
                }
                object margin = chunk.ReadLocalValue(Block.MarginProperty);
                if (!lastFlg && !(margin is Thickness m && new Thickness(0) == m))
                {
                    return false;
                }
            }
            Table last = a_chunks[a_chunks.Count - 1];
            double? lastBottom = ExpectedLastChunkBottomMargin(a_chunks);
            object lastMargin = last.ReadLocalValue(Block.MarginProperty);
            if (lastBottom.HasValue ? !(lastMargin is Thickness lm && new Thickness(0, 0, 0, lastBottom.Value) == lm)
                                    : DependencyProperty.UnsetValue != lastMargin)
            {
                return false;
            }
            return 1 == a_chunks.Count || ColumnsInSync(a_chunks);
        }

        /// <summary>
        /// 表の行をすべて取り出し、CHUNK_ROWS 行ずつ、新しい行グループ（まだどの表にも入れていない）へ入れ直す。表の行グループは
        /// すべて取り除く（a_table は行グループの無い表になる。呼び出し側が、返した行グループを入れる）。
        /// 行は、必ず行グループの先頭の行から取り出す（WPF の不具合の回避）。WPF は、行グループの2行目以降の行のような、
        /// 兄弟の先頭でない構造要素（段落・行・セル等）を取り除くと、その要素が IME（TSF）向けに数えている文字数（要素の前の
        /// 区切りの1文字。TextElement.IMELeftEdgeCharCount）を、取り除いた後も数え直さずに持ち続ける
        /// （TextContainer.DeleteContentInternal）。それを別の表へ入れると、文書の IME 向けの文字数が、行1つにつき1文字ずつ
        /// 実際より多くなる。ずれた文書で、ずれた位置より後ろの文字を消すと、TSF（TextInputFramework.dll）が内部の検査で
        /// プロセスを止め、mde が落ちていた（日本語入力のオン・オフに関わらない。TSF は日本語入力がオフでも文書の変更の
        /// 知らせを受け取るため）。兄弟の先頭の要素は区切りを数えない（0文字）ため、先頭から取り出せばずれない。
        /// </summary>
        /// <param name="a_table">表（かたまり）。</param>
        /// <returns>行を入れ直した行グループ（先頭から順）。</returns>
        private static List<TableRowGroup> MoveRowsIntoNewGroups(Table a_table)
        {
            var groups = new List<TableRowGroup>();
            TableRowGroup current = null;
            foreach (TableRowGroup source in a_table.RowGroups.ToList())
            {
                while (0 < source.Rows.Count)
                {
                    TableRow row = source.Rows[0];
                    source.Rows.Remove(row);
                    if (null == current || CHUNK_ROWS <= current.Rows.Count)
                    {
                        current = new TableRowGroup();
                        groups.Add(current);
                    }
                    current.Rows.Add(row);
                }
            }
            while (0 < a_table.RowGroups.Count)
            {
                a_table.RowGroups.Remove(a_table.RowGroups[0]);
            }
            if (0 == groups.Count)
            {
                groups.Add(new TableRowGroup());
            }
            return groups;
        }

        /// <summary>
        /// PDF出力の間だけ、行の前で改ページする（TablePrintPageBreaks。行がページの境目で上下に分かれると、列ごとに
        /// 別々に分かれるため、片方のページのセルが空に見えていた）。行がかたまりの最初の行なら、そのかたまりの前で
        /// 改ページする。かたまりの途中の行なら、その行から後ろを新しい続きのかたまりへ移し、そのかたまりの前で改ページする。
        /// WPF は表（Table）の BreakPageBefore を使わない（表の組み立て（TableParagraph）が読まない）ため、改ページは、
        /// 表の前に入れた高さのほとんど無い空の段落（BreakPageBefore を設定）で行う（CreatePageBreakParagraph）。
        /// ページの先頭に来る行の前には、a_repeatHeaderFlg が true なら表の見出しの行の写しを入れ（CreateHeaderCopyForPrint。
        /// 行が見出しの行そのものなら入れない）、その上に表の上の線を引く（GitHub スタイル。表の始まりと同じ見た目にする）。
        /// a_repeatHeaderFlg が false なら、ページの先頭に来る行の上に線を引く。移した行の下の線
        /// （かたまりの下の線）と下の余白は、新しいかたまりへ移す。行は、行グループの先頭から取り出して移す
        /// （MoveRowsIntoNewGroups 参照。見出しの行の写しも、元に戻す時は行グループの先頭から取り除く）。
        /// 書き出しが終わったら、戻り値の処理を呼んで元に戻すこと（複数の行で呼んだ場合は、呼んだ順と逆の順に戻す）。
        /// </summary>
        /// <param name="a_row">ページの先頭に送る行。</param>
        /// <param name="a_repeatHeaderFlg">行の前に、表の見出しの行の写しを入れるなら true。</param>
        /// <returns>元に戻す処理。行が表の中に無ければ null。</returns>
        public static System.Action BreakPageBeforeRowForPrint(TableRow a_row, bool a_repeatHeaderFlg)
        {
            Table chunk = GetChunkOfRow(a_row);
            if (null == chunk)
            {
                return null;
            }
            List<TableRow> rows = GetRows(new[] { chunk });
            int index = rows.IndexOf(a_row);
            double topLine = BlockStyles.VsCodeStyleFlg ? 0 : BlockStyles.PRINT_TABLE_BORDER_THICKNESS;
            // 見出しの行の写しは、移す前に作る（移した後は、移した先のかたまりが表の先頭として扱われるため）
            TableRow header = a_repeatHeaderFlg ? GetHeaderRow(a_row) : null;
            TableRow headerCopy = null != header && !ReferenceEquals(header, a_row) ? CreateHeaderCopyForPrint(header) : null;
            if (0 >= index)
            {
                // かたまりの最初の行：かたまりの前で改ページし、行の前に見出しの行の写しを入れて、その上に線を引く
                System.Action restoreBorder = SaveLocalValue(chunk, Block.BorderThicknessProperty);
                Thickness border = chunk.BorderThickness;
                chunk.BorderThickness = new Thickness(border.Left, System.Math.Max(border.Top, topLine), border.Right, border.Bottom);
                Paragraph pageBreak = CreatePageBreakParagraph();
                chunk.SiblingBlocks.InsertBefore(chunk, pageBreak);
                if (null != headerCopy)
                {
                    ((TableRowGroup)a_row.Parent).Rows.Insert(0, headerCopy);
                }
                return () =>
                {
                    RemoveHeaderCopy(headerCopy);
                    pageBreak.SiblingBlocks.Remove(pageBreak);
                    restoreBorder();
                };
            }

            // かたまりの途中の行：その行から後ろを、新しい続きのかたまりへ移す
            System.Action restoreMargin = SaveLocalValue(chunk, Block.MarginProperty);
            System.Action restoreChunkBorder = SaveLocalValue(chunk, Block.BorderThicknessProperty);
            var front = new TableRowGroup();
            var back = new TableRowGroup();
            if (null != headerCopy)
            {
                back.Rows.Add(headerCopy);
            }
            foreach (TableRowGroup source in chunk.RowGroups.ToList())
            {
                while (0 < source.Rows.Count)
                {
                    TableRow row = source.Rows[0];
                    source.Rows.Remove(row);
                    (front.Rows.Count < index ? front : back).Rows.Add(row);
                }
            }
            while (0 < chunk.RowGroups.Count)
            {
                chunk.RowGroups.Remove(chunk.RowGroups[0]);
            }
            Table next = CreateContinuationChunk(chunk);
            next.BorderBrush = chunk.BorderBrush;
            Thickness chunkBorder = chunk.BorderThickness;
            next.BorderThickness = new Thickness(chunkBorder.Left, topLine, chunkBorder.Right, chunkBorder.Bottom);
            chunk.BorderThickness = new Thickness(chunkBorder.Left, chunkBorder.Top, chunkBorder.Right, 0);
            Thickness margin = chunk.Margin;
            next.Margin = new Thickness(margin.Left, 0, margin.Right, margin.Bottom);
            chunk.Margin = new Thickness(margin.Left, margin.Top, margin.Right, 0);
            chunk.RowGroups.Add(front);
            next.RowGroups.Add(back);
            Paragraph breakBeforeNext = CreatePageBreakParagraph();
            chunk.SiblingBlocks.InsertAfter(chunk, breakBeforeNext);
            breakBeforeNext.SiblingBlocks.InsertAfter(breakBeforeNext, next);
            return () =>
            {
                // 見出しの行の写しを取り除き、行をすべて、新しい行グループへ先頭から順に入れ直して、元のかたまりへ戻す
                RemoveHeaderCopy(headerCopy);
                var group = new TableRowGroup();
                foreach (Table table in new[] { chunk, next })
                {
                    foreach (TableRowGroup source in table.RowGroups.ToList())
                    {
                        while (0 < source.Rows.Count)
                        {
                            TableRow row = source.Rows[0];
                            source.Rows.Remove(row);
                            group.Rows.Add(row);
                        }
                    }
                    while (0 < table.RowGroups.Count)
                    {
                        table.RowGroups.Remove(table.RowGroups[0]);
                    }
                }
                next.SiblingBlocks.Remove(next);
                breakBeforeNext.SiblingBlocks.Remove(breakBeforeNext);
                chunk.RowGroups.Add(group);
                restoreChunkBorder();
                restoreMargin();
            };
        }

        /// <summary>
        /// PDF出力の間だけ使う、見出しの行の写しを作る（BreakPageBeforeRowForPrint）。行・セル・段落の見た目（罫線・背景・
        /// 文字揃え等のローカル値）を写し、段落の中身は、文字の書式ごと写す（TextRange の保存と読み込み。リンク・画像も
        /// 写る）。Tag 等の意味情報は、書き出しには使わないため、中身には写さない。
        /// 見出しの行が、かたまりの最後の行（見出しの行だけのかたまり）の場合、見出しの行の下の線はセルではなくかたまり
        /// 自身の下の線になっている（BlockStyles.ApplyTableCellBorders）ため、写しではセルの下の線にする。
        /// </summary>
        /// <param name="a_header">見出しの行。</param>
        /// <returns>見出しの行の写し。</returns>
        private static TableRow CreateHeaderCopyForPrint(TableRow a_header)
        {
            var copy = new TableRow();
            CopyLocalValues(a_header, copy);
            Table headChunk = GetChunkOfRow(a_header);
            bool boundaryFlg = null != headChunk && ReferenceEquals(GetRows(new[] { headChunk }).LastOrDefault(), a_header) &&
                0 < headChunk.BorderThickness.Bottom;
            foreach (TableCell cell in a_header.Cells)
            {
                var cellCopy = new TableCell();
                CopyLocalValues(cell, cellCopy);
                if (boundaryFlg && 0 == cellCopy.BorderThickness.Bottom)
                {
                    Thickness thickness = cellCopy.BorderThickness;
                    cellCopy.BorderThickness = new Thickness(thickness.Left, thickness.Top, thickness.Right, headChunk.BorderThickness.Bottom);
                    if (null != headChunk.BorderBrush)
                    {
                        cellCopy.BorderBrush = headChunk.BorderBrush;
                    }
                }
                foreach (Block block in cell.Blocks)
                {
                    var paraCopy = new Paragraph();
                    if (block is Paragraph para)
                    {
                        CopyLocalValues(para, paraCopy);
                    }
                    cellCopy.Blocks.Add(paraCopy);
                    CopyContent(block, paraCopy);
                }
                copy.Cells.Add(cellCopy);
            }
            return copy;
        }

        /// <summary>見出しの行の写し（CreateHeaderCopyForPrint）を、文書から取り除く。</summary>
        /// <param name="a_headerCopy">見出しの行の写し。null なら何もしない。</param>
        private static void RemoveHeaderCopy(TableRow a_headerCopy)
        {
            if (a_headerCopy?.Parent is TableRowGroup group)
            {
                group.Rows.Remove(a_headerCopy);
            }
        }

        /// <summary>ローカル値（書き込めるもの）を、別の要素へ写す。</summary>
        /// <param name="a_source">写す元。</param>
        /// <param name="a_target">写す先。</param>
        private static void CopyLocalValues(DependencyObject a_source, DependencyObject a_target)
        {
            LocalValueEnumerator values = a_source.GetLocalValueEnumerator();
            while (values.MoveNext())
            {
                LocalValueEntry entry = values.Current;
                if (entry.Property.ReadOnly || entry.Value is System.Windows.Expression)
                {
                    continue;
                }
                try
                {
                    a_target.SetValue(entry.Property, entry.Value);
                }
                catch (System.Exception)
                {
                    // 写せない値は写さない（見た目に関わらない値）
                }
            }
        }

        /// <summary>ブロックの中身を、文字の書式ごと段落へ写す（TextRange の保存と読み込み）。書式ごと写せなかった場合は、
        /// 文字だけを写す。</summary>
        /// <param name="a_source">写す元のブロック。</param>
        /// <param name="a_target">写す先の段落（空であること）。</param>
        private static void CopyContent(Block a_source, Paragraph a_target)
        {
            var sourceRange = new TextRange(a_source.ContentStart, a_source.ContentEnd);
            try
            {
                using (var stream = new MemoryStream())
                {
                    sourceRange.Save(stream, DataFormats.XamlPackage);
                    stream.Position = 0;
                    new TextRange(a_target.ContentStart, a_target.ContentEnd).Load(stream, DataFormats.XamlPackage);
                }
            }
            catch (System.Exception)
            {
                a_target.Inlines.Clear();
                a_target.Inlines.Add(new Run(sourceRange.Text.TrimEnd('\r', '\n')));
            }
        }

        /// <summary>
        /// 改ページのための空の段落（BreakPageBeforeRowForPrint）を作る。文字の大きさ・行の高さ・余白をほとんど 0 にし、
        /// ページの先頭に入っても、見た目に影響しないようにする。
        /// </summary>
        /// <returns>段落。</returns>
        private static Paragraph CreatePageBreakParagraph()
        {
            return new Paragraph
            {
                BreakPageBefore = true,
                Margin = new Thickness(0),
                Padding = new Thickness(0),
                FontSize = 0.1,
                LineHeight = 0.1,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            };
        }

        /// <summary>プロパティのローカル値を覚えておき、それに戻す処理を返す（値が未設定だった場合は、戻す時に未設定へ戻す）。</summary>
        /// <param name="a_obj">対象。</param>
        /// <param name="a_property">プロパティ。</param>
        /// <returns>元に戻す処理。</returns>
        private static System.Action SaveLocalValue(DependencyObject a_obj, DependencyProperty a_property)
        {
            object value = a_obj.ReadLocalValue(a_property);
            return () =>
            {
                if (DependencyProperty.UnsetValue == value)
                {
                    a_obj.ClearValue(a_property);
                }
                else
                {
                    a_obj.SetValue(a_property, value);
                }
            };
        }

        /// <summary>続きのかたまり（行グループの無い Table）を作る。列は a_source と同じにする。</summary>
        /// <param name="a_source">列を写す元のかたまり。</param>
        /// <returns>続きのかたまり。</returns>
        private static Table CreateContinuationChunk(Table a_source)
        {
            var chunk = new Table { Tag = new TableContinuationInfo() };
            foreach (TableColumn column in a_source.Columns)
            {
                chunk.Columns.Add(new TableColumn { Width = column.Width });
            }
            // CellSpacing 等の他の見た目は、スタイル（MainWindow.xaml の Table のスタイル）で同じになる。スタイルの無い
            // 文書（保存用の一時的な変換等）でも同じになるよう、先頭にローカル値があれば写す
            object cellSpacing = a_source.ReadLocalValue(Table.CellSpacingProperty);
            if (DependencyProperty.UnsetValue != cellSpacing)
            {
                chunk.CellSpacing = (double)cellSpacing;
            }
            return chunk;
        }

        /// <summary>値が違う時だけ設定する（同じ値を設定し直して、元に戻すの履歴や組み直しを増やさないため）。</summary>
        private static void SetIfDifferent(DependencyObject a_obj, DependencyProperty a_property, Thickness a_value)
        {
            object local = a_obj.ReadLocalValue(a_property);
            if (local is Thickness current && current == a_value)
            {
                return;
            }
            a_obj.SetValue(a_property, a_value);
        }
    }
}
