// HeadingCodeBlockEditor.cs
//
// mde (MarkDown インラインエディタ) の一部。
// 見出しとコードブロックの編集を担当するクラス。段落から見出し/コードブロックへの変換、
// Enterキーでの挙動（見出しは通常段落へ抜ける、コードブロックは行内改行）、
// コードブロック内でのTab/Shift+Tabによるインデント調整を扱う。

using System;
using System.Windows.Controls;
using System.Windows.Documents;

namespace mde
{
    /// <summary>
    /// 見出し・コードブロックの編集機能一式。実際のスタイル適用は静的な BlockStyles に委譲する。
    /// </summary>
    public class HeadingCodeBlockEditor
    {
        private readonly RichTextBox editor;
        private readonly OriginalTextTracker originalTextTracker;
        private readonly Action<Action> runAsProgrammaticChange;

        /// <summary>
        /// HeadingCodeBlockEditorを構築する。
        /// </summary>
        /// <param name="editor">編集対象のRichTextBox。</param>
        /// <param name="originalTextTracker">「元テキスト保持」の追跡役。</param>
        /// <param name="runAsProgrammaticChange">処理を「プログラムによる変更」として実行するdelegate。</param>
        public HeadingCodeBlockEditor(RichTextBox editor, OriginalTextTracker originalTextTracker, Action<Action> runAsProgrammaticChange)
        {
            this.editor = editor;
            this.originalTextTracker = originalTextTracker;
            this.runAsProgrammaticChange = runAsProgrammaticChange;
        }

        /// <summary>段落を見出しに変換する。</summary>
        /// <param name="p">変換する段落。</param>
        /// <param name="level">見出しレベル（1〜6）。</param>
        public void ConvertParagraphToHeading(Paragraph p, int level)
        {
            runAsProgrammaticChange(() =>
            {
                p.Inlines.Clear();
                BlockStyles.ApplyHeadingStyle(p, level);
                editor.CaretPosition = p.ContentStart;
            });
        }

        /// <summary>右クリックメニューから見出しレベルを変更する（本文に戻す場合も含む）。
        /// スタイル変更はTextChangedを発生させないため、明示的に「元テキスト保持」の記憶を破棄する。</summary>
        /// <param name="p">対象の段落。</param>
        /// <param name="level">見出しレベル（0で本文）。</param>
        public void ChangeHeadingLevel(Paragraph p, int level)
        {
            originalTextTracker.InvalidateForBlock(p);
            runAsProgrammaticChange(() => BlockStyles.ApplyHeadingStyle(p, level));
        }

        /// <summary>見出し内でのEnterキー処理。見出しの続きにはならず、その後ろに新しい通常の
        /// 段落を作ってそちらへキャレットを移す。</summary>
        /// <param name="headingPara">現在の見出し段落。</param>
        public void HandleHeadingEnter(Paragraph headingPara)
        {
            runAsProgrammaticChange(() =>
            {
                var newPara = new Paragraph();
                editor.Document.Blocks.InsertAfter(headingPara, newPara);
                editor.CaretPosition = newPara.ContentStart;
            });
        }

        /// <summary>段落をコードブロックに変換し、その後ろに新しい通常の段落を追加する。</summary>
        /// <param name="p">変換する段落。</param>
        /// <param name="language">```の直後に書かれた言語名。</param>
        public void ConvertParagraphToCodeBlock(Paragraph p, string language = "")
        {
            runAsProgrammaticChange(() =>
            {
                p.Inlines.Clear();
                BlockStyles.ApplyCodeBlockStyle(p, language);

                var trailingPara = new Paragraph();
                editor.Document.Blocks.InsertAfter(p, trailingPara);

                editor.CaretPosition = p.ContentStart;
            });
            editor.Focus();
        }

        /// <summary>コードブロック内でのEnterキー処理（新しい段落を作らず、行内改行を挿入する）。</summary>
        public void InsertLineBreakAtCaret()
        {
            editor.CaretPosition = editor.CaretPosition.InsertLineBreak();
        }

        /// <summary>
        /// コードブロック内でのShift+Tab処理。現在の行の先頭にあるタブ1つ、または最大4個までの
        /// 半角スペースを取り除く。現在行および段落の内容を超えて読み書きしないよう範囲を制限している。
        /// </summary>
        /// <param name="p">対象のコードブロック段落。</param>
        public void OutdentCodeLine(Paragraph p)
        {
            originalTextTracker.InvalidateForBlock(p);
            var caret = editor.CaretPosition;
            var lineStart = caret.GetLineStartPosition(0) ?? p.ContentStart;

            TextPointer upperBound = p.ContentEnd;
            var nextLineStart = caret.GetLineStartPosition(1);
            if (nextLineStart != null && nextLineStart.CompareTo(upperBound) < 0)
                upperBound = nextLineStart;

            var probe = lineStart.GetPositionAtOffset(4);
            if (probe == null || probe.CompareTo(upperBound) > 0) probe = upperBound;
            if (probe.CompareTo(lineStart) < 0) probe = lineStart;

            string prefix = new TextRange(lineStart, probe).Text;

            int removeCount = 0;
            if (prefix.StartsWith("\t"))
            {
                removeCount = 1;
            }
            else
            {
                while (removeCount < prefix.Length && removeCount < 4 && prefix[removeCount] == ' ')
                    removeCount++;
            }
            if (removeCount == 0) return;

            var removeEnd = lineStart.GetPositionAtOffset(removeCount);
            if (removeEnd == null) return;

            runAsProgrammaticChange(() =>
            {
                // caretはライブなTextPointerであり、これより前の内容が削除されると自動的に
                // 再アンカーされるため、削除後に手動でオフセット計算をする必要はない。
                new TextRange(lineStart, removeEnd).Text = "";
            });

            editor.CaretPosition = caret;
            editor.Focus();
        }
    }
}
