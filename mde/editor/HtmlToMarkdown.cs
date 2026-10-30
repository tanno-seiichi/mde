// HtmlToMarkdown.cs
//
// 他のアプリ（Claude 等のチャット・Web ページ・Word・Google ドキュメント等）でコピーした書式付きの内容（クリップボードの
// HTML）を、Markdown の文字にする（mdew・mdee の web\js\clipboard.js の htmlToMarkdown の移植）。
// 見出し・リスト（入れ子・番号付き・チェックボックス付き）・太字・斜体・取り消し線・下線・マーカー・リンク・
// インラインコード・コードブロック・引用・表・横線・Web 上の画像を、Markdown の書き方にする。文字の色・大きさ等の
// 飾りは捨てる。Word のリスト（mso-list の段落）は、リストにする。
// WPF には HTML を読む部品が無いため、簡単な HTML の読み取り（HtmlNode・Parse）も持つ（クリップボードの HTML の
// 断片を読む程度のもので、ブラウザと同じ厳密さは無い）。
// mde の書式は1つの文字につき1つだけなので（太字かつ斜体等は無い）、書式の中の書式は、外側の書式だけにする。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace mde.editor
{
    /// <summary>クリップボードの HTML を Markdown にする。</summary>
    public static class HtmlToMarkdown
    {
        /// <summary>変換の結果。</summary>
        public sealed class Result
        {
            /// <summary>Markdown の文字。</summary>
            public string Markdown { get; set; } = "";

            /// <summary>文字だけでは表せない書式（見出し・リスト・リンク・太字等のタグ）があったか。無ければ、
            /// 呼び出し側は今まで通り文字として貼り付ける（VS Code 等の、色付けだけの HTML を文字のまま貼るため）。</summary>
            public bool RichFlg { get; set; }
        }

        // ======================================================================
        //  HTML の読み取り
        // ======================================================================

        /// <summary>HTML の要素・文字のノード。</summary>
        private sealed class HtmlNode
        {
            /// <summary>要素のタグの名前（大文字）。文字のノードは null。</summary>
            public string Tag;
            /// <summary>文字のノードの文字（実体参照は戻した後）。</summary>
            public string Text;
            /// <summary>属性（名前は小文字）。</summary>
            public Dictionary<string, string> Attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            /// <summary>子のノード。</summary>
            public List<HtmlNode> Children = new List<HtmlNode>();
            /// <summary>親の要素。</summary>
            public HtmlNode Parent;

            public bool IsText => null == Tag;
            public string Attr(string a_name) => Attrs.TryGetValue(a_name, out string v) ? v : null;
            public string Style => Attr("style") ?? "";

            /// <summary>中の文字をすべてつなげたもの。</summary>
            public string TextContent
            {
                get
                {
                    if (IsText) return Text;
                    var sb = new StringBuilder();
                    foreach (var c in Children) sb.Append(c.TextContent);
                    return sb.ToString();
                }
            }

            /// <summary>子孫の要素（深さ優先）。</summary>
            public IEnumerable<HtmlNode> Descendants()
            {
                foreach (var c in Children)
                {
                    if (c.IsText) continue;
                    yield return c;
                    foreach (var d in c.Descendants()) yield return d;
                }
            }
        }

        /// <summary>中身を持たない要素。</summary>
        private static readonly HashSet<string> s_voidTags = new HashSet<string>
        {
            "BR", "IMG", "HR", "INPUT", "META", "LINK", "COL", "WBR", "AREA", "BASE", "SOURCE", "PARAM", "TRACK", "EMBED",
        };

        /// <summary>中身を HTML として読まない要素（中身ごと捨てる）。</summary>
        private static readonly HashSet<string> s_rawTextTags = new HashSet<string> { "SCRIPT", "STYLE", "TEXTAREA", "TITLE", "XML" };

        /// <summary>開くと、開いている P を閉じる要素。</summary>
        private static readonly HashSet<string> s_closesParagraphTags = new HashSet<string>
        {
            "P", "DIV", "UL", "OL", "LI", "H1", "H2", "H3", "H4", "H5", "H6", "PRE", "BLOCKQUOTE", "TABLE", "HR", "SECTION",
            "ARTICLE", "HEADER", "FOOTER", "NAV", "ASIDE", "MAIN", "FIGURE", "DL", "DT", "DD", "ADDRESS", "FORM", "FIELDSET",
        };

        private static readonly Regex s_tokenRegex = new Regex(
            "<!--[\\s\\S]*?-->|<!\\[CDATA\\[[\\s\\S]*?\\]\\]>|<![^>]*>|<\\?[^>]*>|</([A-Za-z][\\w:.-]*)\\s*>|<([A-Za-z][\\w:.-]*)((?:\\s+[^\\s=/>]+(?:\\s*=\\s*(?:\"[^\"]*\"|'[^']*'|[^\\s>]+))?)*)\\s*(/?)>",
            RegexOptions.Compiled);

        private static readonly Regex s_attrRegex = new Regex(
            "([^\\s=/>]+)(?:\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+)))?", RegexOptions.Compiled);

        /// <summary>HTML を読んで、ノードの木にする（根は BODY 相当の入れ物）。</summary>
        private static HtmlNode Parse(string a_html)
        {
            var root = new HtmlNode { Tag = "BODY" };
            var stack = new List<HtmlNode> { root };
            HtmlNode Current() => stack[stack.Count - 1];
            void AddText(string a_text)
            {
                if (string.IsNullOrEmpty(a_text)) return;
                var node = new HtmlNode { Text = WebUtility.HtmlDecode(a_text), Parent = Current() };
                Current().Children.Add(node);
            }
            // 開いている要素の中から、指定のタグを探す（a_stopTags の要素より外は探さない）
            int FindOpen(string a_tag, params string[] a_stopTags)
            {
                for (int i = stack.Count - 1; i > 0; i--)
                {
                    if (stack[i].Tag == a_tag) return i;
                    if (a_stopTags.Contains(stack[i].Tag)) return -1;
                }
                return -1;
            }
            void CloseTo(int a_index)
            {
                if (a_index > 0) stack.RemoveRange(a_index, stack.Count - a_index);
            }

            int pos = 0;
            foreach (Match m in s_tokenRegex.Matches(a_html))
            {
                if (m.Index < pos) continue; // 生の文字の要素の中身として読み飛ばした範囲
                AddText(a_html.Substring(pos, m.Index - pos));
                pos = m.Index + m.Length;
                if (m.Groups[1].Success)
                {
                    // 閉じタグ：同じタグが開いていれば、そこまで閉じる（無ければ無視する）
                    string tag = m.Groups[1].Value.ToUpperInvariant();
                    int index = FindOpen(tag);
                    if (index > 0) CloseTo(index);
                    continue;
                }
                if (!m.Groups[2].Success) continue; // コメント・<!DOCTYPE> 等
                string name = m.Groups[2].Value.ToUpperInvariant();
                var element = new HtmlNode { Tag = name };
                foreach (Match a in s_attrRegex.Matches(m.Groups[3].Value))
                {
                    string value = a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Value;
                    element.Attrs[a.Groups[1].Value] = WebUtility.HtmlDecode(value ?? "");
                }
                // 暗黙に閉じる要素
                if (s_closesParagraphTags.Contains(name))
                {
                    int p = FindOpen("P", "TD", "TH", "LI", "BLOCKQUOTE", "DIV");
                    if (p > 0) CloseTo(p);
                }
                if ("LI" == name)
                {
                    int li = FindOpen("LI", "UL", "OL");
                    if (li > 0) CloseTo(li);
                }
                else if ("TD" == name || "TH" == name)
                {
                    int cell = Math.Max(FindOpen("TD", "TR", "TABLE"), FindOpen("TH", "TR", "TABLE"));
                    if (cell > 0) CloseTo(cell);
                }
                else if ("TR" == name)
                {
                    int tr = FindOpen("TR", "TABLE");
                    if (tr > 0) CloseTo(tr);
                }
                else if ("DT" == name || "DD" == name)
                {
                    int d = Math.Max(FindOpen("DT", "DL"), FindOpen("DD", "DL"));
                    if (d > 0) CloseTo(d);
                }
                element.Parent = Current();
                Current().Children.Add(element);
                if (s_rawTextTags.Contains(name))
                {
                    // 中身は読まない（閉じタグまで読み飛ばす）
                    var close = new Regex("</" + Regex.Escape(m.Groups[2].Value) + "\\s*>", RegexOptions.IgnoreCase).Match(a_html, pos);
                    pos = close.Success ? close.Index + close.Length : a_html.Length;
                    continue;
                }
                if (!s_voidTags.Contains(name) && "/" != m.Groups[4].Value)
                {
                    stack.Add(element);
                }
            }
            if (pos < a_html.Length) AddText(a_html.Substring(pos));
            return root;
        }

        /// <summary>クリップボードの HTML（CF_HTML）から、コピーした部分（StartFragment〜EndFragment）を取り出す。</summary>
        private static string ExtractFragment(string a_clipboardHtml)
        {
            string html = a_clipboardHtml;
            int start = html.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase);
            int end = html.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);
            if (start >= 0 && end > start)
            {
                return html.Substring(start + "<!--StartFragment-->".Length, end - start - "<!--StartFragment-->".Length);
            }
            // CF_HTML の見出し（Version:…）の後ろだけを使う
            int tag = html.IndexOf('<');
            return tag > 0 ? html.Substring(tag) : html;
        }

        // ======================================================================
        //  Markdown への変換
        // ======================================================================

        /// <summary>読み飛ばす要素（中身も使わない）。</summary>
        private static readonly HashSet<string> s_skipTags = new HashSet<string>
        {
            "SCRIPT", "STYLE", "META", "LINK", "TITLE", "HEAD", "TEMPLATE", "NOSCRIPT", "BUTTON", "SELECT", "TEXTAREA", "INPUT",
            "SVG", "CANVAS", "IFRAME", "OBJECT", "EMBED", "VIDEO", "AUDIO", "XML",
        };

        /// <summary>中身をブロックの並びとして読む入れ物の要素。</summary>
        private static readonly HashSet<string> s_containerTags = new HashSet<string>
        {
            "BODY", "HTML", "DIV", "SECTION", "ARTICLE", "MAIN", "HEADER", "FOOTER", "NAV", "ASIDE", "FIGURE", "FIGCAPTION",
            "DETAILS", "SUMMARY", "CENTER", "FORM", "FIELDSET", "DL", "DT", "DD", "ADDRESS", "CAPTION",
        };

        /// <summary>中にあれば、その要素を入れ物として読むブロック要素。</summary>
        private static readonly HashSet<string> s_blockTags = new HashSet<string>
        {
            "P", "DIV", "UL", "OL", "LI", "H1", "H2", "H3", "H4", "H5", "H6", "PRE", "BLOCKQUOTE", "TABLE", "HR",
        };

        /// <summary>日本語等の文字の間の HTML の改行（Word の HTML は長い行を改行で折り返す）。空白にせずに詰める。</summary>
        private static readonly Regex s_cjkNewlineRegex = new Regex(
            "([\u2E80-\u9FFF\uF900-\uFAFF\uFF00-\uFFEF])[ \\t]*\\n[ \\t\\n]*(?=[\u2E80-\u9FFF\uF900-\uFAFF\uFF00-\uFFEF])",
            RegexOptions.Compiled);

        /// <summary>変換の状態。</summary>
        private sealed class Context
        {
            public bool RichFlg;
            public string Bullet = "-";
        }

        /// <summary>
        /// クリップボードの HTML を Markdown にする。
        /// </summary>
        /// <param name="a_clipboardHtml">クリップボードの HTML（CF_HTML の見出しが付いたままでよい）。</param>
        /// <param name="a_bullet">箇条書きの記号（"-" 等）。</param>
        /// <returns>変換の結果。HTML が空なら null。</returns>
        public static Result Convert(string a_clipboardHtml, string a_bullet = "-")
        {
            if (string.IsNullOrWhiteSpace(a_clipboardHtml))
            {
                return null;
            }
            HtmlNode root = Parse(ExtractFragment(a_clipboardHtml));
            var ctx = new Context { Bullet = string.IsNullOrEmpty(a_bullet) ? "-" : a_bullet };
            var blocks = ConvertBlocks(root, false, ctx);
            string markdown = string.Join("\n\n", blocks.Where(b => !string.IsNullOrWhiteSpace(b)));
            return new Result { Markdown = markdown, RichFlg = ctx.RichFlg };
        }

        /// <summary>表示されない要素か（display:none・Word の mso-hide:all・hidden 属性）。</summary>
        private static bool IsHidden(HtmlNode a_el) =>
            Regex.IsMatch(a_el.Style, "(?:^|;)\\s*display\\s*:\\s*none|mso-hide\\s*:\\s*all", RegexOptions.IgnoreCase) ||
            a_el.Attrs.ContainsKey("hidden");

        /// <summary>要素の中で、空白・改行をそのまま表示するか（pre・white-space の指定）。</summary>
        private static bool KeepsWhiteSpace(HtmlNode a_el, bool a_inherited)
        {
            if ("PRE" == a_el.Tag) return true;
            var m = Regex.Match(a_el.Style, "white-space\\s*:\\s*([\\w-]+)", RegexOptions.IgnoreCase);
            return m.Success ? Regex.IsMatch(m.Groups[1].Value, "^(?:pre|pre-wrap|pre-line|break-spaces)$", RegexOptions.IgnoreCase) : a_inherited;
        }

        /// <summary>Word のリストの段落の深さ（style の「mso-list:l0 level1 lfo1」）。リストの段落でなければ 0。</summary>
        private static int MsoListLevel(HtmlNode a_node)
        {
            if (a_node.IsText) return 0;
            var m = Regex.Match(a_node.Style, "mso-list\\s*:\\s*l\\d+\\s+level(\\d+)", RegexOptions.IgnoreCase);
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }

        /// <summary>Word のリストの段落が属するリスト（「l0」等）。</summary>
        private static string MsoListId(HtmlNode a_node) =>
            Regex.Match(a_node.Style, "mso-list\\s*:\\s*(l\\d+)", RegexOptions.IgnoreCase).Groups[1].Value;

        /// <summary>Word のリストの記号（style に「mso-list:Ignore」が付いた要素）か。</summary>
        private static bool IsMsoListMarker(HtmlNode a_node) =>
            !a_node.IsText && Regex.IsMatch(a_node.Style, "mso-list\\s*:\\s*ignore", RegexOptions.IgnoreCase);

        /// <summary>
        /// 要素の子を、ブロック（段落・見出し・リスト等）の Markdown の並びにする。ブロックの間にある文字・太字等は、
        /// まとめて段落にする。
        /// </summary>
        private static List<string> ConvertBlocks(HtmlNode a_src, bool a_ws, Context a_ctx)
        {
            var blocks = new List<string>();
            InlineWriter para = null;
            var orphanItems = new List<string>();
            void Flush()
            {
                if (null != para)
                {
                    string text = para.Finish();
                    if (0 < text.Trim().Length) blocks.Add(text);
                    para = null;
                }
            }
            void FlushOrphans()
            {
                if (0 < orphanItems.Count)
                {
                    blocks.Add(string.Join("\n", orphanItems));
                    orphanItems.Clear();
                }
            }
            var nodes = a_src.Children;
            for (int i = 0; i < nodes.Count; i++)
            {
                HtmlNode node = nodes[i];
                if (node.IsText)
                {
                    if (null == para && 0 == node.Text.Trim().Length) continue; // ブロックの間の空白・改行
                    FlushOrphans();
                    para = para ?? new InlineWriter(a_ctx);
                    para.AppendText(node.Text, a_ws);
                    continue;
                }
                string tag = node.Tag;
                if (s_skipTags.Contains(tag) || IsHidden(node)) continue;
                if ("LI" != tag) FlushOrphans();
                bool ws = KeepsWhiteSpace(node, a_ws);
                if (0 < MsoListLevel(node))
                {
                    // Word のリスト：続いた同じリストの段落をまとめて、入れ子のリストにする
                    var items = new List<HtmlNode>();
                    string id = MsoListId(node);
                    int j = i;
                    for (; j < nodes.Count; j++)
                    {
                        HtmlNode n = nodes[j];
                        if (!n.IsText && 0 < MsoListLevel(n) && MsoListId(n) == id) items.Add(n);
                        else if (!(n.IsText && 0 == n.Text.Trim().Length)) break;
                    }
                    i = j - 1;
                    Flush();
                    string list = WordList(items, a_ws, a_ctx);
                    if (null != list) blocks.Add(list);
                    continue;
                }
                if (Regex.IsMatch(tag, "^H[1-6]$"))
                {
                    Flush();
                    var h = new InlineWriter(a_ctx);
                    h.AppendChildren(node, ws);
                    string text = h.Finish().Replace("\n", " ").Trim();
                    if (0 < text.Length)
                    {
                        blocks.Add(new string('#', tag[1] - '0') + " " + text);
                        a_ctx.RichFlg = true;
                    }
                    continue;
                }
                switch (tag)
                {
                    case "P":
                        Flush();
                        para = new InlineWriter(a_ctx);
                        para.AppendChildren(node, ws);
                        Flush();
                        break;
                    case "UL":
                    case "OL":
                    {
                        Flush();
                        string list = ConvertList(node, ws, a_ctx);
                        if (null != list) blocks.Add(list);
                        break;
                    }
                    case "LI":
                        // リストの外の項目（コピーした範囲がリストの一部だけの場合等）：続いた項目を箇条書きにまとめる
                        Flush();
                        orphanItems.Add(ConvertItem(node, a_ctx.Bullet + " ", ws, a_ctx));
                        a_ctx.RichFlg = true;
                        break;
                    case "PRE":
                        Flush();
                        blocks.Add(ConvertPre(node));
                        a_ctx.RichFlg = true;
                        break;
                    case "BLOCKQUOTE":
                    {
                        Flush();
                        var inner = ConvertBlocks(node, ws, a_ctx);
                        if (0 < inner.Count)
                        {
                            string text = string.Join("\n\n", inner);
                            blocks.Add(string.Join("\n", text.Split('\n').Select(l => 0 < l.Length ? "> " + l : ">")));
                            a_ctx.RichFlg = true;
                        }
                        break;
                    }
                    case "TABLE":
                    {
                        Flush();
                        string table = ConvertTable(node, ws, a_ctx);
                        if (null != table) blocks.Add(table);
                        break;
                    }
                    case "HR":
                        Flush();
                        blocks.Add("---");
                        a_ctx.RichFlg = true;
                        break;
                    case "BR":
                        para?.AppendBreak();
                        break;
                    default:
                        // 中にブロック要素がある、太字等の要素（Google ドキュメントの全体を囲む <b> 等）も、入れ物として読む
                        if (s_containerTags.Contains(tag) || s_blockTags.Contains(tag) || "TR" == tag ||
                            node.Descendants().Any(d => s_blockTags.Contains(d.Tag)))
                        {
                            Flush();
                            blocks.AddRange(ConvertBlocks(node, ws, a_ctx));
                        }
                        else
                        {
                            // 文字・太字・リンク・画像等：段落にまとめる
                            para = para ?? new InlineWriter(a_ctx);
                            para.AppendNode(node, a_ws);
                        }
                        break;
                }
            }
            Flush();
            FlushOrphans();
            return blocks;
        }

        /// <summary>リスト（ul・ol）を Markdown にする。項目が無ければ null。</summary>
        private static string ConvertList(HtmlNode a_src, bool a_ws, Context a_ctx)
        {
            bool orderedFlg = "OL" == a_src.Tag;
            int number = 1;
            if (orderedFlg && int.TryParse(a_src.Attr("start"), out int start) && start >= 0)
            {
                number = start;
            }
            var items = new List<string>();
            foreach (HtmlNode child in a_src.Children)
            {
                if (child.IsText) continue;
                if (s_skipTags.Contains(child.Tag) || IsHidden(child)) continue;
                bool ws = KeepsWhiteSpace(child, a_ws);
                if ("UL" == child.Tag || "OL" == child.Tag)
                {
                    // 項目の外に書かれた入れ子のリスト：前の項目の中に入れる
                    string nested = ConvertList(child, ws, a_ctx);
                    if (null == nested) continue;
                    if (0 == items.Count) items.Add(orderedFlg ? $"{number++}. " : a_ctx.Bullet + " ");
                    string marker = orderedFlg ? $"{number - 1}. " : a_ctx.Bullet + " ";
                    items[items.Count - 1] += "\n" + Indent(nested, marker.Length);
                    continue;
                }
                string itemMarker = orderedFlg ? $"{number}. " : a_ctx.Bullet + " ";
                string item = ConvertItem(child, itemMarker, ws, a_ctx);
                if (0 < item.Trim().Length && item.Trim() != itemMarker.Trim())
                {
                    items.Add(item);
                    number++;
                }
            }
            if (0 == items.Count) return null;
            a_ctx.RichFlg = true;
            return string.Join("\n", items);
        }

        /// <summary>リストの項目（li）を Markdown にする（a_marker で始め、続きの行は記号の幅だけ字下げする）。
        /// チェックボックスはタスクリストの項目にする。</summary>
        private static string ConvertItem(HtmlNode a_src, string a_marker, bool a_ws, Context a_ctx)
        {
            string box = "";
            HtmlNode checkbox = a_src.Descendants().FirstOrDefault(d => "INPUT" == d.Tag &&
                string.Equals(d.Attr("type"), "checkbox", StringComparison.OrdinalIgnoreCase));
            if (null != checkbox && ReferenceEquals(ClosestItem(checkbox), a_src))
            {
                box = checkbox.Attrs.ContainsKey("checked") ? "[x] " : "[ ] ";
            }
            var blocks = ConvertBlocks(a_src, a_ws, a_ctx);
            if (0 == blocks.Count)
            {
                return a_marker + box.TrimEnd();
            }
            var sb = new StringBuilder();
            // 最初のブロックが段落なら、項目の文字にする（入れ子のリスト等なら、記号だけの行の下に置く）
            string first = blocks[0];
            bool firstIsTextFlg = !Regex.IsMatch(first, "^(?:[-*+] |\\d+\\. |```|> |\\|)");
            int index = 0;
            if (firstIsTextFlg)
            {
                sb.Append(a_marker).Append(box).Append(Indent(first, a_marker.Length).TrimStart());
                index = 1;
            }
            else
            {
                sb.Append(a_marker).Append(box.TrimEnd());
            }
            for (; index < blocks.Count; index++)
            {
                sb.Append('\n').Append(Indent(blocks[index], a_marker.Length));
            }
            return sb.ToString();
        }

        /// <summary>ノードを含む、いちばん近い LI。</summary>
        private static HtmlNode ClosestItem(HtmlNode a_node)
        {
            for (HtmlNode n = a_node.Parent; null != n; n = n.Parent)
            {
                if ("LI" == n.Tag) return n;
            }
            return null;
        }

        /// <summary>複数行の文字の、各行（空行以外）を字下げする。</summary>
        private static string Indent(string a_text, int a_width)
        {
            string pad = new string(' ', a_width);
            return string.Join("\n", a_text.Split('\n').Select(l => 0 < l.Length ? pad + l : l));
        }

        /// <summary>コードブロック（pre）を Markdown にする。言語は、class の「language-…」「lang-…」から読む。</summary>
        private static string ConvertPre(HtmlNode a_src)
        {
            HtmlNode code = a_src.Descendants().FirstOrDefault(d => "CODE" == d.Tag);
            string classes = (a_src.Attr("class") ?? "") + " " + (code?.Attr("class") ?? "");
            var m = Regex.Match(classes, "(?:^|\\s)(?:language|lang)-([\\w+#.-]+)");
            var sb = new StringBuilder();
            // 1行ずつ div 等に分けて書く Web ページもあるため、ブロック要素の後ろで改行する
            void Walk(HtmlNode a_node)
            {
                foreach (HtmlNode c in a_node.Children)
                {
                    if (c.IsText)
                    {
                        sb.Append(c.Text);
                        continue;
                    }
                    if (s_skipTags.Contains(c.Tag) || IsHidden(c)) continue;
                    if ("BR" == c.Tag)
                    {
                        sb.Append('\n');
                        continue;
                    }
                    Walk(c);
                    if (Regex.IsMatch(c.Tag, "^(?:DIV|P|LI|TR)$") && (0 == sb.Length || '\n' != sb[sb.Length - 1])) sb.Append('\n');
                }
            }
            Walk(a_src);
            string text = sb.ToString().Replace("\r\n", "\n").Replace('\r', '\n').Replace('\u00A0', ' ').TrimEnd('\n');
            string fence = text.Contains("```") ? "~~~" : "```";
            return fence + (m.Success ? m.Groups[1].Value : "") + "\n" + text + "\n" + fence;
        }

        /// <summary>表（table）を Markdown にする。1つ目の行を見出しの行にする。セルの中の改行は &lt;br&gt; にする。
        /// 横に結合したセルは、空のセルで埋める。行が無ければ null。</summary>
        private static string ConvertTable(HtmlNode a_src, bool a_ws, Context a_ctx)
        {
            var rows = new List<List<string>>();
            void CollectRows(HtmlNode a_node)
            {
                foreach (HtmlNode c in a_node.Children)
                {
                    if (c.IsText) continue;
                    if ("TR" == c.Tag)
                    {
                        var row = new List<string>();
                        foreach (HtmlNode cell in c.Children.Where(x => "TD" == x.Tag || "TH" == x.Tag))
                        {
                            var w = new InlineWriter(a_ctx);
                            w.AppendChildren(cell, KeepsWhiteSpace(cell, a_ws));
                            row.Add(w.Finish().Replace("|", "\\|").Replace("\n", "<br>"));
                            int span = int.TryParse(cell.Attr("colspan"), out int s) ? s : 1;
                            for (int k = 1; k < span; k++) row.Add("");
                        }
                        if (0 < row.Count) rows.Add(row);
                    }
                    else if ("TABLE" != c.Tag)
                    {
                        CollectRows(c); // THEAD・TBODY・TFOOT
                    }
                }
            }
            CollectRows(a_src);
            if (0 == rows.Count) return null;
            int columns = rows.Max(r => r.Count);
            string Line(List<string> a_row) =>
                "| " + string.Join(" | ", Enumerable.Range(0, columns).Select(k => k < a_row.Count ? a_row[k] : "")) + " |";
            var lines = new List<string> { Line(rows[0]), "| " + string.Join(" | ", Enumerable.Repeat("---", columns)) + " |" };
            lines.AddRange(rows.Skip(1).Select(Line));
            a_ctx.RichFlg = true;
            return string.Join("\n", lines);
        }

        /// <summary>Word のリストの段落の並びを、入れ子のリストにする。記号が番号（「1.」「a)」「①」等）なら番号付き
        /// リストにする。</summary>
        private static string WordList(List<HtmlNode> a_items, bool a_ws, Context a_ctx)
        {
            var lines = new List<string>();
            var levels = new List<(int Level, bool OrderedFlg, int Number, int Indent)>();
            foreach (HtmlNode p in a_items)
            {
                int level = MsoListLevel(p);
                string marker = string.Concat(p.Descendants().Where(IsMsoListMarker).Select(e => e.TextContent))
                    .Replace('\u00A0', ' ').Trim();
                var num = Regex.Match(marker, "^[(（]?([0-9]+|[A-Za-z]{1,3}|[\u2160-\u217F]+|[一二三四五六七八九十]+)[.)．）、]|^[\u2460-\u24FF]");
                while (0 < levels.Count && levels[levels.Count - 1].Level > level) levels.RemoveAt(levels.Count - 1);
                if (0 == levels.Count || levels[levels.Count - 1].Level < level)
                {
                    int indent = 0 == levels.Count ? 0 : levels[levels.Count - 1].Indent + (levels[levels.Count - 1].OrderedFlg ? 3 : 2);
                    int start = num.Success && int.TryParse(num.Groups[1].Value, out int n) ? n : 1;
                    levels.Add((level, num.Success, start, indent));
                }
                var top = levels[levels.Count - 1];
                var w = new InlineWriter(a_ctx);
                foreach (HtmlNode c in p.Children)
                {
                    if (IsMsoListMarker(c)) continue;
                    w.AppendNode(c, KeepsWhiteSpace(p, a_ws));
                }
                string itemMarker = top.OrderedFlg ? $"{top.Number}. " : a_ctx.Bullet + " ";
                lines.Add(new string(' ', top.Indent) + itemMarker + w.Finish().Replace("\n", " "));
                levels[levels.Count - 1] = (top.Level, top.OrderedFlg, top.Number + 1, top.Indent);
            }
            if (0 == lines.Count) return null;
            a_ctx.RichFlg = true;
            return string.Join("\n", lines);
        }

        // ======================================================================
        //  段落の中身（文字・書式・リンク・画像）
        // ======================================================================

        /// <summary>
        /// 段落等の中身を Markdown にする。続いた空白・改行を1つの空白にし、書式（太字等）の記号で囲む。書式の中の
        /// 書式・リンク等は、外側の書式だけにする（mde の書式は1つの文字につき1つだけのため）。
        /// </summary>
        private sealed class InlineWriter
        {
            private readonly StringBuilder m_sb = new StringBuilder();
            private readonly Context m_ctx;
            /// <summary>書式（太字等）の中か（中の書式・リンクは、文字だけにする）。</summary>
            private int m_styleDepth;

            public InlineWriter(Context a_ctx)
            {
                m_ctx = a_ctx;
            }

            /// <summary>前の文字が空白（または行の始まり）か。</summary>
            private bool EndsWithSpace => 0 == m_sb.Length || ' ' == m_sb[m_sb.Length - 1] || '\n' == m_sb[m_sb.Length - 1];

            public void AppendText(string a_text, bool a_ws)
            {
                string text = a_text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\u00A0', ' ');
                if (a_ws)
                {
                    m_sb.Append(EscapeText(text));
                    return;
                }
                text = s_cjkNewlineRegex.Replace(text, "$1");
                text = Regex.Replace(text, "[ \\t\\n\\f]+", " ");
                if (text.StartsWith(" ") && EndsWithSpace) text = text.Substring(1);
                m_sb.Append(EscapeText(text));
            }

            public void AppendBreak()
            {
                // 行の終わりの空白を除いてから改行する
                while (0 < m_sb.Length && ' ' == m_sb[m_sb.Length - 1]) m_sb.Length--;
                m_sb.Append('\n');
            }

            public void AppendChildren(HtmlNode a_node, bool a_ws)
            {
                foreach (HtmlNode c in a_node.Children) AppendNode(c, a_ws);
            }

            /// <summary>書式の記号で囲む（中身の前後の空白は、囲みの外に出す。中身が無ければ空白だけにする）。</summary>
            private void Wrap(HtmlNode a_node, bool a_ws, string a_open, string a_close)
            {
                if (0 < m_styleDepth)
                {
                    AppendChildren(a_node, a_ws);
                    return;
                }
                var inner = new InlineWriter(m_ctx) { m_styleDepth = 1 };
                inner.AppendChildren(a_node, a_ws);
                string content = inner.m_sb.ToString();
                string trimmed = content.Trim(' ', '\n');
                if (0 == trimmed.Length)
                {
                    if (0 < content.Length && !EndsWithSpace) m_sb.Append(' ');
                    return;
                }
                if (content.StartsWith(" ") && !EndsWithSpace) m_sb.Append(' ');
                m_sb.Append(a_open).Append(trimmed.Replace("\n", " ")).Append(a_close);
                if (content.EndsWith(" ")) m_sb.Append(' ');
                m_ctx.RichFlg = true;
            }

            public void AppendNode(HtmlNode a_node, bool a_ws)
            {
                if (a_node.IsText)
                {
                    AppendText(a_node.Text, a_ws);
                    return;
                }
                string tag = a_node.Tag;
                if (s_skipTags.Contains(tag) || IsHidden(a_node) || IsMsoListMarker(a_node)) return;
                bool ws = KeepsWhiteSpace(a_node, a_ws);
                string style = a_node.Style;
                switch (tag)
                {
                    case "BR":
                        AppendBreak();
                        return;
                    case "STRONG":
                    case "B":
                        // Google ドキュメントは全体を <b style="font-weight:normal"> で囲むため、太字の指定を見る
                        if (Regex.IsMatch(style, "font-weight\\s*:\\s*(?:normal|[1-5]00)\\b", RegexOptions.IgnoreCase)) break;
                        Wrap(a_node, ws, "**", "**");
                        return;
                    case "EM":
                    case "I":
                    case "CITE":
                    case "VAR":
                    case "DFN":
                        if (Regex.IsMatch(style, "font-style\\s*:\\s*normal", RegexOptions.IgnoreCase)) break;
                        Wrap(a_node, ws, "*", "*");
                        return;
                    case "S":
                    case "DEL":
                    case "STRIKE":
                        Wrap(a_node, ws, "~~", "~~");
                        return;
                    case "U":
                    case "INS":
                        Wrap(a_node, ws, "<u>", "</u>");
                        return;
                    case "MARK":
                        Wrap(a_node, ws, "==", "==");
                        return;
                    case "CODE":
                    case "KBD":
                    case "SAMP":
                    case "TT":
                    {
                        string text = Regex.Replace(a_node.TextContent.Replace('\u00A0', ' '), "\\s+", " ");
                        if (0 == text.Trim().Length)
                        {
                            if (0 < text.Length && !EndsWithSpace) m_sb.Append(' ');
                            return;
                        }
                        if (0 < m_styleDepth)
                        {
                            m_sb.Append(EscapeText(text));
                            return;
                        }
                        string fence = text.Contains("`") ? "``" : "`";
                        string pad = text.StartsWith("`") || text.EndsWith("`") ? " " : "";
                        m_sb.Append(fence).Append(pad).Append(text).Append(pad).Append(fence);
                        m_ctx.RichFlg = true;
                        return;
                    }
                    case "A":
                    {
                        string href = (a_node.Attr("href") ?? "").Trim();
                        if (!Regex.IsMatch(href, "^(?:https?:|mailto:|ftp:)", RegexOptions.IgnoreCase) || 0 < m_styleDepth) break;
                        var inner = new InlineWriter(m_ctx) { m_styleDepth = 1 };
                        inner.AppendChildren(a_node, ws);
                        string content = inner.m_sb.ToString();
                        string text = content.Replace("\n", " ").Trim();
                        if (0 == text.Length) return;
                        if (content.StartsWith(" ") && !EndsWithSpace) m_sb.Append(' ');
                        string destination = Regex.IsMatch(href, "[\\s()<>]") ? "<" + href + ">" : href;
                        if (text == EscapeText(href) && Regex.IsMatch(href, "^https?:", RegexOptions.IgnoreCase))
                        {
                            m_sb.Append('<').Append(href).Append('>');
                        }
                        else
                        {
                            m_sb.Append('[').Append(text.Replace("[", "\\[").Replace("]", "\\]")).Append("](").Append(destination).Append(')');
                        }
                        if (content.EndsWith(" ")) m_sb.Append(' ');
                        m_ctx.RichFlg = true;
                        return;
                    }
                    case "IMG":
                    {
                        // Web 上の画像だけを入れる（PC の中の画像（file:///…。Word 等の一時ファイル）は入れない）
                        string src = (a_node.Attr("src") ?? "").Trim();
                        if (!Regex.IsMatch(src, "^https?:", RegexOptions.IgnoreCase)) return;
                        string alt = Regex.Replace(a_node.Attr("alt") ?? "", "\\s+", " ").Trim().Replace("[", "\\[").Replace("]", "\\]");
                        m_sb.Append("![").Append(alt).Append("](").Append(src).Append(')');
                        m_ctx.RichFlg = true;
                        return;
                    }
                }
                if (s_blockTags.Contains(tag) || s_containerTags.Contains(tag) || "TR" == tag)
                {
                    // 段落の中のブロック要素（表のセルの中の段落等）：前後で改行する
                    if (0 < m_sb.Length && '\n' != m_sb[m_sb.Length - 1]) AppendBreak();
                    AppendChildren(a_node, ws);
                    if (0 < m_sb.Length && '\n' != m_sb[m_sb.Length - 1]) AppendBreak();
                    return;
                }
                // 文字の飾りの指定（Web ページ・Google ドキュメントの span）による太字・斜体・取り消し線
                if (Regex.IsMatch(style, "font-weight\\s*:\\s*(?:bold|bolder|[6-9]00)\\b", RegexOptions.IgnoreCase))
                {
                    Wrap(a_node, ws, "**", "**");
                    return;
                }
                if (Regex.IsMatch(style, "font-style\\s*:\\s*italic", RegexOptions.IgnoreCase))
                {
                    Wrap(a_node, ws, "*", "*");
                    return;
                }
                if (Regex.IsMatch(style, "text-decoration[\\w-]*\\s*:[^;]*line-through", RegexOptions.IgnoreCase))
                {
                    Wrap(a_node, ws, "~~", "~~");
                    return;
                }
                AppendChildren(a_node, ws);
            }

            /// <summary>中身を仕上げる：各行の前後の空白を除き、先頭・末尾の改行を除く。</summary>
            public string Finish()
            {
                var lines = m_sb.ToString().Split('\n').Select(l => l.Trim(' '));
                return string.Join("\n", lines).Trim('\n');
            }
        }

        /// <summary>
        /// 文字を、Markdown の記号として読まれないようにする（mdew・mdee の markdown.js の escapeText と同じ）。
        /// </summary>
        public static string EscapeText(string a_text)
        {
            string t = a_text.Replace("\u200B", "");
            t = Regex.Replace(t, "\\\\(?=[!-/:-@\\[-`{-~])", "\\\\");
            t = Regex.Replace(t, "[*`]", "\\$&");
            t = t.Replace("~~", "\\~\\~").Replace("==", "\\=\\=");
            t = Regex.Replace(t, "(^|[^\\p{L}\\p{N}])_|_(?=[^\\p{L}\\p{N}]|$)", m => m.Value.Replace("_", "\\_"));
            t = Regex.Replace(t, "<(?=[a-zA-Z/!])", "\\<");
            if (Regex.IsMatch(t, "\\]\\(|\\]\\["))
            {
                t = t.Replace("[", "\\[");
            }
            return t;
        }
    }
}
