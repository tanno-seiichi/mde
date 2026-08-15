// InlineStyleEditor.cs
//
// mde (MarkDown インラインエディタ) の一部。
// 文中の太字・取り消し線・インラインコード・リンクの装飾を担当するクラス。
// 右クリックメニューからの適用、入力中のリアルタイム変換（**text**などを打ち終えた瞬間に
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
        private static readonly Brush LinkBrush = new SolidColorBrush(Color.FromRgb(0x09, 0x69, 0xDA));
        private static readonly Brush CodeBlockBackground = BlockStyles.CodeBlockBackgroundBrush;

        private readonly RichTextBox editor;
        private readonly OriginalTextTracker originalTextTracker;
        private readonly Action<Action> runAsProgrammaticChange;
        private readonly Action markDirty;
        private readonly Action refreshOutline;
        private readonly Func<Block, string> blockToMarkdown;

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
        private readonly Func<string> getCurrentFileDirectory;
        private readonly Action<string> loadFile;
        private readonly Func<string, bool> isWithinLoadedFolder;
        private readonly Action<string, string> openInNewWindow;

        /// <summary>
        /// InlineStyleEditorを構築する。
        /// </summary>
        /// <param name="editor">編集対象のRichTextBox。</param>
        /// <param name="originalTextTracker">「元テキスト保持」の追跡役。</param>
        /// <param name="runAsProgrammaticChange">処理を「プログラムによる変更」として実行するdelegate。</param>
        /// <param name="markDirty">ファイルが変更されたことを通知するdelegate。</param>
        /// <param name="refreshOutline">アウトラインペインの再構築を依頼するdelegate。</param>
        /// <param name="blockToMarkdown">ブロックをMarkDownテキストへ変換するdelegate（コードブロックのコピーに使う）。</param>
        /// <param name="getCurrentFileDirectory">現在のファイルの保存先フォルダを返すdelegate（ファイルリンクの相対パス解決に使う）。</param>
        /// <param name="loadFile">同じウィンドウでファイルを開くdelegate。</param>
        /// <param name="isWithinLoadedFolder">指定フォルダが、現在フォルダペインに表示されているフォルダの範囲内かどうかを判定するdelegate。</param>
        /// <param name="openInNewWindow">フォルダペインの範囲外にあるファイルを、新しいウィンドウで開くdelegate（パスとジャンプ先アンカーを受け取る）。</param>
        public InlineStyleEditor(
            RichTextBox editor,
            OriginalTextTracker originalTextTracker,
            Action<Action> runAsProgrammaticChange,
            Action markDirty,
            Action refreshOutline,
            Func<Block, string> blockToMarkdown,
            Func<string> getCurrentFileDirectory,
            Action<string> loadFile,
            Func<string, bool> isWithinLoadedFolder,
            Action<string, string> openInNewWindow)
        {
            this.editor = editor;
            this.originalTextTracker = originalTextTracker;
            this.runAsProgrammaticChange = runAsProgrammaticChange;
            this.markDirty = markDirty;
            this.refreshOutline = refreshOutline;
            this.blockToMarkdown = blockToMarkdown;
            this.getCurrentFileDirectory = getCurrentFileDirectory;
            this.loadFile = loadFile;
            this.isWithinLoadedFolder = isWithinLoadedFolder;
            this.openInNewWindow = openInNewWindow;
        }

        // ======================================================================
        //  右クリックメニュー「文字装飾」
        // ======================================================================

        /// <summary>右クリック「文字装飾」メニューの処理。"link" が選ばれた場合はURL入力
        /// ダイアログを表示し、それ以外は選択範囲へ直接スタイルを適用する。</summary>
        /// <param name="style">"normal"/"code"/"bold"/"strikethrough"/"link"。</param>
        /// <param name="ownerWindow">URL入力ダイアログの親ウィンドウ。</param>
        public void ApplyTextStyleFromMenu(string style, Window ownerWindow)
        {
            if (style == "link")
            {
                if (editor.Selection.IsEmpty) return;
                var dlg = new LinkInputDialog { Owner = ownerWindow };
                if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Url))
                {
                    ApplyLinkStyle(dlg.Url);
                }
                return;
            }
            ApplyInlineStyle(style);
        }

        /// <summary>現在の選択範囲を、指定URLへのリンクRunに置き換える。</summary>
        /// <param name="url">リンク先URL。</param>
        /// <summary>
        /// 指定した範囲のテキストを、リストのマーカー記号を巻き込まない安全な方法で取得する。
        /// editor.Selection.TextやTextRange.Textは、選択範囲が箇条書き項目の中にある場合、
        /// 行頭のマーカー記号（「1.」や「•」）まで文字列に含んでしまうことがあるため、
        /// 代わりにRunのテキストを直接1区間ずつたどって連結する。
        /// </summary>
        /// <param name="start">範囲の開始位置。</param>
        /// <param name="end">範囲の終了位置。</param>
        /// <returns>範囲内のプレーンテキスト。</returns>
        private string GetSafeRangeText(TextPointer start, TextPointer end)
        {
            var sb = new StringBuilder();
            TextPointer navigator = start;
            int guard = 0;
            while (navigator != null && navigator.CompareTo(end) < 0 && guard < 10000)
            {
                guard++;
                if (navigator.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
                {
                    string runText = navigator.GetTextInRun(LogicalDirection.Forward);
                    if (!string.IsNullOrEmpty(runText))
                    {
                        TextPointer runEnd = navigator.GetPositionAtOffset(runText.Length);
                        if (runEnd != null && runEnd.CompareTo(end) > 0)
                        {
                            // このRunが範囲の終端をまたいでいるので、1文字ずつ終端まで数える
                            TextPointer probe = navigator;
                            int fitCount = 0;
                            for (int i = 0; i < runText.Length; i++)
                            {
                                TextPointer next = probe.GetPositionAtOffset(1);
                                if (next == null || next.CompareTo(end) > 0) break;
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

        public void ApplyLinkStyle(string url)
        {
            if (editor.Selection == null || editor.Selection.IsEmpty) return;
            string text = GetSafeRangeText(editor.Selection.Start, editor.Selection.End);
            if (string.IsNullOrEmpty(text)) return;

            originalTextTracker.Invalidate(editor.Selection.Start);

            runAsProgrammaticChange(() =>
            {
                TextPointer start = editor.Selection.Start;
                editor.Selection.Text = "";
                var newRun = new Run(text, start)
                {
                    Foreground = LinkBrush,
                    TextDecorations = TextDecorations.Underline,
                    Tag = new LinkInfo { Url = url, IsAutoLink = false },
                    ToolTip = url
                };
                editor.Selection.Select(newRun.ContentStart, newRun.ContentEnd);
                editor.CaretPosition = newRun.ContentEnd;
            });
            refreshOutline();
            markDirty();
        }

        /// <summary>右クリック「文字装飾」の通常スタイル実装。現在の選択範囲を、指定スタイルの
        /// 新しいRunで置き換える。既存のRunのプロパティを個別にリセットしようとすると、
        /// WPFの仕様上（FontFamilyをnullにできない等）うまくいかないケースがあるため、
        /// 常に新しいRunを作り直す方式にしている。</summary>
        /// <param name="style">"normal"、"code"、"bold"、"strikethrough"のいずれか。</param>
        private void ApplyInlineStyle(string style)
        {
            if (editor.Selection == null || editor.Selection.IsEmpty) return;
            string text = GetSafeRangeText(editor.Selection.Start, editor.Selection.End);
            if (string.IsNullOrEmpty(text)) return;

            originalTextTracker.Invalidate(editor.Selection.Start);

            runAsProgrammaticChange(() =>
            {
                TextPointer start = editor.Selection.Start;
                editor.Selection.Text = ""; // 元の（別スタイルだったかもしれない）内容を削除

                Run newRun;
                switch (style)
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
                            Background = CodeBlockBackground,
                            Tag = "inline-code"
                        };
                        break;
                    default: // "normal"
                        newRun = new Run(text, start);
                        break;
                }

                editor.Selection.Select(newRun.ContentStart, newRun.ContentEnd);
                editor.CaretPosition = newRun.ContentEnd;
            });
            refreshOutline();
            markDirty();
        }

        // ======================================================================
        //  リンクの右クリックメニュー
        // ======================================================================

        /// <summary>ContextLinkRunのリンクを既定のブラウザで開く。</summary>
        public void OpenContextLink()
        {
            if (ContextLinkRun?.Tag is LinkInfo li) NavigateLink(li.Url);
        }

        /// <summary>ContextLinkRunのURLをクリップボードへコピーする。</summary>
        public void CopyContextLinkUrl()
        {
            if (ContextLinkRun?.Tag is LinkInfo li)
            {
                try { Clipboard.SetText(li.Url); } catch { /* 失敗しても致命的ではない */ }
            }
        }

        /// <summary>ContextLinkRunのURLを、ダイアログで入力した新しいURLに置き換える。</summary>
        /// <param name="ownerWindow">ダイアログの親ウィンドウ。</param>
        public void EditContextLink(Window ownerWindow)
        {
            if (!(ContextLinkRun?.Tag is LinkInfo li)) return;
            var dlg = new LinkInputDialog(li.Url) { Owner = ownerWindow };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Url))
            {
                originalTextTracker.Invalidate(ContextLinkRun.ContentStart);
                li.Url = dlg.Url;
                li.IsAutoLink = false;
                ContextLinkRun.ToolTip = dlg.Url;
                markDirty();
            }
        }

        /// <summary>ContextLinkRunからリンクの見た目・Tagを取り除き、通常のテキストに戻す。</summary>
        public void RemoveContextLink()
        {
            if (ContextLinkRun == null) return;
            originalTextTracker.Invalidate(ContextLinkRun.ContentStart);
            runAsProgrammaticChange(() =>
            {
                ContextLinkRun.Tag = null;
                ContextLinkRun.ClearValue(TextElement.ForegroundProperty);
                ContextLinkRun.ClearValue(Inline.TextDecorationsProperty);
                ContextLinkRun.ToolTip = null;
            });
            markDirty();
        }

        /// <summary>
        /// コードブロック全体を、```フェンスと言語タグを含む、そのまま貼り付け可能な
        /// MarkDownとしてコピーする。選択テキストの通常のCtrl+Cとは異なり、コード内容だけでなく
        /// フェンス自体もコピーされる。
        /// </summary>
        public void CopyCodeBlockAsMarkdown()
        {
            if (ContextParagraph == null || !(ContextParagraph.Tag is CodeBlockInfo)) return;
            string md = blockToMarkdown(ContextParagraph);
            if (!string.IsNullOrEmpty(md)) Clipboard.SetText(md);
        }

        /// <summary>URLを既定のブラウザで開く。</summary>
        /// <param name="url">開くURL。</param>
        public void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
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
        /// <param name="url">リンクのURL部分。</param>
        public void NavigateLink(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;

            if (Regex.IsMatch(url, "^[a-zA-Z][a-zA-Z0-9+.-]*:") && !Regex.IsMatch(url, "^[a-zA-Z]:[\\\\/]"))
            {
                // http://, https://, mailto: など（Windowsのドライブレター "C:\..." は除く）は
                // 外部URLとして扱う。
                OpenUrl(url);
                return;
            }

            string filePart;
            string anchor;
            int hashIdx = url.IndexOf('#');
            if (hashIdx < 0) { filePart = url; anchor = null; }
            else { filePart = url.Substring(0, hashIdx); anchor = url.Substring(hashIdx + 1); }

            if (!string.IsNullOrEmpty(filePart))
            {
                string dir = getCurrentFileDirectory();
                string resolved = filePart;
                try
                {
                    if (!System.IO.Path.IsPathRooted(filePart) && !string.IsNullOrEmpty(dir))
                        resolved = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, filePart.Replace('/', System.IO.Path.DirectorySeparatorChar)));
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
                if (isWithinLoadedFolder(resolvedDir))
                {
                    loadFile(resolved);
                }
                else
                {
                    // フォルダペインに表示されていない範囲のファイルは、現在の文書を置き換えず
                    // 新しいウィンドウで開く。
                    openInNewWindow(resolved, anchor);
                    return;
                }
            }

            if (!string.IsNullOrEmpty(anchor))
            {
                JumpToAnchor(anchor);
            }
        }

        /// <summary>現在の文書内で、指定した見出しテキストまたはカスタムアンカー（&lt;a id&gt;）に
        /// 一致するジャンプ先を探し、そこまでスクロールする。</summary>
        /// <param name="anchor">見出しの完全なテキスト、またはアンカーのid。</param>
        public void JumpToAnchor(string anchor)
        {
            anchor = anchor.Trim();
            if (anchor.Length == 0) return;

            foreach (Block block in editor.Document.Blocks)
            {
                if (block is Paragraph p && p.Tag is int level && level > 0)
                {
                    string text = new TextRange(p.ContentStart, p.ContentEnd).Text.Trim();
                    if (text == anchor)
                    {
                        editor.CaretPosition = p.ContentStart;
                        OutlineManager.ScrollParagraphToTop(p, editor);
                        return;
                    }
                }
            }

            Run anchorRun = FindAnchorRun(editor.Document, anchor);
            if (anchorRun != null)
            {
                DependencyObject node = anchorRun;
                while (node != null && !(node is Paragraph))
                    node = (node as TextElement)?.Parent ?? (node is TableCell cell ? cell.Parent : null);
                if (node is Paragraph anchorPara)
                {
                    editor.CaretPosition = anchorRun.ContentStart;
                    OutlineManager.ScrollParagraphToTop(anchorPara, editor);
                }
            }
        }

        private Run FindAnchorRun(FlowDocument doc, string id)
        {
            foreach (Block block in doc.Blocks)
            {
                var found = FindAnchorRunInBlock(block, id);
                if (found != null) return found;
            }
            return null;
        }

        private Run FindAnchorRunInBlock(Block block, string id)
        {
            if (block is Paragraph p) return FindAnchorRunInInlines(p.Inlines, id);
            if (block is List list)
            {
                foreach (ListItem li in list.ListItems)
                    foreach (Block b in li.Blocks)
                    {
                        var found = FindAnchorRunInBlock(b, id);
                        if (found != null) return found;
                    }
            }
            else if (block is Table table)
            {
                foreach (TableRowGroup rg in table.RowGroups)
                    foreach (TableRow row in rg.Rows)
                        foreach (TableCell cell in row.Cells)
                            foreach (Block b in cell.Blocks)
                            {
                                var found = FindAnchorRunInBlock(b, id);
                                if (found != null) return found;
                            }
            }
            return null;
        }

        private Run FindAnchorRunInInlines(InlineCollection inlines, string id)
        {
            foreach (Inline inline in inlines)
            {
                if (inline is Run run && run.Tag is AnchorInfo info && info.Id == id) return run;
                if (inline is Span span)
                {
                    var found = FindAnchorRunInInlines(span.Inlines, id);
                    if (found != null) return found;
                }
            }
            return null;
        }

        /// <summary>Ctrl+クリックでリンクを開く。Ctrl無しのクリックは通常のキャレット移動に
        /// 任せ、何もしない。</summary>
        public void HandlePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;

            var pos = editor.GetPositionFromPoint(e.GetPosition(editor), true);
            if (pos == null) return;

            if (pos.Parent is Run run && run.Tag is LinkInfo linkInfo && !string.IsNullOrWhiteSpace(linkInfo.Url))
            {
                NavigateLink(linkInfo.Url);
                e.Handled = true;
            }
        }

        // ======================================================================
        //  入力中のリアルタイム変換
        // ======================================================================

        /// <summary>
        /// 直前に入力した文字が、キャレット位置で `コード`、**太字**、~~取り消し線~~、
        /// または[リンク](url)の記法を閉じたかどうかを調べ、そうであればそのMarkDown記法を
        /// スタイル付きのRunへ即座に置き換える。1つのRunの中のテキストだけでなく、
        /// 後ろ向きに複数のRunをまたいでたどるため、段落中のどこでも（箇条書き項目の中でも）
        /// 確実に動作する。
        /// </summary>
        /// <returns>装飾を適用した場合は true。</returns>
        /// <summary>
        /// 指定位置にある区切り記号（`/**/~~/[ の開始位置）が、直前の連続する\の個数（奇数なら
        /// エスケープされている）から見てエスケープされているかどうかを判定する。
        /// </summary>
        /// <param name="text">対象の文字列。</param>
        /// <param name="position">区切り記号の開始位置。</param>
        /// <returns>エスケープされていれば true。</returns>
        private bool IsEscapedAt(string text, int position)
        {
            int count = 0;
            int i = position - 1;
            while (i >= 0 && text[i] == '\\') { count++; i--; }
            return count % 2 == 1;
        }

        public bool CheckInlineFormatTrigger()
        {
            var caret = editor.CaretPosition;
            var para = caret.Paragraph;
            if (para == null || para.Tag is CodeBlockInfo) return false;

            // 1つのRun区間ずつ後ろ向きにたどり、各区間自身のテキストと、その区間の開始位置の
            // TextPointerを記憶していく。こうすることで、複数の区間をまたいだ位置計算を
            // 一切行わずに済み、段落がいくつのRunで構成されていても確実に動作する。
            // Tag="escaped"のRun（\によって既に確定した文字）由来の区間は別途記録しておき、
            // それが後から「普通の」区切り記号として再解釈されてしまわないようにする。
            var segments = new List<(string text, TextPointer segStart, bool isEscaped)>();
            TextPointer walker = caret;
            int totalLen = 0;
            int guard = 0;
            while (walker != null && walker.CompareTo(para.ContentStart) > 0 && totalLen < 300 && guard < 50)
            {
                guard++;
                string chunk = walker.GetTextInRun(LogicalDirection.Backward);
                if (!string.IsNullOrEmpty(chunk))
                {
                    var segStart = walker.GetPositionAtOffset(-chunk.Length);
                    if (segStart == null) break;
                    bool isEscaped = (walker.Parent as Run)?.Tag as string == "escaped";
                    segments.Insert(0, (chunk, segStart, isEscaped));
                    totalLen += chunk.Length;
                    walker = segStart;
                }
                else
                {
                    var prevContext = walker.GetNextContextPosition(LogicalDirection.Backward);
                    if (prevContext == null || prevContext.CompareTo(walker) == 0) break;
                    walker = prevContext;
                }
            }
            if (segments.Count == 0) return false;

            // textBeforeは実際のマッチング用：エスケープ済みの区間は、区切り記号として絶対に
            // 認識されない目印文字（\uE0FF）で長さを保ったまま置き換える。これにより、
            // \によって既に確定した文字が、後から入力される別の記号と組み合わさって
            // 誤って装飾のトリガーとして再解釈されることを防ぐ。
            string textBefore = string.Concat(segments.Select(s => s.isEscaped ? new string('\uE0FF', s.text.Length) : s.text));
            if (textBefore.Length == 0) return false;

            char lastChar = textBefore[textBefore.Length - 1];
            string style = null;
            Match match = null;
            string linkUrl = null;

            if (lastChar == ')')
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
            if (style == null && lastChar == '`')
            {
                match = Regex.Match(textBefore, "`([^`]+)`$");
                if (match.Success && !IsEscapedAt(textBefore, match.Index)) style = "code";
                else match = null;
            }
            if (style == null && lastChar == '*')
            {
                match = Regex.Match(textBefore, "\\*\\*([^*]+)\\*\\*$");
                if (match.Success && !IsEscapedAt(textBefore, match.Index)) style = "bold";
                else match = null;
            }
            if (style == null && lastChar == '~')
            {
                match = Regex.Match(textBefore, "~~([^~]+)~~$");
                if (match.Success && !IsEscapedAt(textBefore, match.Index)) style = "strikethrough";
                else match = null;
            }
            if (style == null)
            {
                // 上のどの装飾トリガーにも一致しなかった場合、直前が「\ + 直前の1文字」という
                // 完成したエスケープ記法になっていないかを確認する。なっていれば、\を隠して
                // 実際の文字だけをその場で表示する（保存時に再び\付きで書き戻せるよう
                // Tag="escaped" を付ける）。
                if (textBefore.Length >= 2 && textBefore[textBefore.Length - 2] == '\\' &&
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
                    if (escStart != null)
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
            if (start == null) return false;

            if (style == "link")
                ReplaceTextBeforeCaretWithLinkRun(caret, start, match.Groups[1].Value, linkUrl);
            else
                ReplaceTextBeforeCaretWithStyledRun(caret, start, match.Groups[1].Value, style);
            return true;
        }

        /// <summary>
        /// 入力し終えた「\ + 1文字」のエスケープ記法を、\を隠して実際の文字だけを表示する
        /// Run（Tag="escaped"）に置き換える。保存時にはこのTagを見て再び\付きで書き戻す。
        /// </summary>
        /// <param name="caret">現在のキャレット位置（エスケープ対象の文字の直後）。</param>
        /// <param name="start">\の開始位置。</param>
        /// <param name="escapedChar">エスケープされた文字。</param>
        private void ReplaceEscapedCharacter(TextPointer caret, TextPointer start, char escapedChar)
        {
            runAsProgrammaticChange(() =>
            {
                new TextRange(start, caret).Text = "";
                var newRun = new Run(escapedChar.ToString(), start) { Tag = "escaped" };
                var trailingRun = new Run("\u200B", newRun.ContentEnd);
                editor.CaretPosition = trailingRun.ContentEnd;
            });
        }

        /// <summary>入力し終えた [text](url) 記法を、スタイル付きのリンクRunに置き換える。</summary>
        private void ReplaceTextBeforeCaretWithLinkRun(TextPointer caret, TextPointer start, string linkText, string url)
        {
            runAsProgrammaticChange(() =>
            {
                new TextRange(start, caret).Text = "";
                var newRun = new Run(linkText, start)
                {
                    Foreground = LinkBrush,
                    TextDecorations = TextDecorations.Underline,
                    Tag = new LinkInfo { Url = url, IsAutoLink = false },
                    ToolTip = url
                };
                var trailingRun = new Run("\u200B", newRun.ContentEnd);
                editor.CaretPosition = trailingRun.ContentEnd;
            });
        }

        /// <summary>入力し終えた `コード`/**太字**/~~取り消し線~~ 記法を、スタイル付きのRunに
        /// 置き換える。直後に、通常スタイルの空のゼロ幅スペースRunを続けて挿入することで、
        /// 以降の入力がそのスタイルを引き継がないようにしている。</summary>
        private void ReplaceTextBeforeCaretWithStyledRun(TextPointer caret, TextPointer start, string content, string style)
        {
            runAsProgrammaticChange(() =>
            {
                new TextRange(start, caret).Text = "";

                Run newRun;
                if (style == "bold")
                    newRun = new Run(content, start) { FontWeight = FontWeights.Bold, Tag = "bold" };
                else if (style == "strikethrough")
                    newRun = new Run(content, start) { TextDecorations = TextDecorations.Strikethrough, Tag = "strikethrough" };
                else
                    newRun = new Run(content, start)
                    {
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 13.5,
                        Background = CodeBlockBackground,
                        Tag = "inline-code"
                    };

                // 通常スタイルの、目に見えないゼロ幅スペースRun。以降の入力が今適用した
                // 太字/取り消し線/コードのスタイルを引き継がないようにするためのもの。
                // 保存時には自動的に取り除かれる（MarkdownConverter.AppendInlinesMarkdown参照）。
                var trailingRun = new Run("\u200B", newRun.ContentEnd);
                editor.CaretPosition = trailingRun.ContentEnd;
            });
        }
    }
}
