// ListEditor.cs
//
// mde (MarkDown インラインエディタ) の一部。
// 箇条書き・順序付きリストの編集を担当するクラス。段落からリスト項目への変換、
// Enterキーでの項目追加/リスト脱出、Tab/Shift+Tabでの字下げ・字下げ解除を扱う。

using System;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace mde
{
    /// <summary>
    /// 箇条書き・順序付きリストの編集機能一式。MainWindowとは疎結合で、Editor本体・
    /// 「元テキスト保持」の追跡役・プログラム的変更ラップ用のdelegateだけを受け取って動作する。
    /// </summary>
    public class ListEditor
    {
        private readonly RichTextBox m_editor;
        private readonly OriginalTextTracker m_originalTextTracker;
        private readonly Action<Action> m_runAsProgrammaticChange;

        /// <summary>
        /// ListEditorを構築する。
        /// </summary>
        /// <param name="a_editor">編集対象のRichTextBox。</param>
        /// <param name="a_originalTextTracker">「元テキスト保持」の追跡役。</param>
        /// <param name="a_runAsProgrammaticChange">処理を「プログラムによる変更」として実行するdelegate。</param>
        public ListEditor(RichTextBox a_editor, OriginalTextTracker a_originalTextTracker, Action<Action> a_runAsProgrammaticChange)
        {
            this.m_editor = a_editor;
            this.m_originalTextTracker = a_originalTextTracker;
            this.m_runAsProgrammaticChange = a_runAsProgrammaticChange;
        }

        /// <summary>段落がリスト項目に含まれているかどうかを調べる。</summary>
        /// <param name="a_para">調べる段落。</param>
        /// <param name="a_liRf">含まれているListItem（見つかった場合）。</param>
        /// <param name="a_parentListRf">そのListItemを含むList（見つかった場合）。</param>
        /// <returns>リスト項目の段落であれば true。</returns>
        public bool IsInListItem(Paragraph a_para, out ListItem a_liRf, out List a_parentListRf)
        {
            a_liRf = a_para.Parent as ListItem;
            if (null != a_liRf)
            {
                a_parentListRf = a_liRf.Parent as List;
                return null != a_parentListRf;
            }
            a_parentListRf = null;
            return false;
        }

        /// <summary>段落をリスト項目（新規または既存リストへの追加）に変換する。</summary>
        /// <param name="a_p">変換する段落（そのテキストが最初の項目のテキストになる）。</param>
        /// <param name="a_marker">箇条書き記号（"*"または"-"）。順序付きリストの場合はnull。</param>
        /// <param name="a_orderedFlg">順序付きリストなら true、箇条書きなら false。</param>
        public void ConvertParagraphToListItem(Paragraph a_p, string a_marker, bool a_orderedFlg)
        {
            m_runAsProgrammaticChange(() =>
            {
                Block prev = a_p.PreviousBlock;
                var newLiPara = new Paragraph { Margin = new Thickness(0) };
                var newLi = new ListItem(newLiPara);

                if (prev is List prevList &&
                    (prevList.MarkerStyle == TextMarkerStyle.Decimal) == a_orderedFlg)
                {
                    prevList.ListItems.Add(newLi);
                    m_editor.Document.Blocks.Remove(a_p);
                }
                else
                {
                    var list = new List
                    {
                        MarkerStyle = a_orderedFlg ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                        Tag = a_orderedFlg ? null : a_marker
                    };
                    list.ListItems.Add(newLi);
                    m_editor.Document.Blocks.InsertBefore(a_p, list);
                    m_editor.Document.Blocks.Remove(a_p);
                }
                m_editor.CaretPosition = newLiPara.ContentStart;
            });
            FinishCaretMoveToNewParagraph();
        }

        /// <summary>リスト項目内の段落先頭に入力された"[ ] "/"[x] "をタスクリストのチェックボックスに
        /// 変換する（ライブ入力時用）。MarkdownConverter.BuildNestedListのバッチ変換と対になる処理。
        /// 2026-08-29追記（DESIGN.md参照）。</summary>
        /// <param name="a_p">変換する段落（リスト項目直下のものであること）。</param>
        /// <param name="a_checked">"[x] "ならtrue、"[ ] "ならfalse。</param>
        public void ConvertListItemTextToTaskCheckbox(Paragraph a_p, bool a_checked)
        {
            m_runAsProgrammaticChange(() =>
            {
                a_p.Inlines.Clear();
                a_p.Inlines.Add(new InlineUIContainer(BlockStyles.CreateTaskCheckbox(a_checked)));
                m_editor.CaretPosition = a_p.ContentEnd;

                // 2026-08-29追記（同日中の追加修正、v2.0.34.0で試みたが、v2.0.35.0で撤回）：
                // Typora等と同様に箇条書きマーカー（●等）を非表示にしてチェックボックスだけを
                // 見せる対応を一度実装したが、List.MarkerStyleがListItemではなくList単位でしか
                // 持てないというWPFの制約により、Enterキーでの新規項目作成時に「同じListの
                // 新しい項目までマーカーが消えて見た目上リストを抜けたように見える」・
                // マーカー用に予約された余白がそのまま残ることによるインデントのずれ、といった
                // 副作用が実機で確認されたため撤回した（詳細はDESIGN.md参照）。
            });
            FinishCaretMoveToNewParagraph();
        }

        /// <summary>
        /// その場で新しく作った段落へCaretPositionを合わせた直後に呼ぶ後始末処理。新しく作った
        /// （一度もレイアウトが計算されていない）段落へキャレットを合わせた直後にIME（日本語入力）で
        /// 入力を始めると、変換候補は表示されるのに確定した文字が一切反映されず、入力できなくなる
        /// 不具合が実機で確認された（DESIGN.md 14.12参照）。まずUpdateLayoutでレイアウトを
        /// 強制的に確定させ、次にKeyboard.ClearFocus()で一旦キーボードフォーカスを完全に外して
        /// からFocus()で当て直すことで、内部のTextEditor/IME関連付けを作り直す（単にFocus()を
        /// 呼ぶだけでは、既にフォーカスがある場合は何もしないため効果がない）。
        /// </summary>
        private void FinishCaretMoveToNewParagraph()
        {
            m_editor.UpdateLayout();
            Keyboard.ClearFocus();
            m_editor.Focus();
        }

        /// <summary>リスト項目自身のテキストを取得する（入れ子のサブリストは含まない）。
        /// マーカー記号を拾ってしまうTextRangeではなく、Inlinesを直接読むため安全。</summary>
        /// <param name="a_li">調べる項目。</param>
        /// <returns>トリム済みのテキスト。画像だけがあり文字がない場合はゼロ幅スペース1文字。</returns>
        public string GetOwnListItemText(ListItem a_li)
        {
            var ownPara = a_li.Blocks.FirstBlock as Paragraph;
            if (null == ownPara)
            {
                return "";
            }
            var sb = new StringBuilder();
            AppendPlainInlineText(ownPara.Inlines, sb);
            string t = sb.ToString().Trim();
            if (0 == t.Length && HasDescendantImage(ownPara))
            {
                return "\u200B";
            }
            return t;
        }

        /// <summary>段落自身のプレーンテキストを取得する（Inlinesを直接読むため、TextRange.Textの
        /// ようにリスト項目のマーカー記号（「1.」や「•」）を文字として拾ってしまう問題が起きない）。
        /// `GetOwnListItemText`と異なりトリムしない生のテキストを返す（末尾の空白の有無を
        /// 呼び出し元が判定できるようにするため）。2026-08-29追記（DESIGN.md参照。ライブ入力での
        /// タスクリスト変換判定に、`TextRange(para.ContentStart, para.ContentEnd).Text`を
        /// 使ってマーカー記号を巻き込んでしまい、正規表現が一致しなくなっていた不具合の修正）。</summary>
        /// <param name="a_p">対象の段落。</param>
        /// <returns>Inlinesのプレーンテキストをそのまま連結した文字列（トリムなし）。</returns>
        public string GetParagraphPlainText(Paragraph a_p)
        {
            var sb = new StringBuilder();
            AppendPlainInlineText(a_p.Inlines, sb);
            return sb.ToString();
        }

        /// <summary>Inlinesコレクションの中の、Runのリテラルテキストだけを連結する
        /// （Spanは再帰的にたどり、LineBreakは改行として扱う）。TextRange.Textと違い、
        /// リスト項目のマーカー記号を文字として拾ってしまうことがない。</summary>
        /// <param name="a_inlines">対象のInlineコレクション。</param>
        /// <param name="a_sb">追記先のStringBuilder。</param>
        private void AppendPlainInlineText(InlineCollection a_inlines, StringBuilder a_sb)
        {
            foreach (Inline inline in a_inlines)
            {
                if (inline is Run run)
                {
                    a_sb.Append(run.Text);
                }
                else if (inline is Span span)
                {
                    AppendPlainInlineText(span.Inlines, a_sb);
                }
                else if (inline is LineBreak)
                {
                    a_sb.Append('\n');
                }
            }
        }

        private bool HasDescendantImage(Paragraph a_p)
        {
            foreach (Inline inline in a_p.Inlines)
            {
                if (InlineContainsImage(inline))
                {
                    return true;
                }
            }
            return false;
        }

        private bool InlineContainsImage(Inline a_inline)
        {
            // 2026-08-29追記（同日中の追加修正、v2.0.37.0）：以前はここでCheckBoxも画像と
            // 同様に「文字を持たないが空とはみなさない」対象に含めていたが、これによって
            // 文字の無いタスク項目（チェックボックスだけの項目）でEnterキーを押しても
            // 空項目とみなされずリストを抜けられない、という意図しない挙動になっていた
            // ことが実機で確認された（DESIGN.md参照）。チェックボックスだけの項目は、
            // 画像と異なり中身が空の箇条書き項目と同じ扱いにすべきという判断から、
            // CheckBoxはこの判定から除外した（画像のみ引き続き対象とする）。
            if (a_inline is InlineUIContainer iuc && iuc.Child is Image)
            {
                return true;
            }
            if (a_inline is Span span)
            {
                foreach (Inline child in span.Inlines)
                {
                    if (InlineContainsImage(child))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// 指定したListが何段目のネストにあるかを返す（1段目＝ネストなしなら1、
        /// 2段目なら2、以下同様）。ScheduleCaretMoveToNewParagraphで、ネストの深さに応じて
        /// 後始末を遅らせる回数を決めるために使う（DESIGN.md 14.12 追記7参照）。
        /// </summary>
        /// <param name="a_list">調べたいList。</param>
        /// <returns>ネストの段数（1始まり）。</returns>
        private int GetListNestingDepth(List a_list)
        {
            int depth = 1;
            DependencyObject cursor = a_list.Parent;
            while (cursor is ListItem ownerLi)
            {
                depth++;
                cursor = (ownerLi.Parent as List)?.Parent;
            }
            return depth;
        }

        /// <summary>リスト項目内でのEnterキー処理。項目が空ならリストを抜けて新しい通常の段落を
        /// 作り、空でなければ新しい項目を後ろに追加する。</summary>
        /// <param name="a_li">現在の項目。</param>
        /// <param name="a_parentList">現在の項目を含むList。</param>
        public void HandleListEnter(ListItem a_li, List a_parentList)
        {
            DebugLogger.Log($"HandleListEnter: 呼び出し depth={GetListNestingDepth(a_parentList)} MarkerStyle={a_parentList.MarkerStyle}");
            // a_parentListが入れ子（親がListItem）かどうかで、後始末の方法を変える。
            // ネストしたリストの2段目以降でだけ、新しい項目を作った直後にIME入力を始めると
            // 変換候補が確定できなくなる不具合が実機で確認されている（DESIGN.md 14.12参照）。
            // 1段目（ネストしていない）では発生しないことも実機で確認済みなので、1段目は
            // 従来どおり同期的にドキュメントを書き換えてキャレットを移動する。
            bool nestedFlg = a_parentList.Parent is ListItem;

            m_originalTextTracker.InvalidateForBlock(a_parentList);

            if (nestedFlg)
            {
                // 追記11：以前はドキュメントの書き換え（新しい項目の挿入等）自体は即座に行い、
                // CaretPositionの移動だけを遅らせていたが、それでも実機で症状が再現し続けた。
                // 「書き換え自体も、今のキー入力の処理から完全に離れたタイミングで行う」よう
                // 変更した（DESIGN.md 14.12 追記11参照）。ネストが深いListItemほど遅延の回数を
                // 増やす狙い（追記5〜7参照）は変わらない。実際の遅延処理・フォーカスの当て直し・
                // WPF内部状態のリセットは、見出し変換等とも共通のImeCaretMoveHelperに任せる。
                int depth = GetListNestingDepth(a_parentList);
                // 追記14〜15：a_cycleFocus=falseの実験は、症状に変化なし（フォーカスの当て直し
                // 自体は原因ではない）と判明したため既定のtrueに戻した。追記16〜18：
                // a_toggleInputMethod=true（WPFの公開API`InputMethod`によるTSF張り直し）も、
                // IMEをオンにした有効なテストで症状を安定して防げなかったため、falseに戻した。
                // 追記19〜20：さらに低いレベルのWin32 API（imm32.dllの`ImmAssociateContextEx`）
                // で入力コンテキストを張り直す a_reassociateImeContext=true を試したが、
                // 症状を防ぐどころか、より深刻な症状（何も入力できない完全な入力不能）を
                // 引き起こした疑いが強いため撤回し、falseに戻した（DESIGN.md 14.12 追記20参照）。
                // 現在は`ClearFocus`/`Focus`の当て直しと`TryClearSuggestedX`のみを行っている。
                ImeCaretMoveHelper.ScheduleCaretMove(
                    m_editor,
                    () =>
                    {
                        m_runAsProgrammaticChange(() =>
                        {
                            Paragraph targetPara = CreateOrExitListItem(a_li, a_parentList);
                            m_editor.CaretPosition = GetCaretPositionForNewListParagraph(targetPara);
                        });
                    },
                    System.Math.Max(1, depth - 1),
                    a_cycleFocus: true,
                    a_toggleInputMethod: false,
                    a_reassociateImeContext: false);
            }
            else
            {
                m_runAsProgrammaticChange(() =>
                {
                    Paragraph targetPara = CreateOrExitListItem(a_li, a_parentList);
                    m_editor.CaretPosition = GetCaretPositionForNewListParagraph(targetPara);
                });
                FinishCaretMoveToNewParagraph();
            }
        }

        /// <summary>Enterキーで新しく作った段落へキャレットを合わせる際の位置を決める。
        /// 段落の先頭がタスクチェックボックス（InlineUIContainer）で始まっている場合は
        /// その直後（ContentEnd）へ、そうでなければ段落の先頭（ContentStart）へ合わせる。
        /// 2026-08-29追記（同日中の追加修正、v2.0.36.0。DESIGN.md参照）。</summary>
        /// <param name="a_para">キャレットを合わせる先の段落。</param>
        /// <returns>合わせるべきTextPointer。</returns>
        private TextPointer GetCaretPositionForNewListParagraph(Paragraph a_para)
        {
            return (a_para.Inlines.FirstInline is InlineUIContainer) ? a_para.ContentEnd : a_para.ContentStart;
        }

        /// <summary>
        /// HandleListEnterの本体：項目が空ならリストを抜けて新しい通常の段落を作り、
        /// 空でなければ新しい項目を後ろに追加する。呼び出し元（同期／
        /// ImeCaretMoveHelper経由の遅延の両方）から共通で使う。
        /// </summary>
        /// <param name="a_li">現在の項目。</param>
        /// <param name="a_parentList">現在の項目を含むList。</param>
        /// <returns>キャレットを移す先の新しい段落。</returns>
        private Paragraph CreateOrExitListItem(ListItem a_li, List a_parentList)
        {
            bool hasNestedListFlg = a_li.Blocks.Count > 1;
            bool isEmptyFlg = !hasNestedListFlg && 0 == GetOwnListItemText(a_li).Length;
            DebugLogger.Log($"CreateOrExitListItem: 実行 isEmptyFlg={isEmptyFlg} hasNestedListFlg={hasNestedListFlg}");

            if (isEmptyFlg)
            {
                List topList = a_parentList;
                DependencyObject cursor = a_parentList.Parent;
                while (cursor is ListItem ownerLi)
                {
                    topList = ownerLi.Parent as List;
                    cursor = topList?.Parent;
                }

                var newPara = new Paragraph();
                m_editor.Document.Blocks.InsertAfter(topList, newPara);

                a_parentList.ListItems.Remove(a_li);
                if (0 == a_parentList.ListItems.Count)
                {
                    RemoveEmptyList(a_parentList);
                }
                return newPara;
            }
            else
            {
                // 以前はListItems.Clear()してから全項目を1つずつAddし直すことで「a_liの
                // 直後に挿入する」を実現していたが、これだとa_li自身を含む既存の項目も
                // すべて一旦作り直されることになり、リスト全体の表示要素が丸ごと
                // 再構築されてしまっていた。ListItemCollectionはBlockCollection等と同じく
                // InsertAfterを持つため、新しい項目1つだけをa_liの直後に挿入すれば済み、
                // 既存の項目には触れずに済む。
                var newPara = new Paragraph { Margin = new Thickness(0) };
                // 2026-08-29追記（同日中の追加修正、v2.0.36.0）：Enterを押す前の項目がタスク
                // チェックボックス項目だった場合、Typora等と同様に新しい項目にも未チェックの
                // チェックボックスをあらかじめ入れておく（DESIGN.md参照）。ここで行っているのは
                // ConvertListItemTextToTaskCheckboxと同じ「段落のInlinesにInlineUIContainerを
                // 1つ追加する」だけの操作であり、v2.0.34.0で問題になったList単位のマーカー
                // 非表示（List.MarkerStyleの変更）とは異なりList自体には触れないため、
                // 他の項目に影響しない。
                if (IsTaskCheckboxItem(a_li))
                {
                    newPara.Inlines.Add(new InlineUIContainer(BlockStyles.CreateTaskCheckbox(false)));
                }
                var newLi = new ListItem(newPara);
                a_parentList.ListItems.InsertAfter(a_li, newLi);
                return newPara;
            }
        }

        /// <summary>指定した項目自身の段落が、タスクリストのチェックボックスで始まっているかを
        /// 調べる（入れ子のサブリストは見ない）。2026-08-29追記（同日中の追加修正、
        /// v2.0.36.0。DESIGN.md参照）。</summary>
        /// <param name="a_li">調べる項目。</param>
        /// <returns>段落の先頭がタスクチェックボックスならtrue。</returns>
        private bool IsTaskCheckboxItem(ListItem a_li)
        {
            return a_li.Blocks.FirstBlock is Paragraph ownPara &&
                ownPara.Inlines.FirstInline is InlineUIContainer iuc &&
                iuc.Child is CheckBox cb &&
                "task-checkbox" == (cb.Tag as string);
        }

        private void RemoveEmptyList(List a_list)
        {
            if (a_list.Parent is ListItem ownerLi)
            {
                ownerLi.Blocks.Remove(a_list);
            }
            else if (a_list.Parent is FlowDocument doc)
            {
                doc.Blocks.Remove(a_list);
            }
        }

        /// <summary>Tabキーでのリスト項目の字下げ。前の兄弟項目の下に新しい入れ子リストを作り、
        /// そこへ移す。</summary>
        /// <param name="a_li">字下げする項目。</param>
        /// <param name="a_parentList">現在の（字下げ前の）親List。</param>
        public void IndentListItem(ListItem a_li, List a_parentList)
        {
            m_originalTextTracker.InvalidateForBlock(a_parentList);
            ListItem prevLi = null;
            foreach (ListItem item in a_parentList.ListItems)
            {
                if (item == a_li)
                {
                    break;
                }
                prevLi = item;
            }
            if (null == prevLi)
            {
                return; // 先頭の項目は字下げできない
            }

            // 2026-08-28修正：以前はここで`new List{...}`＋`Blocks.Add`／
            // `ListItems.Remove`／`ListItems.Add`という公開APIでの手動操作に
            // よってネストしたListオブジェクトを新規構築していたが、これが
            // IME変換中の確定前に入力が固まってしまう不具合の原因であることが、
            // mde_minimalでの詳細な切り分け調査で判明した（DESIGN.md 14.12
            // 追記28〜45）。手動構築したListオブジェクトは、見た目・構造上は
            // WPF標準の字下げコマンドが作るものと同一に見えても、その後その
            // List内で新しい項目を作成しIME入力を行うと、最初の1文字目の
            // 変換候補が確定されずに固まってしまうことが確認された。そのため、
            // Listオブジェクトの構築（新規作成・複数のListコレクション間での
            // 項目の移動）自体はWPF標準の`EditingCommands.IncreaseIndentation`
            // コマンドに任せ、mde独自の見た目・データ（ネストした階層を示す
            // `MarkerStyle=Circle`・箇条書き文字を伝える`Tag`）は、コマンド
            // 実行後にできあがった既存のListオブジェクトの**プロパティだけ**を
            // 変更する形で復元する（Listオブジェクト自体の新規作成・
            // ListItems/Blocksコレクションへの直接操作は行わない）。
            //
            // 追記47（DESIGN.md 14.12参照）：実機テストの結果、mde本体ではこのコマンドが
            // 構造を一切変更しない（字下げが起きない）まま、以下のプロパティ復元だけが
            // 実行されてしまい、a_parentList自身（＝トップレベルのListそのもの）の
            // MarkerStyleが書き換わって、字下げされていない全項目の記号が意図せず
            // ○に変わってしまう不具合が見つかった。mde_minimal側の診断アプリでは
            // 同じコードで問題が起きなかったため比較したところ、唯一の目立つ違いが
            // RichTextBoxのAcceptsTabプロパティ（mde本体はFalse、mde_minimalはTrue）
            // だったため、これが原因という仮説のもとMainWindow.xamlのAcceptsTabを
            // Trueに変更した。あわせて、この仮説が外れていた場合に同じ見た目上の
            // 事故（全項目のマーカーが意図せず変わる）を二度と起こさないよう、
            // 「実際にa_parentListとは別の新しいListオブジェクトが作られた場合のみ」
            // プロパティを復元するようガードを追加し、実際に字下げが起きたかどうかを
            // 判定できるデバッグログも追加した。
            bool parentOrderedFlg = a_parentList.MarkerStyle == TextMarkerStyle.Decimal;
            int beforeCount = a_parentList.ListItems.Count;

            m_runAsProgrammaticChange(() =>
            {
                EditingCommands.IncreaseIndentation.Execute(null, m_editor);

                if (m_editor.CaretPosition?.Paragraph is Paragraph fp &&
                    fp.Parent is ListItem li &&
                    li.Parent is List nestedList &&
                    !ReferenceEquals(nestedList, a_parentList))
                {
                    // 2026-08-29追記（同日中の追加修正、v2.0.38.0で段数によらずDiscに統一したが、
                    // VSCode同様「1段目Disc・2段目Circle・3段目以降Box」の方を試したいとの
                    // 要望を受け、v2.0.39.0で段数に応じた見た目に変更した。実際の段数は
                    // GetListNestingDepthでnestedList自身（字下げ後に実際に入れ子として
                    // 出来上がったListオブジェクト）から数える。DESIGN.md参照）。
                    nestedList.MarkerStyle = parentOrderedFlg
                        ? TextMarkerStyle.Decimal
                        : BlockStyles.UnorderedMarkerStyleForDepth(GetListNestingDepth(nestedList));
                    nestedList.Tag = parentOrderedFlg ? null : ((a_parentList.Tag as string) ?? "*");
                    DebugLogger.Log($"IndentListItem: 字下げ成功（別のListオブジェクトへ移動） " +
                        $"beforeCount={beforeCount} afterParentCount={a_parentList.ListItems.Count}");
                }
                else
                {
                    DebugLogger.Log($"IndentListItem: IncreaseIndentationコマンドが構造を変更しなかった " +
                        $"（字下げ失敗の可能性。AcceptsTab修正の効果を要確認） " +
                        $"beforeCount={beforeCount} afterParentCount={a_parentList.ListItems.Count}");
                }
            });
        }

        /// <summary>Shift+Tabキーでのリスト項目の字下げ解除。自分より後ろの兄弟項目も一緒に
        /// 自分の下の入れ子リストへ移してから、1段上の親リストへ昇格させる。</summary>
        /// <param name="a_li">字下げ解除する項目。</param>
        /// <param name="a_parentList">現在の（解除前の）親List。</param>
        public void OutdentListItem(ListItem a_li, List a_parentList)
        {
            m_originalTextTracker.InvalidateForBlock(a_parentList);
            if (!(a_parentList.Parent is ListItem parentLi))
            {
                return; // すでにトップレベル
            }
            if (!(parentLi.Parent is List grandList))
            {
                return;
            }

            // 2026-08-28修正：IndentListItemと同じ理由（上記コメント・DESIGN.md
            // 14.12 追記45参照）で、手動でのList/ListItem操作（`ListItems.Clear`
            // ＋`Add`の繰り返しによる並べ替え、後続の兄弟項目をa_liの下へ新しい
            // Listとしてまとめる処理等）をやめ、WPF標準の
            // `EditingCommands.DecreaseIndentation`コマンドに構造変更自体を
            // 任せる。ただし、IndentListItem側と異なり、こちらは実際にIME
            // 固まり症状を再現・確認したわけではない予防的な修正であり、
            // 特に「a_liより後ろの兄弟項目（trailing siblings）がa_liの下に
            // 入れ子として残る」というmde独自の挙動を、WPF標準コマンドが
            // 同じように再現するかどうかは未検証。実機での動作確認を必ず
            // 行うこと。
            //
            // 追記47（DESIGN.md 14.12参照）：IndentListItem側で、このコマンドが
            // mde本体では構造を変更しない（RichTextBoxのAcceptsTab=Falseが原因の
            // 疑い）という不具合が見つかり、AcceptsTab=Trueに修正した（詳細は
            // IndentListItem側のコメント参照）。OutdentListItem側は元々
            // 「li.Blocks.Count > 1」という、実際に構造が変わった場合しか
            // 満たされない条件でガードされているため、同じ原因があったとしても
            // Indent側のような見た目の事故（無関係な項目のマーカーが変わる）は
            // 起きないはずだが、字下げ解除そのものが無反応になっていた可能性は
            // ある。診断のためデバッグログを追加した。
            bool parentOrderedFlg = a_parentList.MarkerStyle == TextMarkerStyle.Decimal;

            m_runAsProgrammaticChange(() =>
            {
                EditingCommands.DecreaseIndentation.Execute(null, m_editor);

                // a_li自身が（Blocksの2番目として）trailing siblingsをまとめた
                // 入れ子Listを持つことになった場合、そのMarkerStyle/Tagを
                // 復元する。CaretPositionから辿ることで、a_li自身のオブジェクト
                // 参照がコマンド実行後も同一であることに依存しないようにする
                // （IndentListItem側の修正と同じ考え方）。
                if (m_editor.CaretPosition?.Paragraph is Paragraph fp &&
                    fp.Parent is ListItem li &&
                    li.Blocks.Count > 1 &&
                    li.Blocks.LastBlock is List ownNestedList)
                {
                    // 2026-08-29追記（同日中の追加修正、v2.0.39.0）：IndentListItem側と同じ理由で
                    // 段数（GetListNestingDepth）に応じたマーカーに変更した（DESIGN.md参照）。
                    ownNestedList.MarkerStyle = parentOrderedFlg
                        ? TextMarkerStyle.Decimal
                        : BlockStyles.UnorderedMarkerStyleForDepth(GetListNestingDepth(ownNestedList));
                    ownNestedList.Tag = parentOrderedFlg ? null : ((a_parentList.Tag as string) ?? "*");
                    DebugLogger.Log("OutdentListItem: 字下げ解除成功（trailing siblingsを入れ子化）");
                }
                else
                {
                    DebugLogger.Log("OutdentListItem: DecreaseIndentationコマンドが構造を変更しなかった " +
                        "（字下げ解除失敗の可能性。AcceptsTab修正の効果を要確認）");
                }
            });
        }
    }
}
