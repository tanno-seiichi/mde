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
                // v1.5.5.5適用後の調査用ログにより、この部分にListEditor.
                // ConvertParagraphToListItemで既に見つけて直した不具合と同じ問題が残っていた
                // ことが判明した。GetPositionAtOffsetのオフセットはWPF内部のシンボリックな
                // 単位であり、実際の文字数と1対1に対応しないことがあるため、"#"＋半角スペース
                // の除去のつもりでもマーカーの一部（特に末尾の半角スペース）が消しきれずに
                // 残ってしまうことがあった。この取り残された半角スペースが、後から見出しを
                // BackSpaceで空にしようとした際、「画面上は空に見えるのにキャレットを右に
                // 動かせる」「BackSpaceを繰り返しても最後まで消せない」という症状の原因に
                // なっていた。ListEditor側の修正と同じAdvanceByCharCountを使い、実際に
                // 取り出した文字列の長さだけを頼りに確実にa_level+1文字分を取り除くようにする。
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
            // v1.5.5.2で「変化なし」というご報告をいただいたための調査用ログ。
            // この処理が実際に呼び出され、最後まで実行されているかを確認する。
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
            // 調査用：処理後にTagが実際にnullへ戻っているか（本文スタイルに戻っているか）を記録する。
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
        /// 削除する。v1.5.5.4適用後のログ調査により、IME入力で組み立てた見出し段落の
        /// 最後の1文字に対しては、WPF標準のBackSpaceコマンド自体が（このアプリのコードとは
        /// 無関係に）何もせず反応しないことがある（EditorTextChangedイベントすら発生しない）
        /// という、WPF側の不具合が新たに判明した。IME合成の内部的な部分確定の繰り返しにより
        /// 段落内のRun（区画）が細かく分かれた状態が残ることが原因と推測される。標準の
        /// BackSpaceに委ねる代わりに、この処理で確実に最後の1文字を取り除く。</summary>
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
        /// 【2026-09追記・第1版】以前はここだけ、他の構造変更メソッド（ConvertParagraphToHeading・
        /// ConvertParagraphToCodeBlock・OutdentCodeLine等、本ファイル内の他のすべてのメソッド）
        /// と異なり、実際のドキュメント変更をm_runAsProgrammaticChangeで囲んでいなかったため、
        /// これを他のメソッドと同じ保護で囲むよう統一した（詳細は下記追記参照）。
        /// 【2026-09追記・第2版（実機の画面録画・デバッグログで確認、後に不十分と判明）】上記の
        /// 対策だけでは、箇条書き項目でShift+Enterの直後に文字を入力すると改行が無効になる
        /// 不具合が直らないことが、ユーザーから提供された実機の画面録画とデバッグログにより
        /// 判明した。ログを確認したところ、行内改行の挿入イベント自体（EditorTextChangedの
        /// Off値）に対して、その直後に入力される文字（IME確定文字を含む）の挿入位置（Off値）
        /// が、常に「1つ手前」（＝挿入した行内改行の直前の位置）になっていることが分かった。
        /// これは、TextPointer.InsertLineBreakでCaretPositionをプログラム的に動かしても、
        /// IME（TSF）側が新しいキャレット位置を認識できていないことを示している。見出し・
        /// コードブロックへの変換や、ネストしていない箇条書き項目でのEnterによる新規項目作成
        /// など、本ファイル・ListEditor.cs内の他の「キャレットをプログラム的に移動する」処理は
        /// すべて、移動の直後に`m_editor.UpdateLayout()`→`Keyboard.ClearFocus()`→
        /// `m_editor.Focus()`という一連の処理（IMEに新しいキャレット位置を再認識させるための、
        /// 実機検証で確立された対策）で解決していたため、同じ対策をここにも適用した。
        /// 【2026-09追記・第3版（実機の再現ログで、第2版策も不十分と判明。ネスト箇条書きと
        /// 同じ非同期パターンへ変更）】第2版の対策（同期的なUpdateLayout→ClearFocus→Focus）を
        /// 適用したビルドで改めて実機検証いただいたところ、それでもなお同じ不具合が再現する
        /// ことが、新たに共有されたデバッグログにより判明した。ログでは、行内改行の挿入
        /// （Off=10、1段目＝ネストしていない箇条書きでの再現。HandleListEnterのログでdepth=1
        /// と確認済み）の直後、ClearFocus/Focusのサイクル（ログ上も実際に発生を確認）を経て
        /// なお、次のIME入力が改行の1つ手前（Off=9）に挿入されてしまっていた。つまり、同期的に
        /// （キー入力の処理と同じタイミングで）ClearFocus/Focusを行うだけでは、IME側に新しい
        /// キャレット位置を認識させるのに間に合っていない。これは、ListEditor.HandleListEnterで
        /// 「ネストした箇条書き（2段目以降）でだけ」発生するとして対策されていた不具合と、
        /// 実質的に同じ種類の競合状態が、行内改行ではネストの有無に関わらず起きていることを
        /// 示唆している。ネストした箇条書きの対策で使われているImeCaretMoveHelper.
        /// ScheduleCaretMove（Dispatcher.BeginInvokeでキー入力の処理から実際のドキュメント
        /// 変更・フォーカスの当て直しを1テンポ切り離す、より強い対策）に、この行内改行の処理も
        /// 合わせることにした。なお、ImeCaretMoveHelper.cs自身のコメントにもある通り、この
        /// 対策はIME（TSF）内部のタイミングに依存する競合状態への対策であり、発生確率を下げる
        /// ことはできても100%の解消を保証するものではない。
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
        /// 箇条書き項目・表のセル内でのShift+Enter/Enter処理。上のInsertLineBreakAtCaretとは
        /// 異なり、行内改行（LineBreak要素）は一切使わず、Shift+Enter等が押された位置で段落
        /// （Paragraph）そのものを2つに分割する（見た目には1つの段落がそのまま改行している
        /// ように見せるため、分割した両方の段落のMarginを揃えて0にする）。
        /// 【なぜLineBreakではなくこの方式にしたか】上のInsertLineBreakAtCaretのコメントに
        /// 記録されている通り、行内改行（LineBreak要素の挿入）を使う限り、キャレット位置の
        /// 設定・通知方法をどう工夫しても（同期的なUpdateLayout→ClearFocus→Focus、
        /// ImeCaretMoveHelperによる非同期化、WPF標準コマンドの使用、Selection.Selectでの
        /// 設定、WPF内部のTextStoreクラスへのリフレクション経由の直接通知など、思いつく限り
        /// 9通りの対策）、次のIME入力が改行の「1つ手前」（挿入した行内改行の直前の位置）に
        /// 入ってしまう不具合を解消できなかった。これは、WPFのTSF（IME）連携が、LineBreakと
        /// いう要素自体とうまくかみ合っていない、プラットフォーム側の制約と判断した
        /// （詳細な調査の経緯はDEVELOPMENT_LOG.md該当セクション参照）。
        /// 独立した最小再現アプリ（mde本体とは別プロジェクト）での検証により、LineBreakを
        /// 一切使わず、Shift+Enter等が押された位置で段落そのものを2つに分割する方式では、
        /// この症状が再現しないことが実機で確認された。これはLineBreakの挿入という
        /// 「インライン要素の挿入」ではなく、段落という「ブロック要素の追加」であり、WPFの
        /// TSF連携にとって操作の性質そのものが異なるためと考えられる。この方式では
        /// ImeCaretMoveHelperによる非同期化も不要だった（最小再現アプリでの検証で、
        /// 同期的なm_runAsProgrammaticChangeのみで問題なく動作することを確認済み。新しい
        /// 段落は、分割元の段落と同じ親（ListItem/TableCell）の中に追加されるため、既存の
        /// レイアウト済み要素の隣に挿入される形になり、見出し等の変換処理と同じ理由で、
        /// 未レイアウトの新規要素特有の問題が起きにくいためと推測している）。
        /// Markdownとしての書き出し・読み込みは、この内部表現の違いに影響されない
        /// （MarkdownConverter.ListToMarkdown・TableToMarkdown・対応する読み込み側の
        /// 各コメント参照。項目・セルの内容の中身のテキストと改行位置だけを基準にした変換で
        /// あり、内部がLineBreakを持つ1つの段落か、複数の段落かには依存しない）。
        /// キャレットより後ろにあった内容（書式を含む）は、そのままInline単位で新しい段落へ
        /// 移す。ListEditor.SplitInlinesAtCaret・CloneRunForSplit（項目のEnterでの分割に
        /// もともと使われていた、実績のあるロジック）をそのまま共有して使う。TextPointerの
        /// オフセットベースでテキストを抜き出す方式は、箇条書きマーカー記号を巻き込んでしまう
        /// 既知の不具合（ListEditor.GetOwnListItemText等のコメント参照）を避けるため、
        /// あえて使わない。
        /// </summary>
        /// <param name="a_para">分割対象の段落（現在キャレットがある段落）。親がListItemまたは
        /// TableCellでない場合（コードブロック等）は何もしない（呼び出し側は、そのような場合には
        /// 代わりに従来通りInsertLineBreakAtCaretを呼ぶこと。コードブロックは今回の調査・対策の
        /// 対象外であり、これまで通り行内改行のままにする）。</param>
        public void InsertParagraphSplitAtCaret(Paragraph a_para)
        {
            if (null == a_para)
            {
                return;
            }
            object parent = a_para.Parent;
            if (!(parent is ListItem) && !(parent is TableCell))
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
                // KeepTogether=true・文字揃え。箇条書き項目ならMargin=0）を、新しい段落にも
                // そのまま引き継ぐ。ハードコードせず分割元から読み取ることで、呼び出し元
                // （ListItem・TableCellのどちらか）に関わらず常に整合する。TagはTableCellの
                // 「明示的な左揃え」の印（MarkdownConverter.ALIGN_LEFT_EXPLICIT_TAG）を想定
                // しているが、これはMarkdown書き出し時にセルの最初の段落からしか参照されない
                // ため、2つめ以降の段落にも同じ値が付くこと自体は無害（書き出し結果には影響しない）。
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
