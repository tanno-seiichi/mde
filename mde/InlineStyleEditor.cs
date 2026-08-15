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
        public InlineStyleEditor(
            RichTextBox editor,
            OriginalTextTracker originalTextTracker,
            Action<Action> runAsProgrammaticChange,
            Action markDirty,
            Action refreshOutline,
            Func<Block, string> blockToMarkdown)
        {
            this.editor = editor;
            this.originalTextTracker = originalTextTracker;
            this.runAsProgrammaticChange = runAsProgrammaticChange;
            this.markDirty = markDirty;
            this.refreshOutline = refreshOutline;
            this.blockToMarkdown = blockToMarkdown;
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
            if (ContextLinkRun?.Tag is LinkInfo li) OpenUrl(li.Url);
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

        /// <summary>Ctrl+クリックでリンクを開く。Ctrl無しのクリックは通常のキャレット移動に
        /// 任せ、何もしない。</summary>
        public void HandlePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;

            var pos = editor.GetPositionFromPoint(e.GetPosition(editor), true);
            if (pos == null) return;

            if (pos.Parent is Run run && run.Tag is LinkInfo linkInfo && !string.IsNullOrWhiteSpace(linkInfo.Url))
            {
                OpenUrl(linkInfo.Url);
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
        public bool CheckInlineFormatTrigger()
        {
            var caret = editor.CaretPosition;
            var para = caret.Paragraph;
            if (para == null || para.Tag is CodeBlockInfo) return false;

            // 1つのRun区間ずつ後ろ向きにたどり、各区間自身のテキストと、その区間の開始位置の
            // TextPointerを記憶していく。こうすることで、複数の区間をまたいだ位置計算を
            // 一切行わずに済み、段落がいくつのRunで構成されていても確実に動作する。
            var segments = new List<(string text, TextPointer segStart)>();
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
                    segments.Insert(0, (chunk, segStart));
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

            string textBefore = string.Concat(segments.Select(s => s.text));
            if (textBefore.Length == 0) return false;

            char lastChar = textBefore[textBefore.Length - 1];
            string style = null;
            Match match = null;
            string linkUrl = null;

            if (lastChar == ')')
            {
                match = Regex.Match(textBefore, "(?<!!)\\[([^\\]]*)\\]\\(([^)\\s]+)\\)$");
                if (match.Success)
                {
                    linkUrl = match.Groups[2].Value;
                    style = "link";
                }
            }
            if (style == null && lastChar == '`')
            {
                match = Regex.Match(textBefore, "`([^`]+)`$");
                if (match.Success) style = "code";
            }
            if (style == null && lastChar == '*')
            {
                match = Regex.Match(textBefore, "\\*\\*([^*]+)\\*\\*$");
                if (match.Success) style = "bold";
            }
            if (style == null && lastChar == '~')
            {
                match = Regex.Match(textBefore, "~~([^~]+)~~$");
                if (match.Success) style = "strikethrough";
            }
            if (style == null) return false;

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
