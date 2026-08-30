// MarkdownConverter.cs
//
// mde (MarkDown インラインエディタ) の一部。
// MarkDownテキストとFlowDocument（画面表示用の内部構造）を相互に変換するクラス。
// 画像の生成・解決にはImageManagerを、「編集していないブロックは元テキストのまま保存」の
// 仕組みにはOriginalTextTrackerを、それぞれ協力オブジェクトとして利用する。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace mde
{
    /// <summary>
    /// MarkDown文字列 ⇔ FlowDocument の相互変換一式。見出し・段落・箇条書き（入れ子・順序付き
    /// 含む）・表・コードブロック・インライン装飾（太字/取り消し線/インラインコード/リンク/画像）
    /// に対応する。
    /// </summary>
    public class MarkdownConverter
    {
        private static readonly Brush HEADER_BACKGROUND = new SolidColorBrush(Color.FromRgb(0xF8, 0xF8, 0xF8));
        private static readonly Brush CELL_BORDER = new SolidColorBrush(Color.FromRgb(0xDD, 0xDF, 0xE2));
        private static readonly Brush LINK_BRUSH = new SolidColorBrush(Color.FromRgb(0x09, 0x69, 0xDA));

        /// <summary>
        /// 表の列揃えで、「明示的な左揃え（:---）」と「揃え指定なし（---）」を区別して保存する
        /// ために、表セルの段落（Paragraph）のTagに設定するマーカー文字列。2026-08-29追記
        /// （DESIGN.md参照）。WPFの Paragraph.TextAlignment は Left/Center/Right/Justify の
        /// 4値しか持てず、この2つはどちらも TextAlignment.Left になってしまい区別できないため、
        /// 別途Tagで「明示的に左揃えだった」ことだけを覚えておく（Center/Rightは
        /// TextAlignmentの値自体が一意に対応するので、この印は不要）。
        /// </summary>
        private const string ALIGN_LEFT_EXPLICIT_TAG = "align-left-explicit";

        /// <summary>
        /// 文中の画像（HTML/MarkDown）、`コード`、**太字**、~~取り消し線~~、&lt;u&gt;下線&lt;/u&gt;、
        /// ==ハイライト==、[リンク](a_url)、&lt;https://...&gt; 自動リンク、
        /// &lt;email@example.com&gt; メールアドレス自動リンク を検出する正規表現。
        /// CommonMark/GFMには下線の標準的な記法がないため、下線はHTMLの&lt;u&gt;タグをそのまま
        /// 埋め込む方式にしている（既存の&lt;a id="..."&gt;アンカー・&lt;url&gt;自動リンクと同じ
        /// 「HTMLタグをそのまま埋め込む」という前例に倣った）。
        /// 2026-08-29追記：グループ番号は以下の通り（ハイライト・メールアドレス自動リンクを
        /// 追加したため、それ以前からある番号も一部詰め直している。DESIGN.md参照）。
        /// 1:imgタグ／2,3,4:![alt](src)（alt,src)／5,6:`code`／7,8:**bold**／9,10:~~strike~~／
        /// 11,12:&lt;u&gt;underline&lt;/u&gt;／13,14:==highlight==／15,16,17:[text](url)（text,url)／
        /// 18,19:&lt;http(s)://url&gt;／20,21:&lt;email&gt;／22,23:&lt;a id="..."&gt;&lt;/a&gt;
        /// </summary>
        private static readonly Regex INLINE_CONTENT_REGEX = new Regex(
            "(<img\\s+[^>]*?/?>)|(!\\[([^\\]]*)\\]\\(((?:[^()]|\\([^()]*\\))+)\\))|(`([^`]+)`)|(\\*\\*([^*]+)\\*\\*)|(~~([^~]+)~~)|(<u>([^<]+)</u>)|(==([^=]+)==)|((?<!!)\\[([^\\]]*)\\]\\(((?:[^()]|\\([^()]*\\))+)\\))|(<(https?://[^\\s<>]+)>)|(<([^\\s<>@]+@[^\\s<>@]+\\.[^\\s<>@]+)>)|(<a\\s+id=\"([^\"]+)\"\\s*>\\s*</a>)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// [text](url "title") / ![alt](src "title") の "(...)" の中身（URLとタイトルの両方を
        /// 含む生文字列）を、URL部分とタイトル部分（あれば）に分割する。2026-08-29追記
        /// （DESIGN.md参照）。以前はタイトル部分を解析できず、URLの一部として誤って解釈して
        /// しまいリンク・画像が壊れる不具合があったため対応した。
        /// </summary>
        /// <param name="a_raw">"(...)" の中身の生文字列。</param>
        /// <param name="a_url">分割後のURL部分。</param>
        /// <param name="a_title">分割後のタイトル部分（タイトルが無ければnull）。</param>
        private static void SplitUrlAndTitle(string a_raw, out string a_url, out string a_title)
        {
            var m = Regex.Match(a_raw, "^(.*?)\\s+[\"']([^\"']*)[\"']\\s*$");
            if (m.Success)
            {
                a_url = m.Groups[1].Value;
                a_title = m.Groups[2].Value;
            }
            else
            {
                a_url = a_raw;
                a_title = null;
            }
        }

        private readonly OriginalTextTracker m_originalTextTracker;
        private readonly ImageManager m_imageManager;
        private readonly Func<bool> m_preserveSourceLineBreaksFlg;

        /// <summary>
        /// MarkdownConverterを構築する。
        /// </summary>
        /// <param name="a_originalTextTracker">「元テキスト保持」の追跡役。</param>
        /// <param name="a_imageManager">画像の生成・解決を担当するクラス。</param>
        /// <param name="a_preserveSourceLineBreaksFlg">段落中の途中改行（空行を伴わない、ソース上の
        /// 単純な改行）を、そのまま見た目の改行として表示するか（true。mde/Typora従来の表示）、
        /// それとも空行が入るまでは改行しない、CommonMark/VSCodeのMarkDownプレビュー標準の表示に
        /// するか（false）を返すデリゲート。省略時（null）はtrue（従来動作）として扱う。</param>
        public MarkdownConverter(OriginalTextTracker a_originalTextTracker, ImageManager a_imageManager,
            Func<bool> a_preserveSourceLineBreaksFlg = null)
        {
            this.m_originalTextTracker = a_originalTextTracker;
            this.m_imageManager = a_imageManager;
            this.m_preserveSourceLineBreaksFlg = a_preserveSourceLineBreaksFlg;
        }

        // ======================================================================
        //  FlowDocument → MarkDown
        // ======================================================================

        /// <summary>
        /// FlowDocumentをMarkDown文字列へ書き出す。編集されていないブロックは元テキストを
        /// そのまま使い（OriginalTextTracker参照）、編集済み・新規のブロックは現在の構造から
        /// 新しく組み立て直す。
        /// </summary>
        /// <param name="a_doc">対象の文書。</param>
        /// <returns>改行はすべて "\n" のMarkDownテキスト（実ファイルへの改行コード変換は保存時に行う）。</returns>
        public string DocumentToMarkdown(FlowDocument a_doc)
        {
            var lines = new List<string>();
            foreach (Block block in a_doc.Blocks)
            {
                string s = m_originalTextTracker.TryGetOriginal(block, out var original) ? original : BlockToMarkdown(block);
                if (!string.IsNullOrWhiteSpace(s))
                {
                    lines.Add(s);
                }
            }
            return string.Join("\n\n", lines);
        }

        /// <summary>1つのトップレベルブロックを、種類（見出し/段落・コードブロック・箇条書き・表）に
        /// 応じて適切な変換関数へ振り分ける。コードブロック丸ごとコピー機能からも利用される。</summary>
        /// <param name="a_block">対象のブロック。</param>
        /// <returns>変換後のMarkDown文字列。</returns>
        public string BlockToMarkdown(Block a_block)
        {
            if (a_block is Paragraph p)
            {
                if (p.Tag is HorizontalRuleInfo)
                {
                    return "---";
                }
                if (p.Tag is CodeBlockInfo codeInfo)
                {
                    var sb = new StringBuilder();
                    AppendInlinesMarkdown(p.Inlines, sb);
                    string codeText = sb.ToString().Trim('\r', '\n');
                    return "```" + codeInfo.m_language + "\n" + codeText + "\n```";
                }
                int level = p.Tag is int lv ? lv : 0;
                string text = ParagraphInlineToMarkdown(p);
                return level > 0 ? new string('#', level) + " " + text : text;
            }
            if (a_block is List list)
            {
                return ListToMarkdown(list, 0);
            }
            if (a_block is Table table)
            {
                return TableToMarkdown(table);
            }
            return "";
        }

        /// <summary>
        /// 箇条書き（入れ子含む）をMarkDownへ書き出す。順序付きリストは連番で書き出すが、
        /// リストが「常に同じ数字を使う」スタイル（Tag=="const"）としてマークされている場合は
        /// すべての項目を "1." として書き出す。
        /// </summary>
        /// <param name="a_list">対象のリスト。</param>
        /// <param name="a_level">見出しレベル。</param>
        /// <returns>変換後のMarkDown文字列。</returns>
        private string ListToMarkdown(List a_list, int a_level)
        {
            string indent = new string(' ', a_level * 3);
            bool orderedFlg = a_list.MarkerStyle == TextMarkerStyle.Decimal;
            bool constantNumberingFlg = orderedFlg && (a_list.Tag as string) == "const";
            string bulletMarker = orderedFlg ? null : ((a_list.Tag as string) ?? "*");
            var lines = new List<string>();
            int number = 1;
            foreach (ListItem li in a_list.ListItems)
            {
                var ownPara = li.Blocks.FirstBlock as Paragraph;
                string ownText = null != ownPara ? ParagraphInlineToMarkdown(ownPara) : "";
                var parts = ownText.Split('\n');
                string prefix = orderedFlg ? ((constantNumberingFlg ? 1 : number) + ". ") : (bulletMarker + " ");
                lines.Add(indent + prefix + parts[0]);
                string contIndent = indent + new string(' ', prefix.Length);
                for (int k = 1; k < parts.Length; k++)
                {
                    lines.Add(contIndent + parts[k]);
                }

                foreach (Block b in li.Blocks)
                {
                    if (b is List nested)
                    {
                        lines.Add(ListToMarkdown(nested, a_level + 1));
                    }
                }
                number++;
            }
            return string.Join("\n", lines);
        }

        /// <summary>表をGitHub-flavored MarkDown形式の表構文へ書き出す。</summary>
        /// <param name="a_table">対象の表。</param>
        /// <returns>変換後のMarkDown文字列。</returns>
        private string TableToMarkdown(Table a_table)
        {
            var rows = new List<TableRow>();
            foreach (TableRowGroup rg in a_table.RowGroups)
            {
                foreach (TableRow r in rg.Rows)
                {
                    rows.Add(r);
                }
            }
            if (0 == rows.Count)
            {
                return "";
            }

            var mdRows = new List<string>();
            foreach (var row in rows)
            {
                var cells = new List<string>();
                foreach (TableCell cell in row.Cells)
                {
                    var sb = new StringBuilder();
                    foreach (Block b in cell.Blocks)
                    {
                        if (b is Paragraph cp)
                        {
                            sb.Append(ParagraphInlineToMarkdown(cp));
                        }
                    }
                    cells.Add(sb.ToString().Replace("|", "\\|"));
                }
                mdRows.Add("| " + string.Join(" | ", cells) + " |");
            }
            // 2026-08-29追記：ヘッダー行の各セルの段落のTextAlignmentから、区切り行の
            // コロンを組み立てる（DESIGN.md参照）。中央揃えは":---:"、右揃えは"---:"。
            // 左揃えは、Paragraph.Tagに付けた印（ALIGN_LEFT_EXPLICIT_TAG）を見て、
            // 「明示的な左揃え（:---）」だったか「揃え指定なし（---）」だったかを区別する
            // （同日中の追記：TextAlignmentだけではこの2つを区別できないため、Tagで別途
            // 覚えておくようにした）。
            var sepCells = new List<string>();
            foreach (TableCell headerCell in rows[0].Cells)
            {
                var headerPara = headerCell.Blocks.FirstBlock as Paragraph;
                TextAlignment alignment = headerPara?.TextAlignment ?? TextAlignment.Left;
                if (TextAlignment.Center == alignment)
                {
                    sepCells.Add(":---:");
                }
                else if (TextAlignment.Right == alignment)
                {
                    sepCells.Add("---:");
                }
                else if (ALIGN_LEFT_EXPLICIT_TAG == (headerPara?.Tag as string))
                {
                    sepCells.Add(":---");
                }
                else
                {
                    sepCells.Add("---");
                }
            }
            string sep = "| " + string.Join(" | ", sepCells) + " |";

            var result = new List<string> { mdRows[0], sep };
            result.AddRange(mdRows.Skip(1));
            return string.Join("\n", result);
        }

        /// <summary>段落のInlinesをMarkDownテキストへ書き出す（見出し・段落・箇条書き項目・
        /// 表セルの内容で共通して使う）。</summary>
        /// <param name="a_p">対象の段落。</param>
        /// <returns>変換後のMarkDown文字列。</returns>
        private string ParagraphInlineToMarkdown(Paragraph a_p)
        {
            var sb = new StringBuilder();
            AppendInlinesMarkdown(a_p.Inlines, sb);
            return sb.ToString().Trim();
        }

        /// <summary>
        /// インライン要素の中心となる書き出し処理。太字/取り消し線/インラインコードが
        /// 改行をまたいで連続している場合は、行ごとに分割せず1つの**/~~/`にまとめる。
        /// リンクのRunは [a_text](a_url) または &lt;a_url&gt; に戻す。
        /// </summary>
        /// <param name="a_inlines">対象のInlineコレクション。</param>
        /// <param name="a_sb">追記先のStringBuilder。</param>
        private void AppendInlinesMarkdown(InlineCollection a_inlines, StringBuilder a_sb)
        {
            var inlineList = a_inlines.Cast<Inline>().ToList();
            int i = 0;
            while (i < inlineList.Count)
            {
                Inline inline = inlineList[i];

                if (inline is Run run && run.Tag is string tag &&
                    ("bold" == tag ||
                     "strikethrough" == tag ||
                     "underline" == tag ||
                     "highlight" == tag ||
                     "inline-code" == tag))
                {
                    var spanText = new StringBuilder();
                    spanText.Append(run.Text.Replace("​", ""));
                    int j = i + 1;
                    while (j + 1 < inlineList.Count &&
                           inlineList[j] is LineBreak &&
                           inlineList[j + 1] is Run nextRun &&
                           (nextRun.Tag as string) == tag)
                    {
                        spanText.Append('\n').Append(nextRun.Text.Replace("​", ""));
                        j += 2;
                    }

                    string content = spanText.ToString();
                    if ("inline-code" == tag)
                    {
                        a_sb.Append('`').Append(content).Append('`');
                    }
                    else if ("bold" == tag)
                    {
                        a_sb.Append("**").Append(content).Append("**");
                    }
                    else if ("strikethrough" == tag)
                    {
                        a_sb.Append("~~").Append(content).Append("~~");
                    }
                    else if ("highlight" == tag)
                    {
                        a_sb.Append("==").Append(content).Append("==");
                    }
                    else
                    {
                        a_sb.Append("<u>").Append(content).Append("</u>");
                    }

                    i = j;
                    continue;
                }

                if (inline is LineBreak)
                {
                    a_sb.Append('\n');
                }
                else if (inline is Run linkRun && linkRun.Tag is LinkInfo linkInfo)
                {
                    string content = linkRun.Text.Replace("​", "");
                    if (linkInfo.m_isEmailAutoLinkFlg)
                    {
                        // 2026-08-29追記：メールアドレス自動リンク。保存されているURLは
                        // "mailto:"付きなので、書き戻す際は取り除く（DESIGN.md参照）。
                        string email = linkInfo.m_url != null && linkInfo.m_url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                            ? linkInfo.m_url.Substring("mailto:".Length)
                            : linkInfo.m_url;
                        a_sb.Append('<').Append(email).Append('>');
                    }
                    else if (linkInfo.m_isAutoLinkFlg && content == linkInfo.m_url)
                    {
                        a_sb.Append('<').Append(linkInfo.m_url).Append('>');
                    }
                    else
                    {
                        a_sb.Append('[').Append(content).Append("](").Append(linkInfo.m_url);
                        if (!string.IsNullOrEmpty(linkInfo.m_title))
                        {
                            // 2026-08-29追記：タイトル属性（DESIGN.md参照）。
                            a_sb.Append(" \"").Append(linkInfo.m_title).Append('"');
                        }
                        a_sb.Append(')');
                    }
                }
                else if (inline is Run anchorRun && anchorRun.Tag is AnchorInfo anchorInfo)
                {
                    a_sb.Append("<a id=\"").Append(anchorInfo.m_id).Append("\"></a>");
                }
                else if (inline is Run escapedRun && (escapedRun.Tag as string) == "escaped")
                {
                    a_sb.Append('\\').Append(escapedRun.Text);
                }
                else if (inline is Run plainRun)
                {
                    a_sb.Append(plainRun.Text.Replace("​", ""));
                }
                else if (inline is InlineUIContainer iuc && iuc.Child is Image img)
                {
                    a_sb.Append(ImageToMarkdownString(img));
                }
                else if (inline is InlineUIContainer cbIuc && cbIuc.Child is CheckBox taskCheckBox)
                {
                    // 2026-08-29追記：タスクリストのチェックボックス（DESIGN.md参照）。
                    a_sb.Append(true == taskCheckBox.IsChecked ? "[x] " : "[ ] ");
                }
                else if (inline is Span span)
                {
                    AppendInlinesMarkdown(span.Inlines, a_sb);
                }
                i++;
            }
        }

        /// <summary>埋め込み画像を、元の記法（MarkDownの![]()、またはHTMLの&lt;a_img&gt;）に戻す。</summary>
        /// <param name="a_img">対象の画像。</param>
        /// <returns>変換後のMarkDown文字列（画像の記法）。</returns>
        private string ImageToMarkdownString(Image a_img)
        {
            var info = a_img.Tag as ImageInfo;
            string src = info?.m_originalSrc ?? "";
            string alt = info?.m_alt ?? "";
            if ("md" == info?.m_format)
            {
                string result = "![" + alt + "](" + src;
                if (!string.IsNullOrEmpty(info?.m_title))
                {
                    // 2026-08-29追記：タイトル属性（DESIGN.md参照）。
                    result += " \"" + info.m_title + "\"";
                }
                result += ")";
                return result;
            }

            string tag = "<img src=\"" + src + "\" alt=\"" + alt + "\"";
            if (!string.IsNullOrEmpty(info?.m_style))
            {
                tag += " style=\"" + info.m_style + "\"";
            }
            tag += " />";
            return tag;
        }

        // ======================================================================
        //  MarkDown → FlowDocument
        // ======================================================================

        /// <summary>
        /// MarkDown文字列全体を解析し、FlowDocumentへ反映する。見出し・フェンス付き
        /// コードブロック・箇条書き（入れ子・「緩い」リストの空行含む）・順序付きリスト・表・
        /// 通常の段落に対応する。各ブロックの元のソーステキストも記憶し、
        /// あとで無編集のまま保存する場合にそのまま使えるようにする。
        /// </summary>
        /// <param name="a_md">解析するMarkDownソース。</param>
        /// <param name="a_doc">反映先の文書（最初にクリアされる）。</param>
        public void MarkdownToDocument(string a_md, FlowDocument a_doc)
        {
            a_doc.Blocks.Clear();
            m_originalTextTracker.Clear();
            var lines = a_md.Replace("\r\n", "\n").Split('\n');
            int i = 0;
            while (i < lines.Length)
            {
                string line = lines[i];
                if (IsEffectivelyBlankLine(line)) { i++; continue; }

                int blockStart = i;

                if (line.TrimStart().StartsWith("```"))
                {
                    string language = Regex.Match(line.TrimStart(), "^```(\\S*)").Groups[1].Value;
                    i++;
                    var codeLines = new List<string>();
                    while (i < lines.Length &&
                           "```" != lines[i].Trim())
                    {
                        codeLines.Add(lines[i]);
                        i++;
                    }
                    if (i < lines.Length)
                    {
                        i++; // 閉じフェンスを読み飛ばす
                    }

                    var codePara = new Paragraph();
                    BlockStyles.ApplyCodeBlockStyle(codePara, language);
                    for (int k = 0; k < codeLines.Count; k++)
                    {
                        if (k > 0)
                        {
                            codePara.Inlines.Add(new LineBreak());
                        }
                        codePara.Inlines.Add(new Run(codeLines[k]));
                    }
                    a_doc.Blocks.Add(codePara);
                    m_originalTextTracker.Record(codePara, lines, blockStart, i);
                    continue;
                }

                var hMatch = Regex.Match(line, "^(#{1,6})\\s+(.*)$");
                if (hMatch.Success)
                {
                    var p = new Paragraph();
                    BlockStyles.ApplyHeadingStyle(p, hMatch.Groups[1].Value.Length);
                    AppendInlineMarkdownToParagraph(p, hMatch.Groups[2].Value, false);
                    a_doc.Blocks.Add(p);
                    i++;
                    m_originalTextTracker.Record(p, lines, blockStart, i);
                    continue;
                }

                // 2026-08-29追記：水平線（---/***/___。3個以上の同じ文字が行全体を占める場合のみ。
                // "- - -" のように間にスペースが入る書き方には対応しない。箇条書きの判定
                // ロジック（マーカーの直後に空白が必要）と衝突しないよう、意図的にこの
                // 「間にスペースなし」の書き方のみをサポート対象にしている（DESIGN.md参照）。
                if (Regex.IsMatch(line, "^ {0,3}([-*_])\\1{2,}\\s*$"))
                {
                    var hrPara = new Paragraph();
                    BlockStyles.ApplyHorizontalRuleStyle(hrPara);
                    a_doc.Blocks.Add(hrPara);
                    i++;
                    m_originalTextTracker.Record(hrPara, lines, blockStart, i);
                    continue;
                }

                if (Regex.IsMatch(line, "^\\s*([*+-]|\\d+\\.)\\s+"))
                {
                    var listLines = new List<string>();
                    while (i < lines.Length)
                    {
                        if (Regex.IsMatch(lines[i], "^\\s*([*+-]|\\d+\\.)\\s+"))
                        {
                            listLines.Add(lines[i]);
                            i++;
                            continue;
                        }
                        if (listLines.Count > 0 && !string.IsNullOrWhiteSpace(lines[i]) &&
                            Regex.IsMatch(lines[i], "^\\s+\\S") &&
                            !lines[i].TrimStart().StartsWith("|") &&
                            !Regex.IsMatch(lines[i], "^\\s*#{1,6}\\s"))
                        {
                            listLines.Add(lines[i]);
                            i++;
                            continue;
                        }
                        if (string.IsNullOrWhiteSpace(lines[i]))
                        {
                            // 空行だけでリストが終わるのは、その後に非リスト行が続く場合のみ。
                            // 空行の先にもリスト項目が続くなら（標準MarkDownの「緩いリスト」）、
                            // 同じリストとして扱い続ける。
                            int j = i;
                            while (j < lines.Length && string.IsNullOrWhiteSpace(lines[j]))
                            {
                                j++;
                            }
                            if (j < lines.Length && Regex.IsMatch(lines[j], "^\\s*([*+-]|\\d+\\.)\\s+"))
                            {
                                while (i < j) { listLines.Add(lines[i]); i++; }
                                continue;
                            }
                        }
                        break;
                    }
                    var list = BuildNestedList(listLines);
                    a_doc.Blocks.Add(list);
                    m_originalTextTracker.Record(list, lines, blockStart, i);
                    continue;
                }

                if (line.TrimStart().StartsWith("|") && i + 1 < lines.Length &&
                    Regex.IsMatch(lines[i + 1], "^[\\s|:-]+$") && lines[i + 1].Contains("-"))
                {
                    var headerCells = ParseTableRow(line);
                    // 2026-08-29追記：区切り行（|:---:|---:|...|）のコロンから列ごとの
                    // 文字揃えを読み取る（DESIGN.md参照）。
                    var alignSepCells = ParseTableRow(lines[i + 1]);
                    var columnAlignments = alignSepCells.Select(ParseColumnAlignment).ToList();
                    i += 2;
                    var table = new Table();
                    foreach (var _ in headerCells)
                    {
                        table.Columns.Add(new TableColumn());
                    }
                    var rg = new TableRowGroup();
                    table.RowGroups.Add(rg);

                    (TextAlignment alignment, bool explicitLeftFlg) GetColumnAlignment(int a_colIndex) =>
                        a_colIndex < columnAlignments.Count ? columnAlignments[a_colIndex] : (TextAlignment.Left, false);

                    var headerRow = new TableRow();
                    int headerColIndex = 0;
                    foreach (var txt in headerCells)
                    {
                        // KeepTogether: ページの余白付近で表の行が跨ると、跨いだ先のセルは
                        // 罫線（BorderBrush/BorderThickness）が描画されないままテキストだけが
                        // 続いてしまうというWPFの制約がある。セル内の段落をページ内で分割
                        // させないようにすることで、間に合わなければ行ごと次のページへ送られる
                        // ようにし、行の途中で表が壊れて見える問題を避ける。
                        var (headerAlignment, headerExplicitLeftFlg) = GetColumnAlignment(headerColIndex);
                        var hp = new Paragraph
                        {
                            Margin = new Thickness(0),
                            // 表のセルは「行間（EditorLineHeight）」の設定値を継承させない。
                            // Style TargetType="Paragraph"はFlowDocument内のすべての段落へ
                            // 暗黙的に適用されるため、ここで明示的にLineHeightを上書きしない限り、
                            // ユーザーが本文用に設定した行間がそのまま表のセルにも反映されてしまい、
                            // 1行しかない短いセルまで縦に間延びして見える（DESIGN.md 14.11節と
                            // 同種の「LineHeightが意図しない場所へ伝播する」問題）。double.NaNを
                            // 指定すると、WPF標準の「未指定（フォントの自然な行の高さ）」の挙動に
                            // 戻るため、表は常にコンパクトな行の高さで表示される。
                            LineHeight = double.NaN,
                            KeepTogether = true,
                            TextAlignment = headerAlignment,
                            // 2026-08-29追記：「明示的な左揃え（:---）」を「揃え指定なし（---）」と
                            // 区別して保存できるようにするための印（DESIGN.md参照）。
                            Tag = headerExplicitLeftFlg ? ALIGN_LEFT_EXPLICIT_TAG : null
                        };
                        AppendInlineMarkdownToParagraph(hp, txt, false);
                        var cell = new TableCell(hp)
                        {
                            FontWeight = FontWeights.Bold,
                            Background = HEADER_BACKGROUND,
                            BorderBrush = CELL_BORDER,
                            BorderThickness = new Thickness(1),
                            Padding = new Thickness(8, 6, 8, 6)
                        };
                        headerRow.Cells.Add(cell);
                        headerColIndex++;
                    }
                    rg.Rows.Add(headerRow);

                    while (i < lines.Length && lines[i].TrimStart().StartsWith("|"))
                    {
                        var cellTexts = ParseTableRow(lines[i]);
                        var row = new TableRow();
                        int bodyColIndex = 0;
                        foreach (var txt in cellTexts)
                        {
                            // KeepTogether: 上のヘッダーセルと同じ理由（罫線が崩れる問題への対策）。
                            var (bodyAlignment, bodyExplicitLeftFlg) = GetColumnAlignment(bodyColIndex);
                            var cp = new Paragraph
                            {
                                Margin = new Thickness(0),
                                // ヘッダーセルと同じ理由でLineHeightを明示的に上書きする
                                // （上のhp参照。行間設定の影響を受けず、常にコンパクトな
                                // 行の高さで表示されるようにする）。
                                LineHeight = double.NaN,
                                KeepTogether = true,
                                TextAlignment = bodyAlignment,
                                // ヘッダーセルと同様の印（保存時に読むのはヘッダー行だけだが、
                                // 表全体で一貫させておく）。
                                Tag = bodyExplicitLeftFlg ? ALIGN_LEFT_EXPLICIT_TAG : null
                            };
                            AppendInlineMarkdownToParagraph(cp, txt, false);
                            var cell = new TableCell(cp)
                            {
                                BorderBrush = CELL_BORDER,
                                BorderThickness = new Thickness(1),
                                Padding = new Thickness(8, 6, 8, 6)
                            };
                            row.Cells.Add(cell);
                            bodyColIndex++;
                        }
                        rg.Rows.Add(row);
                        i++;
                    }
                    // 2026-08-30：列幅を内容に合わせてコンパクトにする試み
                    // （BlockStyles.ApplyContentBasedColumnWidths）を一時的に無効化した。
                    // Pixel幅を指定した列がかえって広く取られ、Star指定した列（内容が長い列）
                    // が極端に狭く押しつぶされる、という意図と逆の現象が実機で確認されたため
                    // （原因未特定。WPFのTable列幅アルゴリズムがGridと同じStar/Pixelの
                    // 比率計算をしていない可能性がある）。原因を確認できるまで、既定の
                    // 列幅（間延びはするが崩れない状態）に戻す。
                    // BlockStyles.ApplyContentBasedColumnWidths(table);
                    a_doc.Blocks.Add(table);
                    m_originalTextTracker.Record(table, lines, blockStart, i);
                    continue;
                }

                // 通常の段落：空行や、次の別種のブロックを示す行が現れるまで、連続する行を
                // まとめて1つの段落にする（内部的には行ごとにLineBreakでつなぐ）。1行ずつ
                // 別々の段落にしてしまうと、元のファイルでは単純な改行だった箇所が、保存や
                // ソースモード表示のたびに「空行付きの段落区切り」として書き出されてしまい、
                // 元のファイルに存在しない空行が増えてしまう。
                var paraLines = new List<string> { line };
                i++;
                while (i < lines.Length && !IsBlockBoundaryLine(lines[i]))
                {
                    paraLines.Add(lines[i]);
                    i++;
                }
                var para = new Paragraph();
                AppendInlineMarkdownToParagraph(para, JoinParagraphSourceLines(paraLines), false);
                a_doc.Blocks.Add(para);
                m_originalTextTracker.Record(para, lines, blockStart, i);
            }

            if (0 == a_doc.Blocks.Count)
            {
                a_doc.Blocks.Add(new Paragraph());
            }

            m_imageManager.ResolveImages(a_doc);
        }

        /// <summary>
        /// 行が「実質的に空行」かどうかを判定する。単純な空白文字だけの行に加え、ゼロ幅
        /// スペース（U+200B）など、見た目には空行にしか見えない書式文字（Unicodeの
        /// Cfカテゴリ）のみで構成される行も空行として扱う。string.IsNullOrWhiteSpaceは
        /// これらの文字を空白とは判定しないため、そのままでは（例えばエディタ内での
        /// 編集時の副作用などにより）このような文字が紛れ込んだ行が、見た目上は空行なのに
        /// 段落の区切りとして認識されない、という不具合につながりうる。
        /// </summary>
        /// <param name="a_line">判定したい行。</param>
        /// <returns>実質的に空行とみなせるなら true。</returns>
        private static bool IsEffectivelyBlankLine(string a_line)
        {
            if (string.IsNullOrEmpty(a_line))
            {
                return true;
            }
            string stripped = a_line
                .Replace("​", "")
                .Replace("‌", "")
                .Replace("‍", "")
                .Replace("﻿", "");
            return string.IsNullOrWhiteSpace(stripped);
        }

        /// <summary>
        /// 段落を組み立てている、空行を挟まない連続したソース行を、1つの文字列へ結合する。
        /// 「表示」→「段落中の改行」の設定に応じて、結合のしかたを切り替える。設定が
        /// 「ソースの通りに改行する」（既定）なら、行を"\n"でつなぐ（AppendInlineMarkdown
        /// ToParagraphが後段でこれを実際のLineBreakへ変換し、mde/Typora従来通り、ソース上の
        /// 改行がそのまま見た目の改行になる）。「空行が入るまで改行しない」なら、各行の前後の
        /// 空白を落としたうえで単純な半角スペース1つでつなぐ（結果に"\n"が含まれなくなるため、
        /// 後段でLineBreakは一切生成されず、CommonMark/VSCodeのMarkDownプレビューと同じく、
        /// 空行が入るまでは改行されずに1つの段落として続けて表示される）。
        /// </summary>
        /// <param name="a_lines">結合対象のソース行一覧。</param>
        /// <returns>結合した文字列。</returns>
        private string JoinParagraphSourceLines(List<string> a_lines)
        {
            bool preserveFlg = null == m_preserveSourceLineBreaksFlg || m_preserveSourceLineBreaksFlg();
            if (preserveFlg)
            {
                return string.Join("\n", a_lines);
            }
            return string.Join(" ", a_lines.Select(l => l.Trim()));
        }

        /// <summary>"| a | b | c |" 形式の表の行を、各セルのテキストへ分割する。</summary>
        /// <param name="a_line">対象の行。</param>
        /// <returns>各セルのテキストの一覧。</returns>
        /// <summary>
        /// 通常の段落を組み立てている最中に、この行で段落を打ち切るべきかどうかを判定する。
        /// 空行のほか、見出し・コードブロック・箇条書き・表など、他の種類のブロックの開始を
        /// 示す行であれば true を返す。
        /// </summary>
        /// <param name="a_line">判定したい行。</param>
        /// <returns>この行より前で段落を打ち切るべきなら true。</returns>
        private bool IsBlockBoundaryLine(string a_line)
        {
            if (IsEffectivelyBlankLine(a_line))
            {
                return true;
            }
            if (a_line.TrimStart().StartsWith("```"))
            {
                return true;
            }
            if (Regex.IsMatch(a_line, "^(#{1,6})\\s+"))
            {
                return true;
            }
            if (Regex.IsMatch(a_line, "^\\s*([*+-]|\\d+\\.)\\s+"))
            {
                return true;
            }
            if (a_line.TrimStart().StartsWith("|"))
            {
                return true;
            }
            if (Regex.IsMatch(a_line, "^ {0,3}([-*_])\\1{2,}\\s*$"))
            {
                return true;
            }
            return false;
        }

        /// <summary>
        /// 表の1行分のソーステキストを、セルごとの文字列に分割する。単純に「|」で分割すると、
        /// セルの中のインラインコード（`&amp;&amp;`/`||` のように、コードの中に「|」自体が
        /// 含まれる場合）まで区切ってしまい、列がずれて表が壊れてしまう。そのため、バック
        /// クォートで囲まれたコード区間の中にある「|」は区切りとして扱わないようにしている。
        /// </summary>
        /// <param name="a_line">表の1行分のソーステキスト。</param>
        /// <returns>セルごとに分割した文字列。</returns>
        private List<string> ParseTableRow(string a_line)
        {
            string t = a_line.Trim();
            if (t.StartsWith("|"))
            {
                t = t.Substring(1);
            }
            if (t.EndsWith("|"))
            {
                t = t.Substring(0, t.Length - 1);
            }

            var cells = new List<string>();
            var current = new StringBuilder();
            bool insideCodeFlg = false;
            foreach (char c in t)
            {
                if ('`' == c)
                {
                    insideCodeFlg = !insideCodeFlg;
                    current.Append(c);
                }
                else if ('|' == c && !insideCodeFlg)
                {
                    cells.Add(current.ToString().Trim());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }
            cells.Add(current.ToString().Trim());
            return cells;
        }

        /// <summary>
        /// 表の区切り行（例: ":---:" / "---:" / ":---" / "---"）の1セル分の文字列から、
        /// 列の文字揃えを判定する。2026-08-29追記（DESIGN.md参照。同日中に、「左寄せの明示
        /// （:---）」と「揃え指定なし（---）」を区別できるよう改良した）。
        /// TextAlignment自体はLeft/Center/Right/Justifyの4値しか持てず、この2つはどちらも
        /// TextAlignment.Leftになってしまうため、区別が必要な「明示的な左揃え」だけを
        /// 呼び出し元でParagraph.Tag（ALIGN_LEFT_EXPLICIT_TAG）に記録できるよう、
        /// 戻り値にその旨のフラグを含める。
        /// </summary>
        /// <param name="a_sepCell">区切り行の1セル分の文字列。</param>
        /// <returns>対応するTextAlignmentと、「明示的な左揃え（:---）」だったかどうか。</returns>
        private static (TextAlignment alignment, bool explicitLeftFlg) ParseColumnAlignment(string a_sepCell)
        {
            string s = a_sepCell.Trim();
            bool leftColonFlg = s.StartsWith(":");
            bool rightColonFlg = s.EndsWith(":");
            if (leftColonFlg && rightColonFlg)
            {
                return (TextAlignment.Center, false);
            }
            if (rightColonFlg)
            {
                return (TextAlignment.Right, false);
            }
            if (leftColonFlg)
            {
                return (TextAlignment.Left, true);
            }
            return (TextAlignment.Left, false);
        }

        /// <summary>
        /// 箇条書き項目のソース行の連なりから、（入れ子を含む）Listを組み立てる。
        /// ネストの各段で箇条書き記号か順序付き数字かを判定し、各項目の1行目と継続行を
        /// すべて結合してからインライン装飾を解析する（**太字**などが継続行をまたいでいても
        /// 正しく認識できるようにするため）。また、すべての項目が同じ数字を使っている
        /// 順序付きリストには印を付け、保存時にそのスタイルを維持できるようにする。
        /// </summary>
        /// <param name="a_listLines">箇条書きのソース行一覧。</param>
        /// <returns>組み立てた（入れ子を含む）List。</returns>
        private List BuildNestedList(List<string> a_listLines)
        {
            var rootList = new List { MarkerStyle = TextMarkerStyle.Disc };
            var stack = new List<(List list, int level)> { (rootList, 0) };
            bool rootMarkerSetFlg = false;
            var numbersByList = new Dictionary<List, List<int>>();

            Paragraph pendingPara = null;
            var pendingTextLines = new List<string>();
            // 2026-08-29追記：タスクリスト（[ ]/[x]）対応（DESIGN.md参照）。
            // 直前に確定した箇条書き項目がタスク項目だったかどうか（未確定ならnull）。
            bool? pendingTaskChecked = null;

            // 保留中の項目のテキストを解析して段落へ反映し、タスク項目ならチェックボックスを
            // 段落の先頭に挿入する。新しい項目の開始時／ループ終了後の両方から呼ばれる。
            void FlushPendingItem()
            {
                if (null == pendingPara)
                {
                    return;
                }
                AppendInlineMarkdownToParagraph(pendingPara, JoinParagraphSourceLines(pendingTextLines), false);
                if (pendingTaskChecked.HasValue)
                {
                    // 2026-08-29追記：チェックボックスの見た目はBlockStyles.CreateTaskCheckboxに
                    // 一本化し、ライブ入力変換側（ListEditor.ConvertListItemTextToTaskCheckbox）と
                    // 食い違わないようにしている（DESIGN.md参照）。
                    var checkBox = BlockStyles.CreateTaskCheckbox(pendingTaskChecked.Value);
                    var container = new InlineUIContainer(checkBox);
                    if (pendingPara.Inlines.Count > 0)
                    {
                        pendingPara.Inlines.InsertBefore(pendingPara.Inlines.FirstInline, container);
                    }
                    else
                    {
                        pendingPara.Inlines.Add(container);
                    }
                    // 2026-08-29追記（同日中の追加修正、v2.0.34.0で試みたが、v2.0.35.0で撤回）：
                    // Typora等と同様に箇条書きマーカーを非表示にする対応を一度実装したが、
                    // ListEditor.ConvertListItemTextToTaskCheckboxと同じ理由（WPFの制約による
                    // 実機での副作用）で撤回した（詳細はDESIGN.md参照）。
                }
            }

            foreach (var line in a_listLines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue; // 「緩い」リストの項目間の空行
                }

                var m = Regex.Match(line, "^(\\s*)(?:([*+-])|(\\d+)\\.)\\s+(.*)$");
                if (m.Success)
                {
                    FlushPendingItem();

                    int indent = m.Groups[1].Value.Length;
                    bool orderedFlg = m.Groups[3].Success;
                    string bulletMarker = orderedFlg ? null : m.Groups[2].Value;
                    int level = Math.Max(0, (int)Math.Round(indent / 3.0));
                    string text = m.Groups[4].Value;

                    // タスクリスト項目（"[ ] " / "[x] " / "[X] "）かどうかを判定する。
                    bool? taskChecked = null;
                    var taskMatch = Regex.Match(text, "^\\[([ xX])\\]\\s+(.*)$");
                    if (taskMatch.Success)
                    {
                        taskChecked = " " != taskMatch.Groups[1].Value;
                        text = taskMatch.Groups[2].Value;
                    }

                    if (!rootMarkerSetFlg)
                    {
                        rootList.MarkerStyle = orderedFlg ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc;
                        rootList.Tag = orderedFlg ? null : bulletMarker;
                        rootMarkerSetFlg = true;
                    }

                    while (stack.Count > 1 &&
                           stack[stack.Count - 1].level > level)
                        stack.RemoveAt(stack.Count - 1);

                    var top = stack[stack.Count - 1];
                    if (top.level < level &&
                        top.list.ListItems.Count > 0)
                    {
                        var lastLi = top.list.ListItems.Cast<ListItem>().Last();
                        List nestedList = lastLi.Blocks.Count > 1 ? lastLi.Blocks.LastBlock as List : null;
                        if (null == nestedList)
                        {
                            // 2026-08-29追記（同日中の追加修正、v2.0.39.0）：VSCode同様「1段目Disc・
                            // 2段目Circle・3段目以降Box」にするため、BlockStyles.
                            // UnorderedMarkerStyleForDepthで段数に応じたマーカーを決める
                            // （ListEditor.IndentListItem等と同じ理由。DESIGN.md参照）。この時点で
                            // stack.Count は「これから作る新しいnestedListの1つ手前までの段数」と
                            // 一致するため、新しいnestedList自身の段数はstack.Count + 1になる。
                            nestedList = new List
                            {
                                MarkerStyle = orderedFlg
                                    ? TextMarkerStyle.Decimal
                                    : BlockStyles.UnorderedMarkerStyleForDepth(stack.Count + 1),
                                Tag = orderedFlg ? null : bulletMarker
                            };
                            lastLi.Blocks.Add(nestedList);
                        }
                        stack.Add((nestedList, level));
                        top = stack[stack.Count - 1];
                    }

                    if (orderedFlg)
                    {
                        if (!numbersByList.TryGetValue(top.list, out var nums))
                        {
                            nums = new List<int>();
                            numbersByList[top.list] = nums;
                        }
                        nums.Add(int.Parse(m.Groups[3].Value));
                    }

                    var para = new Paragraph { Margin = new Thickness(0) };
                    top.list.ListItems.Add(new ListItem(para));

                    pendingPara = para;
                    pendingTextLines = new List<string> { text };
                    pendingTaskChecked = taskChecked;
                }
                else
                {
                    if (null != pendingPara)
                    {
                        pendingTextLines.Add(line.Trim());
                    }
                }
            }

            FlushPendingItem();

            // ソースがすべての項目で同じ数字を使っていた場合（例: "1." / "1." / "1."。
            // レンダラーに自動採番させる一般的なMarkDownの書き方）は、そのスタイルを維持する。
            // ListToMarkdown は連番ではなく常に "1." で書き出すようになる。
            foreach (var kv in numbersByList)
            {
                if (kv.Value.Count > 1 &&
                    1 == kv.Value.Distinct().Count())
                    kv.Key.Tag = "const";
            }

            return rootList;
        }

        /// <summary>
        /// 1行分のテキストからインライン装飾（画像、`コード`、**太字**、~~取り消し線~~、
        /// [リンク](a_url)、&lt;自動リンク&gt;）を解析し、段落のInlinesへ追加する。
        /// </summary>
        /// <param name="p">追加先の段落。</param>
        /// <param name="text">解析対象のテキスト（項目の1行目＋継続行を結合した、
        /// 改行を含む複数行テキストの場合もある）。</param>
        /// <param name="append">falseなら、追加する前に段落の既存Inlinesをクリアする。</param>
        // \ に続く1文字をエスケープする際、正規表現の特殊文字マッチングから守るために一時的に
        // 使うプレースホルダ文字（Unicode私用領域）。マッチング後、実際の文字に戻す。
        private static readonly Dictionary<char, char> ESCAPE_PLACEHOLDERS = new Dictionary<char, char>
        {
            ['*'] = '',
            ['~'] = '',
            ['`'] = '',
            ['\\'] = '',
            ['['] = '',
            [']'] = '',
            ['('] = '',
            [')'] = '',
            ['<'] = '',
            ['>'] = '',
        };
        private static readonly Dictionary<char, char> PLACEHOLDER_TO_CHAR =
            ESCAPE_PLACEHOLDERS.ToDictionary(kv => kv.Value, kv => kv.Key);

        /// <summary>
        /// エスケープ記法（\ + 1文字）を解決する。\の直後の1文字を、以降の**/~~/`/[]などの
        /// パターンマッチングに巻き込まれないよう一時的なプレースホルダ文字に置き換える
        /// （実際の文字への復元はRun生成時に行う）。
        /// ・\が2つ以上連続する場合（例: \\\\）は、1つ目の\だけが「エスケープする側」として
        ///   消費され、2つ目以降の\はすべてそのまま表示される（さらなるエスケープ処理はしない）。
        /// ・[リンク文字列](URL) のリンク文字列（[]の中）では、\はエスケープ文字として扱わず
        ///   そのまま表示する。
        /// </summary>
        /// <param name="a_text">1行分の生テキスト。</param>
        /// <returns>エスケープ処理を適用した後のテキスト（プレースホルダ文字を含む）。</returns>
        private string PreprocessEscapes(string a_text)
        {
            if (a_text.IndexOf('\\') < 0)
            {
                return a_text;
            }

            // リンク/ファイルリンクの [表示文字] 部分は、エスケープ処理の対象外とする範囲として
            // 先に洗い出しておく。
            var exemptRanges = new List<(int start, int end)>();
            foreach (Match m in Regex.Matches(a_text, "(?<!!)\\[([^\\]]*)\\]\\((?:[^()]|\\([^()]*\\))+\\)"))
            {
                var g = m.Groups[1];
                exemptRanges.Add((g.Index, g.Index + g.Length));
            }

            // 画像記法 ![alt](パス) の「パス」部分（()の中身）も対象外にする。ここは表示文字
            // ではなくファイルパス/URLであり、Windowsの画像パスは"\"区切りで書かれることも
            // あるため、\をエスケープ文字として解釈して消してしまうとパスが壊れてしまうため。
            foreach (Match m in Regex.Matches(a_text, "!\\[[^\\]]*\\]\\(((?:[^()]|\\([^()]*\\))+)\\)"))
            {
                var g = m.Groups[1];
                exemptRanges.Add((g.Index, g.Index + g.Length));
            }
            bool IsExempt(int a_idx)
            {
                foreach (var (s, e) in exemptRanges)
                {
                    if (a_idx >= s &&
                        a_idx < e)
                    {
                        return true;
                    }
                }
                return false;
            }

            var sb = new StringBuilder();
            int i = 0;
            while (i < a_text.Length)
            {
                if ('\\' != a_text[i]) { sb.Append(a_text[i]); i++; continue; }

                int runStart = i;
                int runLen = 0;
                while (i < a_text.Length &&
                       '\\' == a_text[i]) { runLen++; i++; }

                if (IsExempt(runStart))
                {
                    for (int k = 0; k < runLen; k++)
                    {
                        sb.Append('\\');
                    }
                    continue;
                }

                if (1 == runLen)
                {
                    if (i < a_text.Length)
                    {
                        char next = a_text[i];
                        sb.Append(ESCAPE_PLACEHOLDERS.TryGetValue(next, out char ph) ? ph : next);
                        i++;
                    }
                    else
                    {
                        sb.Append('\\'); // 行末の孤立した\は、エスケープ対象がないのでそのまま表示
                    }
                }
                else
                {
                    // 2つ以上連続する場合：1つ目が2つ目を「エスケープ」して消費し（結果として
                    // \が1つ分表示される）、3つ目以降はさらなるエスケープ処理をせずそのまま表示する。
                    sb.Append(ESCAPE_PLACEHOLDERS['\\']);
                    for (int k = 0; k < runLen - 2; k++)
                    {
                        sb.Append('\\');
                    }
                }
            }
            return sb.ToString();
        }

        /// <summary>エスケープ処理で使ったプレースホルダ文字を、実際の文字へ戻す。</summary>
        /// <param name="a_text">対象の文字列。</param>
        /// <returns>実際の文字に戻したテキスト。</returns>
        private string RestorePlaceholders(string a_text)
        {
            if (0 == a_text.Length)
            {
                return a_text;
            }
            var sb = new StringBuilder(a_text.Length);
            foreach (char c in a_text)
            {
                sb.Append(PLACEHOLDER_TO_CHAR.TryGetValue(c, out char real) ? real : c);
            }
            return sb.ToString();
        }

        public void AppendInlineMarkdownToParagraph(Paragraph a_p, string a_text, bool a_appendFlg)
        {
            if (!a_appendFlg)
            {
                a_p.Inlines.Clear();
            }
            a_text = PreprocessEscapes(a_text);
            int lastIndex = 0;
            foreach (Match m in INLINE_CONTENT_REGEX.Matches(a_text))
            {
                if (m.Index > lastIndex)
                {
                    AppendPlainTextWithLineBreaks(a_p, a_text.Substring(lastIndex, m.Index - lastIndex));
                }

                if (m.Groups[1].Success)
                {
                    a_p.Inlines.Add(new InlineUIContainer(m_imageManager.BuildImageFromHtmlTag(m.Groups[1].Value)));
                }
                else if (m.Groups[2].Success)
                {
                    SplitUrlAndTitle(m.Groups[4].Value, out string imgSrc, out string imgTitle);
                    a_p.Inlines.Add(new InlineUIContainer(m_imageManager.BuildImageFromMarkdown(m.Groups[3].Value, imgSrc, imgTitle)));
                }
                else if (m.Groups[5].Success)
                {
                    AppendStyledRunsWithLineBreaks(a_p, m.Groups[6].Value, "code");
                }
                else if (m.Groups[7].Success)
                {
                    AppendStyledRunsWithLineBreaks(a_p, m.Groups[8].Value, "bold");
                }
                else if (m.Groups[9].Success)
                {
                    AppendStyledRunsWithLineBreaks(a_p, m.Groups[10].Value, "strikethrough");
                }
                else if (m.Groups[11].Success)
                {
                    AppendStyledRunsWithLineBreaks(a_p, m.Groups[12].Value, "underline");
                }
                else if (m.Groups[13].Success)
                {
                    AppendStyledRunsWithLineBreaks(a_p, m.Groups[14].Value, "highlight");
                }
                else if (m.Groups[15].Success)
                {
                    SplitUrlAndTitle(m.Groups[17].Value, out string linkUrlRaw, out string linkTitleRaw);
                    string linkTitle = null == linkTitleRaw ? null : RestorePlaceholders(linkTitleRaw);
                    a_p.Inlines.Add(BuildLinkRun(RestorePlaceholders(m.Groups[16].Value), RestorePlaceholders(linkUrlRaw), false, linkTitle));
                }
                else if (m.Groups[18].Success)
                {
                    a_p.Inlines.Add(BuildLinkRun(RestorePlaceholders(m.Groups[19].Value), RestorePlaceholders(m.Groups[19].Value), true));
                }
                else if (m.Groups[20].Success)
                {
                    string email = RestorePlaceholders(m.Groups[21].Value);
                    a_p.Inlines.Add(BuildLinkRun(email, "mailto:" + email, false, null, true));
                }
                else if (m.Groups[22].Success)
                {
                    a_p.Inlines.Add(new Run("") { Tag = new AnchorInfo { m_id = m.Groups[23].Value } });
                }

                lastIndex = m.Index + m.Length;
            }
            if (lastIndex < a_text.Length)
            {
                AppendPlainTextWithLineBreaks(a_p, a_text.Substring(lastIndex));
            }
        }

        /// <summary>スタイル付きのクリック可能なリンクRunを組み立てる。</summary>
        /// <param name="a_linkText">表示文字。</param>
        /// <param name="a_url">リンク先URL。</param>
        /// <param name="a_isAutoLinkFlg">[a_text](a_url)ではなく&lt;a_url&gt;形式から来た場合はtrue。</param>
        /// <param name="a_title">タイトル属性（[a_text](a_url "title")のtitle部分。省略時はnull）。
        /// 2026-08-29追記。</param>
        /// <param name="a_isEmailAutoLinkFlg">&lt;email@example.com&gt;形式のメールアドレス
        /// 自動リンクから来た場合はtrue。2026-08-29追記。</param>
        /// <returns>組み立てたリンクのRun。</returns>
        public Run BuildLinkRun(string a_linkText, string a_url, bool a_isAutoLinkFlg, string a_title = null, bool a_isEmailAutoLinkFlg = false)
        {
            return new Run(a_linkText)
            {
                Foreground = LINK_BRUSH,
                TextDecorations = TextDecorations.Underline,
                Tag = new LinkInfo
                {
                    m_url = a_url,
                    m_isAutoLinkFlg = a_isAutoLinkFlg,
                    m_title = a_title,
                    m_isEmailAutoLinkFlg = a_isEmailAutoLinkFlg
                },
                ToolTip = string.IsNullOrEmpty(a_title) ? a_url : a_title
            };
        }

        /// <summary>プレーンテキストを段落へ追加する。埋め込まれた "\n" は実際のLineBreakへ
        /// 変換する（Run単体では改行文字を改行として描画できないため）。エスケープ処理で
        /// 実際の文字に戻された部分は、保存時に再び \ を付けて書き戻せるよう
        /// Tag="escaped" を付けた専用のRunに分けておく。</summary>
        /// <param name="a_p">対象の段落。</param>
        /// <param name="a_text">対象の文字列。</param>
        public void AppendPlainTextWithLineBreaks(Paragraph a_p, string a_text)
        {
            var segments = a_text.Split('\n');
            for (int i = 0; i < segments.Length; i++)
            {
                if (i > 0)
                {
                    a_p.Inlines.Add(new LineBreak());
                }
                AppendPlainSegmentWithEscapeTags(a_p, segments[i]);
            }
        }

        /// <summary>1行分（改行を含まない）のプレーンテキストを、エスケープされた文字ごとに
        /// Runを分けながら段落へ追加する。</summary>
        /// <param name="a_p">対象の段落。</param>
        /// <param name="a_segment">対象の1行分のテキスト。</param>
        private void AppendPlainSegmentWithEscapeTags(Paragraph a_p, string a_segment)
        {
            if (0 == a_segment.Length)
            {
                return;
            }
            var plain = new StringBuilder();
            foreach (char c in a_segment)
            {
                if (PLACEHOLDER_TO_CHAR.TryGetValue(c, out char real))
                {
                    if (plain.Length > 0) { a_p.Inlines.Add(new Run(plain.ToString())); plain.Clear(); }
                    a_p.Inlines.Add(new Run(real.ToString()) { Tag = "escaped" });
                }
                else
                {
                    plain.Append(c);
                }
            }
            if (plain.Length > 0)
            {
                a_p.Inlines.Add(new Run(plain.ToString()));
            }
        }

        /// <summary>
        /// AppendPlainTextWithLineBreaksと同様だが、`コード`/**太字**/~~取り消し線~~の内容自体が
        /// 複数行にまたがる場合向け。各行が同じスタイルのRunになり、本物のLineBreakでつながる。
        /// </summary>
        /// <param name="a_p">対象の段落。</param>
        /// <param name="a_content">対象の内容。</param>
        /// <param name="a_style">適用するスタイル。</param>
        public void AppendStyledRunsWithLineBreaks(Paragraph a_p, string a_content, string a_style)
        {
            var segments = a_content.Split('\n');
            for (int i = 0; i < segments.Length; i++)
            {
                if (i > 0)
                {
                    a_p.Inlines.Add(new LineBreak());
                }
                string segText = RestorePlaceholders(segments[i]);
                Run run;
                if ("bold" == a_style)
                {
                    run = new Run(segText) { FontWeight = FontWeights.Bold, Tag = "bold" };
                }
                else if ("strikethrough" == a_style)
                {
                    run = new Run(segText) { TextDecorations = TextDecorations.Strikethrough, Tag = "strikethrough" };
                }
                else if ("underline" == a_style)
                {
                    run = new Run(segText) { TextDecorations = TextDecorations.Underline, Tag = "underline" };
                }
                else if ("highlight" == a_style)
                {
                    run = new Run(segText) { Background = BlockStyles.HighlightBrush, Tag = "highlight" };
                }
                else
                    run = new Run(segText)
                    {
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 13.5,
                        Background = BlockStyles.CodeBlockBackgroundBrush,
                        Tag = "inline-code"
                    };
                a_p.Inlines.Add(run);
            }
        }

        // ======================================================================
        //  生のMarkdownテキストを、任意の位置へその場で挿入する（貼り付け用）
        // ======================================================================

        /// <summary>
        /// 他アプリ等からの生のMarkdownテキスト（**太字**・~~取り消し線~~・&lt;u&gt;下線&lt;/u&gt;・
        /// `コード`・[リンク](url)等）を、インラインの装飾記法として解釈しながら、指定した
        /// 位置（キャレット位置）へその場で挿入する。<see cref="AppendInlineMarkdownToParagraph"/>
        /// と同じ正規表現・解釈ロジックを使うが、あちらが「段落の末尾へ追記する」動作なのに
        /// 対し、こちらは段落の途中の任意の位置へ挿入できる点が異なる（貼り付け機能向け）。
        /// 各要素は、mdeの他のインライン装飾機能（InlineStyleEditor）と同じ、TextPointerを
        /// 直接渡すRunコンストラクタの方式で挿入する（Inlinesコレクション間でオブジェクトを
        /// 移動させるような操作は行わない）。
        /// </summary>
        /// <param name="a_position">挿入する位置。</param>
        /// <param name="a_text">解釈する生のテキスト。</param>
        /// <returns>挿入した内容の末尾の位置（挿入後のキャレット移動先として使う）。</returns>
        public TextPointer InsertInlineMarkdownAtPosition(TextPointer a_position, string a_text)
        {
            TextPointer cursor = a_position;
            a_text = PreprocessEscapes(a_text);
            int lastIndex = 0;
            foreach (Match m in INLINE_CONTENT_REGEX.Matches(a_text))
            {
                if (m.Index > lastIndex)
                {
                    cursor = InsertPlainTextWithLineBreaksAt(cursor, a_text.Substring(lastIndex, m.Index - lastIndex));
                }

                if (m.Groups[1].Success)
                {
                    var img = new InlineUIContainer(m_imageManager.BuildImageFromHtmlTag(m.Groups[1].Value), cursor);
                    cursor = img.ElementEnd;
                }
                else if (m.Groups[2].Success)
                {
                    SplitUrlAndTitle(m.Groups[4].Value, out string imgSrc, out string imgTitle);
                    var img = new InlineUIContainer(m_imageManager.BuildImageFromMarkdown(m.Groups[3].Value, imgSrc, imgTitle), cursor);
                    cursor = img.ElementEnd;
                }
                else if (m.Groups[5].Success)
                {
                    cursor = InsertStyledRunsWithLineBreaksAt(cursor, m.Groups[6].Value, "code");
                }
                else if (m.Groups[7].Success)
                {
                    cursor = InsertStyledRunsWithLineBreaksAt(cursor, m.Groups[8].Value, "bold");
                }
                else if (m.Groups[9].Success)
                {
                    cursor = InsertStyledRunsWithLineBreaksAt(cursor, m.Groups[10].Value, "strikethrough");
                }
                else if (m.Groups[11].Success)
                {
                    cursor = InsertStyledRunsWithLineBreaksAt(cursor, m.Groups[12].Value, "underline");
                }
                else if (m.Groups[13].Success)
                {
                    cursor = InsertStyledRunsWithLineBreaksAt(cursor, m.Groups[14].Value, "highlight");
                }
                else if (m.Groups[15].Success)
                {
                    string linkText = RestorePlaceholders(m.Groups[16].Value);
                    SplitUrlAndTitle(m.Groups[17].Value, out string urlRaw, out string titleRaw);
                    string url = RestorePlaceholders(urlRaw);
                    string title = null == titleRaw ? null : RestorePlaceholders(titleRaw);
                    var run = new Run(linkText, cursor)
                    {
                        Foreground = LINK_BRUSH,
                        TextDecorations = TextDecorations.Underline,
                        Tag = new LinkInfo { m_url = url, m_isAutoLinkFlg = false, m_title = title },
                        ToolTip = string.IsNullOrEmpty(title) ? url : title
                    };
                    cursor = run.ContentEnd;
                }
                else if (m.Groups[18].Success)
                {
                    string url = RestorePlaceholders(m.Groups[19].Value);
                    var run = new Run(url, cursor)
                    {
                        Foreground = LINK_BRUSH,
                        TextDecorations = TextDecorations.Underline,
                        Tag = new LinkInfo { m_url = url, m_isAutoLinkFlg = true },
                        ToolTip = url
                    };
                    cursor = run.ContentEnd;
                }
                else if (m.Groups[20].Success)
                {
                    string email = RestorePlaceholders(m.Groups[21].Value);
                    var run = new Run(email, cursor)
                    {
                        Foreground = LINK_BRUSH,
                        TextDecorations = TextDecorations.Underline,
                        Tag = new LinkInfo { m_url = "mailto:" + email, m_isAutoLinkFlg = false, m_isEmailAutoLinkFlg = true },
                        ToolTip = email
                    };
                    cursor = run.ContentEnd;
                }
                else if (m.Groups[22].Success)
                {
                    var run = new Run("", cursor) { Tag = new AnchorInfo { m_id = m.Groups[23].Value } };
                    cursor = run.ContentEnd;
                }

                lastIndex = m.Index + m.Length;
            }
            if (lastIndex < a_text.Length)
            {
                cursor = InsertPlainTextWithLineBreaksAt(cursor, a_text.Substring(lastIndex));
            }
            return cursor;
        }

        /// <summary>AppendPlainTextWithLineBreaksの位置指定版。改行は本物のLineBreakとして挿入する。</summary>
        /// <param name="a_position">挿入位置。</param>
        /// <param name="a_text">挿入するプレーンテキスト。</param>
        /// <returns>挿入後の末尾位置。</returns>
        private TextPointer InsertPlainTextWithLineBreaksAt(TextPointer a_position, string a_text)
        {
            TextPointer cursor = a_position;
            var segments = a_text.Split('\n');
            for (int i = 0; i < segments.Length; i++)
            {
                if (i > 0)
                {
                    var lb = new LineBreak(cursor);
                    cursor = lb.ElementEnd;
                }
                cursor = InsertPlainSegmentWithEscapeTagsAt(cursor, segments[i]);
            }
            return cursor;
        }

        /// <summary>AppendPlainSegmentWithEscapeTagsの位置指定版。</summary>
        /// <param name="a_position">挿入位置。</param>
        /// <param name="a_segment">1行分（改行を含まない）のテキスト。</param>
        /// <returns>挿入後の末尾位置。</returns>
        private TextPointer InsertPlainSegmentWithEscapeTagsAt(TextPointer a_position, string a_segment)
        {
            if (0 == a_segment.Length)
            {
                return a_position;
            }
            TextPointer cursor = a_position;
            var plain = new StringBuilder();
            foreach (char c in a_segment)
            {
                if (PLACEHOLDER_TO_CHAR.TryGetValue(c, out char real))
                {
                    if (plain.Length > 0)
                    {
                        var plainRun = new Run(plain.ToString(), cursor);
                        cursor = plainRun.ContentEnd;
                        plain.Clear();
                    }
                    var escRun = new Run(real.ToString(), cursor) { Tag = "escaped" };
                    cursor = escRun.ContentEnd;
                }
                else
                {
                    plain.Append(c);
                }
            }
            if (plain.Length > 0)
            {
                var plainRun = new Run(plain.ToString(), cursor);
                cursor = plainRun.ContentEnd;
            }
            return cursor;
        }

        /// <summary>AppendStyledRunsWithLineBreaksの位置指定版。</summary>
        /// <param name="a_position">挿入位置。</param>
        /// <param name="a_content">対象の内容。</param>
        /// <param name="a_style">適用するスタイル。</param>
        /// <returns>挿入後の末尾位置。</returns>
        private TextPointer InsertStyledRunsWithLineBreaksAt(TextPointer a_position, string a_content, string a_style)
        {
            TextPointer cursor = a_position;
            var segments = a_content.Split('\n');
            for (int i = 0; i < segments.Length; i++)
            {
                if (i > 0)
                {
                    var lb = new LineBreak(cursor);
                    cursor = lb.ElementEnd;
                }
                string segText = RestorePlaceholders(segments[i]);
                Run run;
                if ("bold" == a_style)
                {
                    run = new Run(segText, cursor) { FontWeight = FontWeights.Bold, Tag = "bold" };
                }
                else if ("strikethrough" == a_style)
                {
                    run = new Run(segText, cursor) { TextDecorations = TextDecorations.Strikethrough, Tag = "strikethrough" };
                }
                else if ("underline" == a_style)
                {
                    run = new Run(segText, cursor) { TextDecorations = TextDecorations.Underline, Tag = "underline" };
                }
                else if ("highlight" == a_style)
                {
                    run = new Run(segText, cursor) { Background = BlockStyles.HighlightBrush, Tag = "highlight" };
                }
                else
                {
                    run = new Run(segText, cursor)
                    {
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 13.5,
                        Background = BlockStyles.CodeBlockBackgroundBrush,
                        Tag = "inline-code"
                    };
                }
                cursor = run.ContentEnd;
            }
            return cursor;
        }
    }
}
