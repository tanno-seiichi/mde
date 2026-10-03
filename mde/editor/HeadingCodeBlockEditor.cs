// HeadingCodeBlockEditor.cs
//
// mde (Markdown インラインエディタ) の一部。
// 見出しとコードブロックの編集を担当するクラス。段落から見出し/コードブロックへの変換、
// Enterキーでの挙動（見出しは通常段落へ抜ける、コードブロックは段落分割による続きの行の
// 追加。CodeBlockContinuationInfo参照）、コードブロック内でのTab/Shift+Tabによる
// インデント調整を扱う。

using mde.common;
using mde.logger;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace mde.editor
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

        /// <summary>段落を引用に変換する。マーカー文字列だけを先頭から取り除き、それ以降に
        /// 既にあった内容は書式ごとそのまま引用の内容として引き継ぐ点はConvertParagraphToHeading
        /// と同じ。ただし、引用を抜ける操作をコードブロックと統一した（通常のEnterは常に
        /// 引用の続きの行を増やすだけで、引用から「抜ける」専用の操作は持たない。
        /// InsertQuoteContinuationParagraph・MainWindow.EditorPreviewKeyDown参照）ため、
        /// ConvertParagraphToCodeBlockと同様、変換した直後に後ろへ新しい通常の段落を
        /// 1つ追加しておく。これが、矢印キーやクリックで引用の外へ移動する時の行き先になる。</summary>
        /// <param name="a_p">変換する段落。</param>
        /// <param name="a_markerCharCount">取り除くマーカー文字数（"&gt; "の場合は2）。</param>
        public void ConvertParagraphToQuote(Paragraph a_p, int a_markerCharCount)
        {
            DebugLogger.Log("ConvertParagraphToQuote: 呼び出し");
            // ConvertParagraphToHeadingと同じ理由で、ImeCaretMoveHelper経由の
            // Dispatcher.BeginInvoke遅延は使わず、同期的に書き換えてその場でUpdateLayout・
            // ClearFocus/Focusを行うパターンにする。
            m_runAsProgrammaticChange(() =>
            {
                // 変換のきっかけとなった"> "（a_markerCharCount文字）の部分だけを段落の
                // 先頭から取り除く。ConvertParagraphToHeadingと同じ理由で、単純なオフセット
                // 指定ではなくAdvanceByCharCountで実際の文字数を数える。
                TextPointer markerEnd = AdvanceByCharCount(a_p.ContentStart, a_markerCharCount);
                new TextRange(a_p.ContentStart, markerEnd).Text = "";
                BlockStyles.ApplyQuoteStyle(a_p, true);

                // ConvertParagraphToCodeBlockと同じく、引用から「抜ける」ための行き先として、
                // 後ろに新しい通常の段落を1つ用意しておく。
                var trailingPara = new Paragraph();
                m_editor.Document.Blocks.InsertAfter(a_p, trailingPara);

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

        /// <summary>引用段落の中身をBackSpaceで空にした状態から、もう一度BackSpaceが
        /// 押された時の処理。RevertEmptyHeadingOnBackspaceと全く同じ理由・同じパターンで、
        /// 引用の書式だけを本文へ戻す。</summary>
        /// <param name="a_p">対象の引用段落（既に空であること）。</param>
        public void RevertEmptyQuoteOnBackspace(Paragraph a_p)
        {
            DebugLogger.Log("RevertEmptyQuoteOnBackspace: 呼び出し");
            m_originalTextTracker.InvalidateForBlock(a_p);
            m_runAsProgrammaticChange(() =>
            {
                BlockStyles.ApplyQuoteStyle(a_p, false);
                m_editor.CaretPosition = a_p.ContentStart;
            });
            m_editor.UpdateLayout();
            Keyboard.ClearFocus();
            m_editor.Focus();
            DebugLogger.Log($"RevertEmptyQuoteOnBackspace: 完了 Tag={a_p.Tag ?? "null"}");
        }

        /// <summary>引用の先頭行（QuoteInfo）がBackSpaceで空になった時点で、直後に続きの行
        /// （QuoteContinuationInfo）が残っている場合に呼ばれる。RevertEmptyQuoteOnBackspaceを
        /// そのまま使うと、空になった先頭段落だけが本文に戻り、後ろのQuoteContinuationInfo
        /// 段落が「先頭を示すQuoteInfoの無い、引用の書式だけが残った段落」として取り残されて
        /// しまい、見た目が崩れる（保存時には、本文に戻った空段落は出力されず、取り残された
        /// 続きの行だけが前の内容へ改行だけでつながってしまうため、空行や見た目も再現できない）。
        /// この問題を避けるため、空になった先頭段落を本文へ戻すのではなく削除し、代わりに
        /// 直後の続きの行を新しい先頭行（QuoteInfo）へ格上げする（箇条書き・表のセルとは異なり、
        /// 引用はBrContinuationInfoと同じ「段落分割」方式のため、先頭を1つ後ろへずらすだけで
        /// 残りの続きの行の連なりはそのまま保たれる）。</summary>
        /// <param name="a_emptyQuotePara">空になった引用の先頭段落。</param>
        /// <param name="a_nextContinuationPara">その直後にある、引用の続きの段落
        /// （QuoteContinuationInfoが設定されていること）。</param>
        public void PromoteQuoteContinuationOnEmptyFirstLineBackspace(Paragraph a_emptyQuotePara, Paragraph a_nextContinuationPara)
        {
            DebugLogger.Log("PromoteQuoteContinuationOnEmptyFirstLineBackspace: 呼び出し");
            m_originalTextTracker.InvalidateForBlock(a_emptyQuotePara);
            m_runAsProgrammaticChange(() =>
            {
                double bottomMargin = a_nextContinuationPara.Margin.Bottom;
                // 格上げ前の時点でa_nextContinuationParaが既にグループ最後の行だったかどうかは、
                // 下方向のパディングが付いていたか（>0）で判別できる（ApplyQuoteContinuationStyle
                // のa_isLastLineと対応）。bottomMarginと同じく、既存の状態をそのまま引き継ぐ。
                bool wasLastLineFlg = a_nextContinuationPara.Padding.Bottom > 0;
                BlockStyles.ApplyQuoteStyle(a_nextContinuationPara, true, wasLastLineFlg);
                a_nextContinuationPara.Margin = new Thickness(0, 4, 0, bottomMargin);
                m_editor.Document.Blocks.Remove(a_emptyQuotePara);
                m_editor.CaretPosition = a_nextContinuationPara.ContentStart;
            });
            m_editor.UpdateLayout();
            Keyboard.ClearFocus();
            m_editor.Focus();
            DebugLogger.Log($"PromoteQuoteContinuationOnEmptyFirstLineBackspace: 完了 Tag={a_nextContinuationPara.Tag?.GetType().Name ?? "null"}");
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

        /// <summary>引用段落（先頭行・続きの行のいずれも対象）の末尾（キャレット位置）から、
        /// 実際の文字1つ分だけを自前で削除する。DeleteLastCharInHeadingと全く同じ理由・同じ
        /// パターン（IME合成によるRun分割でWPF標準のBackSpaceが無反応になる問題への対策）。
        /// 引用の2行目以降はQuoteContinuationInfoを持つ別のParagraphとして表現しており
        /// （LineBreakは使わない。QuoteContinuationInfoのコメント参照）、このメソッド自体は
        /// 渡された1つのParagraphの末尾だけを見るので、先頭行・続きの行のどちらであっても
        /// そのまま使える。</summary>
        /// <param name="a_p">対象の引用段落。キャレットは段落末尾にあり、中身は空でないこと。</param>
        public void DeleteLastCharInQuote(Paragraph a_p)
        {
            DebugLogger.Log("DeleteLastCharInQuote: 呼び出し");
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
                "DeleteLastCharInQuote: 完了 text=[" +
                new TextRange(a_p.ContentStart, a_p.ContentEnd).Text.Replace(" ", "[SP]").Replace(" ", "[NBSP]").Replace("\r", "[CR]").Replace("\n", "[LF]") + "]");
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

        /// <summary>
        /// 引用内でEnterが押された時の処理。引用の先頭行・続きの行のどちらで呼ばれても、
        /// 常にこのメソッドが呼ばれる（コードブロックのInsertCodeBlockContinuationParagraphと
        /// 同じく、Enterに特別な「抜ける」機能は持たせず、通常のEnterは常に続きの行を
        /// 増やすだけにする。引用から抜けるには、矢印キーやクリックで
        /// ConvertParagraphToQuoteが用意した後続の通常の段落へ移動する。
        /// MainWindow.EditorPreviewKeyDown参照）。
        /// 以前はShift+Enterの時だけ呼ばれていたが、ユーザーからの指摘を受けて、
        /// コードブロックと操作を統一するため、通常のEnterでも常にこちらを呼ぶように変更した
        /// （以前の「通常のEnterで引用を抜ける」専用処理だったHandleQuoteEnterは削除した）。
        /// 箇条書き項目・表のセル内のShift+Enterと全く同じ理由（LineBreak要素とIMEの組み合わせ
        /// による、キャレット位置がずれる不具合。実機で「Enterで行を増やしても、次の行に
        /// カーソルを移動して入力すると1行目の末尾に入力されてしまう」という形で確認された。
        /// InsertParagraphSplitAtCaretのコメント参照）で、LineBreakは使わず、キャレット位置で
        /// 段落そのものを2つに分割する（ListEditor.SplitInlinesAtCaretで、キャレットより後ろの
        /// 内容を書式ごと新しい段落へ移す、実績のあるロジックを共有する）。
        /// 分割後の2つの段落の間のMarginを0にし、元々a_paraが持っていた（＝このグループの
        /// 最後の行として持っていた）下マージンは、新しくできた方の段落（今後の最後の行に
        /// なる）へ引き継ぐ。これにより、見た目には複数の段落が1つの引用ブロック内の複数行に
        /// しか見えないようにする（BrContinuationInfoの続き段落・MarkdownConverter.
        /// MarkdownToDocumentのbrSegments処理と同じ考え方）。下方向のパディングも、新しくできた
        /// 方の段落（今後の最後の行になる）だけが持つようにし、a_para自身は無くす（コードブロック
        /// のSplitCodeBlockParagraphAtCaretと同じ考え方。行間が不自然に広くならないようにするため
        /// に必要で、これが無いと分割直後、a_paraの下パディングが残ったままになってしまう）。
        /// </summary>
        /// <param name="a_para">分割対象の段落（現在キャレットがある、QuoteInfoまたは
        /// QuoteContinuationInfoが設定された引用の段落）。</param>
        public void InsertQuoteContinuationParagraph(Paragraph a_para)
        {
            DebugLogger.Log("InsertQuoteContinuationParagraph: 呼び出し");
            m_runAsProgrammaticChange(() =>
            {
                TextPointer caret = m_editor.CaretPosition;

                var newPara = new Paragraph();
                // newParaは常にこのグループの新しい最後の行になるため、a_isLastLine=trueで
                // 下方向のパディングを持たせる。
                BlockStyles.ApplyQuoteContinuationStyle(newPara, true);
                // a_paraが分割前に持っていた下マージンを新しい段落（今後の最後の行）へ引き継ぎ、
                // a_para自身の下マージンは0にする（もう最後の行ではなくなるため）。上マージンは
                // どちらも常に0（続きの行には上の余白を持たせない）。
                newPara.Margin = new Thickness(0, 0, 0, a_para.Margin.Bottom);
                a_para.Margin = new Thickness(a_para.Margin.Left, a_para.Margin.Top, a_para.Margin.Right, 0);
                // a_paraはもう最後の行ではなくなるため、下方向のパディングを無くす（上方向の
                // パディングは、a_paraが元々先頭行だったか続きの行だったかで異なる値のままに
                // しておきたいため、ここでは触れない）。
                a_para.Padding = new Thickness(a_para.Padding.Left, a_para.Padding.Top, a_para.Padding.Right, 0);

                foreach (Inline movedInline in ListEditor.SplitInlinesAtCaret(a_para.Inlines, caret))
                {
                    newPara.Inlines.Add(movedInline);
                }

                m_editor.Document.Blocks.InsertAfter(a_para, newPara);
                m_editor.CaretPosition = newPara.ContentStart;
                DebugLogger.Log("InsertQuoteContinuationParagraph: 段落を分割し、新しい段落の先頭へキャレットを移動した");
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

        /// <summary>コードブロック内でキャレット位置の段落を2つに分割する、分割そのものだけを
        /// 行う内部ヘルパー。RunAsProgrammaticChange・UpdateLayout・Focus等の前後処理は
        /// 呼び出し元が担当する（1回のEnterキー処理につき1回だけ呼ぶInsertCodeBlock
        /// ContinuationParagraphと、複数行のテキスト貼り付けで行の数だけ連続して呼ぶ
        /// MainWindow.InsertPlainTextWithLineBreaksForCodeBlockの両方から使うため、前後処理を
        /// 分離してある。後者でループのたびにUpdateLayout・ClearFocus/Focusを行うと、貼り付け
        /// 行数が多い場合に重く・ちらつく原因になる）。
        /// 分割元の段落（a_para）がコードブロックの先頭行（CodeBlockInfo）・続きの行
        /// （CodeBlockContinuationInfo）のどちらであっても使える。分割後、a_paraは「後ろに
        /// 続きの行がある」状態のスタイル（下側の枠線・余白を持たない）に、新しく作る段落は
        /// 「このグループの最後の行」のスタイル（下側の枠線・余白を持つ）にする。a_paraが
        /// それまで実際に最後の行だったかどうかに関わらず、分割後は必ずa_paraの後ろに新しい
        /// 段落が来るため、この決め方でよい。</summary>
        /// <param name="a_para">分割するコードブロックの段落（先頭行・続きの行のいずれか）。</param>
        /// <returns>新しく作られた、分割後の段落（このグループの新しい最後の行）。</returns>
        public Paragraph SplitCodeBlockParagraphAtCaret(Paragraph a_para)
        {
            TextPointer caret = m_editor.CaretPosition;
            var newPara = new Paragraph();
            if (a_para.Tag is CodeBlockInfo codeInfo)
            {
                BlockStyles.ApplyCodeBlockStyle(a_para, codeInfo.Language, false);
            }
            else
            {
                BlockStyles.ApplyCodeBlockContinuationStyle(a_para, false);
            }
            BlockStyles.ApplyCodeBlockContinuationStyle(newPara, true);

            foreach (Inline movedInline in ListEditor.SplitInlinesAtCaret(a_para.Inlines, caret))
            {
                newPara.Inlines.Add(movedInline);
            }

            m_editor.Document.Blocks.InsertAfter(a_para, newPara);
            m_editor.CaretPosition = newPara.ContentStart;
            return newPara;
        }

        /// <summary>コードブロック内でのEnterキー処理。かつては行内改行（LineBreak要素）を
        /// 1つの段落の中に挿入する方式だったが、引用・箇条書き項目・表のセルと同じ理由
        /// （WPFのLineBreakとIMEの組み合わせの不具合。実機で「Enterで行を増やしても、新しい
        /// 行にカーソルを移動して入力すると前の行の末尾に入力されてしまう」という形で確認
        /// 済み）で、LineBreakは一切使わず、段落（Paragraph）そのものを2つに分割する
        /// SplitCodeBlockParagraphAtCaretに作り直した（CodeBlockContinuationInfoのコメント
        /// 参照）。</summary>
        /// <param name="a_para">キャレットがあるコードブロックの段落（先頭行・続きの行の
        /// いずれか）。</param>
        public void InsertCodeBlockContinuationParagraph(Paragraph a_para)
        {
            DebugLogger.Log("InsertCodeBlockContinuationParagraph: 呼び出し");
            m_runAsProgrammaticChange(() =>
            {
                SplitCodeBlockParagraphAtCaret(a_para);
            });
            m_editor.UpdateLayout();
            Keyboard.ClearFocus();
            m_editor.Focus();
            DebugLogger.Log("InsertCodeBlockContinuationParagraph: 完了");
        }

        /// <summary>コードブロックの続きの行（CodeBlockContinuationInfo）の先頭でBackSpaceが
        /// 押された時、WPF標準の「前の段落の末尾へ結合する」動作に相当する処理を、自前で
        /// 行う。標準動作に任せないのは、結合後に残る段落のBorderThickness・Padding・Margin
        /// （箱の下側の枠線・余白）を、結合によって変わった「このグループの最後の行かどうか」
        /// に応じて正しく設定し直す必要があるため（標準の段落結合はPgetMargin等のスタイルを
        /// 再計算してくれない。引用の左側の縦線のような位置に依存しない飾りとは異なり、
        /// コードブロックは四辺を囲む箱のため、結合で末尾の行が変わると見た目が崩れる）。
        /// a_paraが結合前の時点でこのグループの最後の行だった場合、結合先（前の段落）を
        /// 新しい最後の行として再スタイルする。そうでなければ（まだ後ろに続きの行がある
        /// 場合）、結合先の段落は元々「最後の行ではない」スタイルのままで変わらない。</summary>
        /// <param name="a_para">BackSpaceが押された、空でない続きの行。この段落は結合後に
        /// 文書から取り除かれる。</param>
        public void MergeCodeBlockContinuationIntoPrevious(Paragraph a_para)
        {
            DebugLogger.Log("MergeCodeBlockContinuationIntoPrevious: 呼び出し");
            if (!(a_para.PreviousBlock is Paragraph prevPara))
            {
                DebugLogger.Log("MergeCodeBlockContinuationIntoPrevious: 前の段落が無いため何もしなかった");
                return;
            }
            m_originalTextTracker.InvalidateForBlock(prevPara);
            m_runAsProgrammaticChange(() =>
            {
                bool wasLastLineFlg = !(a_para.NextBlock is Paragraph nextPara && nextPara.Tag is CodeBlockContinuationInfo);
                TextPointer joinPoint = prevPara.ContentEnd;

                var movedInlines = new List<Inline>();
                foreach (Inline inl in a_para.Inlines)
                {
                    movedInlines.Add(inl);
                }
                foreach (Inline inl in movedInlines)
                {
                    prevPara.Inlines.Add(inl);
                }
                m_editor.Document.Blocks.Remove(a_para);

                if (wasLastLineFlg)
                {
                    if (prevPara.Tag is CodeBlockInfo prevCodeInfo)
                    {
                        BlockStyles.ApplyCodeBlockStyle(prevPara, prevCodeInfo.Language, true);
                    }
                    else
                    {
                        BlockStyles.ApplyCodeBlockContinuationStyle(prevPara, true);
                    }
                }

                m_editor.CaretPosition = joinPoint;
            });
            m_editor.UpdateLayout();
            Keyboard.ClearFocus();
            m_editor.Focus();
            DebugLogger.Log("MergeCodeBlockContinuationIntoPrevious: 完了");
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
        /// 通常の段落（FlowDocument直下の、見出し・引用・コードブロック・箇条書き項目・表の
        /// セルのいずれでもないただの段落）内でShift+Enterが押された時の処理。引用
        /// （InsertQuoteContinuationParagraph）・コードブロック（InsertCodeBlockContinuation
        /// Paragraph）と全く同じ理由（WPFのLineBreak要素とIMEの組み合わせの不具合。実機で
        /// 「Shift+Enterで行を増やしても、次の行にカーソルを移動して入力すると前の行の末尾に
        /// 入力されてしまう」という形で確認された）で、LineBreakは使わず、キャレット位置で
        /// 段落そのものを2つに分割する。InsertParagraphSplitAtCaret（箇条書き項目・表のセル用）
        /// とは別にこのメソッドを分けているのは、箇条書き項目・表のセルの内部段落は常にMargin=0
        /// で位置による出し分けが不要なのに対し、通常の段落は「段落と段落の間の余白
        /// （EditorBlockSpacing）」を持つため、引用・コードブロックと同じ「分割元の下マージンを
        /// 新しい段落（今後の最後の行）へ付け替え、分割元自身は0にする」という位置依存の調整が
        /// 必要なため。
        /// 新しくできる段落には、通常の段落中の&lt;br&gt;による行内改行の「続き」段落
        /// （MarkdownConverter.MarkdownToDocumentが読み込み時に既に生成していたのと同じ
        /// BrContinuationInfo）を設定する。これにより、保存時にはDocumentToMarkdownの
        /// BrContinuationInfo分岐により&lt;br&gt;として書き出され、「&lt;br&gt;による段落内改行」の
        /// 読み込み側の表現と、ライブ編集でのShift+Enterの表現が一致する。
        /// 分割後の2つの段落の間のMarginは0にし、元々a_paraが持っていた（＝このグループの
        /// 最後の行として持っていた）下マージンは、新しくできた方の段落（今後の最後の行に
        /// なる）へ引き継ぐ。これにより、見た目には複数の段落が1つの段落内の複数行にしか
        /// 見えないようにする（InsertQuoteContinuationParagraph・MarkdownToDocumentの
        /// brSegments処理と全く同じ考え方）。Shift+Enterによるこの行内の行間を、通常のEnterに
        /// よる段落と段落の間の行間より詰まったまま保つのはご要望通りの挙動であり、意図的に
        /// 変更していない。
        /// </summary>
        /// <param name="a_para">分割対象の段落（現在キャレットがある、通常の段落。Tagはnull
        /// （まだ一度もShift+Enterしていない最初の行）、またはBrContinuationInfo（既に続きの
        /// 行）のいずれか）。</param>
        public void InsertBrContinuationParagraph(Paragraph a_para)
        {
            DebugLogger.Log("InsertBrContinuationParagraph: 呼び出し");
            m_runAsProgrammaticChange(() =>
            {
                TextPointer caret = m_editor.CaretPosition;

                var newPara = new Paragraph
                {
                    Margin = new Thickness(0, 0, 0, a_para.Margin.Bottom),
                    LineHeight = a_para.LineHeight,
                    KeepTogether = a_para.KeepTogether,
                    TextAlignment = a_para.TextAlignment,
                    Tag = new BrContinuationInfo()
                };
                // a_paraが分割前に持っていた下マージンは、上でnewParaへ引き継ぎ済みのため、
                // a_para自身の下マージンは0にする（もう最後の行ではなくなるため）。上マージンには
                // 触れない（a_paraが元々このグループの何行目だったかによらず、そのままの値を保つ）。
                a_para.Margin = new Thickness(a_para.Margin.Left, a_para.Margin.Top, a_para.Margin.Right, 0);

                foreach (Inline movedInline in ListEditor.SplitInlinesAtCaret(a_para.Inlines, caret))
                {
                    newPara.Inlines.Add(movedInline);
                }

                m_editor.Document.Blocks.InsertAfter(a_para, newPara);
                m_editor.CaretPosition = newPara.ContentStart;
                DebugLogger.Log("InsertBrContinuationParagraph: 段落を分割し、新しい段落の先頭へキャレットを移動した");
            });
            m_editor.UpdateLayout();
            Keyboard.ClearFocus();
            m_editor.Focus();
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
