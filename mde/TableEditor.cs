// TableEditor.cs
//
// mde (MarkDown インラインエディタ) の一部。
// 表(Table)の編集を担当するクラス。行・列の挿入/削除、セル間の矢印キー移動、
// Excelとのコピー&ペースト連携(TSV/HTML形式)を扱う。
// MainWindow本体への参照は持たず、必要な操作はコンストラクタで渡されたdelegate経由で行う。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace mde
{
    /// <summary>
    /// 表の編集機能一式。MainWindowとは疎結合で、Editor本体・「元テキスト保持」の追跡役・
    /// ダーティ通知/プログラム的変更ラップ用のdelegateだけを受け取って動作する。
    /// </summary>
    public class TableEditor
    {
        private readonly RichTextBox m_editor;
        private readonly OriginalTextTracker m_originalTextTracker;
        private readonly Action m_markDirty;
        private readonly Action<Action> m_runAsProgrammaticChange;
        private readonly Func<bool> m_isSourceMode;
        private readonly Action m_refreshOutline;
        private readonly Action<string> m_insertPlainTextWithLineBreaks;

        /// <summary>右クリック時にマウス下にあったセル。右クリックメニューの各項目から参照される。</summary>
        public TableCell ContextCell { get; set; }

        /// <summary>右クリック時にマウス下にあった段落（表の外に新しい表を挿入する位置の基準）。</summary>
        public Paragraph ContextParagraph { get; set; }

        /// <summary>
        /// TableEditorを構築する。
        /// </summary>
        /// <param name="a_editor">編集対象のRichTextBox。</param>
        /// <param name="a_originalTextTracker">「元テキスト保持」の追跡役。</param>
        /// <param name="a_markDirty">ファイルが変更されたことを通知するdelegate。</param>
        /// <param name="a_runAsProgrammaticChange">処理を「プログラムによる変更」として実行するdelegate。</param>
        /// <param name="a_isSourceMode">現在ソースモードかどうかを返すdelegate。</param>
        /// <param name="a_refreshOutline">アウトラインペインの再構築を依頼するdelegate。</param>
        /// <param name="a_insertPlainTextWithLineBreaks">コードブロックへの貼り付け時に使う、改行対応のプレーンテキスト挿入delegate。</param>
        public TableEditor(
            RichTextBox a_editor,
            OriginalTextTracker a_originalTextTracker,
            Action a_markDirty,
            Action<Action> a_runAsProgrammaticChange,
            Func<bool> a_isSourceMode,
            Action a_refreshOutline,
            Action<string> a_insertPlainTextWithLineBreaks)
        {
            this.m_editor = a_editor;
            this.m_originalTextTracker = a_originalTextTracker;
            this.m_markDirty = a_markDirty;
            this.m_runAsProgrammaticChange = a_runAsProgrammaticChange;
            this.m_isSourceMode = a_isSourceMode;
            this.m_refreshOutline = a_refreshOutline;
            this.m_insertPlainTextWithLineBreaks = a_insertPlainTextWithLineBreaks;
        }

        private static readonly Brush HEADER_BACKGROUND = new SolidColorBrush(Color.FromRgb(0xF8, 0xF8, 0xF8));
        private static readonly Brush CELL_BORDER = new SolidColorBrush(Color.FromRgb(0xDD, 0xDF, 0xE2));

        // ---------------- セル間の矢印キー移動 ----------------

        /// <summary>セルの内容の先頭にキャレットがあるかどうかを調べる（左/上キーでのセル移動判定用）。</summary>
        /// <param name="a_cell">対象のセル。</param>
        /// <returns>セルの内容の先頭にあればtrue。</returns>
        public bool IsCaretAtStart(TableCell a_cell)
        {
            var firstPara = a_cell.Blocks.FirstBlock as Paragraph;
            if (null == firstPara)
            {
                return true;
            }
            return m_editor.CaretPosition.CompareTo(firstPara.ContentStart) <= 0;
        }

        /// <summary>セルの内容の末尾にキャレットがあるかどうかを調べる（右/下キーでのセル移動判定用）。</summary>
        /// <param name="a_cell">対象のセル。</param>
        /// <returns>セルの内容の末尾にあればtrue。</returns>
        public bool IsCaretAtEnd(TableCell a_cell)
        {
            var lastPara = a_cell.Blocks.LastBlock as Paragraph;
            if (null == lastPara)
            {
                return true;
            }
            return m_editor.CaretPosition.CompareTo(lastPara.ContentEnd) >= 0;
        }

        /// <summary>上下キーでの行間移動。キャレットを真上/真下の行の同じ列のセルへ移す。
        /// 先頭行で上キー・末尾行で下キーが押された場合は、表の外（前後のブロック）へ
        /// キャレットを移す。前後にブロックが存在しない場合は、新しい空の段落を作って
        /// そこへ移す。</summary>
        /// <param name="a_cell">現在のセル。</param>
        /// <param name="a_dir">-1で上、+1で下。</param>
        public void MoveVertical(TableCell a_cell, int a_dir)
        {
            if (!(a_cell.Parent is TableRow row))
            {
                return;
            }
            if (!(row.Parent is TableRowGroup rg))
            {
                return;
            }
            int rIdx = rg.Rows.IndexOf(row);
            int cIdx = row.Cells.IndexOf(a_cell);
            int targetIdx = rIdx + a_dir;
            if (targetIdx < 0 ||
                targetIdx >= rg.Rows.Count)
            {
                MoveOutOfTable(rg, a_dir);
                return;
            }
            var targetRow = rg.Rows[targetIdx];
            if (cIdx < targetRow.Cells.Count && targetRow.Cells[cIdx].Blocks.LastBlock is Paragraph tp)
            {
                m_editor.CaretPosition = tp.ContentEnd;
            }
        }

        /// <summary>表の先頭行で上キー・末尾行で下キーが押された時に、キャレットを表の外
        /// （直前/直後のブロック）へ移す。前後にブロックが存在しない場合（表がドキュメントの
        /// 先頭/末尾に隣接ブロックなしで存在する場合）は、新しい空の段落を作ってそこへ移す。</summary>
        /// <param name="a_rg">現在の表の行グループ。</param>
        /// <param name="a_dir">-1で上（表の直前へ）、+1で下（表の直後へ）。</param>
        private void MoveOutOfTable(TableRowGroup a_rg, int a_dir)
        {
            if (!(a_rg.Parent is Table table))
            {
                return;
            }
            Block neighbor = a_dir < 0 ? table.PreviousBlock : table.NextBlock;
            if (null == neighbor)
            {
                var newPara = new Paragraph();
                if (a_dir < 0)
                {
                    m_editor.Document.Blocks.InsertBefore(table, newPara);
                }
                else
                {
                    m_editor.Document.Blocks.InsertAfter(table, newPara);
                }
                m_editor.CaretPosition = newPara.ContentStart;
                m_markDirty();
                return;
            }
            m_editor.CaretPosition = a_dir < 0 ? neighbor.ContentEnd : neighbor.ContentStart;
        }

        /// <summary>左右キーでのセル間移動。行の端では隣の行へ折り返す。</summary>
        /// <param name="a_cell">現在のセル。</param>
        /// <param name="a_dir">-1で左/前、+1で右/次。</param>
        public void MoveHorizontal(TableCell a_cell, int a_dir)
        {
            if (!(a_cell.Parent is TableRow row))
            {
                return;
            }
            if (!(row.Parent is TableRowGroup rg))
            {
                return;
            }
            int rIdx = rg.Rows.IndexOf(row);
            int cIdx = row.Cells.IndexOf(a_cell);

            if (1 == a_dir)
            {
                if (cIdx + 1 < row.Cells.Count)
                {
                    if (row.Cells[cIdx + 1].Blocks.FirstBlock is Paragraph np)
                    {
                        m_editor.CaretPosition = np.ContentStart;
                    }
                    return;
                }
                if (rIdx + 1 < rg.Rows.Count)
                {
                    var nr = rg.Rows[rIdx + 1];
                    if (nr.Cells.Count > 0 && nr.Cells[0].Blocks.FirstBlock is Paragraph np2)
                    {
                        m_editor.CaretPosition = np2.ContentStart;
                    }
                }
            }
            else
            {
                if (cIdx - 1 >= 0)
                {
                    if (row.Cells[cIdx - 1].Blocks.LastBlock is Paragraph pp)
                    {
                        m_editor.CaretPosition = pp.ContentEnd;
                    }
                    return;
                }
                if (rIdx - 1 >= 0)
                {
                    var pr = rg.Rows[rIdx - 1];
                    if (pr.Cells.Count > 0 && pr.Cells[pr.Cells.Count - 1].Blocks.LastBlock is Paragraph pp2)
                    {
                        m_editor.CaretPosition = pp2.ContentEnd;
                    }
                }
            }
        }

        // ---------------- 表の挿入・行/列の挿入・削除 ----------------

        /// <summary>指定した行数・列数の新しい表を、ContextParagraphの後ろに挿入する。</summary>
        /// <param name="a_rows">行数（ヘッダー行込み）。</param>
        /// <param name="a_cols">列数。</param>
        public void InsertTable(int a_rows, int a_cols)
        {
            var table = new Table();
            for (int c = 0; c < a_cols; c++)
            {
                table.Columns.Add(new TableColumn());
            }
            var rg = new TableRowGroup();
            table.RowGroups.Add(rg);

            var headerRow = new TableRow();
            for (int c = 0; c < a_cols; c++)
            {
                // KeepTogether: PDF書き出し時、ページの境目で表の行が跨ると、跨いだ先のセルは
                // 罫線が描画されないままテキストだけが続いてしまうというWPFの制約があるため、
                // セル内の段落をページ内で分割させない（間に合わなければ行ごと次のページへ）。
                var cell = new TableCell(new Paragraph { Margin = new Thickness(0), KeepTogether = true })
                {
                    FontWeight = FontWeights.Bold,
                    Background = HEADER_BACKGROUND,
                    BorderBrush = CELL_BORDER,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8, 6, 8, 6)
                };
                headerRow.Cells.Add(cell);
            }
            rg.Rows.Add(headerRow);

            for (int r = 0; r < a_rows - 1; r++)
            {
                var row = new TableRow();
                for (int c = 0; c < a_cols; c++)
                {
                    var cell = new TableCell(new Paragraph { Margin = new Thickness(0), KeepTogether = true })
                    {
                        BorderBrush = CELL_BORDER,
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(8, 6, 8, 6)
                    };
                    row.Cells.Add(cell);
                }
                rg.Rows.Add(row);
            }

            var trailingPara = new Paragraph();

            m_runAsProgrammaticChange(() =>
            {
                if (null != ContextParagraph && ContextParagraph.Parent is FlowDocument)
                {
                    m_editor.Document.Blocks.InsertAfter(ContextParagraph, table);
                    m_editor.Document.Blocks.InsertAfter(table, trailingPara);
                }
                else
                {
                    m_editor.Document.Blocks.Add(table);
                    m_editor.Document.Blocks.Add(trailingPara);
                }
            });

            if (headerRow.Cells[0].Blocks.FirstBlock is Paragraph hp)
            {
                m_editor.CaretPosition = hp.ContentStart;
            }
            m_editor.Focus();
            m_markDirty();
        }

        /// <summary>ContextCellの表に新しい空の行を挿入する。</summary>
        /// <param name="a_aboveFlg">true なら現在の行の上に、false なら下に挿入する。</param>
        public void InsertRow(bool a_aboveFlg)
        {
            if (null == ContextCell)
            {
                return;
            }
            m_originalTextTracker.Invalidate(ContextCell.ContentStart);
            if (!(ContextCell.Parent is TableRow row))
            {
                return;
            }
            if (!(row.Parent is TableRowGroup rg))
            {
                return;
            }

            int colCount = row.Cells.Count;
            var newRow = new TableRow();
            for (int c = 0; c < colCount; c++)
            {
                var cell = new TableCell(new Paragraph { Margin = new Thickness(0), KeepTogether = true })
                {
                    BorderBrush = CELL_BORDER,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8, 6, 8, 6)
                };
                newRow.Cells.Add(cell);
            }

            int idx = rg.Rows.IndexOf(row);
            int insertIdx = a_aboveFlg ? idx : idx + 1;
            rg.Rows.Insert(insertIdx, newRow);

            if (newRow.Cells.Count > 0 && newRow.Cells[0].Blocks.FirstBlock is Paragraph np)
            {
                m_editor.CaretPosition = np.ContentStart;
            }
            m_editor.Focus();
            m_markDirty();
        }

        /// <summary>ContextCellの表に新しい空の列を挿入する。</summary>
        /// <param name="a_leftFlg">true なら現在の列の左に、false なら右に挿入する。</param>
        public void InsertColumn(bool a_leftFlg)
        {
            if (null == ContextCell)
            {
                return;
            }
            m_originalTextTracker.Invalidate(ContextCell.ContentStart);
            if (!(ContextCell.Parent is TableRow row))
            {
                return;
            }
            if (!(row.Parent is TableRowGroup rg))
            {
                return;
            }
            if (!(rg.Parent is Table table))
            {
                return;
            }

            int colIdx = row.Cells.IndexOf(ContextCell);
            int insertIdx = a_leftFlg ? colIdx : colIdx + 1;
            var rows = rg.Rows.Cast<TableRow>().ToList();

            TableCell firstNewCell = null;
            for (int r = 0; r < rows.Count; r++)
            {
                var targetRow = rows[r];
                var cell = new TableCell(new Paragraph { Margin = new Thickness(0), KeepTogether = true })
                {
                    BorderBrush = CELL_BORDER,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8, 6, 8, 6)
                };
                if (0 == r)
                {
                    cell.FontWeight = FontWeights.Bold;
                    cell.Background = HEADER_BACKGROUND;
                }

                int idxInRow = Math.Min(insertIdx, targetRow.Cells.Count);
                targetRow.Cells.Insert(idxInRow, cell);
                if (targetRow == row)
                {
                    firstNewCell = cell;
                }
            }

            var newColumn = new TableColumn();
            int colInsertIdx = Math.Min(insertIdx, table.Columns.Count);
            table.Columns.Insert(colInsertIdx, newColumn);

            if (firstNewCell?.Blocks.FirstBlock is Paragraph np)
            {
                m_editor.CaretPosition = np.ContentStart;
            }
            m_editor.Focus();
            m_markDirty();
        }

        /// <summary>ContextCellが属する行を削除する（表の唯一の行なら表ごと削除する）。</summary>
        public void DeleteRow()
        {
            if (null == ContextCell)
            {
                return;
            }
            m_originalTextTracker.Invalidate(ContextCell.ContentStart);
            if (!(ContextCell.Parent is TableRow row))
            {
                return;
            }
            if (!(row.Parent is TableRowGroup rg))
            {
                return;
            }

            if (rg.Rows.Count <= 1)
            {
                if (rg.Parent is Table table)
                {
                    m_editor.Document.Blocks.Remove(table);
                }
                m_markDirty();
                return;
            }
            rg.Rows.Remove(row);
            // 行削除後、残ったセルの枠線が描画上消えて見えることがあるとの報告があったため、
            // 念のためレイアウトを強制的に再計算させている。ただしコードを読んだ限りでは
            // 枠線のプロパティ自体（BorderBrush/BorderThickness）を書き換えている箇所は
            // 見当たらず、原因はWPFのTable描画側の再描画の問題である可能性を疑っての、
            // 未検証の対症療法。再発する場合は再現手順を教えてほしい。
            m_editor.UpdateLayout();
            m_markDirty();
        }

        /// <summary>ContextCellが属する列を削除する（表の唯一の列なら表ごと削除する）。</summary>
        public void DeleteColumn()
        {
            if (null == ContextCell)
            {
                return;
            }
            m_originalTextTracker.Invalidate(ContextCell.ContentStart);
            if (!(ContextCell.Parent is TableRow row))
            {
                return;
            }
            if (!(row.Parent is TableRowGroup rg))
            {
                return;
            }
            if (!(rg.Parent is Table table))
            {
                return;
            }

            int colIndex = row.Cells.IndexOf(ContextCell);

            if (row.Cells.Count <= 1)
            {
                m_editor.Document.Blocks.Remove(table);
                m_markDirty();
                return;
            }

            foreach (TableRow r in rg.Rows)
            {
                if (colIndex < r.Cells.Count)
                {
                    r.Cells.RemoveAt(colIndex);
                }
            }
            if (table.Columns.Count > colIndex)
            {
                table.Columns.RemoveAt(colIndex);
            }
            // DeleteRow側と同じ理由（このメソッド内のコメント参照）による、未検証の対症療法。
            m_editor.UpdateLayout();
            m_markDirty();
        }

        // ---------------- Excelとのコピー&ペースト連携 ----------------

        private List<TableRow> GetTableRows(Table a_table)
        {
            var rows = new List<TableRow>();
            foreach (TableRowGroup rg in a_table.RowGroups)
            {
                foreach (TableRow r in rg.Rows)
                {
                    rows.Add(r);
                }
            }
            return rows;
        }

        private Table FindEnclosingTable(TableCell a_cell)
        {
            if (!(a_cell.Parent is TableRow row))
            {
                return null;
            }
            if (!(row.Parent is TableRowGroup rg))
            {
                return null;
            }
            return rg.Parent as Table;
        }

        private class CellRange
        {
            public int m_minRow, m_maxRow, m_minCol, m_maxCol;
        }

        /// <summary>
        /// startCellとendCellを表の行/列グリッド内で特定し、両方を含む最小の矩形範囲を返す。
        /// どちらかのセルが見つからなければ null を返す。
        /// </summary>
        /// <param name="a_rows">対象の行一覧。</param>
        /// <param name="a_startCell">選択範囲の開始セル。</param>
        /// <param name="a_endCell">選択範囲の終了セル。</param>
        /// <returns>選択されたセル範囲。どちらかのセルが見つからなければnull。</returns>
        private CellRange GetSelectedCellRange(List<TableRow> a_rows, TableCell a_startCell, TableCell a_endCell)
        {
            int startRow = -1, startCol = -1, endRow = -1, endCol = -1;
            for (int r = 0; r < a_rows.Count; r++)
            {
                int c = a_rows[r].Cells.IndexOf(a_startCell);
                if (c >= 0) { startRow = r; startCol = c; }
                c = a_rows[r].Cells.IndexOf(a_endCell);
                if (c >= 0) { endRow = r; endCol = c; }
            }
            if (startRow < 0 ||
                endRow < 0) return null;

            return new CellRange
            {
                m_minRow = Math.Min(startRow, endRow),
                m_maxRow = Math.Max(startRow, endRow),
                m_minCol = Math.Min(startCol, endCol),
                m_maxCol = Math.Max(startCol, endCol)
            };
        }

        private string RangeToTsv(List<TableRow> a_rows, CellRange a_range)
        {
            var lines = new List<string>();
            for (int r = a_range.m_minRow; r <= a_range.m_maxRow; r++)
            {
                var cells = a_rows[r].Cells.Cast<TableCell>().ToList();
                var rowTexts = new List<string>();
                for (int c = a_range.m_minCol;
                     c <= a_range.m_maxCol &&
                     c < cells.Count;
                     c++)
                {
                    rowTexts.Add(CellPlainText(cells[c]).Replace('\t', ' ').Replace("\r", " ").Replace("\n", " "));
                }
                lines.Add(string.Join("\t", rowTexts));
            }
            return string.Join("\r\n", lines);
        }

        private string RangeToHtmlFragment(List<TableRow> a_rows, CellRange a_range)
        {
            var sb = new StringBuilder();
            sb.Append("<table border=\"1\" cellspacing=\"0\" cellpadding=\"4\" style=\"border-collapse:collapse;\">");
            for (int r = a_range.m_minRow; r <= a_range.m_maxRow; r++)
            {
                sb.Append("<tr>");
                var cells = a_rows[r].Cells.Cast<TableCell>().ToList();
                for (int c = a_range.m_minCol;
                     c <= a_range.m_maxCol &&
                     c < cells.Count;
                     c++)
                {
                    // 選択範囲の先頭行が「表そのもののヘッダー行」である場合だけ th として書き出す。
                    string tag = 0 == r ? "th" : "td";
                    string text = WebUtility.HtmlEncode(CellPlainText(cells[c])).Replace("\n", "<br>");
                    sb.Append('<').Append(tag).Append(" style=\"border:1px solid #999999;padding:4px 8px;\">")
                      .Append(text).Append("</").Append(tag).Append('>');
                }
                sb.Append("</tr>");
            }
            sb.Append("</table>");
            return sb.ToString();
        }

        private string CellPlainText(TableCell a_cell)
        {
            var sb = new StringBuilder();
            foreach (Block b in a_cell.Blocks)
            {
                if (b is Paragraph p)
                {
                    sb.Append(new TextRange(p.ContentStart, p.ContentEnd).Text);
                }
            }
            return sb.ToString().Trim();
        }

        private string TableToTsv(Table a_table)
        {
            var lines = new List<string>();
            foreach (var row in GetTableRows(a_table))
            {
                var cellTexts = row.Cells.Cast<TableCell>()
                    .Select(c => CellPlainText(c).Replace('\t', ' ').Replace("\r", " ").Replace("\n", " "));
                lines.Add(string.Join("\t", cellTexts));
            }
            return string.Join("\r\n", lines);
        }

        private string TableToHtmlFragment(Table a_table)
        {
            var sb = new StringBuilder();
            sb.Append("<table border=\"1\" cellspacing=\"0\" cellpadding=\"4\" style=\"border-collapse:collapse;\">");
            var rows = GetTableRows(a_table);
            for (int r = 0; r < rows.Count; r++)
            {
                sb.Append("<tr>");
                foreach (TableCell cell in rows[r].Cells)
                {
                    string tag = 0 == r ? "th" : "td";
                    string text = WebUtility.HtmlEncode(CellPlainText(cell)).Replace("\n", "<br>");
                    sb.Append('<').Append(tag).Append(" style=\"border:1px solid #999999;padding:4px 8px;\">")
                      .Append(text).Append("</").Append(tag).Append('>');
                }
                sb.Append("</tr>");
            }
            sb.Append("</table>");
            return sb.ToString();
        }

        /// <summary>
        /// HTMLフラグメントを、WindowsのCF_HTMLクリップボード形式が要求するヘッダー
        /// （Version/StartHTML/EndHTML/StartFragment/EndFragmentのバイトオフセット）で包む。
        /// これにより、Excel・Word・ブラウザ等への貼り付け時に単なるプレーンテキストではなく
        /// 本物のHTMLとして認識される。オフセットはUTF-8バイト数で計算しているため、
        /// 日本語などASCII以外を含むセルでも正しく動作する。
        /// </summary>
        /// <param name="a_htmlBodyFragment">HTMLフラグメントの本文。</param>
        /// <returns>CF_HTML形式のヘッダーを付けて包んだ文字列。</returns>
        private string BuildHtmlClipboardFragment(string a_htmlBodyFragment)
        {
            const string HTML_PREFIX = "<html><body><!--StartFragment-->";
            const string HTML_SUFFIX = "<!--EndFragment--></body></html>";
            const string HEADER_TEMPLATE =
                "Version:0.9\r\n" +
                "StartHTML:{0:0000000000}\r\n" +
                "EndHTML:{1:0000000000}\r\n" +
                "StartFragment:{2:0000000000}\r\n" +
                "EndFragment:{3:0000000000}\r\n";

            int headerByteLength = Encoding.UTF8.GetByteCount(string.Format(HEADER_TEMPLATE, 0, 0, 0, 0));
            int startHtml = headerByteLength;
            int startFragment = startHtml + Encoding.UTF8.GetByteCount(HTML_PREFIX);
            int endFragment = startFragment + Encoding.UTF8.GetByteCount(a_htmlBodyFragment);
            int endHtml = endFragment + Encoding.UTF8.GetByteCount(HTML_SUFFIX);

            string header = string.Format(HEADER_TEMPLATE, startHtml, endHtml, startFragment, endFragment);
            return header + HTML_PREFIX + a_htmlBodyFragment + HTML_SUFFIX;
        }

        /// <summary>Ctrl+Cでの表セル範囲コピー時に、TSVとCF_HTML形式をクリップボードへ追加し、
        /// Excelへの貼り付けが正しく表として認識されるようにする。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        public void HandleCopying(object a_sender, DataObjectCopyingEventArgs a_args)
        {
            if (m_isSourceMode() || a_args.IsDragDrop)
            {
                return;
            }

            var selection = m_editor.Selection;
            if (null == selection || selection.IsEmpty)
            {
                return;
            }

            var startCell = selection.Start?.Paragraph?.Parent as TableCell;
            var endCell = selection.End?.Paragraph?.Parent as TableCell;
            var anyCell = startCell ?? endCell;
            if (null == anyCell)
            {
                return;
            }

            var table = FindEnclosingTable(anyCell);
            if (null == table)
            {
                return;
            }

            var rows = GetTableRows(table);
            CellRange range = null;
            if (null != startCell &&
                null != endCell &&
                FindEnclosingTable(startCell) == table &&
                FindEnclosingTable(endCell) == table)
            {
                range = GetSelectedCellRange(rows, startCell, endCell);
            }

            // 正確なセル範囲が特定できればその範囲だけをコピーし、そうでなければ
            // （選択範囲が表の外にはみ出している場合など）表全体を書き出す。
            string tsv = null != range ? RangeToTsv(rows, range) : TableToTsv(table);
            string htmlFragment = null != range ? RangeToHtmlFragment(rows, range) : TableToHtmlFragment(table);
            a_args.DataObject.SetData(DataFormats.Text, tsv);
            a_args.DataObject.SetData(DataFormats.Html, BuildHtmlClipboardFragment(htmlFragment));
        }

        private List<List<string>> TryParseHtmlTable(string a_html)
        {
            var tableMatch = Regex.Match(a_html, "<table[^>]*>(.*?)</table>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (!tableMatch.Success)
            {
                return null;
            }

            var rows = new List<List<string>>();
            foreach (Match rowMatch in Regex.Matches(tableMatch.Groups[1].Value, "<tr[^>]*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                var cells = new List<string>();
                foreach (Match cellMatch in Regex.Matches(rowMatch.Groups[1].Value, "<t[dh][^>]*>(.*?)</t[dh]>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
                {
                    cells.Add(StripHtmlToText(cellMatch.Groups[1].Value));
                }
                if (cells.Count > 0)
                {
                    rows.Add(cells);
                }
            }
            return rows.Count > 0 ? rows : null;
        }

        private string StripHtmlToText(string a_html)
        {
            string text = Regex.Replace(a_html, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<[^>]+>", "");
            text = WebUtility.HtmlDecode(text);
            return text.Trim();
        }

        private bool LooksLikeTsv(string a_text)
        {
            return !string.IsNullOrEmpty(a_text) && a_text.Contains('\t');
        }

        private List<List<string>> ParseTsv(string a_text)
        {
            var lines = a_text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            return lines.Select(line => line.Split('\t').ToList()).ToList();
        }

        /// <summary>解析済みの行/セルテキストからWPFのTableを組み立てて挿入する（HTMLまたはTSVの貼り付けから）。</summary>
        /// <param name="a_rows">解析済みの行データ（先頭行がヘッダーとして扱われる）。</param>
        public void InsertParsedTable(List<List<string>> a_rows)
        {
            if (null == a_rows ||
                0 == a_rows.Count) return;
            int colCount = a_rows.Max(r => r.Count);
            if (0 == colCount)
            {
                return;
            }

            var table = new Table();
            for (int c = 0; c < colCount; c++)
            {
                table.Columns.Add(new TableColumn());
            }
            var rg = new TableRowGroup();
            table.RowGroups.Add(rg);

            for (int r = 0; r < a_rows.Count; r++)
            {
                var row = new TableRow();
                for (int c = 0; c < colCount; c++)
                {
                    string text = c < a_rows[r].Count ? a_rows[r][c] : "";
                    var cell = new TableCell(new Paragraph(new Run(text)) { KeepTogether = true })
                    {
                        BorderBrush = CELL_BORDER,
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(8, 6, 8, 6)
                    };
                    if (0 == r)
                    {
                        cell.FontWeight = FontWeights.Bold;
                        cell.Background = HEADER_BACKGROUND;
                    }
                    row.Cells.Add(cell);
                }
                rg.Rows.Add(row);
            }

            m_runAsProgrammaticChange(() =>
            {
                var para = m_editor.CaretPosition?.Paragraph;
                var trailingPara = new Paragraph();
                if (null != para && para.Parent is FlowDocument)
                {
                    m_editor.Document.Blocks.InsertAfter(para, table);
                    m_editor.Document.Blocks.InsertAfter(table, trailingPara);
                    if (string.IsNullOrWhiteSpace(new TextRange(para.ContentStart, para.ContentEnd).Text))
                    {
                        m_editor.Document.Blocks.Remove(para);
                    }
                }
                else
                {
                    m_editor.Document.Blocks.Add(table);
                    m_editor.Document.Blocks.Add(trailingPara);
                }

                if (rg.Rows.Count > 0 &&
                    rg.Rows[0].Cells.Count > 0 &&
                    rg.Rows[0].Cells[0].Blocks.FirstBlock is Paragraph fp)
                    m_editor.CaretPosition = fp.ContentStart;
            });

            m_refreshOutline();
            m_editor.Focus();
            m_markDirty();
        }

        /// <summary>貼り付け処理の入口。Excelなどからの表（HTML/TSV）を検出してTableへ変換し、
        /// コードブロック内へのプレーンテキスト貼り付けにも対応する。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        public void HandlePasting(object a_sender, DataObjectPastingEventArgs a_args)
        {
            if (m_isSourceMode())
            {
                return;
            }

            // コードブロックへの貼り付け：常にリテラルなテキストとして、同じフェンス内に挿入する
            // （既定の貼り付け動作のままだと新しい段落に分割されてしまうため）。
            var currentPara = m_editor.CaretPosition?.Paragraph;
            if (null != currentPara && currentPara.Tag is CodeBlockInfo && a_args.SourceDataObject.GetDataPresent(DataFormats.Text))
            {
                string codeText = (string)a_args.SourceDataObject.GetData(DataFormats.Text);
                a_args.CancelCommand();
                m_insertPlainTextWithLineBreaks(codeText);
                return;
            }

            // Excel（および大半のリッチなコピー元）は、クリップボードにHTMLの<m_table>を乗せてくる。
            // これが「表がコピーされた」ことを検出する最も確実な方法。
            if (a_args.SourceDataObject.GetDataPresent(DataFormats.Html))
            {
                string html = (string)a_args.SourceDataObject.GetData(DataFormats.Html);
                var tableData = TryParseHtmlTable(html);
                if (null != tableData)
                {
                    a_args.CancelCommand();
                    InsertParsedTable(tableData);
                    return;
                }
            }

            // フォールバック：タブ区切りのプレーンテキスト（コピー時にHTMLを出さないアプリ向け）。
            // 2026-08-29修正：Xaml/Rtf形式（mde自身や他のリッチテキストアプリからのコピー）が
            // 付いている場合はこのフォールバックを一切試みない。理由：mdeの箇条書き項目は、
            // WPFの既定のプレーンテキスト抽出で「マーカー\t本文」のようにタブ区切りで表現される
            // ことがあり、見出し・段落・箇条書き・表・画像を含む文書全体をmdeの別ウィンドウへ
            // コピー&ペーストした際、この抽出結果にタブが複数含まれることをもって文書全体を
            // 表として誤認識し、内容が丸ごと崩れた表に化けてしまう不具合が実機で確認された。
            // Xaml/Rtf形式があれば、WPF標準のリッチテキスト貼り付けの方が常に正確なので、
            // そちらに任せる。
            if (!a_args.SourceDataObject.GetDataPresent(DataFormats.Xaml) &&
                !a_args.SourceDataObject.GetDataPresent(DataFormats.Rtf) &&
                a_args.SourceDataObject.GetDataPresent(DataFormats.Text))
            {
                string text = (string)a_args.SourceDataObject.GetData(DataFormats.Text);
                if (LooksLikeTsv(text))
                {
                    var tableData = ParseTsv(text);
                    if (null != tableData &&
                        tableData.Any(r => r.Count > 1))
                    {
                        a_args.CancelCommand();
                        InsertParsedTable(tableData);
                    }
                }
            }
        }
    }
}
