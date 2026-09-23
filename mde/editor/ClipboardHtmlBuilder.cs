// ClipboardHtmlBuilder.cs
//
// mde (Markdown インラインエディタ) の一部。
// 見出し・段落・箇条書き・タスクリスト・コードブロック・水平線・画像・表など、選択した
// 内容全般を右クリックメニュー「Excel用にコピー」でコピーした際、Excel等へ貼り付けたときに
// 見た目（書式）を保ったまま展開されるよう、クリップボード用のHTML断片（CF_HTML）と
// プレーンテキストを組み立てる。表のセル範囲だけをきっちり選択したコピー（TableEditor.
// TryCopySelectionForExcel）と対になる処理で、MainWindow.CopyForExcelItemClickが
// TableEditor側を先に呼び、そちらがtrueを返さなかった場合（表のセル範囲にきっちり収まる
// 選択ではなかった場合）にこちらを呼ぶ（詳細はTryCopySelectionForExcelのコメント参照）。
// 見出しや段落と表が混在する選択範囲（表全体を含むがセル範囲としては綺麗に特定できない
// もの）は、このクラスが表も含めて展開する。
//
// 以前はCtrl+C自体をDataObject.AddCopyingHandlerで乗っ取り、自動的にこの処理を行っていたが、
// クリップボードに常時HTML形式が載るようになった結果、mde同士のCtrl+C→Ctrl+V（本来Xaml/Rtf
// 形式で構造をそのまま復元するはずの貼り付け）まで壊れてしまうことが実機で判明したため撤回し、
// 右クリックメニューからの明示的な呼び出しのみにした（詳細はDEVELOPMENT_LOG.md参照）。
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
        /// （TryCopySelectionForExcel参照）。</summary>
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
            /// 空白行を挟む（箇条書きの項目同士・表の行同士は詰めたままにする。TryCopySelectionForExcel
            /// 参照）。</summary>
            public bool ParagraphKindFlg;

            /// <summary>行内に画像が含まれる場合、その画像の実際の高さ（ピクセル）。0の場合は
            /// 画像が無い、またはサイズ未確定。Excelでは&lt;img&gt;が行（セル）の高さを自動的に
            /// 広げてくれず、セルのheightスタイルを指定するだけでも次の行と重なって表示されて
            /// しまうことがあるため、この高さが実際のExcelの行何行分に相当するかを計算し、
            /// その行数分の空白行を後ろに挿入することで重なりを防ぐ（AppendImage・
            /// TryCopySelectionForExcel・ExcelDefaultRowHeightPx参照）。</summary>
            public double ImageHeightPx;
        }

        /// <summary>
        /// 右クリックメニュー「Excel用にコピー」用。表以外の部分（見出し・段落・箇条書き等）を
        /// 含む選択範囲について、TSV代わりのプレーンテキストとCF_HTML形式でクリップボードへ
        /// コピーし、Excelへの貼り付けが見た目を保ったまま行として展開されるようにする。
        /// TableEditor.TryCopySelectionForExcelが先に呼ばれ、選択範囲が表のセル範囲にきっちり
        /// 収まっていた場合はそちらがtrueを返して既にコピー済みのため、このメソッドは呼ばれない
        /// （MainWindow.CopyForExcelItemClick参照）。
        /// 以前はCtrl+C自体をDataObject.AddCopyingHandlerで乗っ取って自動的に行っていたが、
        /// クリップボードに常時HTML形式（&lt;table&gt;を含む）が載るようになった結果、mde同士の
        /// Ctrl+C→Ctrl+V（本来Xaml/Rtf形式で構造をそのまま復元するはずの貼り付け）まで、
        /// TableEditor.HandlePastingの「HTMLに&lt;table&gt;があれば表として解釈する」分岐に
        /// 誤って捕まってしまい、画像等が失われる形で壊れてしまうことが実機で判明したため撤回し、
        /// 明示的なメニュー操作からのみ呼び出す方式にした（詳細はDEVELOPMENT_LOG.md参照）。
        /// </summary>
        /// <returns>クリップボードへコピーした場合はtrue。選択が空、コピー対象の内容が
        /// 無かった場合等はfalseを返す。</returns>
        public bool TryCopySelectionForExcel()
        {
            if (m_isSourceMode())
            {
                return false;
            }

            var selection = m_editor.Selection;
            if (null == selection || selection.IsEmpty)
            {
                return false;
            }

            // 選択範囲が単一の段落（箇条書き項目・見出し含む）内に完全に収まっており、
            // かつその段落の一部分だけを選択している場合は、このメソッドでは何もしない
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
                    return false;
                }
            }

            var rows = new List<Row>();
            CollectRows(m_editor.Document.Blocks, selection.Start, selection.End, rows);
            var contentRows = rows.Where(r => r.HasContent).ToList();
            if (0 == contentRows.Count)
            {
                return false;
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
                    //
                    // 「折り返して全体を表示する」を貼り付け時に無効化するため、white-space:
                    // nowrap;を付けている（表のセル自体には付けない。TableEditor.cs参照。
                    // §14.108でのユーザー指摘により、表のセル内は引き続き有効のままにする）。
                    // §14.109でこれを追加した際、箇条書きの本文が途中で切れて見えるという
                    // report があり、当時はnowrapによる見た目上のオーバーフロー切り捨てが
                    // 原因と判断していったん撤回した（§14.110）。しかしその後、実際の原因は
                    // nowrapとは無関係に、箇条書き項目内でShift+Enterによる継続段落が
                    // BrContinuationInfoタグ不足によりコピー処理から丸ごと欠落していたこと
                    // であると判明し、CollectListRows側を修正済み（§14.111）。原因が別にあった
                    // ことが確認できたため、改めてnowrap指定を付け直している。
                    html.Append("<tr><td style=\"padding:4px 8px;white-space:nowrap;")
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

            var data = new DataObject();
            data.SetData(DataFormats.Text, string.Join("\r\n", textLines));
            data.SetData(DataFormats.Html, TableEditor.BuildHtmlClipboardFragment(html.ToString()));
            Clipboard.SetDataObject(data);
            return true;
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
                    // 表のセル範囲にきっちり収まる選択は、既存のTableEditor.
                    // TryCopySelectionForExcelが先に処理し、その場合このメソッド自体が
                    // 呼ばれない（MainWindow.CopyForExcelItemClick参照）。ここに来るのは、
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
                    if (b is Paragraph contPara)
                    {
                        // BrContinuationInfoタグでは判定しない。Shift+Enterによる項目内の
                        // 段落分割（HeadingCodeBlockEditor.InsertParagraphSplitAtCaret）は
                        // 生成する段落に元の段落のTagをそのままコピーするだけでBrContinuationInfo
                        // を設定せず、Markdown読み込み時の箇条書き項目（MarkdownConverter.
                        // BuildNestedList内のFlushPendingItem）も同様に新しい段落へTagを設定
                        // しないため、このタグはライブ入力・ファイル読み込みのどちらの経路でも
                        // 箇条書き項目内の継続段落には確実には付いていない。li.Blocks内では
                        // 先頭のmainPara・入れ子のList以外は必ず（Shift+Enterによる）項目自身の
                        // 継続段落であるという構造上の前提に基づき、MarkdownConverter.
                        // ListToMarkdown（Markdown書き出し側）・TableEditor.CellPlainText/
                        // CellHtmlContent（表セルのコピー）と同じく、タグを見ずに構造上の位置
                        // だけで継続段落と判定する。
                        if (!Intersects(contPara, a_selStart, a_selEnd))
                        {
                            continue;
                        }
                        if (null != itemRow)
                        {
                            AppendParagraphContinuation(itemRow, contPara, true);
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
            // （TryCopySelectionForExcelでの空白行の挿入判定に使う。Row.ParagraphKindFlg参照）。
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

        /// <param name="a_row">追記先の行。</param>
        /// <param name="a_contPara">継続段落（Shift+Enterによる改行後の段落）。</param>
        /// <param name="a_indentFlg">true の場合、行頭に半角スペース2つ分のインデントを付ける
        /// （箇条書きの項目内での継続行だと分かるようにするユーザー依頼。段落・見出し等、
        /// 箇条書きの外での継続行では付けない）。</param>
        private void AppendParagraphContinuation(Row a_row, Paragraph a_contPara, bool a_indentFlg = false)
        {
            a_row.Html.Append("<br>");
            a_row.Text.Append('\n');
            if (a_indentFlg)
            {
                // 通常の半角スペースのままだと、HTMLとして解釈される際に連続する空白が
                // 1つに詰められてしまい、Excelへの貼り付け時にインデントが消えてしまう
                // ことがあるため、HTML側は&nbsp;で書き出す（BlankRowHtmlの空白セルと
                // 同じ理由）。プレーンテキスト側は通常の半角スペースのままでよい。
                a_row.Html.Append("&nbsp;&nbsp;");
                a_row.Text.Append("  ");
            }
            var inlineList = new List<Inline>();
            foreach (Inline inl in a_contPara.Inlines)
            {
                inlineList.Add(inl);
            }
            for (int i = 0; i < inlineList.Count; i++)
            {
                AppendInline(a_row, inlineList[i], i + 1 < inlineList.Count);
            }
        }

        private void AppendInlinesToRow(Row a_row, InlineCollection a_inlines, string a_prefixText, bool a_skipFirstInlineFlg = false)
        {
            if (!string.IsNullOrEmpty(a_prefixText))
            {
                a_row.Html.Append(WebUtility.HtmlEncode(a_prefixText));
                a_row.Text.Append(a_prefixText);
            }
            var inlineList = new List<Inline>();
            foreach (Inline inl in a_inlines)
            {
                inlineList.Add(inl);
            }
            for (int i = 0; i < inlineList.Count; i++)
            {
                if (a_skipFirstInlineFlg && 0 == i)
                {
                    continue;
                }
                AppendInline(a_row, inlineList[i], i + 1 < inlineList.Count);
            }
        }

        /// <summary>1つのInlineをHTML/プレーンテキストへ変換して追記する。</summary>
        /// <param name="a_row">追記先の行。</param>
        /// <param name="a_inline">対象のInline。</param>
        /// <param name="a_hasMoreFlg">この呼び出し元の並びの中で、このInlineの後にさらに
        /// 内容が続くかどうか。画像の直後に他の内容（テキスト等）が同じ&lt;td&gt;内で
        /// 続くと、Excelへの貼り付け時に画像そのものが表示されなくなることが実機で
        /// 判明したため（AppendImage参照）、画像を独立した行にするための&lt;br&gt;を
        /// 挿入するかどうかの判断に使う。</param>
        private void AppendInline(Row a_row, Inline a_inline, bool a_hasMoreFlg)
        {
            if (a_inline is LineBreak)
            {
                a_row.Html.Append("<br>");
                a_row.Text.Append('\n');
            }
            else if (a_inline is InlineUIContainer iuc && iuc.Child is Image img)
            {
                AppendImage(a_row, img, a_hasMoreFlg);
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
                var nestedList = new List<Inline>();
                foreach (Inline nested in span.Inlines)
                {
                    nestedList.Add(nested);
                }
                for (int i = 0; i < nestedList.Count; i++)
                {
                    // Spanの末尾の要素については、Span自体の後にさらに内容が続くかどうか
                    // （呼び出し元から渡されたa_hasMoreFlg）も引き継ぐ。
                    bool childHasMoreFlg = i + 1 < nestedList.Count || a_hasMoreFlg;
                    AppendInline(a_row, nestedList[i], childHasMoreFlg);
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

        private void AppendImage(Row a_row, Image a_img, bool a_hasMoreFlg)
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

            // セルの左・上の罫線と画像が接して重なって見える点について、右下へずらす表示位置
            // 調整を試みたが（§14.115〜14.117。style="margin:...;"のショートハンド・
            // ロングハンド指定、<img>直前への&amp;nbsp;挿入のいずれも実機で全く効果が
            // 見られなかった）、ユーザー判断によりこの調整自体を断念した。Excelは、HTML
            // 貼り付けで挿入した<img>を、CSSのボックスモデルにもHTML上の出現位置にも
            // 影響されず、独自の埋め込みPictureオブジェクトとしてセルの左上へ固定的に
            // 配置しているとみられ、この経路での位置調整は行えない模様（詳細はDEVELOPMENT_LOG
            // §14.115〜14.118参照）。同じ調整を再度試みないよう記録しておく。
            a_row.Html.Append("<img src=\"").Append(WebUtility.HtmlEncode(fileUri)).Append('"');
            if (!double.IsNaN(a_img.Width) && a_img.Width > 0)
            {
                a_row.Html.Append(" width=\"").Append((int)a_img.Width).Append('"');
            }
            a_row.Html.Append('>');
            // 画像の直後に他の内容（テキスト等）が同じ<td>内で続く場合、Excelへの貼り付け時に
            // 画像そのものが表示されなくなる（画像が丸ごと消えてしまう）ことが実機で判明した。
            // 画像単体だけが<td>の内容である場合は問題なく表示されるため、画像を他の内容から
            // 独立した行にする（TableEditor.AppendCellInlinesの同じ対応も参照）。
            if (a_hasMoreFlg)
            {
                a_row.Html.Append("<br>");
            }
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
        /// HTML。段落間の余白・画像の高さ分の空白行のどちらにも同じものを使う。上記の
        /// 通常行と同じ理由でwhite-space:nowrap;を付けている（中身が&amp;nbsp;のみのため
        /// 実質的な影響は無いが、スタイルの一貫性のため揃えている）。</summary>
        private static string BlankRowHtml()
        {
            return "<tr><td style=\"padding:4px 8px;white-space:nowrap;\">&nbsp;</td></tr>";
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
