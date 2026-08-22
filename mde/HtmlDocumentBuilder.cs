// HtmlDocumentBuilder.cs
//
// mde (MarkDown インラインエディタ) の一部。
// ChromiumPdfExporter（headless ChromeでのPDF書き出し）が使う、FlowDocumentからHTML文書
// （文字列）を組み立てる処理。実際のブラウザのCSSエンジンでレンダリングするため、取り消し線は
// 本物のtext-decoration:line-throughで表現でき、フォントも画面表示と同じ游ゴシック UIを
// そのまま指定して問題なく使える。
//
// 見出し・カスタムアンカーへのジャンプは、実際のHTMLの<a href="#id">とid="..."属性で
// 表現する。これがPDF化した後もリンクとして機能することは、実機での動作確認で確認済み。

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Windows.Controls;
using System.Windows.Documents;

namespace mde
{
    /// <summary>現在のFlowDocumentの内容から、印刷用のHTML文書（文字列）を組み立てる。</summary>
    public class HtmlDocumentBuilder
    {
        private readonly ImageManager m_imageManager;

        // 見出し・カスタムアンカーのHTML id を、それぞれのWPF側のオブジェクトごとに
        // 記録しておく（1回目の走査で採番し、2回目の走査で実際に出力する）。
        private readonly Dictionary<Paragraph, string> m_headingIds = new Dictionary<Paragraph, string>();
        private readonly Dictionary<Run, string> m_customAnchorIds = new Dictionary<Run, string>();

        // 「#アンカー名」形式のリンクを解決するための対応表。キーは、見出しの完全なテキスト・
        // 見出しのスラッグ・カスタムアンカーのidのいずれか。値はHTML id。
        private readonly Dictionary<string, string> m_anchorTargets = new Dictionary<string, string>();

        /// <param name="a_imageManager">画像のsrcから実ファイルパスを解決するために使う。</param>
        public HtmlDocumentBuilder(ImageManager a_imageManager)
        {
            m_imageManager = a_imageManager;
        }

        /// <summary>指定したFlowDocumentの内容から、単体のHTML文書（文字列）を組み立てる。</summary>
        /// <param name="a_doc">対象の文書。</param>
        /// <returns>組み立てたHTML文字列。</returns>
        public string BuildHtml(FlowDocument a_doc)
        {
            m_headingIds.Clear();
            m_customAnchorIds.Clear();
            m_anchorTargets.Clear();

            CollectAnchors(a_doc);

            var body = new StringBuilder();
            foreach (Block block in a_doc.Blocks)
            {
                AppendBlock(body, block);
            }

            var html = new StringBuilder();
            html.Append("<!DOCTYPE html><html lang=\"ja\"><head><meta charset=\"UTF-8\"><style>");
            html.Append(CSS);
            html.Append("</style></head><body>");
            html.Append(body);
            html.Append("</body></html>");
            return html.ToString();
        }

        private const string CSS = @"
            * { box-sizing: border-box; }
            body { font-family: 'Yu Gothic UI', 'Yu Gothic', 'Meiryo', sans-serif; font-size: 10.5pt;
                   line-height: 1.7; color: #1a1a1a; margin: 0; }
            h1, h2, h3, h4, h5, h6 { font-family: 'Yu Gothic UI', 'Yu Gothic', 'Meiryo', sans-serif;
                   font-weight: bold; break-after: avoid-page; margin: 1em 0 0.45em; }
            h1 { font-size: 22pt; border-bottom: 0.75pt solid #B4B4B4; padding-bottom: 3pt; }
            h2 { font-size: 18pt; border-bottom: 0.75pt solid #B4B4B4; padding-bottom: 3pt; }
            h3 { font-size: 15.5pt; }
            h4 { font-size: 14pt; }
            h5 { font-size: 13pt; }
            h6 { font-size: 12pt; }
            p { margin: 0 0 8pt; }
            a { color: #0969DA; text-decoration: underline; }
            a.mde-plain-link { color: #0969DA; text-decoration: underline; pointer-events: none; }
            s { text-decoration: line-through; }
            pre.mde-codeblock { font-family: Consolas, monospace; background: #ECE8DC;
                   border: 1px solid #B4B4B4; border-radius: 2pt; padding: 6pt 8pt; font-size: 9.5pt;
                   white-space: pre-wrap; word-break: break-all; margin: 3pt 0 10pt; }
            code.mde-inline-code { font-family: Consolas, monospace; background: #f0efe9;
                   padding: 0 3pt; border-radius: 2pt; font-size: 0.92em; }
            table { border-collapse: collapse; width: 100%; margin: 4pt 0 12pt; }
            th, td { border: 0.5pt solid #B4B4B4; padding: 4pt 6pt; text-align: left; vertical-align: middle; }
            thead { display: table-header-group; }
            th { background: #F8F8F8; font-weight: bold; }
            tr { break-inside: avoid-page; }
            ul, ol { margin: 0 0 8pt; padding-left: 1.4em; }
            li { margin: 2pt 0; }
            img { max-width: 100%; }
        ";

        // ======================================================================
        //  1回目の走査：見出し・カスタムアンカーのHTML idを採番する
        // ======================================================================

        private void CollectAnchors(FlowDocument a_doc)
        {
            int headingCounter = 0;
            int anchorCounter = 0;
            foreach (Block block in a_doc.Blocks)
            {
                CollectAnchorsInBlock(block, ref headingCounter, ref anchorCounter);
            }
        }

        private void CollectAnchorsInBlock(Block a_block, ref int a_headingCounter, ref int a_anchorCounter)
        {
            if (a_block is Paragraph p)
            {
                if (p.Tag is int level && level > 0)
                {
                    string text = new TextRange(p.ContentStart, p.ContentEnd).Text.Trim();
                    string id = "h" + a_headingCounter++;
                    m_headingIds[p] = id;
                    if (!m_anchorTargets.ContainsKey(text))
                    {
                        m_anchorTargets[text] = id;
                    }
                    string slug = SlugifyHeading(text);
                    if (!m_anchorTargets.ContainsKey(slug))
                    {
                        m_anchorTargets[slug] = id;
                    }
                }
                CollectAnchorsInInlines(p.Inlines, ref a_anchorCounter);
            }
            else if (a_block is List list)
            {
                foreach (ListItem li in list.ListItems)
                {
                    foreach (Block b in li.Blocks)
                    {
                        CollectAnchorsInBlock(b, ref a_headingCounter, ref a_anchorCounter);
                    }
                }
            }
            else if (a_block is Table table)
            {
                foreach (TableRowGroup rg in table.RowGroups)
                {
                    foreach (TableRow row in rg.Rows)
                    {
                        foreach (TableCell cell in row.Cells)
                        {
                            foreach (Block b in cell.Blocks)
                            {
                                CollectAnchorsInBlock(b, ref a_headingCounter, ref a_anchorCounter);
                            }
                        }
                    }
                }
            }
        }

        private void CollectAnchorsInInlines(InlineCollection a_inlines, ref int a_anchorCounter)
        {
            foreach (Inline inline in a_inlines)
            {
                if (inline is Run run && run.Tag is AnchorInfo info)
                {
                    string id = "a" + a_anchorCounter++;
                    m_customAnchorIds[run] = id;
                    if (!string.IsNullOrEmpty(info.m_id) && !m_anchorTargets.ContainsKey(info.m_id))
                    {
                        m_anchorTargets[info.m_id] = id;
                    }
                }
                else if (inline is Span span)
                {
                    CollectAnchorsInInlines(span.Inlines, ref a_anchorCounter);
                }
            }
        }

        /// <summary>見出しのテキストから、GitHub等が採用している方式に近いアンカー用スラッグを
        /// 生成する（InlineStyleEditor.SlugifyHeadingと同じ規則）。</summary>
        private static string SlugifyHeading(string a_headingText)
        {
            string lower = a_headingText.ToLowerInvariant();
            var sb = new StringBuilder();
            foreach (char c in lower)
            {
                if (char.IsLetterOrDigit(c) || '_' == c || '-' == c || ' ' == c)
                {
                    sb.Append(c);
                }
            }
            return sb.ToString().Trim().Replace(' ', '-');
        }

        // ======================================================================
        //  2回目の走査：実際にHTMLを組み立てる
        // ======================================================================

        private void AppendBlock(StringBuilder a_html, Block a_block)
        {
            if (a_block is Paragraph p)
            {
                if (p.Tag is CodeBlockInfo)
                {
                    AppendCodeBlock(a_html, p);
                }
                else if (p.Tag is int level && level > 0)
                {
                    a_html.Append("<h").Append(level).Append(" id=\"").Append(m_headingIds[p]).Append("\">");
                    AppendInlines(a_html, p.Inlines);
                    a_html.Append("</h").Append(level).Append('>');
                }
                else
                {
                    a_html.Append("<p>");
                    AppendInlines(a_html, p.Inlines);
                    a_html.Append("</p>");
                }
            }
            else if (a_block is List list)
            {
                AppendList(a_html, list);
            }
            else if (a_block is Table table)
            {
                AppendTable(a_html, table);
            }
        }

        private void AppendCodeBlock(StringBuilder a_html, Paragraph a_p)
        {
            string text = new TextRange(a_p.ContentStart, a_p.ContentEnd).Text;
            a_html.Append("<pre class=\"mde-codeblock\">").Append(WebUtility.HtmlEncode(text)).Append("</pre>");
        }

        private void AppendList(StringBuilder a_html, List a_list)
        {
            bool orderedFlg = System.Windows.TextMarkerStyle.Decimal == a_list.MarkerStyle;
            string tag = orderedFlg ? "ol" : "ul";
            a_html.Append('<').Append(tag).Append('>');
            foreach (ListItem li in a_list.ListItems)
            {
                a_html.Append("<li>");
                bool firstBlockFlg = true;
                foreach (Block b in li.Blocks)
                {
                    if (b is Paragraph itemPara)
                    {
                        // 項目内の最初の段落は<li>自体の中身として、2つ目以降は改行を挟んで出力する
                        // （<li>の中に複数の<p>を入れるとブラウザによって余白が不安定になるため）。
                        if (!firstBlockFlg)
                        {
                            a_html.Append("<br>");
                        }
                        AppendInlines(a_html, itemPara.Inlines);
                        firstBlockFlg = false;
                    }
                    else if (b is List nestedList)
                    {
                        AppendList(a_html, nestedList);
                    }
                    else if (b is Table nestedTable)
                    {
                        AppendTable(a_html, nestedTable);
                    }
                }
                a_html.Append("</li>");
            }
            a_html.Append("</").Append(tag).Append('>');
        }

        private void AppendTable(StringBuilder a_html, Table a_table)
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
                return;
            }

            a_html.Append("<table>");
            for (int r = 0; r < rows.Count; r++)
            {
                bool headerRowFlg = 0 == r;
                if (headerRowFlg)
                {
                    a_html.Append("<thead>");
                }
                else if (1 == r)
                {
                    a_html.Append("<tbody>");
                }
                a_html.Append("<tr>");
                string cellTag = headerRowFlg ? "th" : "td";
                foreach (TableCell cell in rows[r].Cells)
                {
                    a_html.Append('<').Append(cellTag).Append('>');
                    foreach (Block b in cell.Blocks)
                    {
                        if (b is Paragraph cp)
                        {
                            AppendInlines(a_html, cp.Inlines);
                        }
                    }
                    a_html.Append("</").Append(cellTag).Append('>');
                }
                a_html.Append("</tr>");
                if (headerRowFlg)
                {
                    a_html.Append("</thead>");
                }
            }
            a_html.Append("</tbody></table>");
        }

        private void AppendInlines(StringBuilder a_html, InlineCollection a_inlines)
        {
            foreach (Inline inline in a_inlines)
            {
                if (inline is LineBreak)
                {
                    a_html.Append("<br>");
                }
                else if (inline is InlineUIContainer iuc && iuc.Child is Image img)
                {
                    AppendImage(a_html, img);
                }
                else if (inline is Run run)
                {
                    AppendRun(a_html, run);
                }
                else if (inline is Span span)
                {
                    AppendInlines(a_html, span.Inlines);
                }
            }
        }

        private void AppendRun(StringBuilder a_html, Run a_run)
        {
            if (a_run.Tag is AnchorInfo)
            {
                if (m_customAnchorIds.TryGetValue(a_run, out string anchorId))
                {
                    a_html.Append("<a id=\"").Append(anchorId).Append("\"></a>");
                }
                return;
            }

            if (string.IsNullOrEmpty(a_run.Text))
            {
                return;
            }

            if (a_run.Tag is LinkInfo linkInfo)
            {
                AppendLinkRun(a_html, a_run.Text, linkInfo.m_url);
                return;
            }

            string encoded = WebUtility.HtmlEncode(a_run.Text);
            string styleTag = a_run.Tag as string;
            if ("bold" == styleTag)
            {
                a_html.Append("<b>").Append(encoded).Append("</b>");
            }
            else if ("strikethrough" == styleTag)
            {
                a_html.Append("<s>").Append(encoded).Append("</s>");
            }
            else if ("inline-code" == styleTag)
            {
                a_html.Append("<code class=\"mde-inline-code\">").Append(encoded).Append("</code>");
            }
            else
            {
                a_html.Append(encoded);
            }
        }

        private void AppendLinkRun(StringBuilder a_html, string a_text, string a_url)
        {
            string encodedText = WebUtility.HtmlEncode(a_text);
            if (string.IsNullOrWhiteSpace(a_url))
            {
                a_html.Append(encodedText);
                return;
            }

            bool isExternalFlg = System.Text.RegularExpressions.Regex.IsMatch(a_url, "^[a-zA-Z][a-zA-Z0-9+.-]*:") &&
                !System.Text.RegularExpressions.Regex.IsMatch(a_url, "^[a-zA-Z]:[\\\\/]");

            if (isExternalFlg)
            {
                a_html.Append("<a href=\"").Append(WebUtility.HtmlEncode(a_url)).Append("\">")
                    .Append(encodedText).Append("</a>");
                return;
            }

            if (a_url.StartsWith("#"))
            {
                string key = a_url.Substring(1);
                if (m_anchorTargets.TryGetValue(key, out string targetId))
                {
                    a_html.Append("<a href=\"#").Append(targetId).Append("\">").Append(encodedText).Append("</a>");
                    return;
                }
            }

            // 対象外のリンク：見た目だけリンクらしく保ったクリックできないテキストにする。
            a_html.Append("<a class=\"mde-plain-link\" href=\"javascript:void(0)\">").Append(encodedText).Append("</a>");
        }

        private void AppendImage(StringBuilder a_html, Image a_img)
        {
            string path = m_imageManager.GetExportableFilePath(a_img);
            if (string.IsNullOrEmpty(path))
            {
                string alt = (a_img.Tag as ImageInfo)?.m_alt;
                a_html.Append("<i>[画像").Append(string.IsNullOrEmpty(alt) ? "" : ": " + WebUtility.HtmlEncode(alt))
                    .Append("]</i>");
                return;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                string base64 = Convert.ToBase64String(bytes);
                string mime = GuessMimeType(path);
                a_html.Append("<img src=\"data:").Append(mime).Append(";base64,").Append(base64).Append('"');
                if (!double.IsNaN(a_img.Width) && a_img.Width > 0)
                {
                    // 実行環境のカルチャ（小数点の書式）に依存しないよう、常にInvariantCultureで整形する
                    // （CSSの値としては常に "." が小数点である必要があるため）。
                    a_html.Append(" style=\"width:")
                        .Append(a_img.Width.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
                        .Append("px;\"");
                }
                a_html.Append('>');
            }
            catch
            {
                a_html.Append("<i>[画像を読み込めませんでした]</i>");
            }
        }

        private static string GuessMimeType(string a_path)
        {
            string ext = Path.GetExtension(a_path).ToLowerInvariant();
            switch (ext)
            {
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".bmp": return "image/bmp";
                case ".webp": return "image/webp";
                case ".svg": return "image/svg+xml";
                default: return "image/png";
            }
        }
    }
}
