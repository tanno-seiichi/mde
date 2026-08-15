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
        private static readonly Brush HeaderBackground = new SolidColorBrush(Color.FromRgb(0xF8, 0xF8, 0xF8));
        private static readonly Brush CellBorder = new SolidColorBrush(Color.FromRgb(0xDD, 0xDF, 0xE2));
        private static readonly Brush LinkBrush = new SolidColorBrush(Color.FromRgb(0x09, 0x69, 0xDA));

        /// <summary>
        /// 文中の画像（HTML/MarkDown）、`コード`、**太字**、~~取り消し線~~、[リンク](url)、
        /// &lt;自動リンク&gt; を検出する正規表現。
        /// </summary>
        private static readonly Regex InlineContentRegex = new Regex(
            "(<img\\s+[^>]*?/?>)|(!\\[([^\\]]*)\\]\\(((?:[^()]|\\([^()]*\\))+)\\))|(`([^`]+)`)|(\\*\\*([^*]+)\\*\\*)|(~~([^~]+)~~)|((?<!!)\\[([^\\]]*)\\]\\(((?:[^()]|\\([^()]*\\))+)\\))|(<(https?://[^\\s<>]+)>)|(<a\\s+id=\"([^\"]+)\"\\s*>\\s*</a>)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly OriginalTextTracker originalTextTracker;
        private readonly ImageManager imageManager;

        /// <summary>
        /// MarkdownConverterを構築する。
        /// </summary>
        /// <param name="originalTextTracker">「元テキスト保持」の追跡役。</param>
        /// <param name="imageManager">画像の生成・解決を担当するクラス。</param>
        public MarkdownConverter(OriginalTextTracker originalTextTracker, ImageManager imageManager)
        {
            this.originalTextTracker = originalTextTracker;
            this.imageManager = imageManager;
        }

        // ======================================================================
        //  FlowDocument → MarkDown
        // ======================================================================

        /// <summary>
        /// FlowDocumentをMarkDown文字列へ書き出す。編集されていないブロックは元テキストを
        /// そのまま使い（OriginalTextTracker参照）、編集済み・新規のブロックは現在の構造から
        /// 新しく組み立て直す。
        /// </summary>
        /// <param name="doc">対象の文書。</param>
        /// <returns>改行はすべて "\n" のMarkDownテキスト（実ファイルへの改行コード変換は保存時に行う）。</returns>
        public string DocumentToMarkdown(FlowDocument doc)
        {
            var lines = new List<string>();
            foreach (Block block in doc.Blocks)
            {
                string s = originalTextTracker.TryGetOriginal(block, out var original) ? original : BlockToMarkdown(block);
                if (!string.IsNullOrWhiteSpace(s)) lines.Add(s);
            }
            return string.Join("\n\n", lines);
        }

        /// <summary>1つのトップレベルブロックを、種類（見出し/段落・コードブロック・箇条書き・表）に
        /// 応じて適切な変換関数へ振り分ける。コードブロック丸ごとコピー機能からも利用される。</summary>
        public string BlockToMarkdown(Block block)
        {
            if (block is Paragraph p)
            {
                if (p.Tag is CodeBlockInfo codeInfo)
                {
                    var sb = new StringBuilder();
                    AppendInlinesMarkdown(p.Inlines, sb);
                    string codeText = sb.ToString().Trim('\r', '\n');
                    return "```" + codeInfo.Language + "\n" + codeText + "\n```";
                }
                int level = p.Tag is int lv ? lv : 0;
                string text = ParagraphInlineToMarkdown(p);
                return level > 0 ? new string('#', level) + " " + text : text;
            }
            if (block is List list) return ListToMarkdown(list, 0);
            if (block is Table table) return TableToMarkdown(table);
            return "";
        }

        /// <summary>
        /// 箇条書き（入れ子含む）をMarkDownへ書き出す。順序付きリストは連番で書き出すが、
        /// リストが「常に同じ数字を使う」スタイル（Tag=="const"）としてマークされている場合は
        /// すべての項目を "1." として書き出す。
        /// </summary>
        private string ListToMarkdown(List list, int level)
        {
            string indent = new string(' ', level * 3);
            bool ordered = list.MarkerStyle == TextMarkerStyle.Decimal;
            bool constantNumbering = ordered && (list.Tag as string) == "const";
            string bulletMarker = ordered ? null : ((list.Tag as string) ?? "*");
            var lines = new List<string>();
            int number = 1;
            foreach (ListItem li in list.ListItems)
            {
                var ownPara = li.Blocks.FirstBlock as Paragraph;
                string ownText = ownPara != null ? ParagraphInlineToMarkdown(ownPara) : "";
                var parts = ownText.Split('\n');
                string prefix = ordered ? ((constantNumbering ? 1 : number) + ". ") : (bulletMarker + " ");
                lines.Add(indent + prefix + parts[0]);
                string contIndent = indent + new string(' ', prefix.Length);
                for (int k = 1; k < parts.Length; k++) lines.Add(contIndent + parts[k]);

                foreach (Block b in li.Blocks)
                {
                    if (b is List nested) lines.Add(ListToMarkdown(nested, level + 1));
                }
                number++;
            }
            return string.Join("\n", lines);
        }

        /// <summary>表をGitHub-flavored MarkDown形式の表構文へ書き出す。</summary>
        private string TableToMarkdown(Table table)
        {
            var rows = new List<TableRow>();
            foreach (TableRowGroup rg in table.RowGroups)
                foreach (TableRow r in rg.Rows) rows.Add(r);
            if (rows.Count == 0) return "";

            var mdRows = new List<string>();
            foreach (var row in rows)
            {
                var cells = new List<string>();
                foreach (TableCell cell in row.Cells)
                {
                    var sb = new StringBuilder();
                    foreach (Block b in cell.Blocks)
                        if (b is Paragraph cp) sb.Append(ParagraphInlineToMarkdown(cp));
                    cells.Add(sb.ToString().Replace("|", "\\|"));
                }
                mdRows.Add("| " + string.Join(" | ", cells) + " |");
            }
            int colCount = rows[0].Cells.Count;
            string sep = "| " + string.Join(" | ", Enumerable.Repeat("---", colCount)) + " |";

            var result = new List<string> { mdRows[0], sep };
            result.AddRange(mdRows.Skip(1));
            return string.Join("\n", result);
        }

        /// <summary>段落のInlinesをMarkDownテキストへ書き出す（見出し・段落・箇条書き項目・
        /// 表セルの内容で共通して使う）。</summary>
        private string ParagraphInlineToMarkdown(Paragraph p)
        {
            var sb = new StringBuilder();
            AppendInlinesMarkdown(p.Inlines, sb);
            return sb.ToString().Trim();
        }

        /// <summary>
        /// インライン要素の中心となる書き出し処理。太字/取り消し線/インラインコードが
        /// 改行をまたいで連続している場合は、行ごとに分割せず1つの**/~~/`にまとめる。
        /// リンクのRunは [text](url) または &lt;url&gt; に戻す。
        /// </summary>
        private void AppendInlinesMarkdown(InlineCollection inlines, StringBuilder sb)
        {
            var inlineList = inlines.Cast<Inline>().ToList();
            int i = 0;
            while (i < inlineList.Count)
            {
                Inline inline = inlineList[i];

                if (inline is Run run && run.Tag is string tag &&
                    (tag == "bold" || tag == "strikethrough" || tag == "inline-code"))
                {
                    var spanText = new StringBuilder();
                    spanText.Append(run.Text.Replace("\u200B", ""));
                    int j = i + 1;
                    while (j + 1 < inlineList.Count &&
                           inlineList[j] is LineBreak &&
                           inlineList[j + 1] is Run nextRun &&
                           (nextRun.Tag as string) == tag)
                    {
                        spanText.Append('\n').Append(nextRun.Text.Replace("\u200B", ""));
                        j += 2;
                    }

                    string content = spanText.ToString();
                    if (tag == "inline-code") sb.Append('`').Append(content).Append('`');
                    else if (tag == "bold") sb.Append("**").Append(content).Append("**");
                    else sb.Append("~~").Append(content).Append("~~");

                    i = j;
                    continue;
                }

                if (inline is LineBreak)
                {
                    sb.Append('\n');
                }
                else if (inline is Run linkRun && linkRun.Tag is LinkInfo linkInfo)
                {
                    string content = linkRun.Text.Replace("\u200B", "");
                    if (linkInfo.IsAutoLink && content == linkInfo.Url)
                        sb.Append('<').Append(linkInfo.Url).Append('>');
                    else
                        sb.Append('[').Append(content).Append("](").Append(linkInfo.Url).Append(')');
                }
                else if (inline is Run anchorRun && anchorRun.Tag is AnchorInfo anchorInfo)
                {
                    sb.Append("<a id=\"").Append(anchorInfo.Id).Append("\"></a>");
                }
                else if (inline is Run escapedRun && (escapedRun.Tag as string) == "escaped")
                {
                    sb.Append('\\').Append(escapedRun.Text);
                }
                else if (inline is Run plainRun)
                {
                    sb.Append(plainRun.Text.Replace("\u200B", ""));
                }
                else if (inline is InlineUIContainer iuc && iuc.Child is Image img)
                {
                    sb.Append(ImageToMarkdownString(img));
                }
                else if (inline is Span span)
                {
                    AppendInlinesMarkdown(span.Inlines, sb);
                }
                i++;
            }
        }

        /// <summary>埋め込み画像を、元の記法（MarkDownの![]()、またはHTMLの&lt;img&gt;）に戻す。</summary>
        private string ImageToMarkdownString(Image img)
        {
            var info = img.Tag as ImageInfo;
            string src = info?.OriginalSrc ?? "";
            string alt = info?.Alt ?? "";
            if (info?.Format == "md") return "![" + alt + "](" + src + ")";

            string tag = "<img src=\"" + src + "\" alt=\"" + alt + "\"";
            if (!string.IsNullOrEmpty(info?.Style)) tag += " style=\"" + info.Style + "\"";
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
        /// <param name="md">解析するMarkDownソース。</param>
        /// <param name="doc">反映先の文書（最初にクリアされる）。</param>
        public void MarkdownToDocument(string md, FlowDocument doc)
        {
            doc.Blocks.Clear();
            originalTextTracker.Clear();
            var lines = md.Replace("\r\n", "\n").Split('\n');
            int i = 0;
            while (i < lines.Length)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) { i++; continue; }

                int blockStart = i;

                if (line.TrimStart().StartsWith("```"))
                {
                    string language = Regex.Match(line.TrimStart(), "^```(\\S*)").Groups[1].Value;
                    i++;
                    var codeLines = new List<string>();
                    while (i < lines.Length && lines[i].Trim() != "```")
                    {
                        codeLines.Add(lines[i]);
                        i++;
                    }
                    if (i < lines.Length) i++; // 閉じフェンスを読み飛ばす

                    var codePara = new Paragraph();
                    BlockStyles.ApplyCodeBlockStyle(codePara, language);
                    for (int k = 0; k < codeLines.Count; k++)
                    {
                        if (k > 0) codePara.Inlines.Add(new LineBreak());
                        codePara.Inlines.Add(new Run(codeLines[k]));
                    }
                    doc.Blocks.Add(codePara);
                    originalTextTracker.Record(codePara, lines, blockStart, i);
                    continue;
                }

                var hMatch = Regex.Match(line, "^(#{1,6})\\s+(.*)$");
                if (hMatch.Success)
                {
                    var p = new Paragraph();
                    BlockStyles.ApplyHeadingStyle(p, hMatch.Groups[1].Value.Length);
                    AppendInlineMarkdownToParagraph(p, hMatch.Groups[2].Value, false);
                    doc.Blocks.Add(p);
                    i++;
                    originalTextTracker.Record(p, lines, blockStart, i);
                    continue;
                }

                if (Regex.IsMatch(line, "^\\s*([*-]|\\d+\\.)\\s+"))
                {
                    var listLines = new List<string>();
                    while (i < lines.Length)
                    {
                        if (Regex.IsMatch(lines[i], "^\\s*([*-]|\\d+\\.)\\s+"))
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
                            while (j < lines.Length && string.IsNullOrWhiteSpace(lines[j])) j++;
                            if (j < lines.Length && Regex.IsMatch(lines[j], "^\\s*([*-]|\\d+\\.)\\s+"))
                            {
                                while (i < j) { listLines.Add(lines[i]); i++; }
                                continue;
                            }
                        }
                        break;
                    }
                    var list = BuildNestedList(listLines);
                    doc.Blocks.Add(list);
                    originalTextTracker.Record(list, lines, blockStart, i);
                    continue;
                }

                if (line.TrimStart().StartsWith("|") && i + 1 < lines.Length &&
                    Regex.IsMatch(lines[i + 1], "^[\\s|:-]+$") && lines[i + 1].Contains("-"))
                {
                    var headerCells = ParseTableRow(line);
                    i += 2;
                    var table = new Table();
                    foreach (var _ in headerCells) table.Columns.Add(new TableColumn());
                    var rg = new TableRowGroup();
                    table.RowGroups.Add(rg);

                    var headerRow = new TableRow();
                    foreach (var txt in headerCells)
                    {
                        var hp = new Paragraph();
                        AppendInlineMarkdownToParagraph(hp, txt, false);
                        var cell = new TableCell(hp)
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

                    while (i < lines.Length && lines[i].TrimStart().StartsWith("|"))
                    {
                        var cellTexts = ParseTableRow(lines[i]);
                        var row = new TableRow();
                        foreach (var txt in cellTexts)
                        {
                            var cp = new Paragraph();
                            AppendInlineMarkdownToParagraph(cp, txt, false);
                            var cell = new TableCell(cp)
                            {
                                BorderBrush = CellBorder,
                                BorderThickness = new Thickness(1),
                                Padding = new Thickness(8, 6, 8, 6)
                            };
                            row.Cells.Add(cell);
                        }
                        rg.Rows.Add(row);
                        i++;
                    }
                    doc.Blocks.Add(table);
                    originalTextTracker.Record(table, lines, blockStart, i);
                    continue;
                }

                var para = new Paragraph();
                AppendInlineMarkdownToParagraph(para, line, false);
                doc.Blocks.Add(para);
                i++;
                originalTextTracker.Record(para, lines, blockStart, i);
            }

            if (doc.Blocks.Count == 0) doc.Blocks.Add(new Paragraph());

            imageManager.ResolveImages(doc);
        }

        /// <summary>"| a | b | c |" 形式の表の行を、各セルのテキストへ分割する。</summary>
        private List<string> ParseTableRow(string line)
        {
            string t = line.Trim();
            if (t.StartsWith("|")) t = t.Substring(1);
            if (t.EndsWith("|")) t = t.Substring(0, t.Length - 1);
            return t.Split('|').Select(s => s.Trim()).ToList();
        }

        /// <summary>
        /// 箇条書き項目のソース行の連なりから、（入れ子を含む）Listを組み立てる。
        /// ネストの各段で箇条書き記号か順序付き数字かを判定し、各項目の1行目と継続行を
        /// すべて結合してからインライン装飾を解析する（**太字**などが継続行をまたいでいても
        /// 正しく認識できるようにするため）。また、すべての項目が同じ数字を使っている
        /// 順序付きリストには印を付け、保存時にそのスタイルを維持できるようにする。
        /// </summary>
        private List BuildNestedList(List<string> listLines)
        {
            var rootList = new List { MarkerStyle = TextMarkerStyle.Disc };
            var stack = new List<(List list, int level)> { (rootList, 0) };
            bool rootMarkerSet = false;
            var numbersByList = new Dictionary<List, List<int>>();

            Paragraph pendingPara = null;
            var pendingTextLines = new List<string>();

            foreach (var line in listLines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue; // 「緩い」リストの項目間の空行

                var m = Regex.Match(line, "^(\\s*)(?:([*-])|(\\d+)\\.)\\s+(.*)$");
                if (m.Success)
                {
                    if (pendingPara != null)
                        AppendInlineMarkdownToParagraph(pendingPara, string.Join("\n", pendingTextLines), false);

                    int indent = m.Groups[1].Value.Length;
                    bool ordered = m.Groups[3].Success;
                    string bulletMarker = ordered ? null : m.Groups[2].Value;
                    int level = Math.Max(0, (int)Math.Round(indent / 3.0));
                    string text = m.Groups[4].Value;

                    if (!rootMarkerSet)
                    {
                        rootList.MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc;
                        rootList.Tag = ordered ? null : bulletMarker;
                        rootMarkerSet = true;
                    }

                    while (stack.Count > 1 && stack[stack.Count - 1].level > level)
                        stack.RemoveAt(stack.Count - 1);

                    var top = stack[stack.Count - 1];
                    if (top.level < level && top.list.ListItems.Count > 0)
                    {
                        var lastLi = top.list.ListItems.Cast<ListItem>().Last();
                        List nestedList = lastLi.Blocks.Count > 1 ? lastLi.Blocks.LastBlock as List : null;
                        if (nestedList == null)
                        {
                            nestedList = new List
                            {
                                MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Circle,
                                Tag = ordered ? null : bulletMarker
                            };
                            lastLi.Blocks.Add(nestedList);
                        }
                        stack.Add((nestedList, level));
                        top = stack[stack.Count - 1];
                    }

                    if (ordered)
                    {
                        if (!numbersByList.TryGetValue(top.list, out var nums))
                        {
                            nums = new List<int>();
                            numbersByList[top.list] = nums;
                        }
                        nums.Add(int.Parse(m.Groups[3].Value));
                    }

                    var para = new Paragraph();
                    top.list.ListItems.Add(new ListItem(para));

                    pendingPara = para;
                    pendingTextLines = new List<string> { text };
                }
                else
                {
                    if (pendingPara != null)
                        pendingTextLines.Add(line.Trim());
                }
            }

            if (pendingPara != null)
                AppendInlineMarkdownToParagraph(pendingPara, string.Join("\n", pendingTextLines), false);

            // ソースがすべての項目で同じ数字を使っていた場合（例: "1." / "1." / "1."。
            // レンダラーに自動採番させる一般的なMarkDownの書き方）は、そのスタイルを維持する。
            // ListToMarkdown は連番ではなく常に "1." で書き出すようになる。
            foreach (var kv in numbersByList)
            {
                if (kv.Value.Count > 1 && kv.Value.Distinct().Count() == 1)
                    kv.Key.Tag = "const";
            }

            return rootList;
        }

        /// <summary>
        /// 1行分のテキストからインライン装飾（画像、`コード`、**太字**、~~取り消し線~~、
        /// [リンク](url)、&lt;自動リンク&gt;）を解析し、段落のInlinesへ追加する。
        /// </summary>
        /// <param name="p">追加先の段落。</param>
        /// <param name="text">解析対象のテキスト（項目の1行目＋継続行を結合した、
        /// 改行を含む複数行テキストの場合もある）。</param>
        /// <param name="append">falseなら、追加する前に段落の既存Inlinesをクリアする。</param>
        // \ に続く1文字をエスケープする際、正規表現の特殊文字マッチングから守るために一時的に
        // 使うプレースホルダ文字（Unicode私用領域）。マッチング後、実際の文字に戻す。
        private static readonly Dictionary<char, char> EscapePlaceholders = new Dictionary<char, char>
        {
            ['*'] = '\uE001',
            ['~'] = '\uE002',
            ['`'] = '\uE003',
            ['\\'] = '\uE004',
            ['['] = '\uE005',
            [']'] = '\uE006',
            ['('] = '\uE007',
            [')'] = '\uE008',
            ['<'] = '\uE009',
            ['>'] = '\uE00A',
        };
        private static readonly Dictionary<char, char> PlaceholderToChar =
            EscapePlaceholders.ToDictionary(kv => kv.Value, kv => kv.Key);

        /// <summary>
        /// エスケープ記法（\ + 1文字）を解決する。\の直後の1文字を、以降の**/~~/`/[]などの
        /// パターンマッチングに巻き込まれないよう一時的なプレースホルダ文字に置き換える
        /// （実際の文字への復元はRun生成時に行う）。
        /// ・\が2つ以上連続する場合（例: \\\\）は、1つ目の\だけが「エスケープする側」として
        ///   消費され、2つ目以降の\はすべてそのまま表示される（さらなるエスケープ処理はしない）。
        /// ・[リンク文字列](URL) のリンク文字列（[]の中）では、\はエスケープ文字として扱わず
        ///   そのまま表示する。
        /// </summary>
        /// <param name="text">1行分の生テキスト。</param>
        /// <returns>エスケープ処理を適用した後のテキスト（プレースホルダ文字を含む）。</returns>
        private string PreprocessEscapes(string text)
        {
            if (text.IndexOf('\\') < 0) return text;

            // リンク/ファイルリンクの [表示文字] 部分は、エスケープ処理の対象外とする範囲として
            // 先に洗い出しておく。
            var exemptRanges = new List<(int start, int end)>();
            foreach (Match m in Regex.Matches(text, "(?<!!)\\[([^\\]]*)\\]\\((?:[^()]|\\([^()]*\\))+\\)"))
            {
                var g = m.Groups[1];
                exemptRanges.Add((g.Index, g.Index + g.Length));
            }
            bool IsExempt(int idx)
            {
                foreach (var (s, e) in exemptRanges)
                    if (idx >= s && idx < e) return true;
                return false;
            }

            var sb = new StringBuilder();
            int i = 0;
            while (i < text.Length)
            {
                if (text[i] != '\\') { sb.Append(text[i]); i++; continue; }

                int runStart = i;
                int runLen = 0;
                while (i < text.Length && text[i] == '\\') { runLen++; i++; }

                if (IsExempt(runStart))
                {
                    for (int k = 0; k < runLen; k++) sb.Append('\\');
                    continue;
                }

                if (runLen == 1)
                {
                    if (i < text.Length)
                    {
                        char next = text[i];
                        sb.Append(EscapePlaceholders.TryGetValue(next, out char ph) ? ph : next);
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
                    sb.Append(EscapePlaceholders['\\']);
                    for (int k = 0; k < runLen - 2; k++) sb.Append('\\');
                }
            }
            return sb.ToString();
        }

        /// <summary>エスケープ処理で使ったプレースホルダ文字を、実際の文字へ戻す。</summary>
        private string RestorePlaceholders(string text)
        {
            if (text.Length == 0) return text;
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
                sb.Append(PlaceholderToChar.TryGetValue(c, out char real) ? real : c);
            return sb.ToString();
        }

        public void AppendInlineMarkdownToParagraph(Paragraph p, string text, bool append)
        {
            if (!append) p.Inlines.Clear();
            text = PreprocessEscapes(text);
            int lastIndex = 0;
            foreach (Match m in InlineContentRegex.Matches(text))
            {
                if (m.Index > lastIndex) AppendPlainTextWithLineBreaks(p, text.Substring(lastIndex, m.Index - lastIndex));

                if (m.Groups[1].Success)
                    p.Inlines.Add(new InlineUIContainer(imageManager.BuildImageFromHtmlTag(m.Groups[1].Value)));
                else if (m.Groups[2].Success)
                    p.Inlines.Add(new InlineUIContainer(imageManager.BuildImageFromMarkdown(m.Groups[3].Value, m.Groups[4].Value)));
                else if (m.Groups[5].Success)
                    AppendStyledRunsWithLineBreaks(p, m.Groups[6].Value, "code");
                else if (m.Groups[7].Success)
                    AppendStyledRunsWithLineBreaks(p, m.Groups[8].Value, "bold");
                else if (m.Groups[9].Success)
                    AppendStyledRunsWithLineBreaks(p, m.Groups[10].Value, "strikethrough");
                else if (m.Groups[11].Success)
                    p.Inlines.Add(BuildLinkRun(RestorePlaceholders(m.Groups[12].Value), RestorePlaceholders(m.Groups[13].Value), false));
                else if (m.Groups[14].Success)
                    p.Inlines.Add(BuildLinkRun(RestorePlaceholders(m.Groups[15].Value), RestorePlaceholders(m.Groups[15].Value), true));
                else if (m.Groups[16].Success)
                    p.Inlines.Add(new Run("") { Tag = new AnchorInfo { Id = m.Groups[17].Value } });

                lastIndex = m.Index + m.Length;
            }
            if (lastIndex < text.Length) AppendPlainTextWithLineBreaks(p, text.Substring(lastIndex));
        }

        /// <summary>スタイル付きのクリック可能なリンクRunを組み立てる。</summary>
        /// <param name="linkText">表示文字。</param>
        /// <param name="url">リンク先URL。</param>
        /// <param name="isAutoLink">[text](url)ではなく&lt;url&gt;形式から来た場合はtrue。</param>
        public Run BuildLinkRun(string linkText, string url, bool isAutoLink)
        {
            return new Run(linkText)
            {
                Foreground = LinkBrush,
                TextDecorations = TextDecorations.Underline,
                Tag = new LinkInfo { Url = url, IsAutoLink = isAutoLink },
                ToolTip = url
            };
        }

        /// <summary>プレーンテキストを段落へ追加する。埋め込まれた "\n" は実際のLineBreakへ
        /// 変換する（Run単体では改行文字を改行として描画できないため）。エスケープ処理で
        /// 実際の文字に戻された部分は、保存時に再び \ を付けて書き戻せるよう
        /// Tag="escaped" を付けた専用のRunに分けておく。</summary>
        public void AppendPlainTextWithLineBreaks(Paragraph p, string text)
        {
            var segments = text.Split('\n');
            for (int i = 0; i < segments.Length; i++)
            {
                if (i > 0) p.Inlines.Add(new LineBreak());
                AppendPlainSegmentWithEscapeTags(p, segments[i]);
            }
        }

        /// <summary>1行分（改行を含まない）のプレーンテキストを、エスケープされた文字ごとに
        /// Runを分けながら段落へ追加する。</summary>
        private void AppendPlainSegmentWithEscapeTags(Paragraph p, string segment)
        {
            if (segment.Length == 0) return;
            var plain = new StringBuilder();
            foreach (char c in segment)
            {
                if (PlaceholderToChar.TryGetValue(c, out char real))
                {
                    if (plain.Length > 0) { p.Inlines.Add(new Run(plain.ToString())); plain.Clear(); }
                    p.Inlines.Add(new Run(real.ToString()) { Tag = "escaped" });
                }
                else
                {
                    plain.Append(c);
                }
            }
            if (plain.Length > 0) p.Inlines.Add(new Run(plain.ToString()));
        }

        /// <summary>
        /// AppendPlainTextWithLineBreaksと同様だが、`コード`/**太字**/~~取り消し線~~の内容自体が
        /// 複数行にまたがる場合向け。各行が同じスタイルのRunになり、本物のLineBreakでつながる。
        /// </summary>
        public void AppendStyledRunsWithLineBreaks(Paragraph p, string content, string style)
        {
            var segments = content.Split('\n');
            for (int i = 0; i < segments.Length; i++)
            {
                if (i > 0) p.Inlines.Add(new LineBreak());
                string segText = RestorePlaceholders(segments[i]);
                Run run;
                if (style == "bold")
                    run = new Run(segText) { FontWeight = FontWeights.Bold, Tag = "bold" };
                else if (style == "strikethrough")
                    run = new Run(segText) { TextDecorations = TextDecorations.Strikethrough, Tag = "strikethrough" };
                else
                    run = new Run(segText)
                    {
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 13.5,
                        Background = BlockStyles.CodeBlockBackgroundBrush,
                        Tag = "inline-code"
                    };
                p.Inlines.Add(run);
            }
        }
    }
}
