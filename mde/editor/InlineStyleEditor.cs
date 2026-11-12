// InlineStyleEditor.cs
//
// mde (Markdown インラインエディタ) の一部。
// 文中の太字・取り消し線・下線・インラインコード・リンクの装飾を担当するクラス。
// 右クリックメニューからの適用、入力中のリアルタイム変換（**a_text**などを打ち終えた瞬間に
// 反映する）、リンクのクリック・編集・解除を扱う。

using mde.common;
using mde.dialog;
using mde.logger;
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

        /// <summary>右クリックメニューの文字の書式の項目の処理。"link" が選ばれた場合はURL入力ダイアログを表示し、
        /// "normal" は書式を外して通常の文字に戻す。それ以外は、ショートカットと同じく、選んだ文字がすべてその書式なら
        /// その書式だけを外し、そうでなければその書式を重ねる（mdew と同じ。ToggleInlineStyleFromShortcut）。</summary>
        /// <param name="a_style">"normal"/"code"/"bold"/"italic"/"strikethrough"/"underline"/"highlight"/"link"。</param>
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
            if ("normal" == a_style)
            {
                ApplyInlineStyle(a_style);
                return;
            }
            ToggleInlineStyleFromShortcut(a_style);
        }

        /// <summary>
        /// 右クリックメニューの ✓ 用：選んだ文字がすべてその書式か（文字を選んでいない時は、キャレットの直前の文字の書式）。
        /// </summary>
        /// <param name="a_style">"bold"・"italic"・"underline"・"strikethrough"・"code"・"highlight"のいずれか。</param>
        /// <returns>その書式なら true。</returns>
        public bool IsStyleActive(string a_style)
        {
            if (null == m_editor.Selection)
            {
                return false;
            }
            if (!m_editor.Selection.IsEmpty)
            {
                return IsWholeSelectionStyled(a_style);
            }
            if (!(m_editor.CaretPosition.Parent is Run run))
            {
                return false;
            }
            InlineStyleFlags wanted = InlineStyles.FromStyleName(a_style);
            InlineStyleFlags flags = InlineStyles.Get(run);
            return 0 != (wanted & InlineStyles.ANY_ITALIC)
                ? 0 != (flags & InlineStyles.ANY_ITALIC)
                : InlineStyleFlags.None != wanted && wanted == (flags & wanted);
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
        /// Ctrl+Shift+@（`）：インラインコード。mdew・mdee と同じキー）。選んだ文字がすべてその書式なら、その書式だけを
        /// 外す（ほかの書式は残す。もう一度押すと元に戻る）。それ以外は、その書式を重ねる（右クリックの「文字装飾」と
        /// 同じ処理）。インラインコードは書式を重ねられないため、外す時は通常の文字に戻す。文字を選んでいない時は何もしない。
        /// </summary>
        /// <param name="a_style">"bold"・"italic"・"underline"・"strikethrough"・"code"のいずれか。</param>
        public void ToggleInlineStyleFromShortcut(string a_style)
        {
            if (null == m_editor.Selection || m_editor.Selection.IsEmpty)
            {
                return;
            }
            if (!CanApplyStyle(a_style))
            {
                // 今の選択範囲には付けられない組み合わせ（コードに太字等・書式の付いた文字にコード）：何もしない
                return;
            }
            InlineStyleFlags flag = InlineStyles.FromStyleName(a_style);
            if (!IsWholeSelectionStyled(a_style))
            {
                ApplyInlineStyle(a_style);
            }
            else if (InlineStyleFlags.Code == flag)
            {
                ApplyInlineStyle("normal");
            }
            else
            {
                // 斜体は、* と _ のどちらで書いた斜体も外す
                InlineStyleFlags remove = 0 != (flag & InlineStyles.ANY_ITALIC) ? InlineStyles.ANY_ITALIC : flag;
                ModifySelectionStyles(f => f & ~remove);
            }
        }

        /// <summary>
        /// 今の選択範囲に、その書式を付ける・外すことができるか（mdew と同じ。右クリックメニューで使えない項目を灰色に
        /// するのと、ショートカットで何もしないのに使う）。Markdown ではインラインコードの中に書式を書けないため、
        /// インラインコードを含む範囲には、太字・斜体・下線・取り消し線・ハイライトを付けられない。また、インラインコードに
        /// すると、その範囲の書式は消えてしまうため、書式（太字等）の付いた文字を含む範囲は、インラインコードにできない
        /// （インラインコードだけを選んでいる時は、コードを外すことはできる）。
        /// </summary>
        /// <param name="a_style">"bold"・"italic"・"underline"・"strikethrough"・"code"・"highlight"のいずれか。</param>
        /// <returns>付けられる（外せる）なら true。</returns>
        public bool CanApplyStyle(string a_style)
        {
            if (null == m_editor.Selection || m_editor.Selection.IsEmpty)
            {
                return false;
            }
            InlineStyleFlags flag = InlineStyles.FromStyleName(a_style);
            if (InlineStyleFlags.None == flag)
            {
                return true;
            }
            var (hasCodeFlg, hasStyledFlg) = AnalyzeSelectionStyles();
            return InlineStyleFlags.Code == flag ? !hasStyledFlg : !hasCodeFlg;
        }

        /// <summary>選択範囲（書式を付ける時と同じ区間・範囲）に、インラインコードの文字と、書式（太字等）の付いた文字が
        /// あるかを調べる。</summary>
        /// <returns>(コードの文字があるか, 書式の付いた文字があるか)。</returns>
        private (bool HasCode, bool HasStyled) AnalyzeSelectionStyles()
        {
            bool hasCodeFlg = false;
            bool hasStyledFlg = false;
            foreach (var seg in CollectStyleSegments(m_editor.Selection.Start, m_editor.Selection.End))
            {
                List<(Run Run, int From, int To)> pieces = CollectRunPieces(seg.Start, seg.End);
                TrimWhiteSpacePieces(pieces);
                if (null == pieces)
                {
                    continue;
                }
                foreach (var (run, from, to) in pieces)
                {
                    if (0 == run.Text.Substring(from, to - from).Replace("\u200B", "").Length)
                    {
                        continue;
                    }
                    InlineStyleFlags flags = InlineStyles.Get(run);
                    if (0 != (flags & InlineStyleFlags.Code))
                    {
                        hasCodeFlg = true;
                    }
                    else if (0 != (flags & InlineStyles.STACKABLE_MASK))
                    {
                        hasStyledFlg = true;
                    }
                    if (hasCodeFlg && hasStyledFlg)
                    {
                        return (true, true);
                    }
                }
            }
            return (hasCodeFlg, hasStyledFlg);
        }

        /// <summary>選んだ範囲の文字（ゼロ幅スペース以外）が、すべて指定の書式か。</summary>
        /// <param name="a_style">"bold"・"italic"・"underline"・"strikethrough"・"code"のいずれか。</param>
        /// <returns>すべてその書式なら true。</returns>
        private bool IsWholeSelectionStyled(string a_style)
        {
            // 書式を付ける時と同じ区間・同じ範囲（先頭と末尾の空白を除いた範囲。ReplaceSelectionWithStyledRuns）だけを
            // 調べる。表の四角い範囲を選んだ時に四角の外のセルを見ないように、また、書式の外に残した空白のせいで
            // 「すべてその書式」と判定されなくならないように。
            bool anyTextFlg = false;
            foreach (var seg in CollectStyleSegments(m_editor.Selection.Start, m_editor.Selection.End))
            {
                List<(Run Run, int From, int To)> pieces = CollectRunPieces(seg.Start, seg.End);
                TrimWhiteSpacePieces(pieces);
                if (null == pieces)
                {
                    continue;
                }
                foreach (var (run, from, to) in pieces)
                {
                    if (0 == run.Text.Substring(from, to - from).Replace("\u200B", "").Length)
                    {
                        continue;
                    }
                    InlineStyleFlags flags = InlineStyles.Get(run);
                    InlineStyleFlags wanted = InlineStyles.FromStyleName(a_style);
                    bool sameFlg = 0 != (wanted & InlineStyles.ANY_ITALIC)
                        ? 0 != (flags & InlineStyles.ANY_ITALIC)
                        : InlineStyleFlags.None != wanted && wanted == (flags & wanted);
                    if (!sameFlg)
                    {
                        return false;
                    }
                    anyTextFlg = true;
                }
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
            ReplaceSelectionWithStyledRuns(text => new Run(BlockStyles.AddUrlBreakJoiners(text))
            {
                Foreground = m_linkBrush,
                TextDecorations = BlockStyles.LinkTextDecorations,
                Tag = new LinkInfo { Url = a_url, IsAutoLinkFlg = false },
                ToolTip = a_url
            });
        }

        /// <summary>
        /// 右クリック「文字装飾」の実装。太字・斜体・取り消し線・下線・ハイライトは、選択範囲の文字の今の書式に
        /// 重ねる（ModifySelectionStyles。「~~**太字の取り消し線**~~」のように、書式を重ねがけできる）。インラインコードは
        /// 書式を重ねられない（Markdown ではコードの中に書式を書けない）ため、「通常表記に戻す」と同じく、区間ごとに
        /// 1つの新しい Run に置き換える。
        /// </summary>
        /// <param name="a_style">"normal"、"code"、"bold"、"italic"、"strikethrough"、"underline"、"highlight"のいずれか。</param>
        private void ApplyInlineStyle(string a_style)
        {
            if (null == m_editor.Selection || m_editor.Selection.IsEmpty)
            {
                return;
            }
            InlineStyleFlags flag = InlineStyles.FromStyleName(a_style);
            if (InlineStyleFlags.Code == flag)
            {
                ReplaceSelectionWithStyledRuns(text => InlineStyles.CreateRun(text, InlineStyleFlags.Code));
            }
            else if (InlineStyleFlags.None == flag)
            {
                // "normal"：書式もリンクも外して、通常の文字に戻す
                ReplaceSelectionWithStyledRuns(text => new Run(text));
            }
            else
            {
                // 斜体は * と _ のどちらか1つだけにする（両方を持たせない）
                InlineStyleFlags clear = 0 != (flag & InlineStyles.ANY_ITALIC) ? InlineStyles.ANY_ITALIC : InlineStyleFlags.None;
                ModifySelectionStyles(f => (f & ~clear) | flag);
            }
        }

        /// <summary>
        /// 選択範囲の文字の書式（太字・斜体・取り消し線・下線・ハイライト）を、Run の部分ごとに a_transform で変える
        /// （書式の重ねがけ・一部の書式だけを外す時に使う）。区間の分け方・表の四角い範囲の扱い・先頭と末尾の空白を
        /// 除くことは、ReplaceSelectionWithStyledRuns と同じ。リンクとインラインコードは、Run の一部だけを選んでいても
        /// Run 全体の書式を変える（リンク・コードが途中で分かれないように）。エスケープした文字・コードの印
        /// （InlineStyleFlags.Escaped・Code）はそのまま残す。
        /// </summary>
        /// <param name="a_transform">今の書式から新しい書式を作る処理（重ねがけできる書式だけを変えること）。</param>
        private void ModifySelectionStyles(Func<InlineStyleFlags, InlineStyleFlags> a_transform)
        {
            var segments = new List<(TextPointer Start, List<(Run Run, int From, int To)> Pieces)>();
            foreach (var seg in CollectStyleSegments(m_editor.Selection.Start, m_editor.Selection.End))
            {
                List<(Run Run, int From, int To)> pieces = CollectRunPieces(seg.Start, seg.End);
                TrimWhiteSpacePieces(pieces);
                if (null == pieces || 0 == pieces.Count)
                {
                    continue;
                }
                for (int k = 0; k < pieces.Count; k++)
                {
                    Run run = pieces[k].Run;
                    if (run.Tag is LinkInfo || 0 != (InlineStyles.Get(run) & InlineStyleFlags.Code))
                    {
                        pieces[k] = (run, 0, run.Text.Length);
                    }
                }
                segments.Add((seg.Start, pieces));
            }
            if (0 == segments.Count)
            {
                return;
            }

            foreach (var seg in segments)
            {
                m_originalTextTracker.Invalidate(seg.Start);
            }

            m_runAsProgrammaticChange(() =>
            {
                TextPointer selectionStart = null;
                TextPointer selectionEnd = null;
                foreach (var seg in segments)
                {
                    foreach (var (run, from, to) in seg.Pieces)
                    {
                        InlineStyleFlags oldFlags = InlineStyles.Get(run);
                        InlineStyleFlags newFlags = (a_transform(oldFlags) & InlineStyles.STACKABLE_MASK) |
                            (oldFlags & ~InlineStyles.STACKABLE_MASK);
                        if (newFlags == oldFlags)
                        {
                            selectionStart ??= run.ContentStart.GetPositionAtOffset(from);
                            selectionEnd = run.ContentStart.GetPositionAtOffset(to);
                            continue;
                        }
                        Run newRun = CreateRunWithFlags(run, run.Text.Substring(from, to - from), newFlags);
                        ReplaceSegmentPieces(new List<(Run Run, int From, int To)> { (run, from, to) }, new List<Run> { newRun });
                        selectionStart ??= newRun.ContentStart;
                        selectionEnd = newRun.ContentEnd;
                    }
                }
                if (null != selectionStart && null != selectionEnd)
                {
                    m_editor.Selection.Select(selectionStart, selectionEnd);
                    m_editor.CaretPosition = selectionEnd;
                }
            });
            m_refreshOutline();
            m_markDirty();
        }

        /// <summary>
        /// 元の Run の種類（リンク・コード・エスケープした文字・普通の文字）のまま、書式を a_flags にした新しい Run を作る。
        /// リンクは、リンクの情報（LinkInfo）を写し、重ねた書式を LinkInfo.Styles に持たせる。
        /// </summary>
        /// <param name="a_original">元の Run。</param>
        /// <param name="a_text">新しい Run の文字。</param>
        /// <param name="a_flags">新しい書式。</param>
        /// <returns>新しい Run（まだ文書に入っていない）。</returns>
        private static Run CreateRunWithFlags(Run a_original, string a_text, InlineStyleFlags a_flags)
        {
            if (a_original.Tag is LinkInfo li)
            {
                var run = new Run(a_text)
                {
                    Foreground = m_linkBrush,
                    TextDecorations = BlockStyles.LinkTextDecorations,
                    Tag = new LinkInfo
                    {
                        Url = li.Url,
                        IsAutoLinkFlg = li.IsAutoLinkFlg,
                        Title = li.Title,
                        IsEmailAutoLinkFlg = li.IsEmailAutoLinkFlg,
                        IsBareEmailFlg = li.IsBareEmailFlg,
                        Styles = InlineStyles.ToTag(a_flags & InlineStyles.STACKABLE_MASK)
                    },
                    ToolTip = a_original.ToolTip
                };
                InlineStyles.ApplyLinkVisual(run, a_flags);
                return run;
            }
            return InlineStyles.CreateRun(a_text, a_flags);
        }

        /// <summary>
        /// 選択範囲の文字を、a_createRun で作った新しいRunに置き換える（右クリック「文字装飾」・書式のショートカット・
        /// リンクの設定の共通処理）。選択範囲が表の複数のセルや複数の段落にまたがる時は、セル・段落ごと
        /// （CollectStyleSegments の区間ごと）に置き換える。以前は、選択範囲の文字をすべてつなげた1つのRunを
        /// 選択範囲の先頭へ入れていたため、表の複数のセルを選ぶと、全部のセルの文字が先頭のセルへ集まり、ほかの
        /// セルが空になっていた（複数の段落を選んだ時も、段落が1つにつながっていた）。区間の間にある画像や
        /// チェックボックス・改行（LineBreak）も、置き換えずにそのまま残す。
        /// 置き換えは、TextRange で文字を消してから位置を指定して Run を入れるのではなく、Run を直接切り分けて
        /// 行う（ReplaceSegmentPieces）。TextRange で消すと、中身の無くなった Run（たとえば太字の Run）が残り、
        /// Markdown に「****」のような空の記号が書き出されていたため。
        /// </summary>
        /// <param name="a_createRun">区間の文字から、新しいRunを作る処理。</param>
        private void ReplaceSelectionWithStyledRuns(Func<string, Run> a_createRun)
        {
            // 文書を変える前に、区間ごとの「どの Run の何文字目から何文字目までか」を全部求めておく
            // （変えた後の TextPointer は当てにならないため）。
            var segments = new List<(TextPointer Start, List<(Run Run, int From, int To)> Pieces, string Text)>();
            foreach (var seg in CollectStyleSegments(m_editor.Selection.Start, m_editor.Selection.End))
            {
                List<(Run Run, int From, int To)> pieces = CollectRunPieces(seg.Start, seg.End);
                TrimWhiteSpacePieces(pieces);
                if (null == pieces || 0 == pieces.Count)
                {
                    continue;
                }
                string text = string.Concat(pieces.Select(p => p.Run.Text.Substring(p.From, p.To - p.From)));
                if (0 < text.Replace("\u200B", "").Length)
                {
                    segments.Add((seg.Start, pieces, text));
                }
            }
            if (0 == segments.Count)
            {
                return;
            }

            foreach (var seg in segments)
            {
                m_originalTextTracker.Invalidate(seg.Start);
            }

            m_runAsProgrammaticChange(() =>
            {
                Run firstRun = null;
                Run lastRun = null;
                foreach (var seg in segments)
                {
                    Run newRun = a_createRun(seg.Text);
                    ReplaceSegmentPieces(seg.Pieces, new List<Run> { newRun });
                    firstRun ??= newRun;
                    lastRun = newRun;
                }
                m_editor.Selection.Select(firstRun.ContentStart, lastRun.ContentEnd);
                m_editor.CaretPosition = lastRun.ContentEnd;
            });
            m_refreshOutline();
            m_markDirty();
        }

        /// <summary>
        /// 区間（同じ段落の中で文字が続いている範囲）にかかっている Run と、それぞれの Run の中の範囲（文字の位置）を、
        /// 文書の順に返す。
        /// </summary>
        /// <param name="a_start">区間の開始位置。</param>
        /// <param name="a_end">区間の終了位置。</param>
        /// <returns>(Run, 開始の文字位置, 終了の文字位置) の一覧。Run 以外の要素に文字があった時は null。</returns>
        private static List<(Run Run, int From, int To)> CollectRunPieces(TextPointer a_start, TextPointer a_end)
        {
            var pieces = new List<(Run Run, int From, int To)>();
            TextPointer navigator = a_start;
            int guard = 0;
            while (null != navigator && navigator.CompareTo(a_end) < 0 && guard++ < 100000)
            {
                if (TextPointerContext.Text == navigator.GetPointerContext(LogicalDirection.Forward))
                {
                    if (!(navigator.Parent is Run run))
                    {
                        return null;
                    }
                    string runText = navigator.GetTextInRun(LogicalDirection.Forward);
                    TextPointer textEnd = navigator.GetPositionAtOffset(runText.Length) ?? a_end;
                    if (textEnd.CompareTo(a_end) > 0)
                    {
                        textEnd = a_end;
                    }
                    int from = run.ContentStart.GetOffsetToPosition(navigator);
                    int to = run.ContentStart.GetOffsetToPosition(textEnd);
                    if (from < to)
                    {
                        pieces.Add((run, from, to));
                    }
                    navigator = textEnd;
                    continue;
                }
                navigator = navigator.GetNextContextPosition(LogicalDirection.Forward);
            }
            return pieces;
        }

        /// <summary>
        /// 区間の先頭と末尾の空白を、書式を付ける範囲から外す（空白は元の書式のまま残る）。マウスで段落をまたいで
        /// 選ぶと、WPF は単語の後ろの空白まで選ぶため、そのまま書式を付けると「*gamma *delta」のように、記号の内側に
        /// 空白がある Markdown になり、正しく書式として読まれなくなるため（Typora も空白を外して書式を付ける）。
        /// </summary>
        /// <param name="a_pieces">CollectRunPieces で求めた一覧（この中身を書き換える。null の時は何もしない）。</param>
        private static void TrimWhiteSpacePieces(List<(Run Run, int From, int To)> a_pieces)
        {
            if (null == a_pieces)
            {
                return;
            }
            while (0 < a_pieces.Count)
            {
                var (run, from, to) = a_pieces[a_pieces.Count - 1];
                while (from < to && char.IsWhiteSpace(run.Text[to - 1]))
                {
                    to--;
                }
                if (from < to)
                {
                    a_pieces[a_pieces.Count - 1] = (run, from, to);
                    break;
                }
                a_pieces.RemoveAt(a_pieces.Count - 1);
            }
            while (0 < a_pieces.Count)
            {
                var (run, from, to) = a_pieces[0];
                while (from < to && char.IsWhiteSpace(run.Text[from]))
                {
                    from++;
                }
                if (from < to)
                {
                    a_pieces[0] = (run, from, to);
                    break;
                }
                a_pieces.RemoveAt(0);
            }
        }

        /// <summary>
        /// 区間の文字を、a_newRuns（1つ以上）に置き換える。最初の Run の区間より前の文字と、最後の Run の区間より後ろの
        /// 文字は、元の書式のまま残す（同じ Run の途中だけを置き換える時は、後ろの文字を元の Run と同じ書式の新しい
        /// Run に分ける）。間の Run は取り除き、中身が無くなった Span（リンク等）も取り除く。
        /// </summary>
        /// <param name="a_pieces">CollectRunPieces で求めた、区間にかかっている Run とその中の範囲。</param>
        /// <param name="a_newRuns">置き換える新しい Run の並び（まだ文書に入っていないもの）。</param>
        private static void ReplaceSegmentPieces(List<(Run Run, int From, int To)> a_pieces, List<Run> a_newRuns)
        {
            var (firstRun, firstFrom, firstTo) = a_pieces[0];
            var (lastRun, _, lastTo) = a_pieces[a_pieces.Count - 1];
            string prefix = firstRun.Text.Substring(0, firstFrom);
            string suffix = lastRun.Text.Substring(lastTo);
            bool sameRunFlg = ReferenceEquals(firstRun, lastRun);

            // 新しい Run を入れる：最初の Run に前の文字が残るならその後ろ、残らないならその前
            Inline anchor = firstRun;
            bool afterFlg = 0 < prefix.Length;
            foreach (Run newRun in a_newRuns)
            {
                if (afterFlg)
                {
                    anchor.SiblingInlines.InsertAfter(anchor, newRun);
                }
                else
                {
                    anchor.SiblingInlines.InsertBefore(anchor, newRun);
                    afterFlg = true;
                }
                anchor = newRun;
            }

            if (sameRunFlg)
            {
                if (0 < prefix.Length)
                {
                    firstRun.Text = prefix;
                    if (0 < suffix.Length)
                    {
                        anchor.SiblingInlines.InsertAfter(anchor, ListEditor.CloneRunForSplit(firstRun, suffix));
                    }
                }
                else if (0 < suffix.Length)
                {
                    firstRun.Text = suffix;
                }
                else
                {
                    RemoveInline(firstRun);
                }
                return;
            }

            // 最初の Run：区間より前の文字だけを残す
            if (0 < prefix.Length)
            {
                firstRun.Text = prefix;
            }
            else
            {
                RemoveInline(firstRun);
            }
            // 間の Run：全部の文字が区間に入っているので取り除く
            for (int i = 1; i < a_pieces.Count - 1; i++)
            {
                RemoveInline(a_pieces[i].Run);
            }
            // 最後の Run：区間より後ろの文字だけを残す
            if (0 < suffix.Length)
            {
                lastRun.Text = suffix;
            }
            else
            {
                RemoveInline(lastRun);
            }
        }

        /// <summary>Inline を取り除き、それで中身が無くなった親の Span も取り除く。</summary>
        /// <param name="a_inline">取り除く Inline。</param>
        private static void RemoveInline(Inline a_inline)
        {
            DependencyObject parent = a_inline.Parent;
            a_inline.SiblingInlines?.Remove(a_inline);
            while (parent is Span span && 0 == span.Inlines.Count)
            {
                parent = span.Parent;
                span.SiblingInlines?.Remove(span);
            }
        }

        /// <summary>
        /// 選択範囲を、書式を付け直す区間（同じ段落の中で、文字が続いている範囲）に分ける。区間は、段落・表のセル・
        /// 箇条書きの項目の境目と、画像やチェックボックス（InlineUIContainer）・改行（LineBreak）の所で区切る。
        /// Run・Span（太字やリンクなど）の境目では区切らない。
        /// 表の中の四角い範囲（複数の行と列）を選んだ時は、WPF は、選んだ四角の中のセルだけを選択範囲とするが、
        /// Selection.Start から End までには、四角の外のセル（1行目の右側や2行目の左側）も入るため、四角の外のセルの
        /// 区間は除く。
        /// </summary>
        /// <param name="a_start">選択範囲の開始位置。</param>
        /// <param name="a_end">選択範囲の終了位置。</param>
        /// <returns>文書の順に並んだ区間の一覧。</returns>
        private static List<(TextPointer Start, TextPointer End)> CollectStyleSegments(TextPointer a_start, TextPointer a_end)
        {
            var result = new List<(TextPointer Start, TextPointer End)>();
            Func<TextPointer, bool> isInCellRect = GetCellRectFilter(a_start, a_end);
            TextPointer segStart = null;
            TextPointer segEnd = null;
            void closeSegment()
            {
                if (null != segStart && segStart.CompareTo(segEnd) < 0 && isInCellRect(segStart))
                {
                    result.Add((segStart, segEnd));
                }
                segStart = null;
                segEnd = null;
            }

            TextPointer navigator = a_start;
            int guard = 0;
            while (null != navigator && navigator.CompareTo(a_end) < 0 && guard++ < 1000000)
            {
                TextPointerContext context = navigator.GetPointerContext(LogicalDirection.Forward);
                if (TextPointerContext.Text == context)
                {
                    string runText = navigator.GetTextInRun(LogicalDirection.Forward);
                    TextPointer textEnd = navigator.GetPositionAtOffset(runText.Length) ?? a_end;
                    if (textEnd.CompareTo(a_end) > 0)
                    {
                        textEnd = a_end;
                    }
                    segStart ??= navigator;
                    segEnd = textEnd;
                    navigator = textEnd;
                    continue;
                }
                if (TextPointerContext.ElementStart == context || TextPointerContext.ElementEnd == context)
                {
                    var element = navigator.GetAdjacentElement(LogicalDirection.Forward);
                    if (!(element is Run) && !(element is Span))
                    {
                        closeSegment();
                    }
                }
                else
                {
                    closeSegment(); // EmbeddedElement（画像などのUI要素）
                }
                navigator = navigator.GetNextContextPosition(LogicalDirection.Forward);
            }
            closeSegment();
            return result;
        }

        /// <summary>
        /// 選択範囲の両端が、同じ表の別々のセルにある時（WPF が、セルを単位とした四角い範囲の選択にする時）に、
        /// 位置が選んだ四角の中のセルにあるかどうかを返す判定を作る。それ以外の時は、常に true を返す判定を返す。
        /// </summary>
        /// <param name="a_start">選択範囲の開始位置。</param>
        /// <param name="a_end">選択範囲の終了位置。</param>
        /// <returns>位置が選択範囲の対象かどうかの判定。</returns>
        private static Func<TextPointer, bool> GetCellRectFilter(TextPointer a_start, TextPointer a_end)
        {
            TableCell startCell = ResolveSelectionEndCell(a_start, LogicalDirection.Forward);
            TableCell endCell = ResolveSelectionEndCell(a_end, LogicalDirection.Backward);
            // セルを単位とした選択では、WPF は選択範囲の終わりを「最後のセルの次のセルの先頭」に置く（たとえば
            // 2行目1列目〜3行目2列目を選ぶと、終わりは3行目3列目の文字の前）。そのままでは次のセルまで選んだことに
            // なり、四角が1列（または1行）広がって、選んでいないセルの書式まで変わってしまうため、終わりがセルの
            // 中身の先頭（その前に文字等が無い所）にある時は、1つ前のセルを最後のセルとする。
            if (null != startCell && null != endCell && !ReferenceEquals(startCell, endCell) &&
                ReferenceEquals(FindAncestor<TableCell>(a_end.Parent), endCell) && IsAtCellContentStart(a_end, endCell))
            {
                endCell = GetPreviousCell(endCell) ?? endCell;
            }
            DebugLogger.Log("GetCellRectFilter: start=" + DescribePositionForLog(a_start) + " cell=" + DescribeCellForLog(startCell) +
                " end=" + DescribePositionForLog(a_end) + " cell=" + DescribeCellForLog(endCell));
            if (null == startCell || null == endCell || ReferenceEquals(startCell, endCell) ||
                !(GetCellIndex(startCell) is (Table table, int startRow, int startCol)) ||
                !(GetCellIndex(endCell) is (Table endTable, int endRow, int endCol)) ||
                !ReferenceEquals(table, endTable))
            {
                return _ => true;
            }
            int minRow = Math.Min(startRow, endRow), maxRow = Math.Max(startRow, endRow);
            int minCol = Math.Min(startCol, endCol), maxCol = Math.Max(startCol, endCol);
            return position =>
            {
                TableCell cell = FindAncestor<TableCell>(position.Parent);
                if (null == cell || !(GetCellIndex(cell) is (Table t, int row, int col)) || !ReferenceEquals(t, table))
                {
                    return true;
                }
                return minRow <= row && row <= maxRow && minCol <= col && col <= maxCol;
            };
        }

        /// <summary>
        /// 選択範囲の端の位置があるセルを返す。セルを単位とした選択では、選択範囲の端が、セルの中ではなく、
        /// セルとセルの間（行や表の要素の直下）に来ることがある（たとえば終わりの端が、最後のセルの直後）。その時は、
        /// 開始の端なら後ろへ、終わりの端なら前へたどって、最初に入るセルを返す。
        /// </summary>
        /// <param name="a_position">選択範囲の端の位置。</param>
        /// <param name="a_direction">セルを探す向き（開始の端は Forward、終わりの端は Backward）。</param>
        /// <returns>セル。表の中でない時は null。</returns>
        private static TableCell ResolveSelectionEndCell(TextPointer a_position, LogicalDirection a_direction)
        {
            TableCell cell = FindAncestor<TableCell>(a_position.Parent);
            if (null != cell)
            {
                return cell;
            }
            if (!(a_position.Parent is TableRow) && !(a_position.Parent is TableRowGroup) && !(a_position.Parent is Table))
            {
                return null;
            }
            TextPointer navigator = a_position;
            for (int i = 0; i < 8 && null != navigator; i++)
            {
                navigator = navigator.GetNextContextPosition(a_direction);
                if (null == navigator)
                {
                    break;
                }
                cell = FindAncestor<TableCell>(navigator.Parent);
                if (null != cell)
                {
                    return cell;
                }
            }
            return null;
        }

        /// <summary>位置が、セルの中身の先頭（セルの始まりからその位置までに、文字・画像等が1つも無い所）にあるか。</summary>
        /// <param name="a_position">位置。</param>
        /// <param name="a_cell">位置のあるセル。</param>
        /// <returns>中身の先頭なら true。</returns>
        private static bool IsAtCellContentStart(TextPointer a_position, TableCell a_cell)
        {
            TextPointer navigator = a_cell.ContentStart;
            int guard = 0;
            while (null != navigator && navigator.CompareTo(a_position) < 0 && guard++ < 1000)
            {
                TextPointerContext context = navigator.GetPointerContext(LogicalDirection.Forward);
                if (TextPointerContext.Text == context || TextPointerContext.EmbeddedElement == context ||
                    (TextPointerContext.ElementStart == context &&
                     (navigator.GetAdjacentElement(LogicalDirection.Forward) is InlineUIContainer ||
                      navigator.GetAdjacentElement(LogicalDirection.Forward) is LineBreak)))
                {
                    return false;
                }
                navigator = navigator.GetNextContextPosition(LogicalDirection.Forward);
            }
            return true;
        }

        /// <summary>同じ表の中で、文書の順に1つ前のセル（行の先頭のセルなら、前の行の最後のセル）を返す。</summary>
        /// <param name="a_cell">セル。</param>
        /// <returns>1つ前のセル。無い時は null。</returns>
        private static TableCell GetPreviousCell(TableCell a_cell)
        {
            if (!(a_cell.Parent is TableRow row))
            {
                return null;
            }
            int index = row.Cells.IndexOf(a_cell);
            if (0 < index)
            {
                return row.Cells[index - 1];
            }
            if (!(row.Parent is TableRowGroup group) || !(group.Parent is Table table))
            {
                return null;
            }
            TableRow previousRow = null;
            foreach (TableRowGroup g in table.RowGroups)
            {
                foreach (TableRow r in g.Rows)
                {
                    if (ReferenceEquals(r, row))
                    {
                        return null == previousRow || 0 == previousRow.Cells.Count ? null : previousRow.Cells[previousRow.Cells.Count - 1];
                    }
                    previousRow = r;
                }
            }
            return null;
        }

        /// <summary>ログ用：位置の親要素の種類と、前後の要素の種類。</summary>
        /// <param name="a_position">位置。</param>
        /// <returns>説明の文字列。</returns>
        private static string DescribePositionForLog(TextPointer a_position)
        {
            return (a_position.Parent?.GetType().Name ?? "null") + "(back=" + a_position.GetPointerContext(LogicalDirection.Backward) +
                ",fwd=" + a_position.GetPointerContext(LogicalDirection.Forward) + ")";
        }

        /// <summary>ログ用：セルの行・列の番号と、表の識別。</summary>
        /// <param name="a_cell">セル（null 可）。</param>
        /// <returns>説明の文字列。</returns>
        private static string DescribeCellForLog(TableCell a_cell)
        {
            if (null == a_cell)
            {
                return "null";
            }
            var index = GetCellIndex(a_cell);
            return null == index ? "?" : $"r{index.Value.Row}c{index.Value.Col}@{index.Value.Table.GetHashCode():X}";
        }

        /// <summary>セルの表と、表の中での行番号（全行グループを通した番号）・列番号を返す。</summary>
        /// <param name="a_cell">対象のセル。</param>
        /// <returns>(表, 行番号, 列番号)。分からない時は null。</returns>
        private static (Table Table, int Row, int Col)? GetCellIndex(TableCell a_cell)
        {
            if (!(a_cell.Parent is TableRow row) || !(row.Parent is TableRowGroup group) || !(group.Parent is Table table))
            {
                return null;
            }
            int rowIndex = 0;
            foreach (TableRowGroup g in table.RowGroups)
            {
                if (ReferenceEquals(g, group))
                {
                    rowIndex += g.Rows.IndexOf(row);
                    break;
                }
                rowIndex += g.Rows.Count;
            }
            return (table, rowIndex, row.Cells.IndexOf(a_cell));
        }

        /// <summary>要素（またはその親）をたどって、指定の型の要素を探す。</summary>
        /// <typeparam name="T">探す要素の型。</typeparam>
        /// <param name="a_node">探し始める要素。</param>
        /// <returns>見つかった要素。無ければ null。</returns>
        private static T FindAncestor<T>(DependencyObject a_node) where T : DependencyObject
        {
            DependencyObject node = a_node;
            while (null != node)
            {
                if (node is T found)
                {
                    return found;
                }
                node = (node as TextElement)?.Parent ?? (node as FrameworkContentElement)?.Parent;
            }
            return null;
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
                Clipboard.SetText(BlockStyles.StripUrlBreakJoiners(md));
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
                MessageBox.Show(Loc.T("リンクを開けませんでした: ", "Could not open the link: ") + ex.Message, Loc.T("リンクを開く", "Open Link"),
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
                    MessageBox.Show(Loc.T("リンク先のファイルが見つかりませんでした:\n", "The linked file was not found:\n") + resolved,
                        Loc.T("ファイルリンク", "File Link"),
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

        /// <summary>画像リンクをクリックした時、通常のリンクと同じCtrl要件で開く。</summary>
        /// <param name="a_url">画像を囲むリンクのURL。</param>
        /// <returns>設定を満たしてリンクを開いた場合はtrue。</returns>
        public bool HandleImageLinkClick(string a_url)
        {
            bool requireCtrlFlg = m_requireCtrlForLinkClick?.Invoke() ?? true;
            if (requireCtrlFlg && Keyboard.Modifiers != ModifierKeys.Control)
            {
                return false;
            }

            NavigateLink(a_url);
            return true;
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
                // （MarkdownConverter.m_inlineTokenRegex）と優先順位を合わせるため、
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
                    bool isEscapedFlg = 0 != (InlineStyles.Parse(tag) & InlineStyleFlags.Escaped);
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
        /// BackSpace（a_backwardFlg）・Delete を押した時、キャレットのすぐ前（後ろ）に続くゼロ幅スペース（書式の自動変換が
        /// 書式の後ろに置いたもの。ReplaceTextBeforeCaretWithStyledRun）を取り除く。その向こうの文字は、この後の WPF 標準の
        /// 処理で消える（1回のキーで、目に見える文字が1つ消えるようにする）。同じ段落の Run の中だけを見て、ゼロ幅スペースの
        /// 向こうに文字がある時だけ取り除く。
        /// </summary>
        /// <param name="a_backwardFlg">BackSpace なら true、Delete なら false。</param>
        public void RemoveZeroWidthSpacesAtCaret(bool a_backwardFlg)
        {
            TextPointer caret = m_editor.CaretPosition;
            LogicalDirection direction = a_backwardFlg ? LogicalDirection.Backward : LogicalDirection.Forward;
            TextPointer navigator = caret;
            TextPointer far = null;
            bool textBeyondFlg = false;
            int guard = 0;
            while (null != navigator && guard++ < 50)
            {
                TextPointerContext context = navigator.GetPointerContext(direction);
                if (TextPointerContext.Text == context)
                {
                    string text = navigator.GetTextInRun(direction);
                    char c = a_backwardFlg ? text[text.Length - 1] : text[0];
                    if ('\u200B' != c)
                    {
                        textBeyondFlg = true;
                        break;
                    }
                    navigator = navigator.GetPositionAtOffset(a_backwardFlg ? -1 : 1);
                    far = navigator;
                    continue;
                }
                if ((TextPointerContext.ElementStart == context || TextPointerContext.ElementEnd == context) &&
                    navigator.GetAdjacentElement(direction) is Run)
                {
                    navigator = navigator.GetNextContextPosition(direction);
                    continue;
                }
                break;
            }
            // ゼロ幅スペースの向こうが、同じ段落の文字でない時（段落・行の端、画像等）は何もしない（空の項目・セルの
            // ゼロ幅スペース等は、それぞれのキーの処理に任せる）
            if (null == far || !textBeyondFlg)
            {
                return;
            }
            m_originalTextTracker.Invalidate(caret);
            m_runAsProgrammaticChange(() =>
            {
                if (a_backwardFlg)
                {
                    new TextRange(far, caret).Text = "";
                }
                else
                {
                    new TextRange(caret, far).Text = "";
                }
            });
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
            // 書式の中で打った時は、その書式を引き継ぐ（書式の重ねがけ。InlineStyles）
            InlineStyleFlags context = a_caret.Parent is Run caretRun && InlineStyles.IsStyleRun(caretRun)
                ? InlineStyles.Get(caretRun) & InlineStyles.STACKABLE_MASK
                : InlineStyleFlags.None;
            m_runAsProgrammaticChange(() =>
            {
                new TextRange(a_start, a_caret).Text = "";
                var newRun = new Run(a_escapedChar.ToString(), a_start) { Tag = InlineStyles.ToTag(context | InlineStyleFlags.Escaped) };
                InlineStyles.ApplyVisual(newRun, context);
                var trailingRun = new Run("\u200B", newRun.ContentEnd) { Tag = InlineStyles.ToTag(context) };
                InlineStyles.ApplyVisual(trailingRun, context);
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
                var newRun = new Run(BlockStyles.AddUrlBreakJoiners(a_linkText), a_start)
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
            InlineStyleFlags flag = InlineStyles.FromStyleName(a_style);
            int openLength = "underline" == a_style ? 3 : ("bold" == a_style || "strikethrough" == a_style || "highlight" == a_style) ? 2 : 1;
            int closeLength = "underline" == a_style ? 4 : openLength;
            List<(Run Run, int From, int To)> pieces = CollectRunPieces(a_start, a_caret);
            int totalLength = null == pieces ? -1 : pieces.Sum(p => p.To - p.From);
            if (null == pieces || 0 == pieces.Count || InlineStyleFlags.None == flag || totalLength != openLength + a_content.Length + closeLength)
            {
                ReplaceTextBeforeCaretWithSingleStyledRun(a_caret, a_start, a_content, a_style);
                return;
            }

            // 記号の内側の文字を、元の Run の書式に a_style の書式を重ねた Run にする（書式の中で打った時も、その書式は
            // 残る。書式の重ねがけ）。インラインコードは書式を重ねた文字にする（コードの中に書式は書けないが、
            // コード全体に書式を重ねることはできる）。記号は取り除く。
            var newRuns = new List<Run>();
            int contentStart = openLength;
            int contentEnd = totalLength - closeLength;
            int position = 0;
            foreach (var (run, from, to) in pieces)
            {
                int pieceStart = position;
                int pieceEnd = position + (to - from);
                position = pieceEnd;
                int s = Math.Max(pieceStart, contentStart);
                int e = Math.Min(pieceEnd, contentEnd);
                if (s >= e)
                {
                    continue;
                }
                string text = run.Text.Substring(from + (s - pieceStart), e - s);
                InlineStyleFlags oldFlags = InlineStyles.Get(run);
                InlineStyleFlags newFlags;
                if (InlineStyleFlags.Code == flag)
                {
                    // コードには書式を重ねない（右クリックメニュー・ショートカットと同じ。CanApplyStyle）
                    newFlags = InlineStyleFlags.Code;
                }
                else if (0 != (oldFlags & InlineStyleFlags.Code))
                {
                    // 書式の記号の中のコードは、コードのまま（書式を重ねない）
                    newFlags = oldFlags;
                }
                else
                {
                    InlineStyleFlags clear = 0 != (flag & InlineStyles.ANY_ITALIC) ? InlineStyles.ANY_ITALIC : InlineStyleFlags.None;
                    newFlags = (oldFlags & ~clear) | flag;
                }
                newRuns.Add(run.Tag is LinkInfo ? CreateRunWithFlags(run, text, newFlags) : InlineStyles.CreateRun(text, newFlags));
            }
            if (0 == newRuns.Count)
            {
                ReplaceTextBeforeCaretWithSingleStyledRun(a_caret, a_start, a_content, a_style);
                return;
            }
            // 後ろに続けて打つ文字の書式：キャレットのあった所の書式から、今付けた書式を除いたもの（通常の文字の中なら
            // 通常の文字。保存時に自動除去されるゼロ幅スペースの Run で持つ。MarkdownConverter.CollectMarkdownInlineItems 参照）
            Run caretRun = pieces[pieces.Count - 1].Run;
            InlineStyleFlags context = InlineStyles.IsStyleRun(caretRun)
                ? InlineStyles.Get(caretRun) & InlineStyles.STACKABLE_MASK & ~flag
                : InlineStyleFlags.None;
            if (0 != (flag & InlineStyles.ANY_ITALIC))
            {
                context &= ~InlineStyles.ANY_ITALIC;
            }
            Run trailingRun = InlineStyles.CreateRun("\u200B", context);
            newRuns.Add(trailingRun);

            m_runAsProgrammaticChange(() =>
            {
                ReplaceSegmentPieces(pieces, newRuns);
                m_editor.CaretPosition = trailingRun.ContentEnd;
            });
        }

        /// <summary>
        /// ReplaceTextBeforeCaretWithStyledRun で、記号の範囲を Run ごとに分けられなかった時の処理（以前の方法：範囲の
        /// 文字を消して、a_content を a_style の書式の1つの Run として入れる）。
        /// </summary>
        private void ReplaceTextBeforeCaretWithSingleStyledRun(TextPointer a_caret, TextPointer a_start, string a_content, string a_style)
        {
            InlineStyleFlags flag = InlineStyles.FromStyleName(a_style);
            m_runAsProgrammaticChange(() =>
            {
                new TextRange(a_start, a_caret).Text = "";
                var newRun = new Run(a_content, a_start) { Tag = InlineStyles.ToTag(flag) };
                InlineStyles.ApplyVisual(newRun, flag);
                // 通常スタイルの目に見えないゼロ幅スペースRun。直後の入力へスタイルが
                // 継承されるのを防ぐ（保存時に自動除去。MarkdownConverter.CollectMarkdownInlineItems参照）。
                var trailingRun = new Run("\u200B", newRun.ContentEnd);
                m_editor.CaretPosition = trailingRun.ContentEnd;
            });
        }
    }
}
