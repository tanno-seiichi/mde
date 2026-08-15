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
        private readonly RichTextBox editor;
        private readonly OriginalTextTracker originalTextTracker;
        private readonly Action markDirty;
        private readonly Action<Action> runAsProgrammaticChange;
        private readonly Func<bool> isSourceMode;
        private readonly Action refreshOutline;
        private readonly Action<string> insertPlainTextWithLineBreaks;

        /// <summary>右クリック時にマウス下にあったセル。右クリックメニューの各項目から参照される。</summary>
        public TableCell ContextCell { get; set; }

        /// <summary>右クリック時にマウス下にあった段落（表の外に新しい表を挿入する位置の基準）。</summary>
        public Paragraph ContextParagraph { get; set; }

        /// <summary>
        /// TableEditorを構築する。
        /// </summary>
        /// <param name="editor">編集対象のRichTextBox。</param>
        /// <param name="originalTextTracker">「元テキスト保持」の追跡役。</param>
        /// <param name="markDirty">ファイルが変更されたことを通知するdelegate。</param>
        /// <param name="runAsProgrammaticChange">処理を「プログラムによる変更」として実行するdelegate。</param>
        /// <param name="isSourceMode">現在ソースモードかどうかを返すdelegate。</param>
        /// <param name="refreshOutline">アウトラインペインの再構築を依頼するdelegate。</param>
        /// <param name="insertPlainTextWithLineBreaks">コードブロックへの貼り付け時に使う、改行対応のプレーンテキスト挿入delegate。</param>
        public TableEditor(
            RichTextBox editor,
            OriginalTextTracker originalTextTracker,
            Action markDirty,
            Action<Action> runAsProgrammaticChange,
            Func<bool> isSourceMode,
            Action refreshOutline,
            Action<string> insertPlainTextWithLineBreaks)
        {
            this.editor = editor;
            this.originalTextTracker = originalTextTracker;
            this.markDirty = markDirty;
            this.runAsProgrammaticChange = runAsProgrammaticChange;
            this.isSourceMode = isSourceMode;
            this.refreshOutline = refreshOutline;
            this.insertPlainTextWithLineBreaks = insertPlainTextWithLineBreaks;
        }

        private static readonly Brush HeaderBackground = new SolidColorBrush(Color.FromRgb(0xF8, 0xF8, 0xF8));
        private static readonly Brush CellBorder = new SolidColorBrush(Color.FromRgb(0xDD, 0xDF, 0xE2));

        // ---------------- セル間の矢印キー移動 ----------------

        /// <summary>セルの内容の先頭にキャレットがあるかどうかを調べる（左/上キーでのセル移動判定用）。</summary>
        public bool IsCaretAtStart(TableCell cell)
        {
            var firstPara = cell.Blocks.FirstBlock as Paragraph;
            if (firstPara == null) return true;
            return editor.CaretPosition.CompareTo(firstPara.ContentStart) <= 0;
        }

        /// <summary>セルの内容の末尾にキャレットがあるかどうかを調べる（右/下キーでのセル移動判定用）。</summary>
        public bool IsCaretAtEnd(TableCell cell)
        {
            var lastPara = cell.Blocks.LastBlock as Paragraph;
            if (lastPara == null) return true;
            return editor.CaretPosition.CompareTo(lastPara.ContentEnd) >= 0;
        }

        /// <summary>上下キーでの行間移動。キャレットを真上/真下の行の同じ列のセルへ移す。</summary>
        /// <param name="cell">現在のセル。</param>
        /// <param name="dir">-1で上、+1で下。</param>
        public void MoveVertical(TableCell cell, int dir)
        {
            if (!(cell.Parent is TableRow row)) return;
            if (!(row.Parent is TableRowGroup rg)) return;
            int rIdx = rg.Rows.IndexOf(row);
            int cIdx = row.Cells.IndexOf(cell);
            int targetIdx = rIdx + dir;
            if (targetIdx < 0 || targetIdx >= rg.Rows.Count) return;
            var targetRow = rg.Rows[targetIdx];
            if (cIdx < targetRow.Cells.Count && targetRow.Cells[cIdx].Blocks.LastBlock is Paragraph tp)
                editor.CaretPosition = tp.ContentEnd;
        }

        /// <summary>左右キーでのセル間移動。行の端では隣の行へ折り返す。</summary>
        /// <param name="cell">現在のセル。</param>
        /// <param name="dir">-1で左/前、+1で右/次。</param>
        public void MoveHorizontal(TableCell cell, int dir)
        {
            if (!(cell.Parent is TableRow row)) return;
            if (!(row.Parent is TableRowGroup rg)) return;
            int rIdx = rg.Rows.IndexOf(row);
            int cIdx = row.Cells.IndexOf(cell);

            if (dir == 1)
            {
                if (cIdx + 1 < row.Cells.Count)
                {
                    if (row.Cells[cIdx + 1].Blocks.FirstBlock is Paragraph np) editor.CaretPosition = np.ContentStart;
                    return;
                }
                if (rIdx + 1 < rg.Rows.Count)
                {
                    var nr = rg.Rows[rIdx + 1];
                    if (nr.Cells.Count > 0 && nr.Cells[0].Blocks.FirstBlock is Paragraph np2) editor.CaretPosition = np2.ContentStart;
                }
            }
            else
            {
                if (cIdx - 1 >= 0)
                {
                    if (row.Cells[cIdx - 1].Blocks.LastBlock is Paragraph pp) editor.CaretPosition = pp.ContentEnd;
                    return;
                }
                if (rIdx - 1 >= 0)
                {
                    var pr = rg.Rows[rIdx - 1];
                    if (pr.Cells.Count > 0 && pr.Cells[pr.Cells.Count - 1].Blocks.LastBlock is Paragraph pp2)
                        editor.CaretPosition = pp2.ContentEnd;
                }
            }
        }

        // ---------------- 表の挿入・行/列の挿入・削除 ----------------

        /// <summary>指定した行数・列数の新しい表を、ContextParagraphの後ろに挿入する。</summary>
        /// <param name="rows">行数（ヘッダー行込み）。</param>
        /// <param name="cols">列数。</param>
        public void InsertTable(int rows, int cols)
        {
            var table = new Table();
            for (int c = 0; c < cols; c++) table.Columns.Add(new TableColumn());
            var rg = new TableRowGroup();
            table.RowGroups.Add(rg);

            var headerRow = new TableRow();
            for (int c = 0; c < cols; c++)
            {
                var cell = new TableCell(new Paragraph())
                {
                    FontWeight = FontWeights.Bold,
                    Background = HeaderBackground,
                    BorderBrush = CellBorder,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8, 6, 8, 6)
                };
                headerRow.Cells.Add(cell);
            }
            rg.Rows.Add(headerRow);

            for (int r = 0; r < rows - 1; r++)
            {
                var row = new TableRow();
                for (int c = 0; c < cols; c++)
                {
                    var cell = new TableCell(new Paragraph())
                    {
                        BorderBrush = CellBorder,
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(8, 6, 8, 6)
                    };
                    row.Cells.Add(cell);
                }
                rg.Rows.Add(row);
            }

            var trailingPara = new Paragraph();

            runAsProgrammaticChange(() =>
            {
                if (ContextParagraph != null && ContextParagraph.Parent is FlowDocument)
                {
                    editor.Document.Blocks.InsertAfter(ContextParagraph, table);
                    editor.Document.Blocks.InsertAfter(table, trailingPara);
                }
                else
                {
                    editor.Document.Blocks.Add(table);
                    editor.Document.Blocks.Add(trailingPara);
                }
            });

            if (headerRow.Cells[0].Blocks.FirstBlock is Paragraph hp) editor.CaretPosition = hp.ContentStart;
            editor.Focus();
            markDirty();
        }

        /// <summary>ContextCellの表に新しい空の行を挿入する。</summary>
        /// <param name="above">true なら現在の行の上に、false なら下に挿入する。</param>
        public void InsertRow(bool above)
        {
            if (ContextCell == null) return;
            originalTextTracker.Invalidate(ContextCell.ContentStart);
            if (!(ContextCell.Parent is TableRow row)) return;
            if (!(row.Parent is TableRowGroup rg)) return;

            int colCount = row.Cells.Count;
            var newRow = new TableRow();
            for (int c = 0; c < colCount; c++)
            {
                var cell = new TableCell(new Paragraph())
                {
                    BorderBrush = CellBorder,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8, 6, 8, 6)
                };
                newRow.Cells.Add(cell);
            }

            int idx = rg.Rows.IndexOf(row);
            int insertIdx = above ? idx : idx + 1;
            rg.Rows.Insert(insertIdx, newRow);

            if (newRow.Cells.Count > 0 && newRow.Cells[0].Blocks.FirstBlock is Paragraph np)
                editor.CaretPosition = np.ContentStart;
            editor.Focus();
            markDirty();
        }

        /// <summary>ContextCellの表に新しい空の列を挿入する。</summary>
        /// <param name="left">true なら現在の列の左に、false なら右に挿入する。</param>
        public void InsertColumn(bool left)
        {
            if (ContextCell == null) return;
            originalTextTracker.Invalidate(ContextCell.ContentStart);
            if (!(ContextCell.Parent is TableRow row)) return;
            if (!(row.Parent is TableRowGroup rg)) return;
            if (!(rg.Parent is Table table)) return;

            int colIdx = row.Cells.IndexOf(ContextCell);
            int insertIdx = left ? colIdx : colIdx + 1;
            var rows = rg.Rows.Cast<TableRow>().ToList();

            TableCell firstNewCell = null;
            for (int r = 0; r < rows.Count; r++)
            {
                var targetRow = rows[r];
                var cell = new TableCell(new Paragraph())
                {
                    BorderBrush = CellBorder,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8, 6, 8, 6)
                };
                if (r == 0)
                {
                    cell.FontWeight = FontWeights.Bold;
                    cell.Background = HeaderBackground;
                }

                int idxInRow = Math.Min(insertIdx, targetRow.Cells.Count);
                targetRow.Cells.Insert(idxInRow, cell);
                if (targetRow == row) firstNewCell = cell;
            }

            var newColumn = new TableColumn();
            int colInsertIdx = Math.Min(insertIdx, table.Columns.Count);
            table.Columns.Insert(colInsertIdx, newColumn);

            if (firstNewCell?.Blocks.FirstBlock is Paragraph np) editor.CaretPosition = np.ContentStart;
            editor.Focus();
            markDirty();
        }

        /// <summary>ContextCellが属する行を削除する（表の唯一の行なら表ごと削除する）。</summary>
        public void DeleteRow()
        {
            if (ContextCell == null) return;
            originalTextTracker.Invalidate(ContextCell.ContentStart);
            if (!(ContextCell.Parent is TableRow row)) return;
            if (!(row.Parent is TableRowGroup rg)) return;

            if (rg.Rows.Count <= 1)
            {
                if (rg.Parent is Table table) editor.Document.Blocks.Remove(table);
                markDirty();
                return;
            }
            rg.Rows.Remove(row);
            markDirty();
        }

        /// <summary>ContextCellが属する列を削除する（表の唯一の列なら表ごと削除する）。</summary>
        public void DeleteColumn()
        {
            if (ContextCell == null) return;
            originalTextTracker.Invalidate(ContextCell.ContentStart);
            if (!(ContextCell.Parent is TableRow row)) return;
            if (!(row.Parent is TableRowGroup rg)) return;
            if (!(rg.Parent is Table table)) return;

            int colIndex = row.Cells.IndexOf(ContextCell);

            if (row.Cells.Count <= 1)
            {
                editor.Document.Blocks.Remove(table);
                markDirty();
                return;
            }

            foreach (TableRow r in rg.Rows)
            {
                if (colIndex < r.Cells.Count) r.Cells.RemoveAt(colIndex);
            }
            if (table.Columns.Count > colIndex) table.Columns.RemoveAt(colIndex);
            markDirty();
        }

        // ---------------- Excelとのコピー&ペースト連携 ----------------

        private List<TableRow> GetTableRows(Table table)
        {
            var rows = new List<TableRow>();
            foreach (TableRowGroup rg in table.RowGroups)
                foreach (TableRow r in rg.Rows) rows.Add(r);
            return rows;
        }

        private Table FindEnclosingTable(TableCell cell)
        {
            if (!(cell.Parent is TableRow row)) return null;
            if (!(row.Parent is TableRowGroup rg)) return null;
            return rg.Parent as Table;
        }

        private class CellRange
        {
            public int MinRow, MaxRow, MinCol, MaxCol;
        }

        /// <summary>
        /// startCellとendCellを表の行/列グリッド内で特定し、両方を含む最小の矩形範囲を返す。
        /// どちらかのセルが見つからなければ null を返す。
        /// </summary>
        private CellRange GetSelectedCellRange(List<TableRow> rows, TableCell startCell, TableCell endCell)
        {
            int startRow = -1, startCol = -1, endRow = -1, endCol = -1;
            for (int r = 0; r < rows.Count; r++)
            {
                int c = rows[r].Cells.IndexOf(startCell);
                if (c >= 0) { startRow = r; startCol = c; }
                c = rows[r].Cells.IndexOf(endCell);
                if (c >= 0) { endRow = r; endCol = c; }
            }
            if (startRow < 0 || endRow < 0) return null;

            return new CellRange
            {
                MinRow = Math.Min(startRow, endRow),
                MaxRow = Math.Max(startRow, endRow),
                MinCol = Math.Min(startCol, endCol),
                MaxCol = Math.Max(startCol, endCol)
            };
        }

        private string RangeToTsv(List<TableRow> rows, CellRange range)
        {
            var lines = new List<string>();
            for (int r = range.MinRow; r <= range.MaxRow; r++)
            {
                var cells = rows[r].Cells.Cast<TableCell>().ToList();
                var rowTexts = new List<string>();
                for (int c = range.MinCol; c <= range.MaxCol && c < cells.Count; c++)
                {
                    rowTexts.Add(CellPlainText(cells[c]).Replace('\t', ' ').Replace("\r", " ").Replace("\n", " "));
                }
                lines.Add(string.Join("\t", rowTexts));
            }
            return string.Join("\r\n", lines);
        }

        private string RangeToHtmlFragment(List<TableRow> rows, CellRange range)
        {
            var sb = new StringBuilder();
            sb.Append("<table border=\"1\" cellspacing=\"0\" cellpadding=\"4\" style=\"border-collapse:collapse;\">");
            for (int r = range.MinRow; r <= range.MaxRow; r++)
            {
                sb.Append("<tr>");
                var cells = rows[r].Cells.Cast<TableCell>().ToList();
                for (int c = range.MinCol; c <= range.MaxCol && c < cells.Count; c++)
                {
                    // 選択範囲の先頭行が「表そのもののヘッダー行」である場合だけ th として書き出す。
                    string tag = r == 0 ? "th" : "td";
                    string text = WebUtility.HtmlEncode(CellPlainText(cells[c])).Replace("\n", "<br>");
                    sb.Append('<').Append(tag).Append(" style=\"border:1px solid #999999;padding:4px 8px;\">")
                      .Append(text).Append("</").Append(tag).Append('>');
                }
                sb.Append("</tr>");
            }
            sb.Append("</table>");
            return sb.ToString();
        }

        private string CellPlainText(TableCell cell)
        {
            var sb = new StringBuilder();
            foreach (Block b in cell.Blocks)
                if (b is Paragraph p) sb.Append(new TextRange(p.ContentStart, p.ContentEnd).Text);
            return sb.ToString().Trim();
        }

        private string TableToTsv(Table table)
        {
            var lines = new List<string>();
            foreach (var row in GetTableRows(table))
            {
                var cellTexts = row.Cells.Cast<TableCell>()
                    .Select(c => CellPlainText(c).Replace('\t', ' ').Replace("\r", " ").Replace("\n", " "));
                lines.Add(string.Join("\t", cellTexts));
            }
            return string.Join("\r\n", lines);
        }

        private string TableToHtmlFragment(Table table)
        {
            var sb = new StringBuilder();
            sb.Append("<table border=\"1\" cellspacing=\"0\" cellpadding=\"4\" style=\"border-collapse:collapse;\">");
            var rows = GetTableRows(table);
            for (int r = 0; r < rows.Count; r++)
            {
                sb.Append("<tr>");
                foreach (TableCell cell in rows[r].Cells)
                {
                    string tag = r == 0 ? "th" : "td";
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
        private string BuildHtmlClipboardFragment(string htmlBodyFragment)
        {
            const string htmlPrefix = "<html><body><!--StartFragment-->";
            const string htmlSuffix = "<!--EndFragment--></body></html>";
            const string headerTemplate =
                "Version:0.9\r\n" +
                "StartHTML:{0:0000000000}\r\n" +
                "EndHTML:{1:0000000000}\r\n" +
                "StartFragment:{2:0000000000}\r\n" +
                "EndFragment:{3:0000000000}\r\n";

            int headerByteLength = Encoding.UTF8.GetByteCount(string.Format(headerTemplate, 0, 0, 0, 0));
            int startHtml = headerByteLength;
            int startFragment = startHtml + Encoding.UTF8.GetByteCount(htmlPrefix);
            int endFragment = startFragment + Encoding.UTF8.GetByteCount(htmlBodyFragment);
            int endHtml = endFragment + Encoding.UTF8.GetByteCount(htmlSuffix);

            string header = string.Format(headerTemplate, startHtml, endHtml, startFragment, endFragment);
            return header + htmlPrefix + htmlBodyFragment + htmlSuffix;
        }

        /// <summary>Ctrl+Cでの表セル範囲コピー時に、TSVとCF_HTML形式をクリップボードへ追加し、
        /// Excelへの貼り付けが正しく表として認識されるようにする。</summary>
        public void HandleCopying(object sender, DataObjectCopyingEventArgs e)
        {
            if (isSourceMode() || e.IsDragDrop) return;

            var selection = editor.Selection;
            if (selection == null || selection.IsEmpty) return;

            var startCell = selection.Start?.Paragraph?.Parent as TableCell;
            var endCell = selection.End?.Paragraph?.Parent as TableCell;
            var anyCell = startCell ?? endCell;
            if (anyCell == null) return;

            var table = FindEnclosingTable(anyCell);
            if (table == null) return;

            var rows = GetTableRows(table);
            CellRange range = null;
            if (startCell != null && endCell != null &&
                FindEnclosingTable(startCell) == table && FindEnclosingTable(endCell) == table)
            {
                range = GetSelectedCellRange(rows, startCell, endCell);
            }

            // 正確なセル範囲が特定できればその範囲だけをコピーし、そうでなければ
            // （選択範囲が表の外にはみ出している場合など）表全体を書き出す。
            string tsv = range != null ? RangeToTsv(rows, range) : TableToTsv(table);
            string htmlFragment = range != null ? RangeToHtmlFragment(rows, range) : TableToHtmlFragment(table);
            e.DataObject.SetData(DataFormats.Text, tsv);
            e.DataObject.SetData(DataFormats.Html, BuildHtmlClipboardFragment(htmlFragment));
        }

        private List<List<string>> TryParseHtmlTable(string html)
        {
            var tableMatch = Regex.Match(html, "<table[^>]*>(.*?)</table>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (!tableMatch.Success) return null;

            var rows = new List<List<string>>();
            foreach (Match rowMatch in Regex.Matches(tableMatch.Groups[1].Value, "<tr[^>]*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                var cells = new List<string>();
                foreach (Match cellMatch in Regex.Matches(rowMatch.Groups[1].Value, "<t[dh][^>]*>(.*?)</t[dh]>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
                {
                    cells.Add(StripHtmlToText(cellMatch.Groups[1].Value));
                }
                if (cells.Count > 0) rows.Add(cells);
            }
            return rows.Count > 0 ? rows : null;
        }

        private string StripHtmlToText(string html)
        {
            string text = Regex.Replace(html, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<[^>]+>", "");
            text = WebUtility.HtmlDecode(text);
            return text.Trim();
        }

        private bool LooksLikeTsv(string text)
        {
            return !string.IsNullOrEmpty(text) && text.Contains('\t');
        }

        private List<List<string>> ParseTsv(string text)
        {
            var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            return lines.Select(line => line.Split('\t').ToList()).ToList();
        }

        /// <summary>解析済みの行/セルテキストからWPFのTableを組み立てて挿入する（HTMLまたはTSVの貼り付けから）。</summary>
        /// <param name="rows">解析済みの行データ（先頭行がヘッダーとして扱われる）。</param>
        public void InsertParsedTable(List<List<string>> rows)
        {
            if (rows == null || rows.Count == 0) return;
            int colCount = rows.Max(r => r.Count);
            if (colCount == 0) return;

            var table = new Table();
            for (int c = 0; c < colCount; c++) table.Columns.Add(new TableColumn());
            var rg = new TableRowGroup();
            table.RowGroups.Add(rg);

            for (int r = 0; r < rows.Count; r++)
            {
                var row = new TableRow();
                for (int c = 0; c < colCount; c++)
                {
                    string text = c < rows[r].Count ? rows[r][c] : "";
                    var cell = new TableCell(new Paragraph(new Run(text)))
                    {
                        BorderBrush = CellBorder,
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(8, 6, 8, 6)
                    };
                    if (r == 0)
                    {
                        cell.FontWeight = FontWeights.Bold;
                        cell.Background = HeaderBackground;
                    }
                    row.Cells.Add(cell);
                }
                rg.Rows.Add(row);
            }

            runAsProgrammaticChange(() =>
            {
                var para = editor.CaretPosition?.Paragraph;
                var trailingPara = new Paragraph();
                if (para != null && para.Parent is FlowDocument)
                {
                    editor.Document.Blocks.InsertAfter(para, table);
                    editor.Document.Blocks.InsertAfter(table, trailingPara);
                    if (string.IsNullOrWhiteSpace(new TextRange(para.ContentStart, para.ContentEnd).Text))
                        editor.Document.Blocks.Remove(para);
                }
                else
                {
                    editor.Document.Blocks.Add(table);
                    editor.Document.Blocks.Add(trailingPara);
                }

                if (rg.Rows.Count > 0 && rg.Rows[0].Cells.Count > 0 && rg.Rows[0].Cells[0].Blocks.FirstBlock is Paragraph fp)
                    editor.CaretPosition = fp.ContentStart;
            });

            refreshOutline();
            editor.Focus();
            markDirty();
        }

        /// <summary>貼り付け処理の入口。Excelなどからの表（HTML/TSV）を検出してTableへ変換し、
        /// コードブロック内へのプレーンテキスト貼り付けにも対応する。</summary>
        public void HandlePasting(object sender, DataObjectPastingEventArgs e)
        {
            if (isSourceMode()) return;

            // コードブロックへの貼り付け：常にリテラルなテキストとして、同じフェンス内に挿入する
            // （既定の貼り付け動作のままだと新しい段落に分割されてしまうため）。
            var currentPara = editor.CaretPosition?.Paragraph;
            if (currentPara != null && currentPara.Tag is CodeBlockInfo && e.SourceDataObject.GetDataPresent(DataFormats.Text))
            {
                string codeText = (string)e.SourceDataObject.GetData(DataFormats.Text);
                e.CancelCommand();
                insertPlainTextWithLineBreaks(codeText);
                return;
            }

            // Excel（および大半のリッチなコピー元）は、クリップボードにHTMLの<table>を乗せてくる。
            // これが「表がコピーされた」ことを検出する最も確実な方法。
            if (e.SourceDataObject.GetDataPresent(DataFormats.Html))
            {
                string html = (string)e.SourceDataObject.GetData(DataFormats.Html);
                var tableData = TryParseHtmlTable(html);
                if (tableData != null)
                {
                    e.CancelCommand();
                    InsertParsedTable(tableData);
                    return;
                }
            }

            // フォールバック：タブ区切りのプレーンテキスト（コピー時にHTMLを出さないアプリ向け）。
            if (e.SourceDataObject.GetDataPresent(DataFormats.Text))
            {
                string text = (string)e.SourceDataObject.GetData(DataFormats.Text);
                if (LooksLikeTsv(text))
                {
                    var tableData = ParseTsv(text);
                    if (tableData != null && tableData.Any(r => r.Count > 1))
                    {
                        e.CancelCommand();
                        InsertParsedTable(tableData);
                    }
                }
            }
        }
    }
}
