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
        private readonly RichTextBox m_editor;
        private readonly TextBox m_sourceEditor;
        private readonly MarkdownConverter m_converter;
        private readonly OriginalTextTracker m_originalTextTracker;
        private readonly LineEndingTracker m_lineEndingTracker;
        private readonly Func<bool> m_isSourceMode;
        private readonly Action<Action> m_runAsProgrammaticChange;
        private readonly Action m_refreshOutline;
        private readonly Action<Paragraph> m_scrollParagraphToTop;
        private readonly Func<string> m_getLoadedFolderRootPath;
        private readonly Func<string, string> m_getCurrentContentForFile;
        private readonly Action<string, string> m_setFileContentForReplaceImpl;
        private readonly Action<string> m_openFile;
        private readonly Action<Action> m_runWithoutDirtyMarking;
        private readonly Action<List<TextRange>> m_markOutlineMatches;

        private static readonly System.Windows.Media.Brush MATCH_HIGHLIGHT_BRUSH =
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xE1, 0x66));

        /// <summary>直前に強調表示した一致箇所（フォーカスの有無に関わらず見えるよう、選択
        /// ハイライトではなくRunの背景色を直接変更する方式にしている）。次に検索する前、または
        /// 見えなくなる前にこの背景色を消しておく必要がある。「すべて検索」では複数件を
        /// 同時に強調表示するため、単一ではなく一覧として保持する。</summary>
        private readonly List<TextRange> m_currentHighlights = new List<TextRange>();

        /// <summary>直前にFindNext/FindPreviousInCurrentFileで見つかった一致箇所。次を検索/前を
        /// 検索の検索開始位置の基準にする（CaretPosition/Selectionだけに頼ると、検索方向を
        /// 切り替えた直後に正しく動かないことがあったため）。</summary>
        private TextRange m_lastFoundMatch;

        /// <summary>直前に「すべて検索」で強調表示した際の検索条件。次を検索/前を検索が
        /// 同じ条件で呼ばれた場合、既存の全件ハイライトをクリアせずに残すために使う。</summary>
        private string m_lastHighlightAllTerm;
        private bool m_lastHighlightAllCaseSensitiveFlg;
        private bool m_lastHighlightAllUseRegexFlg;

        /// <summary>
        /// SearchReplaceServiceを構築する。
        /// </summary>
        /// <param name="a_editor">MarkDownモードのRichTextBox。</param>
        /// <param name="a_sourceEditor">ソースモードのTextBox。</param>
        /// <param name="a_converter">MarkDown⇔内部構造の変換クラス。</param>
        /// <param name="a_originalTextTracker">「元テキスト保持」の追跡役。</param>
        /// <param name="a_lineEndingTracker">改行コードの検出・記憶役。</param>
        /// <param name="a_isSourceMode">現在ソースモードかどうかを返すdelegate。</param>
        /// <param name="a_runAsProgrammaticChange">処理を「プログラムによる変更」として実行するdelegate。</param>
        /// <param name="a_refreshOutline">アウトラインペインの再構築を依頼するdelegate。</param>
        /// <param name="a_scrollParagraphToTop">指定した段落までスクロールするdelegate。</param>
        /// <param name="a_getLoadedFolderRootPath">現在読み込んでいるフォルダのルートパスを返すdelegate。</param>
        /// <param name="a_getCurrentContentForFile">ファイルの「今の内容」（編集中エディタ／保留中の編集／ディスク上）を解決するdelegate。</param>
        /// <param name="a_setFileContentForReplaceImpl">ファイルへ新しい内容を反映するdelegate。</param>
        /// <param name="a_openFile">ファイルを開くdelegate（フォルダ内検索結果をダブルクリックした時に使う）。</param>
        public SearchReplaceService(
            RichTextBox a_editor,
            TextBox a_sourceEditor,
            MarkdownConverter a_converter,
            OriginalTextTracker a_originalTextTracker,
            LineEndingTracker a_lineEndingTracker,
            Func<bool> a_isSourceMode,
            Action<Action> a_runAsProgrammaticChange,
            Action a_refreshOutline,
            Action<Paragraph> a_scrollParagraphToTop,
            Func<string> a_getLoadedFolderRootPath,
            Func<string, string> a_getCurrentContentForFile,
            Action<string, string> a_setFileContentForReplaceImpl,
            Action<string> a_openFile,
            Action<Action> a_runWithoutDirtyMarking,
            Action<List<TextRange>> a_markOutlineMatches)
        {
            this.m_editor = a_editor;
            this.m_sourceEditor = a_sourceEditor;
            this.m_converter = a_converter;
            this.m_originalTextTracker = a_originalTextTracker;
            this.m_lineEndingTracker = a_lineEndingTracker;
            this.m_isSourceMode = a_isSourceMode;
            this.m_runAsProgrammaticChange = a_runAsProgrammaticChange;
            this.m_refreshOutline = a_refreshOutline;
            this.m_scrollParagraphToTop = a_scrollParagraphToTop;
            this.m_getLoadedFolderRootPath = a_getLoadedFolderRootPath;
            this.m_getCurrentContentForFile = a_getCurrentContentForFile;
            this.m_setFileContentForReplaceImpl = a_setFileContentForReplaceImpl;
            this.m_openFile = a_openFile;
            this.m_runWithoutDirtyMarking = a_runWithoutDirtyMarking;
            this.m_markOutlineMatches = a_markOutlineMatches;
        }

        // ======================================================================
        //  文字列レベルの基本操作
        // ======================================================================

        /// <summary>文字列中に検索語が何回マッチするかを数える。</summary>
        /// <param name="a_text">対象の文字列。</param>
        /// <param name="a_term">検索する文字列。</param>
        /// <param name="a_caseSensitiveFlg">大文字・小文字を区別するかどうか。</param>
        /// <param name="a_useRegexFlg">正規表現として扱うかどうか。</param>
        /// <returns>一致した件数。</returns>
        public int CountOccurrences(string a_text, string a_term, bool a_caseSensitiveFlg, bool a_useRegexFlg)
        {
            if (string.IsNullOrEmpty(a_text) || string.IsNullOrEmpty(a_term)) return 0;
            if (a_useRegexFlg)
            {
                try
                {
                    var options = a_caseSensitiveFlg ? RegexOptions.None : RegexOptions.IgnoreCase;
                    return Regex.Matches(a_text, a_term, options).Count;
                }
                catch (ArgumentException)
                {
                    return 0;
                }
            }
            var comparison = a_caseSensitiveFlg ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            int count = 0, idx = 0;
            while ((idx = a_text.IndexOf(a_term, idx, comparison)) >= 0)
            {
                count++;
                idx += a_term.Length;
            }
            return count;
        }

        private string ReplaceAllText(string a_text, string a_term, string a_replacement, bool a_caseSensitiveFlg, bool a_useRegexFlg)
        {
            if (string.IsNullOrEmpty(a_term)) return a_text;
            a_replacement = a_replacement ?? "";
            if (a_useRegexFlg)
            {
                try
                {
                    var options = a_caseSensitiveFlg ? RegexOptions.None : RegexOptions.IgnoreCase;
                    return Regex.Replace(a_text, a_term, a_replacement, options);
                }
                catch (ArgumentException)
                {
                    return a_text;
                }
            }
            if (a_caseSensitiveFlg) return a_text.Replace(a_term, a_replacement);

            var sb = new StringBuilder();
            int idx = 0;
            while (true)
            {
                int found = a_text.IndexOf(a_term, idx, StringComparison.OrdinalIgnoreCase);
                if (found < 0) { sb.Append(a_text, idx, a_text.Length - idx); break; }
                sb.Append(a_text, idx, found - idx);
                sb.Append(a_replacement);
                idx = found + a_term.Length;
            }
            return sb.ToString();
        }

        /// <summary>
        /// fromIndex以降で、テキスト中の次の一致を探す。1件ずつ確認する置換セッション
        /// （現在のファイル・フォルダ全体のどちらでも）が、正規表現の有無によらず統一的に
        /// 使えるように用意されている。
        /// </summary>
        /// <param name="a_text">対象の文字列。</param>
        /// <param name="a_term">検索する文字列。</param>
        /// <param name="a_caseSensitiveFlg">大文字・小文字を区別するかどうか。</param>
        /// <param name="a_useRegexFlg">正規表現として扱うかどうか。</param>
        /// <param name="a_fromIndex">検索を開始する文字位置。</param>
        public (int index, int length)? FindNextMatchInText(string a_text, string a_term, bool a_caseSensitiveFlg, bool a_useRegexFlg, int a_fromIndex)
        {
            if (string.IsNullOrEmpty(a_text) || string.IsNullOrEmpty(a_term) || a_fromIndex > a_text.Length) return null;
            if (a_useRegexFlg)
            {
                try
                {
                    var options = a_caseSensitiveFlg ? RegexOptions.None : RegexOptions.IgnoreCase;
                    var regex = new Regex(a_term, options);
                    var m = regex.Match(a_text, a_fromIndex);
                    if (!m.Success) return null;
                    return (m.Index, m.Length);
                }
                catch (ArgumentException)
                {
                    return null;
                }
            }
            var comparison = a_caseSensitiveFlg ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            int idx = a_text.IndexOf(a_term, a_fromIndex, comparison);
            return idx < 0 ? ((int, int)?)null : (idx, a_term.Length);
        }

        /// <summary>指定位置の一致箇所ちょうど1件だけを置換した結果を返す。</summary>
        /// <param name="a_text">対象の文字列。</param>
        /// <param name="a_term">検索する文字列。</param>
        /// <param name="a_replacement">置換後の文字列。</param>
        /// <param name="a_caseSensitiveFlg">大文字・小文字を区別するかどうか。</param>
        /// <param name="a_useRegexFlg">正規表現として扱うかどうか。</param>
        /// <param name="a_index">一致箇所の開始位置。</param>
        /// <param name="a_length">一致箇所の長さ。</param>
        /// <returns>置換後のテキスト。</returns>
        public string ReplaceOneMatch(string a_text, string a_term, string a_replacement, bool a_caseSensitiveFlg, bool a_useRegexFlg, int a_index, int a_length)
        {
            a_replacement = a_replacement ?? "";
            if (a_useRegexFlg)
            {
                try
                {
                    var options = a_caseSensitiveFlg ? RegexOptions.None : RegexOptions.IgnoreCase;
                    var regex = new Regex(a_term, options);
                    return regex.Replace(a_text, a_replacement, 1, a_index);
                }
                catch (ArgumentException)
                {
                    return a_text;
                }
            }
            return a_text.Substring(0, a_index) + a_replacement + a_text.Substring(a_index + a_length);
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
            if (m_currentHighlights.Count > 0)
            {
                m_runWithoutDirtyMarking(() =>
                {
                    foreach (var range in m_currentHighlights)
                    {
                        try { range.ApplyPropertyValue(TextElement.BackgroundProperty, null); }
                        catch { /* 対象がすでに存在しなくなっていても問題ない */ }
                    }
                });
            }
            m_currentHighlights.Clear();
            m_lastHighlightAllTerm = null;
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
            m_currentHighlights.Clear();
            m_lastHighlightAllTerm = null;
            m_lastFoundMatch = null;
        }

        /// <summary>
        /// 現在の検索条件が、直前の「すべて検索」の条件と同じかどうかを調べる。同じであれば、
        /// 次を検索/前を検索の際に既存の全件ハイライトを残したままにする。
        /// </summary>
        /// <param name="a_term">検索する文字列。</param>
        /// <param name="a_caseSensitiveFlg">大文字・小文字を区別するかどうか。</param>
        /// <param name="a_useRegexFlg">正規表現として扱うかどうか。</param>
        /// <returns>既存のハイライトを残したままにすべきであればtrue。</returns>
        private bool ShouldPreserveAllHighlights(string a_term, bool a_caseSensitiveFlg, bool a_useRegexFlg)
        {
            return m_currentHighlights.Count > 0 &&
                   m_lastHighlightAllTerm == a_term &&
                   m_lastHighlightAllCaseSensitiveFlg == a_caseSensitiveFlg &&
                   m_lastHighlightAllUseRegexFlg == a_useRegexFlg;
        }

        /// <summary>
        /// 指定範囲に背景ハイライトを付ける（既存のハイライトはクリアしない。複数件を同時に
        /// 強調表示する場合は、呼び出し側が先にClearHighlight()を呼んでおくこと）。
        /// RichTextBoxの標準の選択ハイライトは、コントロールがキーボードフォーカスを持っていない
        /// 間（検索と置換ウィンドウを操作している間など）は薄く表示されてしまうため、フォーカスの
        /// 有無に関わらず確実に見えるよう、選択ではなくRunの背景色を直接変更する方式にしている。
        /// </summary>
        /// <param name="a_range">対象の範囲。</param>
        private void AddHighlight(TextRange a_range)
        {
            m_runWithoutDirtyMarking(() => a_range.ApplyPropertyValue(TextElement.BackgroundProperty, MATCH_HIGHLIGHT_BRUSH));
            m_currentHighlights.Add(a_range);
        }

        /// <summary>1件だけを強調表示する（既存のハイライトはクリアする）。</summary>
        /// <param name="a_range">対象の範囲。</param>
        private void ApplyHighlight(TextRange a_range)
        {
            ClearHighlight();
            AddHighlight(a_range);
        }

        /// <summary>
        /// 現在のファイル内のすべての一致箇所を同時に強調表示する（「すべて検索」ボタン用）。
        /// </summary>
        /// <param name="a_term">検索語。</param>
        /// <param name="a_caseSensitiveFlg">大文字・小文字を区別するか。</param>
        /// <param name="a_useRegexFlg">正規表現として扱うか。</param>
        /// <returns>見つかったすべての一致箇所（ライブなTextRange。呼び出し側で一覧表示やジャンプに使える）。</returns>
        public List<TextRange> HighlightAllMatchesInCurrentFile(string a_term, bool a_caseSensitiveFlg, bool a_useRegexFlg)
        {
            ClearHighlight();
            var results = new List<TextRange>();
            if (m_isSourceMode() || string.IsNullOrEmpty(a_term)) return results;

            TextPointer pos = m_editor.Document.ContentStart;
            int guard = 0;
            while (guard++ < 5000)
            {
                TextRange found = FindTextFrom(pos, a_term, a_caseSensitiveFlg, a_useRegexFlg);
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

            m_lastHighlightAllTerm = a_term;
            m_lastHighlightAllCaseSensitiveFlg = a_caseSensitiveFlg;
            m_lastHighlightAllUseRegexFlg = a_useRegexFlg;
            m_markOutlineMatches?.Invoke(results);
            return results;
        }

        /// <summary>指定範囲を選択・スクロール表示する（結果一覧の項目クリックでのジャンプに使う）。</summary>
        /// <param name="a_range">対象の範囲。</param>
        public void SelectAndScrollTo(TextRange a_range)
        {
            m_editor.Selection.Select(a_range.Start, a_range.End);
            m_scrollParagraphToTop(a_range.Start.Paragraph ?? m_editor.Document.Blocks.FirstBlock as Paragraph);
            m_editor.CaretPosition = a_range.End;
            m_editor.Focus();
        }

        public bool FindNextInCurrentFile(string a_term, bool a_caseSensitiveFlg, bool a_useRegexFlg)
        {
            if (m_isSourceMode() || string.IsNullOrEmpty(a_term)) return false;

            // lastFoundMatchがあれば、その終端から続ける。CaretPosition/Selectionだけに頼らない
            // のは、「前を検索」から「次を検索」へ切り替えた直後など、キャレット位置の設定が
            // 検索方向の切り替えとかみ合わず、1回目だけ正しく進まないことがあったため。
            TextPointer startFrom = m_lastFoundMatch?.End ?? m_editor.CaretPosition;
            TextRange found = FindTextFrom(startFrom, a_term, a_caseSensitiveFlg, a_useRegexFlg)
                            ?? FindTextFrom(m_editor.Document.ContentStart, a_term, a_caseSensitiveFlg, a_useRegexFlg);
            if (found == null) return false;

            m_editor.Selection.Select(found.Start, found.End);
            if (!ShouldPreserveAllHighlights(a_term, a_caseSensitiveFlg, a_useRegexFlg)) ApplyHighlight(found);
            m_scrollParagraphToTop(found.Start.Paragraph ?? m_editor.Document.Blocks.FirstBlock as Paragraph);
            m_editor.CaretPosition = found.End;
            m_lastFoundMatch = found;
            m_editor.Focus();
            return true;
        }

        /// <summary>
        /// FindNextInCurrentFileと同様だが、見つからなければ文書の先頭へ折り返さない
        /// （フォルダ全体での「次を検索」の1件ずつの歩みで使う。折り返してしまうと、
        /// 「このファイルにはもう次の一致箇所がない」という判定ができなくなるため）。
        /// </summary>
        /// <param name="a_term">検索する文字列。</param>
        /// <param name="a_caseSensitiveFlg">大文字・小文字を区別するかどうか。</param>
        /// <param name="a_useRegexFlg">正規表現として扱うかどうか。</param>
        /// <returns>見つかればtrue。</returns>
        public bool FindNextInCurrentFileNoWrap(string a_term, bool a_caseSensitiveFlg, bool a_useRegexFlg)
        {
            if (m_isSourceMode() || string.IsNullOrEmpty(a_term)) return false;

            TextRange found = FindTextFrom(m_editor.CaretPosition, a_term, a_caseSensitiveFlg, a_useRegexFlg);
            if (found == null) return false;

            m_editor.Selection.Select(found.Start, found.End);
            if (!ShouldPreserveAllHighlights(a_term, a_caseSensitiveFlg, a_useRegexFlg)) ApplyHighlight(found);
            m_scrollParagraphToTop(found.Start.Paragraph ?? m_editor.Document.Blocks.FirstBlock as Paragraph);
            m_editor.CaretPosition = found.End;
            m_editor.Focus();
            return true;
        }

        private TextRange FindTextFrom(TextPointer a_start, string a_term, bool a_caseSensitiveFlg, bool a_useRegexFlg)
        {
            Regex regex = null;
            if (a_useRegexFlg)
            {
                try { regex = new Regex(a_term, a_caseSensitiveFlg ? RegexOptions.None : RegexOptions.IgnoreCase); }
                catch (ArgumentException) { return null; }
            }
            var comparison = a_caseSensitiveFlg ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            TextPointer navigator = a_start;
            while (navigator != null)
            {
                if (navigator.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
                {
                    string runText = navigator.GetTextInRun(LogicalDirection.Forward);
                    if (!string.IsNullOrEmpty(runText))
                    {
                        int idx, len;
                        if (a_useRegexFlg)
                        {
                            var m = regex.Match(runText);
                            if (m.Success) { idx = m.Index; len = m.Length; }
                            else { idx = -1; len = 0; }
                        }
                        else
                        {
                            idx = runText.IndexOf(a_term, comparison);
                            len = a_term.Length;
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
        /// <param name="a_start">範囲の開始位置。</param>
        /// <param name="a_term">検索する文字列。</param>
        /// <param name="a_caseSensitiveFlg">大文字・小文字を区別するかどうか。</param>
        /// <param name="a_useRegexFlg">正規表現として扱うかどうか。</param>
        /// <returns>見つかった範囲。見つからなければnull。</returns>
        private TextRange FindTextBackwardFrom(TextPointer a_start, string a_term, bool a_caseSensitiveFlg, bool a_useRegexFlg)
        {
            Regex regex = null;
            if (a_useRegexFlg)
            {
                try { regex = new Regex(a_term, a_caseSensitiveFlg ? RegexOptions.None : RegexOptions.IgnoreCase); }
                catch (ArgumentException) { return null; }
            }
            var comparison = a_caseSensitiveFlg ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            TextPointer navigator = a_start;
            while (navigator != null)
            {
                if (navigator.GetPointerContext(LogicalDirection.Backward) == TextPointerContext.Text)
                {
                    string runText = navigator.GetTextInRun(LogicalDirection.Backward);
                    if (!string.IsNullOrEmpty(runText))
                    {
                        int idx, len;
                        if (a_useRegexFlg)
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
                            idx = runText.LastIndexOf(a_term, comparison);
                            len = a_term.Length;
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
        /// <param name="a_term">検索する文字列。</param>
        /// <param name="a_caseSensitiveFlg">大文字・小文字を区別するかどうか。</param>
        /// <param name="a_useRegexFlg">正規表現として扱うかどうか。</param>
        /// <returns>見つかればtrue。</returns>
        public bool FindPreviousInCurrentFile(string a_term, bool a_caseSensitiveFlg, bool a_useRegexFlg)
        {
            if (m_isSourceMode() || string.IsNullOrEmpty(a_term)) return false;

            TextPointer startFrom = m_lastFoundMatch?.Start ?? m_editor.CaretPosition;
            TextRange found = FindTextBackwardFrom(startFrom, a_term, a_caseSensitiveFlg, a_useRegexFlg)
                            ?? FindTextBackwardFrom(m_editor.Document.ContentEnd, a_term, a_caseSensitiveFlg, a_useRegexFlg);
            if (found == null) return false;

            m_editor.Selection.Select(found.Start, found.End);
            if (!ShouldPreserveAllHighlights(a_term, a_caseSensitiveFlg, a_useRegexFlg)) ApplyHighlight(found);
            m_scrollParagraphToTop(found.Start.Paragraph ?? m_editor.Document.Blocks.FirstBlock as Paragraph);
            m_editor.CaretPosition = found.Start; // 次回の「前を検索」がこの一致箇所より前から続けられるようにする
            m_lastFoundMatch = found;
            m_editor.Focus();
            return true;
        }

        /// <summary>FindPreviousInCurrentFileと同様だが、見つからなければ文書の末尾へ折り返さない
        /// （フォルダ全体での「前を検索」で使う）。</summary>
        /// <param name="a_term">検索する文字列。</param>
        /// <param name="a_caseSensitiveFlg">大文字・小文字を区別するかどうか。</param>
        /// <param name="a_useRegexFlg">正規表現として扱うかどうか。</param>
        /// <returns>見つかればtrue。</returns>
        public bool FindPreviousInCurrentFileNoWrap(string a_term, bool a_caseSensitiveFlg, bool a_useRegexFlg)
        {
            if (m_isSourceMode() || string.IsNullOrEmpty(a_term)) return false;

            TextPointer from = !m_editor.Selection.IsEmpty ? m_editor.Selection.Start : m_editor.CaretPosition;
            TextRange found = FindTextBackwardFrom(from, a_term, a_caseSensitiveFlg, a_useRegexFlg);
            if (found == null) return false;

            m_editor.Selection.Select(found.Start, found.End);
            if (!ShouldPreserveAllHighlights(a_term, a_caseSensitiveFlg, a_useRegexFlg)) ApplyHighlight(found);
            m_scrollParagraphToTop(found.Start.Paragraph ?? m_editor.Document.Blocks.FirstBlock as Paragraph);
            m_editor.CaretPosition = found.Start;
            m_editor.Focus();
            return true;
        }

        /// <summary>キャレットを文書の末尾へ移動する（フォルダ全体での「前を検索」で、新しく
        /// 開いたファイルの末尾から後ろ向き検索を始めるために使う）。</summary>
        public void MoveCaretToDocumentEnd()
        {
            if (m_isSourceMode()) return;
            m_editor.CaretPosition = m_editor.Document.ContentEnd;
            m_editor.Selection.Select(m_editor.Document.ContentEnd, m_editor.Document.ContentEnd);
        }

        // ---- 現在のファイルを対象にした、1件ずつ確認しながらの置換（ライブハイライト付き）
        // ---- 現在選択されているテキストが「これから置換される一致箇所」そのものになる。

        /// <summary>次の一致箇所を探して選択・表示する。fromSelectionEndがtrueなら現在の
        /// 選択範囲の直後から再開し（セッションの途中）、falseならキャレット位置から開始する
        /// （新規セッション開始時）。</summary>
        /// <param name="a_term">検索する文字列。</param>
        /// <param name="a_caseSensitiveFlg">大文字・小文字を区別するかどうか。</param>
        /// <param name="a_useRegexFlg">正規表現として扱うかどうか。</param>
        /// <param name="a_fromSelectionEndFlg">現在の選択範囲の末尾から検索を始めるかどうか。</param>
        /// <returns>見つかればtrue。</returns>
        public bool StepFindNext(string a_term, bool a_caseSensitiveFlg, bool a_useRegexFlg, bool a_fromSelectionEndFlg)
        {
            TextPointer from = (a_fromSelectionEndFlg && !m_editor.Selection.IsEmpty) ? m_editor.Selection.End : m_editor.CaretPosition;
            TextRange found = FindTextFrom(from, a_term, a_caseSensitiveFlg, a_useRegexFlg);
            if (found == null) return false;

            m_editor.Selection.Select(found.Start, found.End);
            ApplyHighlight(found);
            m_scrollParagraphToTop(found.Start.Paragraph ?? m_editor.Document.Blocks.FirstBlock as Paragraph);
            m_editor.Focus();
            return true;
        }

        /// <summary>現在選択中の一致箇所を置換し、次の一致箇所を探す。</summary>
        /// <param name="a_term">検索する文字列。</param>
        /// <param name="a_replacement">置換後の文字列。</param>
        /// <param name="a_caseSensitiveFlg">大文字・小文字を区別するかどうか。</param>
        /// <param name="a_useRegexFlg">正規表現として扱うかどうか。</param>
        /// <returns>次の一致箇所が見つかればtrue。</returns>
        public bool StepReplaceAndFindNext(string a_term, string a_replacement, bool a_caseSensitiveFlg, bool a_useRegexFlg)
        {
            m_currentHighlights.Clear(); // 置換対象の範囲は消えるため、古い参照は捨てておく
            if (!m_editor.Selection.IsEmpty)
            {
                m_originalTextTracker.Invalidate(m_editor.Selection.Start);
                m_runAsProgrammaticChange(() => m_editor.Selection.Text = a_replacement ?? "");
                m_refreshOutline();
            }
            return StepFindNext(a_term, a_caseSensitiveFlg, a_useRegexFlg, a_fromSelectionEndFlg: true);
        }

        /// <summary>現在選択中の一致箇所を置換せずスキップし、次の一致箇所を探す。</summary>
        /// <param name="a_term">検索する文字列。</param>
        /// <param name="a_caseSensitiveFlg">大文字・小文字を区別するかどうか。</param>
        /// <param name="a_useRegexFlg">正規表現として扱うかどうか。</param>
        /// <returns>次の一致箇所が見つかればtrue。</returns>
        public bool StepSkipAndFindNext(string a_term, bool a_caseSensitiveFlg, bool a_useRegexFlg)
        {
            return StepFindNext(a_term, a_caseSensitiveFlg, a_useRegexFlg, a_fromSelectionEndFlg: true);
        }

        /// <summary>現在選択中の一致箇所と、残りすべての一致箇所を、確認なしで一気に置換する。</summary>
        /// <param name="a_term">検索する文字列。</param>
        /// <param name="a_replacement">置換後の文字列。</param>
        /// <param name="a_caseSensitiveFlg">大文字・小文字を区別するかどうか。</param>
        /// <param name="a_useRegexFlg">正規表現として扱うかどうか。</param>
        /// <returns>置換した件数。</returns>
        public int StepReplaceAllRemaining(string a_term, string a_replacement, bool a_caseSensitiveFlg, bool a_useRegexFlg)
        {
            m_currentHighlights.Clear();
            int count = 0;
            m_runAsProgrammaticChange(() =>
            {
                if (!m_editor.Selection.IsEmpty)
                {
                    m_originalTextTracker.Invalidate(m_editor.Selection.Start);
                    m_editor.Selection.Text = a_replacement ?? "";
                    count++;
                }
                while (StepFindNext(a_term, a_caseSensitiveFlg, a_useRegexFlg, a_fromSelectionEndFlg: true))
                {
                    m_originalTextTracker.Invalidate(m_editor.Selection.Start);
                    m_editor.Selection.Text = a_replacement ?? "";
                    count++;
                }
            });
            m_refreshOutline();
            return count;
        }

        /// <summary>現在のファイル内のすべての一致箇所を一度に置換する（MarkDownへ書き出してから
        /// 置換し、再度解析し直す方式）。</summary>
        /// <param name="a_term">検索する文字列。</param>
        /// <param name="a_replacement">置換後の文字列。</param>
        /// <param name="a_caseSensitiveFlg">大文字・小文字を区別するかどうか。</param>
        /// <param name="a_useRegexFlg">正規表現として扱うかどうか。</param>
        /// <returns>置換した件数。</returns>
        public int ReplaceAllInCurrentFile(string a_term, string a_replacement, bool a_caseSensitiveFlg, bool a_useRegexFlg)
        {
            if (string.IsNullOrEmpty(a_term)) return 0;
            m_currentHighlights.Clear(); // 文書全体を再構築するため、既存のハイライト参照は無効になる

            if (m_isSourceMode())
            {
                int srcCount = CountOccurrences(m_sourceEditor.Text, a_term, a_caseSensitiveFlg, a_useRegexFlg);
                if (srcCount > 0) m_sourceEditor.Text = ReplaceAllText(m_sourceEditor.Text, a_term, a_replacement, a_caseSensitiveFlg, a_useRegexFlg);
                return srcCount;
            }

            string md = m_converter.DocumentToMarkdown(m_editor.Document);
            int count = CountOccurrences(md, a_term, a_caseSensitiveFlg, a_useRegexFlg);
            if (count == 0) return 0;

            string replaced = ReplaceAllText(md, a_term, a_replacement, a_caseSensitiveFlg, a_useRegexFlg);
            m_runAsProgrammaticChange(() => m_converter.MarkdownToDocument(replaced, m_editor.Document));
            m_refreshOutline();
            return count;
        }

        // ======================================================================
        //  フォルダ全体を対象にした検索・置換
        // ======================================================================

        private List<string> GetAllMarkdownFilesInRoot()
        {
            var result = new List<string>();
            string root = m_getLoadedFolderRootPath();
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
        /// <param name="a_term">検索する文字列。</param>
        /// <param name="a_caseSensitiveFlg">大文字・小文字を区別するかどうか。</param>
        /// <param name="a_useRegexFlg">正規表現として扱うかどうか。</param>
        public List<(string, int)> FindAllInFolder(string a_term, bool a_caseSensitiveFlg, bool a_useRegexFlg)
        {
            var results = new List<(string, int)>();
            if (string.IsNullOrEmpty(a_term)) return results;

            foreach (var file in GetAllMarkdownFilesInRoot())
            {
                string content = m_getCurrentContentForFile(file) ?? SafeReadFile(file);
                if (content == null) continue;
                int count = CountOccurrences(content, a_term, a_caseSensitiveFlg, a_useRegexFlg);
                if (count > 0) results.Add((file, count));
            }
            return results;
        }

        /// <summary>読み込んでいるフォルダ内のすべてのMarkDownファイルを対象に、一括で置換する
        /// （変更されたファイルは保留中の編集として記憶され、保存するまでディスクには
        /// 書き出されない）。</summary>
        /// <param name="a_term">検索する文字列。</param>
        /// <param name="a_replacement">置換後の文字列。</param>
        /// <param name="a_caseSensitiveFlg">大文字・小文字を区別するかどうか。</param>
        /// <param name="a_useRegexFlg">正規表現として扱うかどうか。</param>
        public List<(string, int)> ReplaceAllInFolder(string a_term, string a_replacement, bool a_caseSensitiveFlg, bool a_useRegexFlg)
        {
            var results = new List<(string, int)>();
            if (string.IsNullOrEmpty(a_term)) return results;

            foreach (var file in GetAllMarkdownFilesInRoot())
            {
                string content = m_getCurrentContentForFile(file) ?? SafeReadFile(file);
                if (content == null) continue;

                int count = CountOccurrences(content, a_term, a_caseSensitiveFlg, a_useRegexFlg);
                if (count == 0) continue;

                string replaced = ReplaceAllText(content, a_term, a_replacement, a_caseSensitiveFlg, a_useRegexFlg);
                SetFileContentForReplace(file, replaced);
                results.Add((file, count));
            }
            return results;
        }

        /// <summary>ファイルを読み込み、その改行コードを検出・記憶する。</summary>
        /// <param name="a_path">対象のファイルパス。</param>
        /// <returns>読み込んだファイルの内容。失敗した場合はnull。</returns>
        private string SafeReadFile(string a_path)
        {
            try
            {
                string content = File.ReadAllText(a_path, Encoding.UTF8);
                m_lineEndingTracker.DetectAndRemember(a_path, content);
                return content;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>フォルダ内検索結果からファイルを開く（保留中の編集があればそちらを優先する）。</summary>
        /// <param name="a_path">対象のファイルパス。</param>
        public void OpenFileForFindReplace(string a_path)
        {
            m_openFile(a_path);
        }

        // ---- 1件ずつ確認する置換セッションで使う基本操作 ----

        /// <summary>現在のファイルのライブなMarkDown内容を取得する（FindReplaceWindow用）。</summary>
        /// <returns>現在のファイルのライブなMarkDown内容。</returns>
        public string GetCurrentFileContent()
        {
            return m_isSourceMode() ? m_sourceEditor.Text : m_converter.DocumentToMarkdown(m_editor.Document);
        }

        /// <summary>現在のファイルの内容を置き換える（正規表現での一括置換結果を適用する際などに使う）。</summary>
        /// <param name="a_newContent">新しい内容。</param>
        public void SetCurrentFileContent(string a_newContent)
        {
            m_currentHighlights.Clear();
            if (m_isSourceMode())
            {
                m_sourceEditor.Text = a_newContent;
            }
            else
            {
                m_runAsProgrammaticChange(() => m_converter.MarkdownToDocument(a_newContent, m_editor.Document));
                m_refreshOutline();
            }
        }

        /// <summary>読み込んでいるフォルダ内のMarkDownファイル一覧を取得する（FindReplaceWindowの
        /// フォルダ範囲検索用）。</summary>
        /// <returns>フォルダ内のMarkDownファイルの一覧。</returns>
        public List<string> GetFolderFiles()
        {
            return GetAllMarkdownFilesInRoot();
        }

        /// <summary>ファイルの内容を取得する（編集中のエディタ、保留中の編集、ディスク上の
        /// いずれかを優先順位に従って返す）。</summary>
        /// <param name="a_path">対象のファイルパス。</param>
        /// <returns>そのファイルの内容。</returns>
        public string GetFileContentForReplace(string a_path)
        {
            return m_getCurrentContentForFile(a_path) ?? SafeReadFile(a_path);
        }

        /// <summary>ファイルへ新しい内容を反映する（現在開いているファイルならライブエディタへ
        /// 直接、そうでなければ保留中の編集として記憶する）。</summary>
        /// <param name="a_path">対象のファイルパス。</param>
        /// <param name="a_newContent">新しい内容。</param>
        public void SetFileContentForReplace(string a_path, string a_newContent)
        {
            m_setFileContentForReplaceImpl(a_path, a_newContent);
        }
    }
}
