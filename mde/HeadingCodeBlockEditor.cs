// HeadingCodeBlockEditor.cs
//
// mde (Markdown インラインエディタ) の一部。
// 見出しとコードブロックの編集を担当するクラス。段落から見出し/コードブロックへの変換、
// Enterキーでの挙動（見出しは通常段落へ抜ける、コードブロックは行内改行）、
// コードブロック内でのTab/Shift+Tabによるインデント調整を扱う。

using System;
using System.Collections.Generic;
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

        /// <summary>指定した位置から、実際の文字数で数えてa_charCount文字分だけ先へ進んだ位置を
        /// 返す。ListEditor.AdvanceByCharCountと同じ考え方の見出し側の複製。TextPointer.
        /// GetPositionAtOffsetの「オフセット」はWPF内部のシンボリックな単位であり、実際の
        /// 文字数と1対1に対応しないことがあるため、1シンボリック単位ずつ進めながら、実際に
        /// 消費した文字数（各ステップのTextRange.Textの長さ）だけを積算する、より確実な方法
        /// にする。</summary>
        private static TextPointer AdvanceByCharCount(TextPointer a_start, int a_charCount)
        {
            TextPointer pos = a_start;
            int consumed = 0;
            while (consumed < a_charCount)
            {
                TextPointer next = pos.GetPositionAtOffset(1, LogicalDirection.Forward);
                if (null == next)
                {
                    break;
                }
                consumed += new TextRange(pos, next).Text.Length;
                pos = next;
            }
            return pos;
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
                //
                // GetPositionAtOffsetのオフセットはWPF内部のシンボリックな単位であり実際の
                // 文字数と1対1に対応しないため、単純なオフセット指定ではマーカー末尾のスペースが
                // 消しきれず残ることがある（ListEditor.ConvertParagraphToListItemと同種の問題）。
                // AdvanceByCharCountで実際の文字数を数え、確実にa_level+1文字分だけ取り除く。
                TextPointer markerEnd = AdvanceByCharCount(a_p.ContentStart, a_level + 1);
                DebugLogger.Log(
                    "ConvertParagraphToHeading: マーカー除去前 text=[" +
                    new TextRange(a_p.ContentStart, a_p.ContentEnd).Text.Replace(" ", "[SP]").Replace("\u00A0", "[NBSP]").Replace("\r", "[CR]").Replace("\n", "[LF]") + "]");
                new TextRange(a_p.ContentStart, markerEnd).Text = "";
                DebugLogger.Log(
                    "ConvertParagraphToHeading: マーカー除去後 text=[" +
                    new TextRange(a_p.ContentStart, a_p.ContentEnd).Text.Replace(" ", "[SP]").Replace("\u00A0", "[NBSP]").Replace("\r", "[CR]").Replace("\n", "[LF]") + "]");
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

        /// <summary>見出し段落の中身をBackSpaceで空にした状態から、もう一度BackSpaceが
        /// 押された時の処理。見出しは（箇条書きのListItemとは異なり）WPF的にはただの
        /// Paragraphに書式を適用しているだけなので、標準のBackSpace動作では見出しの
        /// 書式が外れず、空になった見出しがそのまま残ってしまう。段落を削除する代わりに、
        /// 見出しの書式だけを本文へ戻す。</summary>
        /// <param name="a_p">対象の見出し段落（既に空であること）。</param>
        public void RevertEmptyHeadingOnBackspace(Paragraph a_p)
        {
            DebugLogger.Log("RevertEmptyHeadingOnBackspace: 呼び出し");
            // 他の見出し関連の変換と同じ理由で、ImeCaretMoveHelper経由のDispatcher.BeginInvoke
            // 遅延は使わず、同期的に書き換えてその場でUpdateLayout・ClearFocus/Focusを行う
            // パターンにする。
            m_originalTextTracker.InvalidateForBlock(a_p);
            m_runAsProgrammaticChange(() =>
            {
                BlockStyles.ApplyHeadingStyle(a_p, 0);
                m_editor.CaretPosition = a_p.ContentStart;
            });
            m_editor.UpdateLayout();
            Keyboard.ClearFocus();
            m_editor.Focus();
            DebugLogger.Log($"RevertEmptyHeadingOnBackspace: 完了 Tag={a_p.Tag ?? "null"}");
        }

        /// <summary>指定した位置から、実際の文字数で数えてa_charCount文字分だけ後ろへ戻った
        /// 位置を返す。ListEditor.AdvanceByCharCountの逆向き版。TextPointer.GetPositionAtOffset
        /// の「オフセット」はWPF内部のシンボリックな単位であり、実際の文字数と1対1に対応
        /// しないことがあるため、1シンボリック単位ずつ戻りながら、実際に消費した文字数
        /// （各ステップのTextRange.Textの長さ）だけを積算する、より確実な方法にする。</summary>
        private static TextPointer RetreatByCharCount(TextPointer a_end, int a_charCount)
        {
            TextPointer pos = a_end;
            int consumed = 0;
            while (consumed < a_charCount)
            {
                TextPointer prev = pos.GetPositionAtOffset(-1, LogicalDirection.Backward);
                if (null == prev)
                {
                    break;
                }
                consumed += new TextRange(prev, pos).Text.Length;
                pos = prev;
            }
            return pos;
        }

        /// <summary>見出し段落の末尾（キャレット位置）から、実際の文字1つ分だけを自前で
        /// 削除する。IME入力で組み立てた見出し段落は、内部的な部分確定の繰り返しでRun
        /// （区画）が細かく分かれることがあり、その最後の1文字に対してはWPF標準のBackSpace
        /// コマンドが無反応になる（EditorTextChangedすら発生しない）場合があるため、標準の
        /// BackSpaceには委ねず、この処理で確実に取り除く。</summary>
        /// <param name="a_p">対象の見出し段落。キャレットは段落末尾にあり、中身は空でないこと。</param>
        public void DeleteLastCharInHeading(Paragraph a_p)
        {
            DebugLogger.Log("DeleteLastCharInHeading: 呼び出し");
            // 書式は変えないため元テキスト追跡の破棄は不要だが、他の見出し関連処理と
            // 同じ理由で、同期的に書き換えてその場でUpdateLayout・ClearFocus/Focusを行う
            // パターンにする。
            m_runAsProgrammaticChange(() =>
            {
                TextPointer deleteFrom = RetreatByCharCount(a_p.ContentEnd, 1);
                new TextRange(deleteFrom, a_p.ContentEnd).Text = "";
                m_editor.CaretPosition = a_p.ContentEnd;
            });
            m_editor.UpdateLayout();
            Keyboard.ClearFocus();
            m_editor.Focus();
            DebugLogger.Log(
                "DeleteLastCharInHeading: 完了 text=[" +
                new TextRange(a_p.ContentStart, a_p.ContentEnd).Text.Replace(" ", "[SP]").Replace("\u00A0", "[NBSP]").Replace("\r", "[CR]").Replace("\n", "[LF]") + "]");
        }

        /// <summary>見出し内でのEnterキー処理。見出しの文字列の先頭（1文字目より左）に
        /// キャレットがある場合は、見出し自体には触れず、見出し行の「上」に新しい空行を
        /// 挿入する（一般的なエディタで行頭でEnterを押した時の挙動と同じ）。それ以外の位置
        /// （見出しの途中・末尾）でEnterを押した場合は、これまで通り見出しの続きにはならず、
        /// その後ろに新しい通常の段落を作ってそちらへキャレットを移す（見出し自体を分割する
        /// ことはしない）。</summary>
        /// <param name="a_headingPara">現在の見出し段落。</param>
        public void HandleHeadingEnter(Paragraph a_headingPara)
        {
            DebugLogger.Log("HandleHeadingEnter: 呼び出し");

            // 見出しは（箇条書きのListItemとは異なり）行頭にマーカー記号を持たないため、
            // ListEditor側で問題になっている「TextRangeがマーカー記号を巻き込んでしまう」
            // 不具合の対象にならない。既存のBackSpace末尾判定（isCaretAtEndFlg。上の
            // MainWindow.EditorPreviewKeyDown参照）と同じ、a_headingPara.ContentStart起点の
            // TextRangeによる判定で問題ない。
            bool isCaretAtStartFlg = m_editor.Selection.IsEmpty &&
                0 == new TextRange(a_headingPara.ContentStart, m_editor.CaretPosition).Text.Length;
            DebugLogger.Log($"HandleHeadingEnter: isCaretAtStartFlg={isCaretAtStartFlg}");

            // ConvertParagraphToHeadingと同じ理由で、ImeCaretMoveHelper経由の
            // Dispatcher.BeginInvoke遅延は使わず、1段目の箇条書きと同じ同期処理にする。
            m_runAsProgrammaticChange(() =>
            {
                if (isCaretAtStartFlg)
                {
                    // 見出し自身のInlines・書式には一切触れず、新しい空段落を見出しの「前」に
                    // 挿入するだけにする。キャレットは見出し自身の先頭に置いたままにする
                    // （見出し行が1行分下にずれるだけで、見出しの内容・キャレットの相対位置は
                    // 変わらない）。キャレットの移動先が、既にレイアウト済みの見出し段落
                    // （新しく作った段落ではない）であるため、IME対策のImeCaretMoveHelperは
                    // 不要（新しく作った未レイアウトの段落へキャレットを合わせた直後にIME入力を
                    // 始めた場合の不具合が、そもそも起こり得ない状況のため）。
                    var newPara = new Paragraph();
                    m_editor.Document.Blocks.InsertBefore(a_headingPara, newPara);
                    m_editor.CaretPosition = a_headingPara.ContentStart;
                }
                else
                {
                    var newPara = new Paragraph();
                    m_editor.Document.Blocks.InsertAfter(a_headingPara, newPara);
                    m_editor.CaretPosition = newPara.ContentStart;
                }
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

        /// <summary>コードブロック内でのEnterキー処理（新しい段落を作らず、行内改行を挿入する）。
        /// 箇条書き項目内でのShift+Enter、表のセル内でのEnter/Shift+Enterでも共通して使われる
        /// （MainWindow.EditorPreviewKeyDown参照）。
        /// 行内改行（LineBreak）の挿入後、IME（TSF）側が新しいキャレット位置をすぐには認識できず、
        /// 直後の入力が改行の1つ手前に入ってしまう競合状態があるため、キャレット移動を
        /// ImeCaretMoveHelper.ScheduleCaretMoveでキー入力処理から1テンポ切り離して行う
        /// （調査の経緯はDEVELOPMENT_LOG.md参照）。この対策は発生確率を下げるものであり、
        /// 100%の解消を保証するものではない。
        /// </summary>
        public void InsertLineBreakAtCaret()
        {
            ImeCaretMoveHelper.ScheduleCaretMove(
                m_editor,
                () =>
                {
                    m_runAsProgrammaticChange(() =>
                    {
                        m_editor.CaretPosition = m_editor.CaretPosition.InsertLineBreak();
                    });
                });
        }

        /// <summary>
        /// 箇条書き項目・表のセル内でのShift+Enter/Enter処理、および表の直後の段落での
        /// Enter処理。上のInsertLineBreakAtCaretのコメントにある通り、行内改行（LineBreak
        /// 要素）はキャレット位置をどう工夫して設定・通知してもIME側との競合を解消しきれない
        /// ため、こちらではLineBreakを一切使わず、Enter等が押された位置で段落（Paragraph）
        /// そのものを2つに分割する（見た目には1つの段落がそのまま改行しているように見せる
        /// ため、分割した両方の段落のMarginを揃えて0にする）。
        /// キャレットより後ろにあった内容（書式を含む）は、ListEditor.SplitInlinesAtCaret・
        /// CloneRunForSplit（項目のEnterでの分割に使われている、実績のあるロジック）を共有して
        /// Inline単位で新しい段落へ移す。TextPointerのオフセットベースでテキストを抜き出す方式は
        /// 箇条書きマーカー記号を巻き込んでしまう既知の不具合（ListEditor.GetOwnListItemText等の
        /// コメント参照）があるため、あえて使わない。
        /// 表の直後の段落（親がFlowDocumentで、直前のBlockが表）でも同じ分割方式を使うのは、
        /// この位置でEnterキーの処理をWPF標準の動作に委ねると、キャレットが実際には表の外
        /// （この段落）にあるにもかかわらず、WPFが表への行追加として処理してしまい、表の
        /// 罫線が壊れる不具合があったため（MainWindow.EditorPreviewKeyDown参照）。
        /// </summary>
        /// <param name="a_para">分割対象の段落（現在キャレットがある段落）。親がListItem・
        /// TableCellのいずれか、または親がFlowDocumentで直前のBlockが表（Table）である場合
        /// のみ分割する。それ以外（コードブロック等）は何もしない（呼び出し側は、そのような
        /// 場合には代わりに従来通りInsertLineBreakAtCaretを呼ぶこと）。</param>
        public void InsertParagraphSplitAtCaret(Paragraph a_para)
        {
            if (null == a_para)
            {
                return;
            }
            object parent = a_para.Parent;
            bool afterTableFlg = parent is FlowDocument && a_para.PreviousBlock is Table;
            if (!(parent is ListItem) && !(parent is TableCell) && !afterTableFlg)
            {
                DebugLogger.Log(
                    $"InsertParagraphSplitAtCaret: 想定外の親（{parent?.GetType().Name ?? "null"}）だったため、" +
                    "何もしなかった");
                return;
            }

            m_runAsProgrammaticChange(() =>
            {
                TextPointer caret = m_editor.CaretPosition;
                // 分割元の段落が持つ見た目のプロパティ（表のセルならMargin=0・LineHeight=NaN・
                // KeepTogether=true・文字揃え。箇条書き項目・表の直後の段落ならMargin=0）を、
                // 新しい段落にもそのまま引き継ぐ。ハードコードせず分割元から読み取ることで、
                // 呼び出し元（ListItem・TableCell・表の直後の段落のいずれか）に関わらず常に
                // 整合する。TagはTableCellの「明示的な左揃え」の印
                // （MarkdownConverter.ALIGN_LEFT_EXPLICIT_TAG）を想定しているが、これは
                // Markdown書き出し時にセルの最初の段落からしか参照されないため、2つめ以降の
                // 段落にも同じ値が付くこと自体は無害（書き出し結果には影響しない）。
                var newPara = new Paragraph
                {
                    Margin = a_para.Margin,
                    LineHeight = a_para.LineHeight,
                    KeepTogether = a_para.KeepTogether,
                    TextAlignment = a_para.TextAlignment,
                    Tag = a_para.Tag
                };

                foreach (Inline movedInline in ListEditor.SplitInlinesAtCaret(a_para.Inlines, caret))
                {
                    newPara.Inlines.Add(movedInline);
                }

                if (parent is ListItem li)
                {
                    li.Blocks.InsertAfter(a_para, newPara);
                }
                else if (parent is TableCell cell)
                {
                    cell.Blocks.InsertAfter(a_para, newPara);
                }
                else if (parent is FlowDocument doc)
                {
                    doc.Blocks.InsertAfter(a_para, newPara);
                }

                m_editor.CaretPosition = newPara.ContentStart;
                DebugLogger.Log("InsertParagraphSplitAtCaret: 段落を分割し、新しい段落の先頭へキャレットを移動した");
            });
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
