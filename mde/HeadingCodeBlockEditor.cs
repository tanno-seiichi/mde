// HeadingCodeBlockEditor.cs
//
// mde (MarkDown インラインエディタ) の一部。
// 見出しとコードブロックの編集を担当するクラス。段落から見出し/コードブロックへの変換、
// Enterキーでの挙動（見出しは通常段落へ抜ける、コードブロックは行内改行）、
// コードブロック内でのTab/Shift+Tabによるインデント調整を扱う。

using System;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace mde
{
    /// <summary>
    /// 見出し・コードブロックの編集機能一式。実際のスタイル適用は静的な BlockStyles に委譲する。
    /// </summary>
    public class HeadingCodeBlockEditor
    {
        private readonly RichTextBox m_editor;
        private readonly OriginalTextTracker m_originalTextTracker;
        private readonly Action<Action> m_runAsProgrammaticChange;

        /// <summary>
        /// HeadingCodeBlockEditorを構築する。
        /// </summary>
        /// <param name="a_editor">編集対象のRichTextBox。</param>
        /// <param name="a_originalTextTracker">「元テキスト保持」の追跡役。</param>
        /// <param name="a_runAsProgrammaticChange">処理を「プログラムによる変更」として実行するdelegate。</param>
        public HeadingCodeBlockEditor(RichTextBox a_editor, OriginalTextTracker a_originalTextTracker, Action<Action> a_runAsProgrammaticChange)
        {
            this.m_editor = a_editor;
            this.m_originalTextTracker = a_originalTextTracker;
            this.m_runAsProgrammaticChange = a_runAsProgrammaticChange;
        }

        /// <summary>段落を見出しに変換する。</summary>
        /// <param name="a_p">変換する段落。先頭の"#"（1〜6個）＋半角スペース1つだけを取り除き、
        /// それ以降に既にあった内容（記述済みの行の先頭に後から"#"を書き足した場合など）は
        /// そのまま見出しの内容として引き継ぐ。</param>
        /// <param name="a_level">見出しレベル（1〜6）。</param>
        public void ConvertParagraphToHeading(Paragraph a_p, int a_level)
        {
            DebugLogger.Log($"ConvertParagraphToHeading: 呼び出し level={a_level}");
            // 見出しへの変換直後にIME入力を始めると、箇条書きの時と同じ「変換候補ポップアップが
            // 固まって入力できなくなる」症状が実機で確認されたことがある。ImeCaretMoveHelper
            // （Dispatcher.BeginInvokeによる1テンポ遅延）は使わず、1段目の箇条書きと同じ
            // 「同期的に書き換えてその場でUpdateLayout・ClearFocus/Focusを行う」パターンにする。
            m_runAsProgrammaticChange(() =>
            {
                // 変換のきっかけとなった"#"（a_level個）＋半角スペース1つの部分だけを段落の
                // 先頭から取り除く。それ以降に既にあった内容（記述済みの行の先頭に後から"#"を
                // 書き足した場合の、続きの文字列）は、書式ごとそのまま残る。
                TextPointer markerEnd = a_p.ContentStart.GetPositionAtOffset(a_level + 1) ?? a_p.ContentEnd;
                new TextRange(a_p.ContentStart, markerEnd).Text = "";
                BlockStyles.ApplyHeadingStyle(a_p, a_level);
                m_editor.CaretPosition = a_p.ContentStart;
            });
            m_editor.UpdateLayout();
            Keyboard.ClearFocus();
            m_editor.Focus();
        }

        /// <summary>右クリックメニューから見出しレベルを変更する（本文に戻す場合も含む）。
        /// スタイル変更はTextChangedを発生させないため、明示的に「元テキスト保持」の記憶を破棄する。</summary>
        /// <param name="a_p">対象の段落。</param>
        /// <param name="a_level">見出しレベル（0で本文）。</param>
        public void ChangeHeadingLevel(Paragraph a_p, int a_level)
        {
            m_originalTextTracker.InvalidateForBlock(a_p);
            m_runAsProgrammaticChange(() => BlockStyles.ApplyHeadingStyle(a_p, a_level));
        }

        /// <summary>見出し内でのEnterキー処理。見出しの続きにはならず、その後ろに新しい通常の
        /// 段落を作ってそちらへキャレットを移す。</summary>
        /// <param name="a_headingPara">現在の見出し段落。</param>
        public void HandleHeadingEnter(Paragraph a_headingPara)
        {
            DebugLogger.Log("HandleHeadingEnter: 呼び出し");
            // ConvertParagraphToHeadingと同じ理由で、ImeCaretMoveHelper経由の
            // Dispatcher.BeginInvoke遅延は使わず、1段目の箇条書きと同じ同期処理にする。
            m_runAsProgrammaticChange(() =>
            {
                var newPara = new Paragraph();
                m_editor.Document.Blocks.InsertAfter(a_headingPara, newPara);
                m_editor.CaretPosition = newPara.ContentStart;
            });
            m_editor.UpdateLayout();
            Keyboard.ClearFocus();
            m_editor.Focus();
        }

        /// <summary>段落をコードブロックに変換し、その後ろに新しい通常の段落を追加する。</summary>
        /// <param name="a_p">変換する段落。</param>
        /// <param name="a_language">```の直後に書かれた言語名。</param>
        public void ConvertParagraphToCodeBlock(Paragraph a_p, string a_language = "")
        {
            // 見出しと同じ理由で、ImeCaretMoveHelper経由のDispatcher.BeginInvoke遅延は使わず、
            // 1段目の箇条書きと同じ同期処理にする。
            m_runAsProgrammaticChange(() =>
            {
                a_p.Inlines.Clear();
                BlockStyles.ApplyCodeBlockStyle(a_p, a_language);

                var trailingPara = new Paragraph();
                m_editor.Document.Blocks.InsertAfter(a_p, trailingPara);

                m_editor.CaretPosition = a_p.ContentStart;
            });
            m_editor.UpdateLayout();
            Keyboard.ClearFocus();
            m_editor.Focus();
        }

        /// <summary>段落を水平線（&lt;hr&gt;相当）に変換し、その後ろに新しい通常の段落を追加する
        /// （水平線自体には文字を打てないため、キャレットは新しい段落側に置く）。
        /// 他の変換メソッドと同じIME対策の同期処理パターンに従う。</summary>
        /// <param name="a_p">変換する段落。</param>
        public void ConvertParagraphToHorizontalRule(Paragraph a_p)
        {
            m_runAsProgrammaticChange(() =>
            {
                a_p.Inlines.Clear();
                BlockStyles.ApplyHorizontalRuleStyle(a_p);

                var trailingPara = new Paragraph();
                m_editor.Document.Blocks.InsertAfter(a_p, trailingPara);

                m_editor.CaretPosition = trailingPara.ContentStart;
            });
            m_editor.UpdateLayout();
            Keyboard.ClearFocus();
            m_editor.Focus();
        }

        /// <summary>コードブロック内でのEnterキー処理（新しい段落を作らず、行内改行を挿入する）。</summary>
        public void InsertLineBreakAtCaret()
        {
            m_editor.CaretPosition = m_editor.CaretPosition.InsertLineBreak();
        }

        /// <summary>
        /// コードブロック内でのShift+Tab処理。現在の行の先頭にあるタブ1つ、または最大4個までの
        /// 半角スペースを取り除く。現在行および段落の内容を超えて読み書きしないよう範囲を制限している。
        /// </summary>
        /// <param name="a_p">対象のコードブロック段落。</param>
        public void OutdentCodeLine(Paragraph a_p)
        {
            m_originalTextTracker.InvalidateForBlock(a_p);
            var caret = m_editor.CaretPosition;
            var lineStart = caret.GetLineStartPosition(0) ?? a_p.ContentStart;

            TextPointer upperBound = a_p.ContentEnd;
            var nextLineStart = caret.GetLineStartPosition(1);
            if (null != nextLineStart &&
                nextLineStart.CompareTo(upperBound) < 0)
                upperBound = nextLineStart;

            var probe = lineStart.GetPositionAtOffset(4);
            if (null == probe ||
                probe.CompareTo(upperBound) > 0) probe = upperBound;
            if (probe.CompareTo(lineStart) < 0)
            {
                probe = lineStart;
            }

            string prefix = new TextRange(lineStart, probe).Text;

            int removeCount = 0;
            if (prefix.StartsWith("\t"))
            {
                removeCount = 1;
            }
            else
            {
                while (removeCount < prefix.Length &&
                       removeCount < 4 &&
                       ' ' == prefix[removeCount])
                    removeCount++;
            }
            if (0 == removeCount)
            {
                return;
            }

            var removeEnd = lineStart.GetPositionAtOffset(removeCount);
            if (null == removeEnd)
            {
                return;
            }

            m_runAsProgrammaticChange(() =>
            {
                // caretはライブなTextPointerであり、これより前の内容が削除されると自動的に
                // 再アンカーされるため、削除後に手動でオフセット計算をする必要はない。
                new TextRange(lineStart, removeEnd).Text = "";
            });

            m_editor.CaretPosition = caret;
            m_editor.Focus();
        }
    }
}
