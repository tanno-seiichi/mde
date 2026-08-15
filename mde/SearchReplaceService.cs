// SearchReplaceService.cs
//
// mde (MarkDown インラインエディタ) の一部。
// 検索・置換を担当するクラス。FindReplaceWindow から呼び出される公開APIとして、
// 現在のファイル内でのライブ検索・置換、フォルダ全体を対象にした検索・置換、
// 1件ずつ確認しながら進める置換セッションを提供する。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Documents;

namespace mde
{
    /// <summary>
    /// 検索・置換機能一式。MainWindowへの参照は持たず、Editor/SourceEditor本体、
    /// MarkdownConverter、LineEndingTrackerなどの協力オブジェクトと、
    /// ファイルの読み込み・保存に関するdelegateだけを受け取って動作する。
    /// </summary>
    public class SearchReplaceService
    {
        private readonly RichTextBox editor;
        private readonly TextBox sourceEditor;
        private readonly MarkdownConverter converter;
        private readonly OriginalTextTracker originalTextTracker;
        private readonly LineEndingTracker lineEndingTracker;
        private readonly Func<bool> isSourceMode;
        private readonly Action<Action> runAsProgrammaticChange;
        private readonly Action refreshOutline;
        private readonly Action<Paragraph> scrollParagraphToTop;
        private readonly Func<string> getLoadedFolderRootPath;
        private readonly Func<string, string> getCurrentContentForFile;
        private readonly Action<string, string> setFileContentForReplaceImpl;
        private readonly Action<string> openFile;
        private readonly Action<Action> runWithoutDirtyMarking;
        private readonly Action<List<TextRange>> markOutlineMatches;

        private static readonly System.Windows.Media.Brush MatchHighlightBrush =
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xE1, 0x66));

        /// <summary>直前に強調表示した一致箇所（フォーカスの有無に関わらず見えるよう、選択
        /// ハイライトではなくRunの背景色を直接変更する方式にしている）。次に検索する前、または
        /// 見えなくなる前にこの背景色を消しておく必要がある。「すべて検索」では複数件を
        /// 同時に強調表示するため、単一ではなく一覧として保持する。</summary>
        private readonly List<TextRange> currentHighlights = new List<TextRange>();

        /// <summary>直前にFindNext/FindPreviousInCurrentFileで見つかった一致箇所。次を検索/前を
        /// 検索の検索開始位置の基準にする（CaretPosition/Selectionだけに頼ると、検索方向を
        /// 切り替えた直後に正しく動かないことがあったため）。</summary>
        private TextRange lastFoundMatch;

        /// <summary>直前に「すべて検索」で強調表示した際の検索条件。次を検索/前を検索が
        /// 同じ条件で呼ばれた場合、既存の全件ハイライトをクリアせずに残すために使う。</summary>
        private string lastHighlightAllTerm;
        private bool lastHighlightAllCaseSensitive;
        private bool lastHighlightAllUseRegex;

        /// <summary>
        /// SearchReplaceServiceを構築する。
        /// </summary>
        /// <param name="editor">MarkDownモードのRichTextBox。</param>
        /// <param name="sourceEditor">ソースモードのTextBox。</param>
        /// <param name="converter">MarkDown⇔内部構造の変換クラス。</param>
        /// <param name="originalTextTracker">「元テキスト保持」の追跡役。</param>
        /// <param name="lineEndingTracker">改行コードの検出・記憶役。</param>
        /// <param name="isSourceMode">現在ソースモードかどうかを返すdelegate。</param>
        /// <param name="runAsProgrammaticChange">処理を「プログラムによる変更」として実行するdelegate。</param>
        /// <param name="refreshOutline">アウトラインペインの再構築を依頼するdelegate。</param>
        /// <param name="scrollParagraphToTop">指定した段落までスクロールするdelegate。</param>
        /// <param name="getLoadedFolderRootPath">現在読み込んでいるフォルダのルートパスを返すdelegate。</param>
        /// <param name="getCurrentContentForFile">ファイルの「今の内容」（編集中エディタ／保留中の編集／ディスク上）を解決するdelegate。</param>
        /// <param name="setFileContentForReplaceImpl">ファイルへ新しい内容を反映するdelegate。</param>
        /// <param name="openFile">ファイルを開くdelegate（フォルダ内検索結果をダブルクリックした時に使う）。</param>
        public SearchReplaceService(
            RichTextBox editor,
            TextBox sourceEditor,
            MarkdownConverter converter,
            OriginalTextTracker originalTextTracker,
            LineEndingTracker lineEndingTracker,
            Func<bool> isSourceMode,
            Action<Action> runAsProgrammaticChange,
            Action refreshOutline,
            Action<Paragraph> scrollParagraphToTop,
            Func<string> getLoadedFolderRootPath,
            Func<string, string> getCurrentContentForFile,
            Action<string, string> setFileContentForReplaceImpl,
            Action<string> openFile,
            Action<Action> runWithoutDirtyMarking,
            Action<List<TextRange>> markOutlineMatches)
        {
            this.editor = editor;
            this.sourceEditor = sourceEditor;
            this.converter = converter;
            this.originalTextTracker = originalTextTracker;
            this.lineEndingTracker = lineEndingTracker;
            this.isSourceMode = isSourceMode;
            this.runAsProgrammaticChange = runAsProgrammaticChange;
            this.refreshOutline = refreshOutline;
            this.scrollParagraphToTop = scrollParagraphToTop;
            this.getLoadedFolderRootPath = getLoadedFolderRootPath;
            this.getCurrentContentForFile = getCurrentContentForFile;
            this.setFileContentForReplaceImpl = setFileContentForReplaceImpl;
            this.openFile = openFile;
            this.runWithoutDirtyMarking = runWithoutDirtyMarking;
            this.markOutlineMatches = markOutlineMatches;
        }

        // ======================================================================
        //  文字列レベルの基本操作
        // ======================================================================

        /// <summary>文字列中に検索語が何回マッチするかを数える。</summary>
        public int CountOccurrences(string text, string term, bool caseSensitive, bool useRegex)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(term)) return 0;
            if (useRegex)
            {
                try
                {
                    var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                    return Regex.Matches(text, term, options).Count;
                }
                catch (ArgumentException)
                {
                    return 0;
                }
            }
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            int count = 0, idx = 0;
            while ((idx = text.IndexOf(term, idx, comparison)) >= 0)
            {
                count++;
                idx += term.Length;
            }
            return count;
        }

        private string ReplaceAllText(string text, string term, string replacement, bool caseSensitive, bool useRegex)
        {
            if (string.IsNullOrEmpty(term)) return text;
            replacement = replacement ?? "";
            if (useRegex)
            {
                try
                {
                    var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                    return Regex.Replace(text, term, replacement, options);
                }
                catch (ArgumentException)
                {
                    return text;
                }
            }
            if (caseSensitive) return text.Replace(term, replacement);

            var sb = new StringBuilder();
            int idx = 0;
            while (true)
            {
                int found = text.IndexOf(term, idx, StringComparison.OrdinalIgnoreCase);
                if (found < 0) { sb.Append(text, idx, text.Length - idx); break; }
                sb.Append(text, idx, found - idx);
                sb.Append(replacement);
                idx = found + term.Length;
            }
            return sb.ToString();
        }

        /// <summary>
        /// fromIndex以降で、テキスト中の次の一致を探す。1件ずつ確認する置換セッション
        /// （現在のファイル・フォルダ全体のどちらでも）が、正規表現の有無によらず統一的に
        /// 使えるように用意されている。
        /// </summary>
        public (int index, int length)? FindNextMatchInText(string text, string term, bool caseSensitive, bool useRegex, int fromIndex)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(term) || fromIndex > text.Length) return null;
            if (useRegex)
            {
                try
                {
                    var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                    var regex = new Regex(term, options);
                    var m = regex.Match(text, fromIndex);
                    if (!m.Success) return null;
                    return (m.Index, m.Length);
                }
                catch (ArgumentException)
                {
                    return null;
                }
            }
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            int idx = text.IndexOf(term, fromIndex, comparison);
            return idx < 0 ? ((int, int)?)null : (idx, term.Length);
        }

        /// <summary>指定位置の一致箇所ちょうど1件だけを置換した結果を返す。</summary>
        public string ReplaceOneMatch(string text, string term, string replacement, bool caseSensitive, bool useRegex, int index, int length)
        {
            replacement = replacement ?? "";
            if (useRegex)
            {
                try
                {
                    var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                    var regex = new Regex(term, options);
                    return regex.Replace(text, replacement, 1, index);
                }
                catch (ArgumentException)
                {
                    return text;
                }
            }
            return text.Substring(0, index) + replacement + text.Substring(index + length);
        }

        // ======================================================================
        //  現在のファイルでのライブ検索
        // ======================================================================

        /// <summary>
        /// キャレットより後ろで次に一致する箇所を選択・スクロール表示する。見つからなければ
        /// 文書の先頭から探し直す（折り返し検索）。実際のライブ文書に対してTextPointerで
        /// 直接検索するため、画面上でハイライトされる（ただし、一致箇所が見出し・箇条書き項目・
        /// 埋め込み画像などのRunの境界をまたぐ場合は検出できないという制約がある）。
        /// </summary>
        /// <summary>直前の一致箇所の背景ハイライトを取り除く。検索と置換ウィンドウが閉じられた
        /// 時などに、外部から呼び出してハイライトを消すためにも公開している。</summary>
        public void ClearHighlight()
        {
            if (currentHighlights.Count > 0)
            {
                runWithoutDirtyMarking(() =>
                {
                    foreach (var range in currentHighlights)
                    {
                        try { range.ApplyPropertyValue(TextElement.BackgroundProperty, null); }
                        catch { /* 対象がすでに存在しなくなっていても問題ない */ }
                    }
                });
            }
            currentHighlights.Clear();
            lastHighlightAllTerm = null;
        }

        /// <summary>
        /// 文書が別のファイルの内容へ丸ごと入れ替わったことを通知する。それまでのハイライト
        /// 一覧は、破棄された古い文書を指したままの無効な参照になるため、（プロパティを
        /// 触ろうとせず）追跡情報だけを単純にクリアする。これを呼ばずにいると、ファイル切り替え
        /// 後も「直前のすべて検索と同じ条件だから」という誤判定で、新しいファイルでの
        /// ハイライトがスキップされてしまう。
        /// </summary>
        public void OnDocumentReplaced()
        {
            currentHighlights.Clear();
            lastHighlightAllTerm = null;
            lastFoundMatch = null;
        }

        /// <summary>
        /// 現在の検索条件が、直前の「すべて検索」の条件と同じかどうかを調べる。同じであれば、
        /// 次を検索/前を検索の際に既存の全件ハイライトを残したままにする。
        /// </summary>
        private bool ShouldPreserveAllHighlights(string term, bool caseSensitive, bool useRegex)
        {
            return currentHighlights.Count > 0 &&
                   lastHighlightAllTerm == term &&
                   lastHighlightAllCaseSensitive == caseSensitive &&
                   lastHighlightAllUseRegex == useRegex;
        }

        /// <summary>
        /// 指定範囲に背景ハイライトを付ける（既存のハイライトはクリアしない。複数件を同時に
        /// 強調表示する場合は、呼び出し側が先にClearHighlight()を呼んでおくこと）。
        /// RichTextBoxの標準の選択ハイライトは、コントロールがキーボードフォーカスを持っていない
        /// 間（検索と置換ウィンドウを操作している間など）は薄く表示されてしまうため、フォーカスの
        /// 有無に関わらず確実に見えるよう、選択ではなくRunの背景色を直接変更する方式にしている。
        /// </summary>
        private void AddHighlight(TextRange range)
        {
            runWithoutDirtyMarking(() => range.ApplyPropertyValue(TextElement.BackgroundProperty, MatchHighlightBrush));
            currentHighlights.Add(range);
        }

        /// <summary>1件だけを強調表示する（既存のハイライトはクリアする）。</summary>
        private void ApplyHighlight(TextRange range)
        {
            ClearHighlight();
            AddHighlight(range);
        }

        /// <summary>
        /// 現在のファイル内のすべての一致箇所を同時に強調表示する（「すべて検索」ボタン用）。
        /// </summary>
        /// <param name="term">検索語。</param>
        /// <param name="caseSensitive">大文字・小文字を区別するか。</param>
        /// <param name="useRegex">正規表現として扱うか。</param>
        /// <returns>見つかったすべての一致箇所（ライブなTextRange。呼び出し側で一覧表示やジャンプに使える）。</returns>
        public List<TextRange> HighlightAllMatchesInCurrentFile(string term, bool caseSensitive, bool useRegex)
        {
            ClearHighlight();
            var results = new List<TextRange>();
            if (isSourceMode() || string.IsNullOrEmpty(term)) return results;

            TextPointer pos = editor.Document.ContentStart;
            int guard = 0;
            while (guard++ < 5000)
            {
                TextRange found = FindTextFrom(pos, term, caseSensitive, useRegex);
                if (found == null) break;
                AddHighlight(found);
                results.Add(found);

                // found.Endが何らかの理由でposより前へ戻ってしまう（または進まない）場合、
                // 同じ箇所を繰り返し見つけ続けてループが実質止まってしまい、それより後ろに
                // ある一致箇所（別の見出しなど）に到達できなくなる。必ず前へ進むことを
                // 保証する安全策として、進んでいなければ1文字分だけ強制的に前進させる。
                TextPointer next = found.End;
                if (next == null || next.CompareTo(pos) <= 0)
                {
                    next = pos.GetPositionAtOffset(1);
                    if (next == null || next.CompareTo(pos) <= 0) break;
                }
                pos = next;
            }

            lastHighlightAllTerm = term;
            lastHighlightAllCaseSensitive = caseSensitive;
            lastHighlightAllUseRegex = useRegex;
            markOutlineMatches?.Invoke(results);
            return results;
        }

        /// <summary>指定範囲を選択・スクロール表示する（結果一覧の項目クリックでのジャンプに使う）。</summary>
        /// <param name="range">対象の範囲。</param>
        public void SelectAndScrollTo(TextRange range)
        {
            editor.Selection.Select(range.Start, range.End);
            scrollParagraphToTop(range.Start.Paragraph ?? editor.Document.Blocks.FirstBlock as Paragraph);
            editor.CaretPosition = range.End;
            editor.Focus();
        }

        public bool FindNextInCurrentFile(string term, bool caseSensitive, bool useRegex)
        {
            if (isSourceMode() || string.IsNullOrEmpty(term)) return false;

            // lastFoundMatchがあれば、その終端から続ける。CaretPosition/Selectionだけに頼らない
            // のは、「前を検索」から「次を検索」へ切り替えた直後など、キャレット位置の設定が
            // 検索方向の切り替えとかみ合わず、1回目だけ正しく進まないことがあったため。
            TextPointer startFrom = lastFoundMatch?.End ?? editor.CaretPosition;
            TextRange found = FindTextFrom(startFrom, term, caseSensitive, useRegex)
                            ?? FindTextFrom(editor.Document.ContentStart, term, caseSensitive, useRegex);
            if (found == null) return false;

            editor.Selection.Select(found.Start, found.End);
            if (!ShouldPreserveAllHighlights(term, caseSensitive, useRegex)) ApplyHighlight(found);
            scrollParagraphToTop(found.Start.Paragraph ?? editor.Document.Blocks.FirstBlock as Paragraph);
            editor.CaretPosition = found.End;
            lastFoundMatch = found;
            editor.Focus();
            return true;
        }

        /// <summary>
        /// FindNextInCurrentFileと同様だが、見つからなければ文書の先頭へ折り返さない
        /// （フォルダ全体での「次を検索」の1件ずつの歩みで使う。折り返してしまうと、
        /// 「このファイルにはもう次の一致箇所がない」という判定ができなくなるため）。
        /// </summary>
        public bool FindNextInCurrentFileNoWrap(string term, bool caseSensitive, bool useRegex)
        {
            if (isSourceMode() || string.IsNullOrEmpty(term)) return false;

            TextRange found = FindTextFrom(editor.CaretPosition, term, caseSensitive, useRegex);
            if (found == null) return false;

            editor.Selection.Select(found.Start, found.End);
            if (!ShouldPreserveAllHighlights(term, caseSensitive, useRegex)) ApplyHighlight(found);
            scrollParagraphToTop(found.Start.Paragraph ?? editor.Document.Blocks.FirstBlock as Paragraph);
            editor.CaretPosition = found.End;
            editor.Focus();
            return true;
        }

        private TextRange FindTextFrom(TextPointer start, string term, bool caseSensitive, bool useRegex)
        {
            Regex regex = null;
            if (useRegex)
            {
                try { regex = new Regex(term, caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase); }
                catch (ArgumentException) { return null; }
            }
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            TextPointer navigator = start;
            while (navigator != null)
            {
                if (navigator.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
                {
                    string runText = navigator.GetTextInRun(LogicalDirection.Forward);
                    if (!string.IsNullOrEmpty(runText))
                    {
                        int idx, len;
                        if (useRegex)
                        {
                            var m = regex.Match(runText);
                            if (m.Success) { idx = m.Index; len = m.Length; }
                            else { idx = -1; len = 0; }
                        }
                        else
                        {
                            idx = runText.IndexOf(term, comparison);
                            len = term.Length;
                        }

                        if (idx >= 0)
                        {
                            TextPointer matchStart = navigator.GetPositionAtOffset(idx);
                            TextPointer matchEnd = matchStart?.GetPositionAtOffset(len);
                            if (matchStart != null && matchEnd != null)
                                return new TextRange(matchStart, matchEnd);
                        }
                    }
                }
                navigator = navigator.GetNextContextPosition(LogicalDirection.Forward);
            }
            return null;
        }

        /// <summary>
        /// startより手前で、一致箇所を後ろ向き（文書の先頭方向）に探す。FindTextFromの
        /// 逆方向版で、「前を検索」の実装に使う。1つのRun区間内では、その区間の中で
        /// startに最も近い（＝一番後ろにある）一致箇所を選ぶ。
        /// </summary>
        private TextRange FindTextBackwardFrom(TextPointer start, string term, bool caseSensitive, bool useRegex)
        {
            Regex regex = null;
            if (useRegex)
            {
                try { regex = new Regex(term, caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase); }
                catch (ArgumentException) { return null; }
            }
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            TextPointer navigator = start;
            while (navigator != null)
            {
                if (navigator.GetPointerContext(LogicalDirection.Backward) == TextPointerContext.Text)
                {
                    string runText = navigator.GetTextInRun(LogicalDirection.Backward);
                    if (!string.IsNullOrEmpty(runText))
                    {
                        int idx, len;
                        if (useRegex)
                        {
                            var matches = regex.Matches(runText);
                            if (matches.Count > 0)
                            {
                                var m = matches[matches.Count - 1];
                                idx = m.Index; len = m.Length;
                            }
                            else { idx = -1; len = 0; }
                        }
                        else
                        {
                            idx = runText.LastIndexOf(term, comparison);
                            len = term.Length;
                        }

                        if (idx >= 0)
                        {
                            TextPointer runStart = navigator.GetPositionAtOffset(-runText.Length);
                            TextPointer matchStart = runStart?.GetPositionAtOffset(idx);
                            TextPointer matchEnd = matchStart?.GetPositionAtOffset(len);
                            if (matchStart != null && matchEnd != null)
                                return new TextRange(matchStart, matchEnd);
                        }
                    }
                }
                navigator = navigator.GetNextContextPosition(LogicalDirection.Backward);
            }
            return null;
        }

        /// <summary>
        /// キャレット（または選択範囲の先頭）より前で、次に一致する箇所を選択・スクロール
        /// 表示する。見つからなければ文書の末尾から探し直す（折り返し検索）。
        /// </summary>
        public bool FindPreviousInCurrentFile(string term, bool caseSensitive, bool useRegex)
        {
            if (isSourceMode() || string.IsNullOrEmpty(term)) return false;

            TextPointer startFrom = lastFoundMatch?.Start ?? editor.CaretPosition;
            TextRange found = FindTextBackwardFrom(startFrom, term, caseSensitive, useRegex)
                            ?? FindTextBackwardFrom(editor.Document.ContentEnd, term, caseSensitive, useRegex);
            if (found == null) return false;

            editor.Selection.Select(found.Start, found.End);
            if (!ShouldPreserveAllHighlights(term, caseSensitive, useRegex)) ApplyHighlight(found);
            scrollParagraphToTop(found.Start.Paragraph ?? editor.Document.Blocks.FirstBlock as Paragraph);
            editor.CaretPosition = found.Start; // 次回の「前を検索」がこの一致箇所より前から続けられるようにする
            lastFoundMatch = found;
            editor.Focus();
            return true;
        }

        /// <summary>FindPreviousInCurrentFileと同様だが、見つからなければ文書の末尾へ折り返さない
        /// （フォルダ全体での「前を検索」で使う）。</summary>
        public bool FindPreviousInCurrentFileNoWrap(string term, bool caseSensitive, bool useRegex)
        {
            if (isSourceMode() || string.IsNullOrEmpty(term)) return false;

            TextPointer from = !editor.Selection.IsEmpty ? editor.Selection.Start : editor.CaretPosition;
            TextRange found = FindTextBackwardFrom(from, term, caseSensitive, useRegex);
            if (found == null) return false;

            editor.Selection.Select(found.Start, found.End);
            if (!ShouldPreserveAllHighlights(term, caseSensitive, useRegex)) ApplyHighlight(found);
            scrollParagraphToTop(found.Start.Paragraph ?? editor.Document.Blocks.FirstBlock as Paragraph);
            editor.CaretPosition = found.Start;
            editor.Focus();
            return true;
        }

        /// <summary>キャレットを文書の末尾へ移動する（フォルダ全体での「前を検索」で、新しく
        /// 開いたファイルの末尾から後ろ向き検索を始めるために使う）。</summary>
        public void MoveCaretToDocumentEnd()
        {
            if (isSourceMode()) return;
            editor.CaretPosition = editor.Document.ContentEnd;
            editor.Selection.Select(editor.Document.ContentEnd, editor.Document.ContentEnd);
        }

        // ---- 現在のファイルを対象にした、1件ずつ確認しながらの置換（ライブハイライト付き）
        // ---- 現在選択されているテキストが「これから置換される一致箇所」そのものになる。

        /// <summary>次の一致箇所を探して選択・表示する。fromSelectionEndがtrueなら現在の
        /// 選択範囲の直後から再開し（セッションの途中）、falseならキャレット位置から開始する
        /// （新規セッション開始時）。</summary>
        public bool StepFindNext(string term, bool caseSensitive, bool useRegex, bool fromSelectionEnd)
        {
            TextPointer from = (fromSelectionEnd && !editor.Selection.IsEmpty) ? editor.Selection.End : editor.CaretPosition;
            TextRange found = FindTextFrom(from, term, caseSensitive, useRegex);
            if (found == null) return false;

            editor.Selection.Select(found.Start, found.End);
            ApplyHighlight(found);
            scrollParagraphToTop(found.Start.Paragraph ?? editor.Document.Blocks.FirstBlock as Paragraph);
            editor.Focus();
            return true;
        }

        /// <summary>現在選択中の一致箇所を置換し、次の一致箇所を探す。</summary>
        public bool StepReplaceAndFindNext(string term, string replacement, bool caseSensitive, bool useRegex)
        {
            currentHighlights.Clear(); // 置換対象の範囲は消えるため、古い参照は捨てておく
            if (!editor.Selection.IsEmpty)
            {
                originalTextTracker.Invalidate(editor.Selection.Start);
                runAsProgrammaticChange(() => editor.Selection.Text = replacement ?? "");
                refreshOutline();
            }
            return StepFindNext(term, caseSensitive, useRegex, fromSelectionEnd: true);
        }

        /// <summary>現在選択中の一致箇所を置換せずスキップし、次の一致箇所を探す。</summary>
        public bool StepSkipAndFindNext(string term, bool caseSensitive, bool useRegex)
        {
            return StepFindNext(term, caseSensitive, useRegex, fromSelectionEnd: true);
        }

        /// <summary>現在選択中の一致箇所と、残りすべての一致箇所を、確認なしで一気に置換する。</summary>
        public int StepReplaceAllRemaining(string term, string replacement, bool caseSensitive, bool useRegex)
        {
            currentHighlights.Clear();
            int count = 0;
            runAsProgrammaticChange(() =>
            {
                if (!editor.Selection.IsEmpty)
                {
                    originalTextTracker.Invalidate(editor.Selection.Start);
                    editor.Selection.Text = replacement ?? "";
                    count++;
                }
                while (StepFindNext(term, caseSensitive, useRegex, fromSelectionEnd: true))
                {
                    originalTextTracker.Invalidate(editor.Selection.Start);
                    editor.Selection.Text = replacement ?? "";
                    count++;
                }
            });
            refreshOutline();
            return count;
        }

        /// <summary>現在のファイル内のすべての一致箇所を一度に置換する（MarkDownへ書き出してから
        /// 置換し、再度解析し直す方式）。</summary>
        public int ReplaceAllInCurrentFile(string term, string replacement, bool caseSensitive, bool useRegex)
        {
            if (string.IsNullOrEmpty(term)) return 0;
            currentHighlights.Clear(); // 文書全体を再構築するため、既存のハイライト参照は無効になる

            if (isSourceMode())
            {
                int srcCount = CountOccurrences(sourceEditor.Text, term, caseSensitive, useRegex);
                if (srcCount > 0) sourceEditor.Text = ReplaceAllText(sourceEditor.Text, term, replacement, caseSensitive, useRegex);
                return srcCount;
            }

            string md = converter.DocumentToMarkdown(editor.Document);
            int count = CountOccurrences(md, term, caseSensitive, useRegex);
            if (count == 0) return 0;

            string replaced = ReplaceAllText(md, term, replacement, caseSensitive, useRegex);
            runAsProgrammaticChange(() => converter.MarkdownToDocument(replaced, editor.Document));
            refreshOutline();
            return count;
        }

        // ======================================================================
        //  フォルダ全体を対象にした検索・置換
        // ======================================================================

        private List<string> GetAllMarkdownFilesInRoot()
        {
            var result = new List<string>();
            string root = getLoadedFolderRootPath();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return result;
            try
            {
                result.AddRange(Directory.GetFiles(root, "*.md", SearchOption.AllDirectories));
                result.AddRange(Directory.GetFiles(root, "*.markdown", SearchOption.AllDirectories));
            }
            catch
            {
                // アクセスできないフォルダなどは無視する
            }
            return result;
        }

        /// <summary>読み込んでいるフォルダ内のすべてのMarkDownファイルを対象に、一致箇所を探す。</summary>
        public List<(string, int)> FindAllInFolder(string term, bool caseSensitive, bool useRegex)
        {
            var results = new List<(string, int)>();
            if (string.IsNullOrEmpty(term)) return results;

            foreach (var file in GetAllMarkdownFilesInRoot())
            {
                string content = getCurrentContentForFile(file) ?? SafeReadFile(file);
                if (content == null) continue;
                int count = CountOccurrences(content, term, caseSensitive, useRegex);
                if (count > 0) results.Add((file, count));
            }
            return results;
        }

        /// <summary>読み込んでいるフォルダ内のすべてのMarkDownファイルを対象に、一括で置換する
        /// （変更されたファイルは保留中の編集として記憶され、保存するまでディスクには
        /// 書き出されない）。</summary>
        public List<(string, int)> ReplaceAllInFolder(string term, string replacement, bool caseSensitive, bool useRegex)
        {
            var results = new List<(string, int)>();
            if (string.IsNullOrEmpty(term)) return results;

            foreach (var file in GetAllMarkdownFilesInRoot())
            {
                string content = getCurrentContentForFile(file) ?? SafeReadFile(file);
                if (content == null) continue;

                int count = CountOccurrences(content, term, caseSensitive, useRegex);
                if (count == 0) continue;

                string replaced = ReplaceAllText(content, term, replacement, caseSensitive, useRegex);
                SetFileContentForReplace(file, replaced);
                results.Add((file, count));
            }
            return results;
        }

        /// <summary>ファイルを読み込み、その改行コードを検出・記憶する。</summary>
        private string SafeReadFile(string path)
        {
            try
            {
                string content = File.ReadAllText(path, Encoding.UTF8);
                lineEndingTracker.DetectAndRemember(path, content);
                return content;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>フォルダ内検索結果からファイルを開く（保留中の編集があればそちらを優先する）。</summary>
        public void OpenFileForFindReplace(string path)
        {
            openFile(path);
        }

        // ---- 1件ずつ確認する置換セッションで使う基本操作 ----

        /// <summary>現在のファイルのライブなMarkDown内容を取得する（FindReplaceWindow用）。</summary>
        public string GetCurrentFileContent()
        {
            return isSourceMode() ? sourceEditor.Text : converter.DocumentToMarkdown(editor.Document);
        }

        /// <summary>現在のファイルの内容を置き換える（正規表現での一括置換結果を適用する際などに使う）。</summary>
        public void SetCurrentFileContent(string newContent)
        {
            currentHighlights.Clear();
            if (isSourceMode())
            {
                sourceEditor.Text = newContent;
            }
            else
            {
                runAsProgrammaticChange(() => converter.MarkdownToDocument(newContent, editor.Document));
                refreshOutline();
            }
        }

        /// <summary>読み込んでいるフォルダ内のMarkDownファイル一覧を取得する（FindReplaceWindowの
        /// フォルダ範囲検索用）。</summary>
        public List<string> GetFolderFiles()
        {
            return GetAllMarkdownFilesInRoot();
        }

        /// <summary>ファイルの内容を取得する（編集中のエディタ、保留中の編集、ディスク上の
        /// いずれかを優先順位に従って返す）。</summary>
        public string GetFileContentForReplace(string path)
        {
            return getCurrentContentForFile(path) ?? SafeReadFile(path);
        }

        /// <summary>ファイルへ新しい内容を反映する（現在開いているファイルならライブエディタへ
        /// 直接、そうでなければ保留中の編集として記憶する）。</summary>
        public void SetFileContentForReplace(string path, string newContent)
        {
            setFileContentForReplaceImpl(path, newContent);
        }
    }
}
