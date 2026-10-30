// InlineStyleEditor.cs
//
// mde (Markdown インラインエディタ) の一部。
// 文中の太字・取り消し線・下線・インラインコード・リンクの装飾を担当するクラス。
// 右クリックメニューからの適用、入力中のリアルタイム変換（**a_text**などを打ち終えた瞬間に
// 反映する）、リンクのクリック・編集・解除を扱う。

using mde.common;
using mde.dialog;
using mde.manager;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace mde.editor
{
    /// <summary>
    /// インライン装飾（太字/取り消し線/下線/インラインコード/リンク）の一式。MainWindowへの参照は
    /// 持たず、Editor本体・「元テキスト保持」の追跡役・各種delegateだけで動作する。
    /// </summary>
    public class InlineStyleEditor
    {
        private static readonly Brush m_linkBrush = MarkdownConverter.LinkBrush; // 文字の色の切り替えで色が変わる共有のブラシ
        // ハイライト（==text==）用の背景色。
        private static readonly Brush m_highlightBackground = BlockStyles.HighlightBrush;

        private readonly RichTextBox m_editor;
        private readonly OriginalTextTracker m_originalTextTracker;
        private readonly Action<Action> m_runAsProgrammaticChange;
        private readonly Action m_markDirty;
        private readonly Action m_refreshOutline;
        private readonly Func<Block, string> m_blockToMarkdown;

        /// <summary>右クリック時にマウス下にあったリンクのRun。右クリックメニューの各項目から参照される。</summary>
        public Run ContextLinkRun { get; set; }

        /// <summary>右クリック時にマウス下にあった段落（コードブロック丸ごとコピー等に使う）。</summary>
        public Paragraph ContextParagraph { get; set; }

        private readonly Func<string> m_getCurrentFileDirectory;
        private readonly Action<string> m_loadFile;
        private readonly Func<string, bool> m_isWithinLoadedFolder;
        private readonly Action<string, string> m_openInNewWindow;
        private readonly Func<bool> m_requireCtrlForLinkClick;

        /// <summary>
        /// InlineStyleEditorを構築する。
        /// </summary>
        /// <param name="a_editor">編集対象のRichTextBox。</param>
        /// <param name="a_originalTextTracker">「元テキスト保持」の追跡役。</param>
        /// <param name="a_runAsProgrammaticChange">処理を「プログラムによる変更」として実行するdelegate。</param>
        /// <param name="a_markDirty">ファイルが変更されたことを通知するdelegate。</param>
        /// <param name="a_refreshOutline">アウトラインペインの再構築を依頼するdelegate。</param>
        /// <param name="a_blockToMarkdown">ブロックをMarkdownテキストへ変換するdelegate（コードブロックのコピーに使う）。</param>
        /// <param name="a_getCurrentFileDirectory">現在のファイルの保存先フォルダを返すdelegate（ファイルリンクの相対パス解決に使う）。</param>
        /// <param name="a_loadFile">同じウィンドウでファイルを開くdelegate。</param>
        /// <param name="a_isWithinLoadedFolder">指定フォルダが、現在フォルダペインに表示されているフォルダの範囲内かどうかを判定するdelegate。</param>
        /// <param name="a_openInNewWindow">フォルダペインの範囲外にあるファイルを、新しいウィンドウで開くdelegate（パスとジャンプ先アンカーを受け取る）。</param>
        public InlineStyleEditor(
            RichTextBox a_editor,
            OriginalTextTracker a_originalTextTracker,
            Action<Action> a_runAsProgrammaticChange,
            Action a_markDirty,
            Action a_refreshOutline,
            Func<Block, string> a_blockToMarkdown,
            Func<string> a_getCurrentFileDirectory,
            Action<string> a_loadFile,
            Func<string, bool> a_isWithinLoadedFolder,
            Action<string, string> a_openInNewWindow,
            Func<bool> a_requireCtrlForLinkClick)
        {
            this.m_editor = a_editor;
            this.m_originalTextTracker = a_originalTextTracker;
            this.m_runAsProgrammaticChange = a_runAsProgrammaticChange;
            this.m_markDirty = a_markDirty;
            this.m_refreshOutline = a_refreshOutline;
            this.m_blockToMarkdown = a_blockToMarkdown;
            this.m_getCurrentFileDirectory = a_getCurrentFileDirectory;
            this.m_loadFile = a_loadFile;
            this.m_isWithinLoadedFolder = a_isWithinLoadedFolder;
            this.m_openInNewWindow = a_openInNewWindow;
            this.m_requireCtrlForLinkClick = a_requireCtrlForLinkClick;
        }

        // ======================================================================
        //  右クリックメニュー「文字装飾」
        // ======================================================================

        /// <summary>右クリック「文字装飾」メニューの処理。"link" が選ばれた場合はURL入力
        /// ダイアログを表示し、それ以外は選択範囲へ直接スタイルを適用する。</summary>
        /// <param name="a_style">"normal"/"code"/"bold"/"strikethrough"/"underline"/"link"。</param>
        /// <param name="a_ownerWindow">URL入力ダイアログの親ウィンドウ。</param>
        public void ApplyTextStyleFromMenu(string a_style, Window a_ownerWindow)
        {
            if ("link" == a_style)
            {
                if (m_editor.Selection.IsEmpty)
                {
                    return;
                }
                var dlg = new LinkInputDialog { Owner = a_ownerWindow };
                if (true == dlg.ShowDialog() && !string.IsNullOrWhiteSpace(dlg.Url))
                {
                    ApplyLinkStyle(dlg.Url);
                }
                return;
            }
            ApplyInlineStyle(a_style);
        }

        /// <summary>
        /// 選択範囲のテキストを取得する。Selection.TextやTextRange.Textは箇条書き項目内では
        /// 行頭マーカー（「1.」「•」等）まで含んでしまうため、Runのテキストを1区間ずつ
        /// たどって連結する。
        /// </summary>
        /// <param name="a_start">範囲の開始位置。</param>
        /// <param name="a_end">範囲の終了位置。</param>
        /// <returns>範囲内のプレーンテキスト（マーカー記号を含まない）。</returns>
        private string GetSafeRangeText(TextPointer a_start, TextPointer a_end)
        {
            var sb = new StringBuilder();
            TextPointer navigator = a_start;
            int guard = 0;
            while (null != navigator &&
                   navigator.CompareTo(a_end) < 0 &&
                   guard < 10000)
            {
                guard++;
                if (navigator.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
                {
                    string runText = navigator.GetTextInRun(LogicalDirection.Forward);
                    if (!string.IsNullOrEmpty(runText))
                    {
                        TextPointer runEnd = navigator.GetPositionAtOffset(runText.Length);
                        if (null != runEnd &&
                            runEnd.CompareTo(a_end) > 0)
                        {
                            // このRunが範囲の終端をまたいでいるので、1文字ずつ終端まで数える
                            TextPointer probe = navigator;
                            int fitCount = 0;
                            for (int i = 0; i < runText.Length; i++)
                            {
                                TextPointer next = probe.GetPositionAtOffset(1);
                                if (null == next ||
                                    next.CompareTo(a_end) > 0) break;
                                probe = next;
                                fitCount++;
                            }
                            sb.Append(runText.Substring(0, fitCount));
                            break;
                        }
                        sb.Append(runText);
                        navigator = runEnd;
                        continue;
                    }
                }
                navigator = navigator.GetNextContextPosition(LogicalDirection.Forward);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 書式のショートカット（Ctrl+B：太字、Ctrl+I：斜体、Ctrl+U：下線、Alt+Shift+5：取り消し線、
        /// Ctrl+Shift+@（`）：インラインコード。mdew・mdee と同じキー）。選んだ文字がすべてその書式なら、書式を
        /// 外して通常の文字に戻す（もう一度押すと元に戻る）。それ以外は、その書式にする（右クリックの「文字装飾」と
        /// 同じ処理）。文字を選んでいない時は何もしない。
        /// </summary>
        /// <param name="a_style">"bold"・"italic"・"underline"・"strikethrough"・"code"のいずれか。</param>
        public void ToggleInlineStyleFromShortcut(string a_style)
        {
            if (null == m_editor.Selection || m_editor.Selection.IsEmpty)
            {
                return;
            }
            ApplyInlineStyle(IsWholeSelectionStyled(a_style) ? "normal" : a_style);
        }

        /// <summary>選んだ範囲の文字（ゼロ幅スペース以外）が、すべて指定の書式か。</summary>
        /// <param name="a_style">"bold"・"italic"・"underline"・"strikethrough"・"code"のいずれか。</param>
        /// <returns>すべてその書式なら true。</returns>
        private bool IsWholeSelectionStyled(string a_style)
        {
            TextPointer end = m_editor.Selection.End;
            TextPointer navigator = m_editor.Selection.Start;
            bool anyTextFlg = false;
            int guard = 0;
            while (null != navigator && navigator.CompareTo(end) < 0 && guard++ < 100000)
            {
                if (TextPointerContext.Text == navigator.GetPointerContext(LogicalDirection.Forward) &&
                    navigator.Parent is Run run &&
                    0 < run.Text.Replace("\u200B", "").Length)
                {
                    string tag = run.Tag as string;
                    bool sameFlg = "code" == a_style ? "inline-code" == tag
                        : "italic" == a_style ? ("italic" == tag || "italic_" == tag)
                        : a_style == tag;
                    if (!sameFlg)
                    {
                        return false;
                    }
                    anyTextFlg = true;
                }
                navigator = navigator.GetNextContextPosition(LogicalDirection.Forward);
            }
            return anyTextFlg;
        }

        /// <summary>現在の選択範囲を、指定URLへのリンクRunに置き換える。</summary>
        /// <param name="a_url">リンク先URL。</param>
        public void ApplyLinkStyle(string a_url)
        {
            if (null == m_editor.Selection || m_editor.Selection.IsEmpty)
            {
                return;
            }
            string text = GetSafeRangeText(m_editor.Selection.Start, m_editor.Selection.End);
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            m_originalTextTracker.Invalidate(m_editor.Selection.Start);

            m_runAsProgrammaticChange(() =>
            {
                TextPointer start = m_editor.Selection.Start;
                m_editor.Selection.Text = "";
                var newRun = new Run(text, start)
                {
                    Foreground = m_linkBrush,
                    TextDecorations = BlockStyles.LinkTextDecorations,
                    Tag = new LinkInfo { Url = a_url, IsAutoLinkFlg = false },
                    ToolTip = a_url
                };
                m_editor.Selection.Select(newRun.ContentStart, newRun.ContentEnd);
                m_editor.CaretPosition = newRun.ContentEnd;
            });
            m_refreshOutline();
            m_markDirty();
        }

        /// <summary>右クリック「文字装飾」の通常スタイル実装。選択範囲を指定スタイルの新しいRunに
        /// 置き換える。既存Runのプロパティ個別リセットはWPFの制約（FontFamilyをnullにできない等）
        /// で失敗するため、常に新規Runを作り直す。</summary>
        /// <param name="a_style">"normal"、"code"、"bold"、"italic"、"strikethrough"、"underline"、"highlight"のいずれか。</param>
        private void ApplyInlineStyle(string a_style)
        {
            if (null == m_editor.Selection || m_editor.Selection.IsEmpty)
            {
                return;
            }
            string text = GetSafeRangeText(m_editor.Selection.Start, m_editor.Selection.End);
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            m_originalTextTracker.Invalidate(m_editor.Selection.Start);

            m_runAsProgrammaticChange(() =>
            {
                TextPointer start = m_editor.Selection.Start;
                m_editor.Selection.Text = ""; // 元の（別スタイルだったかもしれない）内容を削除

                Run newRun;
                switch (a_style)
                {
                    case "bold":
                        newRun = new Run(text, start) { FontWeight = BlockStyles.BoldFontWeight, Tag = "bold" };
                        break;
                    case "italic":
                        newRun = new Run(text, start) { FontStyle = FontStyles.Italic, Tag = "italic" };
                        break;
                    case "strikethrough":
                        newRun = new Run(text, start) { TextDecorations = TextDecorations.Strikethrough, Tag = "strikethrough" };
                        break;
                    case "underline":
                        newRun = new Run(text, start) { TextDecorations = TextDecorations.Underline, Tag = "underline" };
                        break;
                    case "highlight":
                        newRun = new Run(text, start) { Background = m_highlightBackground, Tag = "highlight" };
                        break;
                    case "code":
                        newRun = BlockStyles.ApplyInlineCodeStyle(new Run(text, start) { Tag = "inline-code" });
                        break;
                    default: // "normal"
                        newRun = new Run(text, start);
                        break;
                }

                m_editor.Selection.Select(newRun.ContentStart, newRun.ContentEnd);
                m_editor.CaretPosition = newRun.ContentEnd;
            });
            m_refreshOutline();
            m_markDirty();
        }

        // ======================================================================
        //  リンクの右クリックメニュー
        // ======================================================================

        /// <summary>ContextLinkRunのリンクを既定のブラウザで開く。</summary>
        public void OpenContextLink()
        {
            if (ContextLinkRun?.Tag is LinkInfo li)
            {
                NavigateLink(LinkUrlOf(ContextLinkRun, li));
            }
        }

        /// <summary>ContextLinkRunのURLをクリップボードへコピーする。</summary>
        public void CopyContextLinkUrl()
        {
            if (ContextLinkRun?.Tag is LinkInfo li)
            {
                try { Clipboard.SetText(LinkUrlOf(ContextLinkRun, li)); } catch { /* 失敗しても致命的ではない */ }
            }
        }

        /// <summary>ContextLinkRunのURLを、ダイアログで入力した新しいURLに置き換える。</summary>
        /// <param name="a_ownerWindow">ダイアログの親ウィンドウ。</param>
        public void EditContextLink(Window a_ownerWindow)
        {
            if (!(ContextLinkRun?.Tag is LinkInfo li))
            {
                return;
            }
            var dlg = new LinkInputDialog(li.Url) { Owner = a_ownerWindow };
            if (true == dlg.ShowDialog() && !string.IsNullOrWhiteSpace(dlg.Url))
            {
                m_originalTextTracker.Invalidate(ContextLinkRun.ContentStart);
                li.Url = dlg.Url;
                li.IsAutoLinkFlg = false;
                // リンク先を指定し直したので、文中にそのまま書かれたメールアドレスではなくなる
                // （保存時は [文字](URL) の形で書き出す）
                li.IsBareEmailFlg = false;
                ContextLinkRun.ToolTip = dlg.Url;
                m_markDirty();
            }
        }

        /// <summary>ContextLinkRunからリンクの見た目・Tagを取り除き、通常のテキストに戻す。</summary>
        public void RemoveContextLink()
        {
            if (null == ContextLinkRun)
            {
                return;
            }
            m_originalTextTracker.Invalidate(ContextLinkRun.ContentStart);
            m_runAsProgrammaticChange(() =>
            {
                ContextLinkRun.Tag = null;
                ContextLinkRun.ClearValue(TextElement.ForegroundProperty);
                ContextLinkRun.ClearValue(Inline.TextDecorationsProperty);
                ContextLinkRun.ToolTip = null;
            });
            m_markDirty();
        }

        /// <summary>
        /// コードブロック全体を、```フェンスと言語タグを含む、そのまま貼り付け可能な
        /// Markdownとしてコピーする。選択テキストの通常のCtrl+Cとは異なり、コード内容だけでなく
        /// フェンス自体もコピーされる。コードブロックが複数行（先頭行のCodeBlockInfo＋続きの行
        /// のCodeBlockContinuationInfo。HeadingCodeBlockEditor.InsertCodeBlockContinuation
        /// Paragraph参照）に分かれている場合、右クリックされたのがどの行であってもブロック
        /// 全体をコピーできるよう、まず先頭行まで遡ってから、そこから続く行をすべて集めて
        /// 1つのMarkdown文字列に組み立て直す（m_blockToMarkdownは1行分の断片しか返さない
        /// ため。開き/閉じフェンスの有無は各行のBlockToMarkdown自身が次の行の有無から判定
        /// する）。
        /// </summary>
        public void CopyCodeBlockAsMarkdown()
        {
            if (null == ContextParagraph ||
                !(ContextParagraph.Tag is CodeBlockInfo || ContextParagraph.Tag is CodeBlockContinuationInfo))
            {
                return;
            }
            Paragraph firstLinePara = ContextParagraph;
            while (firstLinePara.PreviousBlock is Paragraph prevPara && prevPara.Tag is CodeBlockContinuationInfo)
            {
                firstLinePara = prevPara;
            }
            var sb = new StringBuilder();
            Block linePara = firstLinePara;
            bool firstLineFlg = true;
            while (linePara is Paragraph p && (p.Tag is CodeBlockInfo || p.Tag is CodeBlockContinuationInfo))
            {
                if (!firstLineFlg)
                {
                    sb.Append("\n");
                }
                sb.Append(m_blockToMarkdown(p));
                firstLineFlg = false;
                linePara = p.NextBlock;
            }
            string md = sb.ToString();
            if (!string.IsNullOrEmpty(md))
            {
                Clipboard.SetText(md);
            }
        }

        /// <summary>URLを既定のブラウザで開く。</summary>
        /// <param name="a_url">開くURL。</param>
        public void OpenUrl(string a_url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(a_url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("リンクを開けませんでした: " + ex.Message, "リンクを開く",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// リンクのURLを見て、外部URL（http(s)/mailto等）ならブラウザで開き、それ以外は
        /// ファイルリンク・見出しジャンプ・カスタムアンカーとして解釈する：
        /// "#見出し" は現在のファイル内でのジャンプ、"path/to/file.md" はファイルを開く、
        /// "path/to/file.md#見出し" は別のファイルを開いたうえでそのジャンプ先へ移動する。
        /// </summary>
        /// <param name="a_url">リンクのURL部分。</param>
        public void NavigateLink(string a_url)
        {
            if (string.IsNullOrWhiteSpace(a_url))
            {
                return;
            }

            if (Regex.IsMatch(a_url, "^[a-zA-Z][a-zA-Z0-9+.-]*:") && !Regex.IsMatch(a_url, "^[a-zA-Z]:[\\\\/]"))
            {
                // http://, https://, mailto: など（Windowsのドライブレター "C:\..." は除く）は
                // 外部URLとして扱う。
                OpenUrl(a_url);
                return;
            }

            string filePart;
            string anchor;
            int hashIdx = a_url.IndexOf('#');
            if (hashIdx < 0) { filePart = a_url; anchor = null; }
            else { filePart = a_url.Substring(0, hashIdx); anchor = a_url.Substring(hashIdx + 1); }

            if (!string.IsNullOrEmpty(filePart))
            {
                string dir = m_getCurrentFileDirectory();
                string resolved = filePart;
                try
                {
                    if (!System.IO.Path.IsPathRooted(filePart) && !string.IsNullOrEmpty(dir))
                    {
                        resolved = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, filePart.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                    }
                }
                catch
                {
                    // パス解決に失敗した場合は、元の文字列のまま試す
                }

                if (!System.IO.File.Exists(resolved))
                {
                    MessageBox.Show("リンク先のファイルが見つかりませんでした:\n" + resolved, "ファイルリンク",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string resolvedDir = System.IO.Path.GetDirectoryName(resolved);
                if (m_isWithinLoadedFolder(resolvedDir))
                {
                    m_loadFile(resolved);
                }
                else
                {
                    // フォルダペインに表示されていない範囲のファイルは、現在の文書を置き換えず
                    // 新しいウィンドウで開く。
                    m_openInNewWindow(resolved, anchor);
                    return;
                }
            }

            if (!string.IsNullOrEmpty(anchor))
            {
                JumpToAnchor(anchor);
            }
        }

        /// <summary>現在の文書内で、指定した見出しテキストまたはカスタムアンカー（&lt;a a_id&gt;）に
        /// 一致する箇所へスクロールする。見出しの完全一致に加え、GitHub形式のスラッグ
        /// （例：「7. 依存ライブラリ」→ #7-依存ライブラリ）にも対応する。</summary>
        /// <param name="a_anchor">見出しの完全なテキスト、見出しのスラッグ、またはアンカーのid。</param>
        public void JumpToAnchor(string a_anchor)
        {
            a_anchor = a_anchor.Trim();
            if (0 == a_anchor.Length)
            {
                return;
            }

            Paragraph slugMatch = null;
            foreach (Block block in m_editor.Document.Blocks)
            {
                if (block is Paragraph p && p.Tag is int level && level > 0)
                {
                    string text = new TextRange(p.ContentStart, p.ContentEnd).Text.Trim();
                    if (text == a_anchor)
                    {
                        m_editor.CaretPosition = p.ContentStart;
                        OutlineManager.ScrollParagraphToTop(p, m_editor);
                        return;
                    }
                    // 完全一致を優先するため即ジャンプせず、スラッグが一致する最初の
                    // 見出しを保持しておく（後続に完全一致がある可能性があるため）。
                    if (null == slugMatch && SlugifyHeading(text) == a_anchor)
                    {
                        slugMatch = p;
                    }
                }
            }
            if (null != slugMatch)
            {
                m_editor.CaretPosition = slugMatch.ContentStart;
                OutlineManager.ScrollParagraphToTop(slugMatch, m_editor);
                return;
            }

            Run anchorRun = FindAnchorRun(m_editor.Document, a_anchor);
            if (null != anchorRun)
            {
                DependencyObject node = anchorRun;
                while (null != node && !(node is Paragraph))
                {
                    node = (node as TextElement)?.Parent ?? (node is TableCell cell ? cell.Parent : null);
                }
                if (node is Paragraph anchorPara)
                {
                    m_editor.CaretPosition = anchorRun.ContentStart;
                    OutlineManager.ScrollParagraphToTop(anchorPara, m_editor);
                }
            }
        }

        /// <summary>
        /// 見出しテキストから、GitHub方式に近いアンカー用スラッグを生成する。小文字化し、
        /// 英数字・_・-・空白・Unicode文字以外の記号を除去して、空白をハイフンに置換する。
        /// </summary>
        /// <param name="a_headingText">見出しの完全なテキスト。</param>
        /// <returns>アンカー用のスラッグ文字列。</returns>
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
                // それ以外の記号（. , ( ) 「」 ： 等）は読み飛ばす（GitHubの挙動に合わせる）。
            }
            return sb.ToString().Trim().Replace(' ', '-');
        }

        private Run FindAnchorRun(FlowDocument a_doc, string a_id)
        {
            foreach (Block block in a_doc.Blocks)
            {
                var found = FindAnchorRunInBlock(block, a_id);
                if (null != found)
                {
                    return found;
                }
            }
            return null;
        }

        private Run FindAnchorRunInBlock(Block a_block, string a_id)
        {
            if (a_block is Paragraph p)
            {
                return FindAnchorRunInInlines(p.Inlines, a_id);
            }
            if (a_block is List list)
            {
                foreach (ListItem li in list.ListItems)
                {
                    foreach (Block b in li.Blocks)
                    {
                        var found = FindAnchorRunInBlock(b, a_id);
                        if (null != found)
                        {
                            return found;
                        }
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
                                var found = FindAnchorRunInBlock(b, a_id);
                                if (null != found)
                                {
                                    return found;
                                }
                            }
                        }
                    }
                }
            }
            return null;
        }

        private Run FindAnchorRunInInlines(InlineCollection a_inlines, string a_id)
        {
            foreach (Inline inline in a_inlines)
            {
                if (inline is Run run && run.Tag is AnchorInfo info && info.Id == a_id)
                {
                    return run;
                }
                if (inline is Span span)
                {
                    var found = FindAnchorRunInInlines(span.Inlines, a_id);
                    if (null != found)
                    {
                        return found;
                    }
                }
            }
            return null;
        }

        /// <summary>クリックでリンクを開く。動作は設定（Ctrl+クリックが必要か、クリックのみで
        /// 開くか）によって切り替わる。条件を満たさないクリックは通常のキャレット移動に
        /// 任せ、何もしない。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        public void HandlePreviewMouseLeftButtonDown(object a_sender, MouseButtonEventArgs a_args)
        {
            bool requireCtrlFlg = m_requireCtrlForLinkClick?.Invoke() ?? true;
            if (requireCtrlFlg && Keyboard.Modifiers != ModifierKeys.Control)
            {
                return;
            }

            // 文字の上をクリックした時だけリンクを開く（第2引数 false：文字の上でなければ null）。true（いちばん近い文字の
            // 位置に寄せる）だと、行の右の空いた所や、スクロールバー等の文書の外をクリックした時も、同じ高さにあるリンクの
            // 文字の位置になり、リンクを開いてしまう（目次の行の横でスクロールバーをクリックすると、目次のリンク先へ移動
            // してしまっていた）。
            var pos = m_editor.GetPositionFromPoint(a_args.GetPosition(m_editor), false);
            if (null == pos)
            {
                return;
            }

            if (pos.Parent is Run run && run.Tag is LinkInfo linkInfo && !string.IsNullOrWhiteSpace(linkInfo.Url))
            {
                NavigateLink(LinkUrlOf(run, linkInfo));
                a_args.Handled = true;
            }
        }

        // ======================================================================
        //  入力中のリアルタイム変換
        // ======================================================================

        /// <summary>
        /// 指定位置にある区切り記号（`/**/~~/[ の開始位置）が、直前の連続する\の個数（奇数なら
        /// エスケープされている）から見てエスケープされているかどうかを判定する。
        /// </summary>
        /// <param name="a_text">対象の文字列。</param>
        /// <param name="a_position">区切り記号の開始位置。</param>
        /// <returns>エスケープされていれば true。</returns>
        private bool IsEscapedAt(string a_text, int a_position)
        {
            int count = 0;
            int i = a_position - 1;
            while (i >= 0 &&
                   '\\' == a_text[i]) { count++; i--; }
            return count % 2 == 1;
        }

        /// <summary>
        /// キャレット位置で `コード`/**太字**/~~取り消し線~~/[リンク](url) の記法が
        /// 閉じられたかを調べ、閉じていればスタイル付きRunへ即座に置き換える。段落内を
        /// Run境界をまたいで後ろ向きにたどるため、箇条書き項目内でも動作する。
        /// </summary>
        /// <returns>装飾を適用した場合は true。</returns>
        public bool CheckInlineFormatTrigger()
        {
            var caret = m_editor.CaretPosition;
            var para = caret.Paragraph;
            if (null == para || para.Tag is CodeBlockInfo || para.Tag is CodeBlockContinuationInfo)
            {
                return false;
            }

            var segments = CollectSegmentsBeforeCaret(caret, para);
            if (0 == segments.Count)
            {
                return false;
            }

            // textBeforeはマッチング用の文字列：エスケープ済み区間は区切り記号として絶対に
            // マッチしない目印文字（\uE0FF）に置き換え、誤ったトリガー再解釈を防ぐ。
            string textBefore = string.Concat(segments.Select(s => s.isEscaped ? new string('\uE0FF', s.text.Length) : s.text));
            if (0 == textBefore.Length)
            {
                return false;
            }

            char lastChar = textBefore[textBefore.Length - 1];
            string style = null;
            Match match = null;
            string linkUrl = null;

            if (')' == lastChar)
            {
                match = Regex.Match(textBefore, "(?<!!)\\[([^\\]]*)\\]\\(((?:[^()]|\\([^()]*\\))+)\\)$");
                if (match.Success && !IsEscapedAt(textBefore, match.Index))
                {
                    linkUrl = match.Groups[2].Value;
                    style = "link";
                }
                else
                {
                    match = null;
                }
            }
            if (null == style &&
                '`' == lastChar)
            {
                match = Regex.Match(textBefore, "`([^`]+)`$");
                if (match.Success && !IsEscapedAt(textBefore, match.Index))
                {
                    style = "code";
                }
                else
                {
                    match = null;
                }
            }
            if (null == style &&
                '*' == lastChar)
            {
                match = Regex.Match(textBefore, "\\*\\*([^*]+)\\*\\*$");
                if (match.Success && !IsEscapedAt(textBefore, match.Index))
                {
                    style = "bold";
                }
                else
                {
                    match = null;
                }
            }
            if (null == style &&
                '*' == lastChar)
            {
                // 斜体（*斜体*）。**太字** の途中（閉じの1つ目の * を打った所）では斜体にしない（直前が * の * からは
                // 始めない。中身に * を含めない）。中身の前後が空白のもの（「2 * 3 * 4」等）も斜体にしない
                // （MarkdownConverter.ITALIC_PATTERN と同じ条件）。
                match = Regex.Match(textBefore, "(?<!\\*)\\*(?![\\s*])([^*]*?[^*\\s])\\*$");
                if (match.Success && !IsEscapedAt(textBefore, match.Index))
                {
                    style = "italic";
                }
                else
                {
                    match = null;
                }
            }
            if (null == style &&
                '_' == lastChar)
            {
                // 斜体（_斜体_）。英数字の単語の途中の _（snake_case 等）からは始めない
                match = Regex.Match(textBefore, "(?<![\\w_])_(?![\\s_])([^_]*?[^_\\s])_$");
                if (match.Success && !IsEscapedAt(textBefore, match.Index))
                {
                    style = "italic_";
                }
                else
                {
                    match = null;
                }
            }
            if (null == style &&
                '~' == lastChar)
            {
                match = Regex.Match(textBefore, "~~([^~]+)~~$");
                if (match.Success && !IsEscapedAt(textBefore, match.Index))
                {
                    style = "strikethrough";
                }
                else
                {
                    match = null;
                }
            }
            if (null == style &&
                '>' == lastChar)
            {
                match = Regex.Match(textBefore, "<u>([^<]+)</u>$", RegexOptions.IgnoreCase);
                if (match.Success && !IsEscapedAt(textBefore, match.Index))
                {
                    style = "underline";
                }
                else
                {
                    match = null;
                }
            }
            if (null == style &&
                '>' == lastChar)
            {
                // URL自動リンク（<https://...>）のライブ入力変換。バッチ変換
                // （MarkdownConverter.m_inlineContentRegex）と優先順位を合わせるため、
                // メールアドレス自動リンクより先にURLとして判定する。
                match = Regex.Match(textBefore, "<(https?://[^\\s<>]+)>$", RegexOptions.IgnoreCase);
                if (match.Success && !IsEscapedAt(textBefore, match.Index))
                {
                    style = "url-autolink";
                }
                else
                {
                    match = null;
                }
            }
            if (null == style &&
                '>' == lastChar)
            {
                // メールアドレス自動リンク（<email@example.com>）のライブ入力変換。
                // バッチ変換（MarkdownConverter）と対になる処理。
                match = Regex.Match(textBefore, "<([^\\s<>@]+@[^\\s<>@]+\\.[^\\s<>@]+)>$");
                if (match.Success && !IsEscapedAt(textBefore, match.Index))
                {
                    style = "email-autolink";
                }
                else
                {
                    match = null;
                }
            }
            if (null == style &&
                '=' == lastChar)
            {
                // ハイライト（==text==）のライブ入力変換。
                match = Regex.Match(textBefore, "==([^=]+)==$");
                if (match.Success && !IsEscapedAt(textBefore, match.Index))
                {
                    style = "highlight";
                }
                else
                {
                    match = null;
                }
            }
            if (null == style && TryLinkifyBareEmail(caret, segments, true))
            {
                // 文中にそのまま書いたメールアドレスの後ろに、空白・句読点等を打った
                return true;
            }
            if (null == style)
            {
                // どの装飾トリガーにも一致しない場合、直前が「\ + 1文字」の完成した
                // エスケープ記法かを確認し、該当すれば\を隠してTag="escaped"を付ける。
                if (textBefore.Length >= 2 &&
                    '\\' == textBefore[textBefore.Length - 2] &&
                    !IsEscapedAt(textBefore, textBefore.Length - 2))
                {
                    TextPointer escStart = null;
                    int escRemaining = textBefore.Length - 2;
                    foreach (var seg in segments)
                    {
                        if (escRemaining <= seg.text.Length)
                        {
                            escStart = seg.segStart.GetPositionAtOffset(escRemaining);
                            break;
                        }
                        escRemaining -= seg.text.Length;
                    }
                    if (null != escStart)
                    {
                        ReplaceEscapedCharacter(caret, escStart, lastChar);
                        return true;
                    }
                }
                return false;
            }

            TextPointer start = null;
            int remaining = match.Index;
            foreach (var seg in segments)
            {
                if (remaining <= seg.text.Length)
                {
                    start = seg.segStart.GetPositionAtOffset(remaining);
                    break;
                }
                remaining -= seg.text.Length;
            }
            if (null == start)
            {
                return false;
            }

            if ("link" == style)
            {
                ReplaceTextBeforeCaretWithLinkRun(caret, start, match.Groups[1].Value, linkUrl);
            }
            else if ("email-autolink" == style)
            {
                // メールアドレス自動リンク。
                string email = match.Groups[1].Value;
                ReplaceTextBeforeCaretWithLinkRun(caret, start, email, "mailto:" + email, false, true);
            }
            else if ("url-autolink" == style)
            {
                // URL自動リンク。
                string url = match.Groups[1].Value;
                ReplaceTextBeforeCaretWithLinkRun(caret, start, url, url, true, false);
            }
            else
            {
                ReplaceTextBeforeCaretWithStyledRun(caret, start, match.Groups[1].Value, style);
            }
            return true;
        }

        /// <summary>
        /// キャレットの前の、同じ段落の文字を、Run区間ごとに後ろ向きにたどって記録する（複数Runを
        /// またぐ位置計算を避けるため。最大300文字程度）。Tag="escaped"の区間（\で確定済みの文字）は
        /// isEscaped、書式の無い通常の文字の区間はisPlainとして区別する。
        /// </summary>
        /// <param name="a_caret">キャレットの位置。</param>
        /// <param name="a_para">キャレットのある段落。</param>
        /// <returns>前から順の区間の一覧（文字・開始位置・エスケープ済みか・書式が無いか）。</returns>
        private static List<(string text, TextPointer segStart, bool isEscaped, bool isPlain)> CollectSegmentsBeforeCaret(
            TextPointer a_caret, Paragraph a_para)
        {
            var segments = new List<(string text, TextPointer segStart, bool isEscaped, bool isPlain)>();
            TextPointer walker = a_caret;
            int totalLen = 0;
            int guard = 0;
            while (null != walker &&
                   walker.CompareTo(a_para.ContentStart) > 0 &&
                   totalLen < 300 &&
                   guard < 50)
            {
                guard++;
                string chunk = walker.GetTextInRun(LogicalDirection.Backward);
                if (!string.IsNullOrEmpty(chunk))
                {
                    var segStart = walker.GetPositionAtOffset(-chunk.Length);
                    if (null == segStart)
                    {
                        break;
                    }
                    object tag = (walker.Parent as Run)?.Tag;
                    bool isEscapedFlg = "escaped" == tag as string;
                    segments.Insert(0, (chunk, segStart, isEscapedFlg, null == tag));
                    totalLen += chunk.Length;
                    walker = segStart;
                }
                else
                {
                    var prevContext = walker.GetNextContextPosition(LogicalDirection.Backward);
                    if (null == prevContext ||
                        0 == prevContext.CompareTo(walker)) break;
                    walker = prevContext;
                }
            }
            return segments;
        }

        /// <summary>
        /// Enterキーを押す直前に呼ぶ：キャレットの直前に書いたメールアドレスを、mailtoのリンクにする
        /// （TryLinkifyBareEmail参照。読み込んだ時と同じ表示にするため）。
        /// </summary>
        /// <returns>リンクにした場合はtrue。</returns>
        public bool LinkifyBareEmailBeforeCaret()
        {
            var caret = m_editor.CaretPosition;
            var para = caret.Paragraph;
            if (null == para || para.Tag is CodeBlockInfo || para.Tag is CodeBlockContinuationInfo)
            {
                return false;
            }
            return TryLinkifyBareEmail(caret, CollectSegmentsBeforeCaret(caret, para), false);
        }

        /// <summary>
        /// キャレットの直前に、文中にそのまま書いたメールアドレスがあれば、mailtoのリンク（LinkInfo.IsBareEmailFlg。
        /// 保存時は書いたまま書き戻す）のRunに置き換える。読み込んだ時（MarkdownConverterのBARE_EMAIL_PATTERN）と
        /// 同じ表示にするため、メールアドレスの後ろに空白・句読点等を打った時（a_typedFlg）と、Enterキーを押す直前に呼ぶ。
        /// 書式の無い通常の文字だけが対象（コード・リンク等の中のメールアドレスはリンクにしない）。
        /// </summary>
        /// <param name="a_caret">キャレットの位置。</param>
        /// <param name="a_segments">CollectSegmentsBeforeCaretで集めた区間。</param>
        /// <param name="a_typedFlg">メールアドレスの後ろに1文字打った直後か（その文字と、直前の「.」はリンクに含めない）。</param>
        /// <returns>リンクにした場合はtrue。</returns>
        private bool TryLinkifyBareEmail(TextPointer a_caret, List<(string text, TextPointer segStart, bool isEscaped, bool isPlain)> a_segments,
            bool a_typedFlg)
        {
            // 書式の無い区間以外は、メールアドレスに使われない目印文字に置き換えて照合する
            string plainBefore = string.Concat(a_segments.Select(s => s.isPlain ? s.text : new string('\uE0FF', s.text.Length)));
            string pattern = MarkdownConverter.BARE_EMAIL_PREV_PATTERN + "(" + MarkdownConverter.BARE_EMAIL_PATTERN + ")" +
                (a_typedFlg ? "(\\.?[^A-Za-z0-9._%+@\\-\uE0FF])$" : "()$");
            Match match = Regex.Match(plainBefore, pattern);
            if (!match.Success)
            {
                return false;
            }
            TextPointer start = null;
            int remaining = match.Groups[1].Index;
            foreach (var seg in a_segments)
            {
                if (remaining <= seg.text.Length)
                {
                    start = seg.segStart.GetPositionAtOffset(remaining);
                    break;
                }
                remaining -= seg.text.Length;
            }
            if (null == start)
            {
                return false;
            }
            string email = match.Groups[1].Value;
            string tail = match.Groups[2].Value;
            m_runAsProgrammaticChange(() =>
            {
                new TextRange(start, a_caret).Text = "";
                var newRun = new Run(email, start)
                {
                    Foreground = m_linkBrush,
                    TextDecorations = BlockStyles.LinkTextDecorations,
                    Tag = new LinkInfo { Url = "mailto:" + email, IsBareEmailFlg = true },
                    ToolTip = email
                };
                // 後ろに打った文字は、リンクではない通常の文字として戻す（先頭のゼロ幅スペースは、
                // 直後の入力へリンクの見た目が継承されるのを防ぐためのもの。保存時に自動除去）
                var trailingRun = new Run("\u200B" + tail, newRun.ContentEnd);
                m_editor.CaretPosition = trailingRun.ContentEnd;
            });
            return true;
        }

        /// <summary>リンクを開く・URLをコピーする時のリンク先。文中にそのまま書かれたメールアドレスの
        /// リンクは、書き直されていても今の文字から作る。</summary>
        /// <param name="a_run">リンクのRun。</param>
        /// <param name="a_linkInfo">そのRunのリンクの情報。</param>
        /// <returns>リンク先のURL。</returns>
        private static string LinkUrlOf(Run a_run, LinkInfo a_linkInfo)
        {
            return a_linkInfo.IsBareEmailFlg ? "mailto:" + a_run.Text.Replace("\u200B", "").Trim() : a_linkInfo.Url;
        }

        /// <summary>
        /// 入力し終えた「\ + 1文字」のエスケープ記法を、\を隠して実際の文字だけを表示する
        /// Run（Tag="escaped"）に置き換える。保存時にはこのTagを見て再び\付きで書き戻す。
        /// </summary>
        /// <param name="a_caret">現在のキャレット位置（エスケープ対象の文字の直後）。</param>
        /// <param name="a_start">\の開始位置。</param>
        /// <param name="a_escapedChar">エスケープされた文字。</param>
        private void ReplaceEscapedCharacter(TextPointer a_caret, TextPointer a_start, char a_escapedChar)
        {
            m_runAsProgrammaticChange(() =>
            {
                new TextRange(a_start, a_caret).Text = "";
                var newRun = new Run(a_escapedChar.ToString(), a_start) { Tag = "escaped" };
                var trailingRun = new Run("\u200B", newRun.ContentEnd);
                m_editor.CaretPosition = trailingRun.ContentEnd;
            });
        }

        /// <summary>入力し終えた [a_text](a_url) 記法を、スタイル付きのリンクRunに置き換える。</summary>
        /// <param name="a_caret">現在のキャレット位置。</param>
        /// <param name="a_start">範囲の開始位置。</param>
        /// <param name="a_linkText">リンクの表示文字列。</param>
        /// <param name="a_url">リンク先URL。</param>
        /// <param name="a_isAutoLinkFlg">true の場合、&lt;url&gt; 形式（山括弧の自動リンク）からの
        /// 変換であることを示す。</param>
        /// <param name="a_isEmailAutoLinkFlg">true の場合、&lt;email@example.com&gt; 形式
        /// （山括弧のメールアドレス自動リンク）からの変換であることを示す。</param>
        private void ReplaceTextBeforeCaretWithLinkRun(TextPointer a_caret, TextPointer a_start, string a_linkText, string a_url,
            bool a_isAutoLinkFlg = false, bool a_isEmailAutoLinkFlg = false)
        {
            m_runAsProgrammaticChange(() =>
            {
                new TextRange(a_start, a_caret).Text = "";
                var newRun = new Run(a_linkText, a_start)
                {
                    Foreground = m_linkBrush,
                    TextDecorations = BlockStyles.LinkTextDecorations,
                    Tag = new LinkInfo { Url = a_url, IsAutoLinkFlg = a_isAutoLinkFlg, IsEmailAutoLinkFlg = a_isEmailAutoLinkFlg },
                    ToolTip = a_url
                };
                var trailingRun = new Run("\u200B", newRun.ContentEnd);
                m_editor.CaretPosition = trailingRun.ContentEnd;
            });
        }

        /// <summary>入力し終えた `コード`/**太字**/~~取り消し線~~ 記法を、スタイル付きのRunに
        /// 置き換える。直後に、通常スタイルの空のゼロ幅スペースRunを続けて挿入することで、
        /// 以降の入力がそのスタイルを引き継がないようにしている。</summary>
        /// <param name="a_caret">現在のキャレット位置。</param>
        /// <param name="a_start">範囲の開始位置。</param>
        /// <param name="a_content">対象の内容。</param>
        /// <param name="a_style">適用するスタイル。</param>
        private void ReplaceTextBeforeCaretWithStyledRun(TextPointer a_caret, TextPointer a_start, string a_content, string a_style)
        {
            m_runAsProgrammaticChange(() =>
            {
                new TextRange(a_start, a_caret).Text = "";

                Run newRun;
                if ("bold" == a_style)
                {
                    newRun = new Run(a_content, a_start) { FontWeight = BlockStyles.BoldFontWeight, Tag = "bold" };
                }
                else if ("strikethrough" == a_style)
                {
                    newRun = new Run(a_content, a_start) { TextDecorations = TextDecorations.Strikethrough, Tag = "strikethrough" };
                }
                else if ("underline" == a_style)
                {
                    newRun = new Run(a_content, a_start) { TextDecorations = TextDecorations.Underline, Tag = "underline" };
                }
                else if ("highlight" == a_style)
                {
                    newRun = new Run(a_content, a_start) { Background = m_highlightBackground, Tag = "highlight" };
                }
                else if ("italic" == a_style || "italic_" == a_style)
                {
                    newRun = new Run(a_content, a_start) { FontStyle = FontStyles.Italic, Tag = a_style };
                }
                else
                    newRun = BlockStyles.ApplyInlineCodeStyle(new Run(a_content, a_start) { Tag = "inline-code" });

                // 通常スタイルの目に見えないゼロ幅スペースRun。直後の入力へスタイルが
                // 継承されるのを防ぐ（保存時に自動除去。MarkdownConverter.AppendInlinesMarkdown参照）。
                var trailingRun = new Run("\u200B", newRun.ContentEnd);
                m_editor.CaretPosition = trailingRun.ContentEnd;
            });
        }
    }
}
