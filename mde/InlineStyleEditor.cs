// InlineStyleEditor.cs
//
// mde (MarkDown インラインエディタ) の一部。
// 文中の太字・取り消し線・インラインコード・リンクの装飾を担当するクラス。
// 右クリックメニューからの適用、入力中のリアルタイム変換（**a_text**などを打ち終えた瞬間に
// 反映する）、リンクのクリック・編集・解除を扱う。

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

namespace mde
{
    /// <summary>
    /// インライン装飾（太字/取り消し線/インラインコード/リンク）の一式。MainWindowへの参照は
    /// 持たず、Editor本体・「元テキスト保持」の追跡役・各種delegateだけで動作する。
    /// </summary>
    public class InlineStyleEditor
    {
        private static readonly Brush LINK_BRUSH = new SolidColorBrush(Color.FromRgb(0x09, 0x69, 0xDA));
        private static readonly Brush CODE_BLOCK_BACKGROUND = BlockStyles.CodeBlockBackgroundBrush;

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

        /// <summary>
        /// InlineStyleEditorを構築する。
        /// </summary>
        /// <param name="editor">編集対象のRichTextBox。</param>
        /// <param name="originalTextTracker">「元テキスト保持」の追跡役。</param>
        /// <param name="runAsProgrammaticChange">処理を「プログラムによる変更」として実行するdelegate。</param>
        /// <param name="markDirty">ファイルが変更されたことを通知するdelegate。</param>
        /// <param name="refreshOutline">アウトラインペインの再構築を依頼するdelegate。</param>
        /// <param name="blockToMarkdown">ブロックをMarkDownテキストへ変換するdelegate（コードブロックのコピーに使う）。</param>
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
        /// <param name="a_blockToMarkdown">ブロックをMarkDownテキストへ変換するdelegate（コードブロックのコピーに使う）。</param>
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
        /// <param name="a_style">"normal"/"code"/"bold"/"strikethrough"/"link"。</param>
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

        /// <summary>現在の選択範囲を、指定URLへのリンクRunに置き換える。</summary>
        /// <param name="url">リンク先URL。</param>
        /// <summary>
        /// 指定した範囲のテキストを、リストのマーカー記号を巻き込まない安全な方法で取得する。
        /// m_editor.Selection.TextやTextRange.Textは、選択範囲が箇条書き項目の中にある場合、
        /// 行頭のマーカー記号（「1.」や「•」）まで文字列に含んでしまうことがあるため、
        /// 代わりにRunのテキストを直接1区間ずつたどって連結する。
        /// </summary>
        /// <param name="a_start">範囲の開始位置。</param>
        /// <param name="a_end">範囲の終了位置。</param>
        /// <returns>範囲内のプレーンテキスト。</returns>
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
                    Foreground = LINK_BRUSH,
                    TextDecorations = TextDecorations.Underline,
                    Tag = new LinkInfo { m_url = a_url, m_isAutoLinkFlg = false },
                    ToolTip = a_url
                };
                m_editor.Selection.Select(newRun.ContentStart, newRun.ContentEnd);
                m_editor.CaretPosition = newRun.ContentEnd;
            });
            m_refreshOutline();
            m_markDirty();
        }

        /// <summary>右クリック「文字装飾」の通常スタイル実装。現在の選択範囲を、指定スタイルの
        /// 新しいRunで置き換える。既存のRunのプロパティを個別にリセットしようとすると、
        /// WPFの仕様上（FontFamilyをnullにできない等）うまくいかないケースがあるため、
        /// 常に新しいRunを作り直す方式にしている。</summary>
        /// <param name="a_style">"normal"、"code"、"bold"、"strikethrough"のいずれか。</param>
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
                        newRun = new Run(text, start) { FontWeight = FontWeights.Bold, Tag = "bold" };
                        break;
                    case "strikethrough":
                        newRun = new Run(text, start) { TextDecorations = TextDecorations.Strikethrough, Tag = "strikethrough" };
                        break;
                    case "code":
                        newRun = new Run(text, start)
                        {
                            FontFamily = new FontFamily("Consolas"),
                            FontSize = 13.5,
                            Background = CODE_BLOCK_BACKGROUND,
                            Tag = "inline-code"
                        };
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
                NavigateLink(li.m_url);
            }
        }

        /// <summary>ContextLinkRunのURLをクリップボードへコピーする。</summary>
        public void CopyContextLinkUrl()
        {
            if (ContextLinkRun?.Tag is LinkInfo li)
            {
                try { Clipboard.SetText(li.m_url); } catch { /* 失敗しても致命的ではない */ }
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
            var dlg = new LinkInputDialog(li.m_url) { Owner = a_ownerWindow };
            if (true == dlg.ShowDialog() && !string.IsNullOrWhiteSpace(dlg.Url))
            {
                m_originalTextTracker.Invalidate(ContextLinkRun.ContentStart);
                li.m_url = dlg.Url;
                li.m_isAutoLinkFlg = false;
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
        /// MarkDownとしてコピーする。選択テキストの通常のCtrl+Cとは異なり、コード内容だけでなく
        /// フェンス自体もコピーされる。
        /// </summary>
        public void CopyCodeBlockAsMarkdown()
        {
            if (null == ContextParagraph || !(ContextParagraph.Tag is CodeBlockInfo))
            {
                return;
            }
            string md = m_blockToMarkdown(ContextParagraph);
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
        /// 一致するジャンプ先を探し、そこまでスクロールする。見出しの完全なテキストとの一致に
        /// 加えて、GitHub等が見出しから自動生成する「スラッグ」形式のアンカー（例：
        /// 見出し「7. 依存ライブラリ」に対する #7-依存ライブラリ）にも対応する。</summary>
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
                    // 完全一致が見つからない場合に備えて、GitHub形式のスラッグが一致する
                    // 最初の見出しを覚えておく（このループの後半で他の見出しが完全一致する
                    // 可能性がまだ残っているため、ここでは即座にジャンプしない）。
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
        /// 見出しのテキストから、GitHub等が採用している方式に近いアンカー用スラッグを生成する。
        /// 小文字化した上で、英数字・アンダースコア・ハイフン・空白・Unicodeの文字（日本語等）
        /// 以外の記号を取り除き、空白をハイフンに置き換える。
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
                if (inline is Run run && run.Tag is AnchorInfo info && info.m_id == a_id)
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

            var pos = m_editor.GetPositionFromPoint(a_args.GetPosition(m_editor), true);
            if (null == pos)
            {
                return;
            }

            if (pos.Parent is Run run && run.Tag is LinkInfo linkInfo && !string.IsNullOrWhiteSpace(linkInfo.m_url))
            {
                NavigateLink(linkInfo.m_url);
                a_args.Handled = true;
            }
        }

        // ======================================================================
        //  入力中のリアルタイム変換
        // ======================================================================

        /// <summary>
        /// 直前に入力した文字が、キャレット位置で `コード`、**太字**、~~取り消し線~~、
        /// または[リンク](a_url)の記法を閉じたかどうかを調べ、そうであればそのMarkDown記法を
        /// スタイル付きのRunへ即座に置き換える。1つのRunの中のテキストだけでなく、
        /// 後ろ向きに複数のRunをまたいでたどるため、段落中のどこでも（箇条書き項目の中でも）
        /// 確実に動作する。
        /// </summary>
        /// <returns>装飾を適用した場合は true。</returns>
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

        public bool CheckInlineFormatTrigger()
        {
            var caret = m_editor.CaretPosition;
            var para = caret.Paragraph;
            if (null == para || para.Tag is CodeBlockInfo)
            {
                return false;
            }

            // 1つのRun区間ずつ後ろ向きにたどり、各区間自身のテキストと、その区間の開始位置の
            // TextPointerを記憶していく。こうすることで、複数の区間をまたいだ位置計算を
            // 一切行わずに済み、段落がいくつのRunで構成されていても確実に動作する。
            // Tag="escaped"のRun（\によって既に確定した文字）由来の区間は別途記録しておき、
            // それが後から「普通の」区切り記号として再解釈されてしまわないようにする。
            var segments = new List<(string text, TextPointer segStart, bool isEscaped)>();
            TextPointer walker = caret;
            int totalLen = 0;
            int guard = 0;
            while (null != walker &&
                   walker.CompareTo(para.ContentStart) > 0 &&
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
                    bool isEscapedFlg = "escaped" == (walker.Parent as Run)?.Tag as string;
                    segments.Insert(0, (chunk, segStart, isEscapedFlg));
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
            if (0 == segments.Count)
            {
                return false;
            }

            // textBeforeは実際のマッチング用：エスケープ済みの区間は、区切り記号として絶対に
            // 認識されない目印文字（\uE0FF）で長さを保ったまま置き換える。これにより、
            // \によって既に確定した文字が、後から入力される別の記号と組み合わさって
            // 誤って装飾のトリガーとして再解釈されることを防ぐ。
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
            if (null == style)
            {
                // 上のどの装飾トリガーにも一致しなかった場合、直前が「\ + 直前の1文字」という
                // 完成したエスケープ記法になっていないかを確認する。なっていれば、\を隠して
                // 実際の文字だけをその場で表示する（保存時に再び\付きで書き戻せるよう
                // Tag="escaped" を付ける）。
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
            else
            {
                ReplaceTextBeforeCaretWithStyledRun(caret, start, match.Groups[1].Value, style);
            }
            return true;
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
        private void ReplaceTextBeforeCaretWithLinkRun(TextPointer a_caret, TextPointer a_start, string a_linkText, string a_url)
        {
            m_runAsProgrammaticChange(() =>
            {
                new TextRange(a_start, a_caret).Text = "";
                var newRun = new Run(a_linkText, a_start)
                {
                    Foreground = LINK_BRUSH,
                    TextDecorations = TextDecorations.Underline,
                    Tag = new LinkInfo { m_url = a_url, m_isAutoLinkFlg = false },
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
                    newRun = new Run(a_content, a_start) { FontWeight = FontWeights.Bold, Tag = "bold" };
                }
                else if ("strikethrough" == a_style)
                {
                    newRun = new Run(a_content, a_start) { TextDecorations = TextDecorations.Strikethrough, Tag = "strikethrough" };
                }
                else
                    newRun = new Run(a_content, a_start)
                    {
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 13.5,
                        Background = CODE_BLOCK_BACKGROUND,
                        Tag = "inline-code"
                    };

                // 通常スタイルの、目に見えないゼロ幅スペースRun。以降の入力が今適用した
                // 太字/取り消し線/コードのスタイルを引き継がないようにするためのもの。
                // 保存時には自動的に取り除かれる（MarkdownConverter.AppendInlinesMarkdown参照）。
                var trailingRun = new Run("\u200B", newRun.ContentEnd);
                m_editor.CaretPosition = trailingRun.ContentEnd;
            });
        }
    }
}
