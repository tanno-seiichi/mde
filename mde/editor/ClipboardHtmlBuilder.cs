// ClipboardHtmlBuilder.cs
//
// mde (Markdown インラインエディタ) の一部。
// 見出し・段落・箇条書き・タスクリスト・コードブロック・水平線・画像・表など、選択した
// 内容全般を選択してコピーした際、Excel等へ貼り付けたときに見た目（書式）を保ったまま
// 展開されるよう、クリップボード用のHTML断片（CF_HTML）とプレーンテキストを組み立てる。
// 表のセル範囲だけをきっちり選択したコピー（TableEditor.HandleCopying）と対になる処理で、
// 両方ともMainWindowでDataObject.AddCopyingHandlerに登録し、TableEditorの方を先に登録する
// ことで、表のセル範囲選択時はそちらが優先されるようにしている（詳細はHandleCopyingの
// コメント参照）。見出しや段落と表が混在する選択範囲（表全体を含むがセル範囲としては
// 綺麗に特定できないもの）は、このクラスが表も含めて展開する。
//
// Excelは、貼り付けるHTMLに<table>タグが無いと、複数の段落や見出しを1つのセルへ改行区切りで
// まとめて貼り付けてしまい、行ごとに別セルへは分かれない。表のコピーと同じ感覚（1項目1行）で
// 貼り付けられるよう、選択範囲に含まれる段落・見出し・箇条書き項目等を、内部的に1列の表の
// 各行として組み立てている（選択範囲に表そのものが含まれる場合は、その表自身の行をそのまま
// 平らに展開する。Row.RawTrHtml参照）。

using mde.common;
using mde.manager;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace mde.editor
{
    /// <summary>
    /// 表以外の部分（見出し・段落・箇条書き・コードブロック・水平線・画像等）のコピー時に、
    /// Excel等への貼り付け用のHTML断片を組み立てる。MainWindowとは疎結合で、Editor本体・
    /// ImageManager（画像の実ファイルパス解決用）・ソースモード判定用delegateだけを受け取って
    /// 動作する。
    /// </summary>
    public class ClipboardHtmlBuilder
    {
        private readonly RichTextBox m_editor;
        private readonly ImageManager m_imageManager;
        private readonly TableEditor m_tableEditor;
        private readonly Func<bool> m_isSourceMode;

        /// <summary>Excelの既定の行の高さ（ピクセル）。既定値15pt（Excelの標準フォント・
        /// 標準の行の高さ）を、96dpiでのピクセル換算（1pt = 96/72px）で表した値。画像を
        /// 含む行の後に挿入する空白行の数（画像の高さが何行分に相当するか）の計算に使う
        /// （HandleCopying参照）。</summary>
        private const double ExcelDefaultRowHeightPx = 20.0;

        /// <param name="a_editor">編集対象のRichTextBox。</param>
        /// <param name="a_imageManager">画像の実ファイルパス解決用。</param>
        /// <param name="a_tableEditor">選択範囲に表が含まれていた場合、その表をHTMLの行として
        /// 展開するために使う（TableEditor.BuildTableRowsHtml／TableToTsv参照）。</param>
        /// <param name="a_isSourceMode">現在ソースモードかどうかを返すdelegate。</param>
        public ClipboardHtmlBuilder(RichTextBox a_editor, ImageManager a_imageManager, TableEditor a_tableEditor, Func<bool> a_isSourceMode)
        {
            m_editor = a_editor;
            m_imageManager = a_imageManager;
            m_tableEditor = a_tableEditor;
            m_isSourceMode = a_isSourceMode;
        }

        /// <summary>組み立て中の1行分の内容（プレーンテキストとHTML断片）。</summary>
        private class Row
        {
            public readonly StringBuilder Text = new StringBuilder();
            public readonly StringBuilder Html = new StringBuilder();
            public bool HasContent;
            public string TdStyleExtra = "";

            /// <summary>選択範囲に含まれる表を展開する場合に使う、&lt;tr&gt;...&lt;/tr&gt;の
            /// 並び（表全体分。複数行になり得る）。nullでなければ、このRowはHtml/TdStyleExtraを
            /// 使わず、この内容をそのまま（&lt;td&gt;で包まずに）出力する。</summary>
            public string RawTrHtml;

            /// <summary>段落・見出し・コードブロック・水平線・画像など、独立したブロックとして
            /// 扱う行はtrue（BuildParagraphRow参照）、箇条書きの項目や表の行はfalseのまま。
            /// true の行の前後には、Excel上でも編集画面に近い余白を再現するため、高さ1行分の
            /// 空白行を挟む（箇条書きの項目同士・表の行同士は詰めたままにする。HandleCopying
            /// 参照）。</summary>
            public bool ParagraphKindFlg;

            /// <summary>行内に画像が含まれる場合、その画像の実際の高さ（ピクセル）。0の場合は
            /// 画像が無い、またはサイズ未確定。Excelでは&lt;img&gt;が行（セル）の高さを自動的に
            /// 広げてくれず、セルのheightスタイルを指定するだけでも次の行と重なって表示されて
            /// しまうことがあるため、この高さが実際のExcelの行何行分に相当するかを計算し、
            /// その行数分の空白行を後ろに挿入することで重なりを防ぐ（AppendImage・
            /// HandleCopying・ExcelDefaultRowHeightPx参照）。</summary>
            public double ImageHeightPx;
        }

        /// <summary>
        /// Ctrl+Cでの表以外の部分（見出し・段落・箇条書き等）のコピー時に、TSV代わりの
        /// プレーンテキストとCF_HTML形式をクリップボードへ追加し、Excelへの貼り付けが
        /// 見た目を保ったまま行として展開されるようにする。TableEditor.HandleCopyingが
        /// このハンドラより先に登録されており、表のセル範囲を選択していた場合は既にHTML形式
        /// をセット済みのはずなので、その場合は何もしない（二重に上書きしない）。
        /// </summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        public void HandleCopying(object a_sender, DataObjectCopyingEventArgs a_args)
        {
            if (m_isSourceMode() || a_args.IsDragDrop)
            {
                return;
            }
            if (a_args.DataObject.GetDataPresent(DataFormats.Html))
            {
                return;
            }

            var selection = m_editor.Selection;
            if (null == selection || selection.IsEmpty)
            {
                return;
            }

            // 選択範囲が単一の段落（箇条書き項目・見出し含む）内に完全に収まっており、
            // かつその段落の一部分だけを選択している場合は、このハンドラでは何もしない
            // （単語の一部を選択しただけのコピーまで、書式付きの1行貼り付けに変えてしまうと、
            // 他アプリへの通常のコピー&ペーストの挙動まで変わってしまうため）。段落全体を
            // 選択している場合（トリプルクリック等）は対象に含める。
            var startPara = selection.Start?.Paragraph;
            var endPara = selection.End?.Paragraph;
            if (null != startPara && ReferenceEquals(startPara, endPara))
            {
                string selectedText = new TextRange(selection.Start, selection.End).Text;
                string wholeText = new TextRange(startPara.ContentStart, startPara.ContentEnd).Text;
                if (selectedText.Trim() != wholeText.Trim())
                {
                    return;
                }
            }

            var rows = new List<Row>();
            CollectRows(m_editor.Document.Blocks, selection.Start, selection.End, rows);
            var contentRows = rows.Where(r => r.HasContent).ToList();
            if (0 == contentRows.Count)
            {
                return;
            }

            // 外側の<table>自体は、表以外の部分（見出し・段落・箇条書き等）をExcelで1行1セルに
            // 展開させるためだけの、見た目には存在しないラッパー。border="1"にすると、
            // 表ではない普通の文章にまで罫線が付いてしまうため、あえてborder="0"にしている。
            // 選択範囲に本物の表（Row.RawTrHtml）が含まれる場合は、その表自身のセルが
            // 個別に罫線のスタイルを持っている（TableEditor.BuildTableRowsHtml参照）ため、
            // この外側のborder="0"の影響は受けない。
            var html = new StringBuilder();
            html.Append("<table border=\"0\" cellspacing=\"0\" cellpadding=\"4\" style=\"border-collapse:collapse;\">");
            var textLines = new List<string>();
            for (int i = 0; i < contentRows.Count; i++)
            {
                var row = contentRows[i];

                // 段落・見出し・コードブロック・水平線・画像（ParagraphKindFlg）は、編集画面上
                // では前後にブロック同士の余白があるが、罫線の無い1列の表として組み立てている
                // だけだと詰まって見えてしまう。前後どちらかがParagraphKindFlgの行との境目に、
                // 高さ1行分の空白行を挟むことで、編集画面に近い見た目に近づける。箇条書きの
                // 項目同士・表の行同士（どちらもParagraphKindFlgがfalse）の間は、元々詰めて
                // 表示されるものなので空白行を挟まない。ただし、直前の行が画像を含む行だった
                // 場合は、その画像の分の空白行（ImageHeightPx参照。下記）に既に余白1行分が
                // 含まれているため、ここでは重ねて挿入しない。
                bool previousRowHadImageFlg = 0 < i && 0 < contentRows[i - 1].ImageHeightPx;
                if (0 < i && !previousRowHadImageFlg && (row.ParagraphKindFlg || contentRows[i - 1].ParagraphKindFlg))
                {
                    html.Append(BlankRowHtml());
                }

                if (null != row.RawTrHtml)
                {
                    // 選択範囲に含まれていた表は、ネストした<table>にすると貼り付け先での
                    // 見え方が不確実なため、その表自身の行をそのままこの外側の<table>へ
                    // 平らに展開する（Row.RawTrHtmlのコメント参照）。
                    html.Append(row.RawTrHtml);
                }
                else
                {
                    // 表ではない行には罫線を付けない（水平線の行は例外として、TdStyleExtra
                    // 自身がborder-topを指定している。BuildParagraphRow参照）。
                    html.Append("<tr><td style=\"padding:4px 8px;")
                        .Append(row.TdStyleExtra).Append("\">")
                        .Append(row.Html).Append("</td></tr>");
                }
                textLines.Add(row.Text.ToString());

                if (0 < row.ImageHeightPx)
                {
                    // Excelは<img>に合わせて行の高さを自動的に広げてくれるとは限らず、
                    // セルのheightスタイルを指定するだけでは次の行と重なって表示されて
                    // しまうことがある。そのため、Excelの既定の行の高さ（約20px。15pt
                    // ×96/72dpi）を基準に、画像の高さが何行分に相当するかを計算し、
                    // その行数だけ実際に空白行を挿入する（＋余白として、さらに1行追加する。
                    // これが前後の段落用の空白行を兼ねる。上記previousRowHadImageFlg参照）。
                    int rowsForImage = (int)Math.Max(1.0, Math.Ceiling(row.ImageHeightPx / ExcelDefaultRowHeightPx));
                    for (int n = 0; n < rowsForImage + 1; n++)
                    {
                        html.Append(BlankRowHtml());
                    }
                }
            }
            html.Append("</table>");

            a_args.DataObject.SetData(DataFormats.Text, string.Join("\r\n", textLines));
            a_args.DataObject.SetData(DataFormats.Html, TableEditor.BuildHtmlClipboardFragment(html.ToString()));
        }

        // ======================================================================
        //  選択範囲に含まれるBlockを、1行ずつのRowへ組み立てる
        // ======================================================================

        private void CollectRows(BlockCollection a_blocks, TextPointer a_selStart, TextPointer a_selEnd, List<Row> a_rows)
        {
            CollectRowsInBlocks(a_blocks, a_selStart, a_selEnd, 0, a_rows);
        }

        private void CollectRowsInBlocks(IEnumerable<Block> a_blocks, TextPointer a_selStart, TextPointer a_selEnd, int a_depth, List<Row> a_rows)
        {
            Row previousRow = null;
            foreach (Block block in a_blocks)
            {
                if (block is Paragraph contPara && contPara.Tag is BrContinuationInfo)
                {
                    if (!Intersects(block, a_selStart, a_selEnd))
                    {
                        continue;
                    }
                    if (null != previousRow)
                    {
                        AppendParagraphContinuation(previousRow, contPara);
                    }
                    else
                    {
                        var row = BuildParagraphRow(contPara);
                        a_rows.Add(row);
                        previousRow = row;
                    }
                    continue;
                }

                if (block is Paragraph p)
                {
                    if (!Intersects(p, a_selStart, a_selEnd))
                    {
                        previousRow = null;
                        continue;
                    }
                    var row = BuildParagraphRow(p);
                    a_rows.Add(row);
                    previousRow = row;
                }
                else if (block is List list)
                {
                    previousRow = null;
                    CollectListRows(list, a_selStart, a_selEnd, a_depth, a_rows);
                }
                else if (block is Table table)
                {
                    // 表を単独で選択してコピーした場合は、既存のTableEditor.HandleCopyingが
                    // 先に処理する（登録順。このハンドラが呼ばれる時点では既にHTML形式が
                    // セット済みで、HandleCopying冒頭でreturnしている）。ここに来るのは、
                    // 見出しや段落と表が混在する選択範囲のうち、表を含む部分。m_tableEditorが
                    // 無い場合（通常は無いはずだが念のため）は対象外とする。
                    previousRow = null;
                    if (null != m_tableEditor && Intersects(table, a_selStart, a_selEnd))
                    {
                        var row = new Row { HasContent = true };
                        row.RawTrHtml = m_tableEditor.BuildTableRowsHtml(table);
                        row.Text.Append(m_tableEditor.TableToTsv(table));
                        a_rows.Add(row);
                    }
                }
                else
                {
                    previousRow = null;
                }
            }
        }

        private void CollectListRows(List a_list, TextPointer a_selStart, TextPointer a_selEnd, int a_depth, List<Row> a_rows)
        {
            bool orderedFlg = System.Windows.TextMarkerStyle.Decimal == a_list.MarkerStyle;
            int index = 0;
            foreach (ListItem li in a_list.ListItems)
            {
                index++;
                bool isTaskItemFlg = li.Blocks.FirstBlock is Paragraph firstPara &&
                    firstPara.Inlines.FirstInline is InlineUIContainer firstIuc &&
                    firstIuc.Child is CheckBox;

                Paragraph mainPara = li.Blocks.FirstBlock as Paragraph;
                Row itemRow = null;
                if (null != mainPara && Intersects(mainPara, a_selStart, a_selEnd))
                {
                    itemRow = BuildListItemRow(mainPara, a_depth, orderedFlg, index, isTaskItemFlg);
                    a_rows.Add(itemRow);
                }

                foreach (Block b in li.Blocks)
                {
                    if (ReferenceEquals(b, mainPara))
                    {
                        continue;
                    }
                    if (b is Paragraph contPara && contPara.Tag is BrContinuationInfo)
                    {
                        if (!Intersects(contPara, a_selStart, a_selEnd))
                        {
                            continue;
                        }
                        if (null != itemRow)
                        {
                            AppendParagraphContinuation(itemRow, contPara);
                        }
                        else
                        {
                            itemRow = BuildListItemRow(contPara, a_depth, orderedFlg, index, false);
                            a_rows.Add(itemRow);
                        }
                    }
                    else if (b is List nestedList)
                    {
                        CollectListRows(nestedList, a_selStart, a_selEnd, a_depth + 1, a_rows);
                    }
                    // ネストした表等、それ以外のBlockはここでは対象外とする。
                }
            }
        }

        // ======================================================================
        //  1行分のRowの組み立て
        // ======================================================================

        private Row BuildParagraphRow(Paragraph a_p)
        {
            // 段落・見出し・コードブロック・水平線・画像は、いずれも編集画面上では独立した
            // ブロックとして前後に余白を持つため、ParagraphKindFlgをtrueにしておく
            // （HandleCopyingでの空白行の挿入判定に使う。Row.ParagraphKindFlg参照）。
            var row = new Row { HasContent = true, ParagraphKindFlg = true };
            if (a_p.Tag is HorizontalRuleInfo)
            {
                row.TdStyleExtra = "border-top:2px solid #999999;padding:2px 8px;";
                row.Html.Append("&nbsp;");
                row.Text.Append("----------------------------------------");
                return row;
            }
            if (a_p.Tag is CodeBlockInfo)
            {
                string codeText = new TextRange(a_p.ContentStart, a_p.ContentEnd).Text;
                row.TdStyleExtra = "font-family:Consolas,monospace;font-size:9.5pt;white-space:pre-wrap;";
                row.Html.Append(WebUtility.HtmlEncode(codeText).Replace("\n", "<br>"));
                row.Text.Append(codeText);
                return row;
            }
            if (a_p.Tag is int level && level > 0)
            {
                row.TdStyleExtra = "font-weight:bold;font-size:" + HeadingFontSizePt(level).ToString(
                    "0.#", System.Globalization.CultureInfo.InvariantCulture) + "pt;";
                AppendInlinesToRow(row, a_p.Inlines, "");
                return row;
            }
            AppendInlinesToRow(row, a_p.Inlines, "");
            return row;
        }

        private Row BuildListItemRow(Paragraph a_mainPara, int a_depth, bool a_orderedFlg, int a_index, bool a_isTaskItemFlg)
        {
            var row = new Row { HasContent = true };
            string marker = a_isTaskItemFlg
                ? (IsChecked(a_mainPara) ? "☑ " : "☐ ")
                : (a_orderedFlg ? a_index + ". " : "・ ");
            string prefix = IndentPrefix(a_depth) + marker;
            AppendInlinesToRow(row, a_mainPara.Inlines, prefix, a_isTaskItemFlg);
            return row;
        }

        private void AppendParagraphContinuation(Row a_row, Paragraph a_contPara)
        {
            a_row.Html.Append("<br>");
            a_row.Text.Append('\n');
            foreach (Inline inline in a_contPara.Inlines)
            {
                AppendInline(a_row, inline);
            }
        }

        private void AppendInlinesToRow(Row a_row, InlineCollection a_inlines, string a_prefixText, bool a_skipFirstInlineFlg = false)
        {
            if (!string.IsNullOrEmpty(a_prefixText))
            {
                a_row.Html.Append(WebUtility.HtmlEncode(a_prefixText));
                a_row.Text.Append(a_prefixText);
            }
            bool firstFlg = true;
            foreach (Inline inline in a_inlines)
            {
                bool skipThisFlg = a_skipFirstInlineFlg && firstFlg;
                firstFlg = false;
                if (skipThisFlg)
                {
                    continue;
                }
                AppendInline(a_row, inline);
            }
        }

        private void AppendInline(Row a_row, Inline a_inline)
        {
            if (a_inline is LineBreak)
            {
                a_row.Html.Append("<br>");
                a_row.Text.Append('\n');
            }
            else if (a_inline is InlineUIContainer iuc && iuc.Child is Image img)
            {
                AppendImage(a_row, img);
            }
            else if (a_inline is InlineUIContainer cbIuc && cbIuc.Child is CheckBox)
            {
                // 箇条書きのタスク項目先頭のチェックボックスは、行の先頭に付けたマーカー文字
                // （☑/☐）で既に状態を表現しているため、ここでは何も出力しない。
            }
            else if (a_inline is Run run)
            {
                AppendRun(a_row, run);
            }
            else if (a_inline is Span span)
            {
                foreach (Inline nested in span.Inlines)
                {
                    AppendInline(a_row, nested);
                }
            }
        }

        private void AppendRun(Row a_row, Run a_run)
        {
            if (a_run.Tag is AnchorInfo)
            {
                return;
            }
            if (string.IsNullOrEmpty(a_run.Text))
            {
                return;
            }
            if (a_run.Tag is LinkInfo linkInfo)
            {
                AppendLinkRun(a_row, a_run.Text, linkInfo.Url);
                return;
            }

            a_row.Text.Append(a_run.Text);
            string encoded = WebUtility.HtmlEncode(a_run.Text);
            string styleTag = a_run.Tag as string;
            if ("bold" == styleTag)
            {
                a_row.Html.Append("<b>").Append(encoded).Append("</b>");
            }
            else if ("strikethrough" == styleTag)
            {
                a_row.Html.Append("<s>").Append(encoded).Append("</s>");
            }
            else if ("underline" == styleTag)
            {
                a_row.Html.Append("<u>").Append(encoded).Append("</u>");
            }
            else if ("inline-code" == styleTag)
            {
                a_row.Html.Append("<code style=\"font-family:Consolas,monospace;background:#f0efe9;padding:0 3pt;\">")
                    .Append(encoded).Append("</code>");
            }
            else if ("highlight" == styleTag)
            {
                a_row.Html.Append("<mark>").Append(encoded).Append("</mark>");
            }
            else
            {
                a_row.Html.Append(encoded);
            }
        }

        private void AppendLinkRun(Row a_row, string a_text, string a_url)
        {
            a_row.Text.Append(a_text);
            string encodedText = WebUtility.HtmlEncode(a_text);
            bool isExternalFlg = !string.IsNullOrWhiteSpace(a_url) &&
                Regex.IsMatch(a_url, "^[a-zA-Z][a-zA-Z0-9+.-]*:") &&
                !Regex.IsMatch(a_url, "^[a-zA-Z]:[\\\\/]");
            if (isExternalFlg)
            {
                a_row.Html.Append("<a href=\"").Append(WebUtility.HtmlEncode(a_url)).Append("\">")
                    .Append(encodedText).Append("</a>");
                return;
            }

            // コピーした断片の中には、見出しへのジャンプ先（アンカー）や、リンク先の他ファイルが
            // 含まれるとは限らないため、文書内のアンカーリンク（#始まり）・他ファイルを開く
            // リンクは、リンクとしては機能しないプレーンテキストとして書き出す。
            a_row.Html.Append(encodedText);
        }

        private void AppendImage(Row a_row, Image a_img)
        {
            // data URI（Base64埋め込み）での実装を試したところ、実機でのExcelへの貼り付けで
            // 画像が表示されないことが判明したため、一時ファイルへコピーしてfile://で参照する
            // 方式にしている（ImageManager.WriteTempFileForClipboard参照）。
            string fileUri = m_imageManager.WriteTempFileForClipboard(a_img);
            if (string.IsNullOrEmpty(fileUri))
            {
                string alt = (a_img.Tag as ImageInfo)?.Alt;
                string placeholder = "[画像" + (string.IsNullOrEmpty(alt) ? "" : ": " + alt) + "]";
                a_row.Text.Append(placeholder);
                a_row.Html.Append(WebUtility.HtmlEncode(placeholder));
                return;
            }

            a_row.Html.Append("<img src=\"").Append(WebUtility.HtmlEncode(fileUri)).Append('"');
            if (!double.IsNaN(a_img.Width) && a_img.Width > 0)
            {
                a_row.Html.Append(" width=\"").Append((int)a_img.Width).Append('"');
            }
            a_row.Html.Append('>');
            a_row.Text.Append("[画像]");

            // 画像の実際の高さを覚えておき、後ろに何行分の空白行を挿入するかの計算に使う
            // （次の行と重ならないようにするため。Row.ImageHeightPx参照）。1つの行に
            // 複数の画像が含まれる場合は、最も高いものに合わせる。
            if (!double.IsNaN(a_img.Height) && a_img.Height > 0)
            {
                a_row.ImageHeightPx = Math.Max(a_row.ImageHeightPx, a_img.Height);
            }
        }

        // ======================================================================
        //  雑多なヘルパー
        // ======================================================================

        /// <summary>組み立てている表へ挿入する、中身が空の1行分（&lt;tr&gt;...&lt;/tr&gt;）の
        /// HTML。段落間の余白・画像の高さ分の空白行のどちらにも同じものを使う。</summary>
        private static string BlankRowHtml()
        {
            return "<tr><td style=\"padding:4px 8px;\">&nbsp;</td></tr>";
        }

        private static bool Intersects(Block a_block, TextPointer a_selStart, TextPointer a_selEnd)
        {
            return a_block.ContentStart.CompareTo(a_selEnd) < 0 && a_block.ContentEnd.CompareTo(a_selStart) > 0;
        }

        private static bool IsChecked(Paragraph a_para)
        {
            return a_para.Inlines.FirstInline is InlineUIContainer iuc &&
                iuc.Child is CheckBox cb &&
                true == cb.IsChecked;
        }

        private static string IndentPrefix(int a_depth)
        {
            return string.Concat(Enumerable.Repeat("　　", a_depth));
        }

        private static double HeadingFontSizePt(int a_level)
        {
            switch (a_level)
            {
                case 1: return 22;
                case 2: return 18;
                case 3: return 15.5;
                case 4: return 14;
                case 5: return 13;
                default: return 12;
            }
        }

    }
}
