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
        /// <param name="a_p">変換する段落。先頭の記号（+スペース）部分だけを取り除き、それ以降に
        /// 既にあった内容（記述済みの行の先頭に後から記号を書き足した場合など）はそのまま
        /// リスト項目の内容として引き継ぐ。</param>
        /// <param name="a_marker">箇条書き記号（"*"または"-"）。順序付きリストの場合はnull。</param>
        /// <param name="a_orderedFlg">順序付きリストなら true、箇条書きなら false。</param>
        /// <param name="a_markerTextLength">段落先頭から取り除く記号部分の文字数（箇条書きなら
        /// 記号1文字＋スペース1文字で2、順序付きリストなら「数字＋ピリオド＋スペース」の
        /// 文字数）。</param>
        public void ConvertParagraphToListItem(Paragraph a_p, string a_marker, bool a_orderedFlg, int a_markerTextLength)
        {
            m_runAsProgrammaticChange(() =>
            {
                Block prev = a_p.PreviousBlock;
                Block next = a_p.NextBlock;

                // 変換のきっかけとなった記号部分だけを段落の先頭から取り除く。それ以降に
                // 既にあった内容（記述済みの行の先頭に後から記号を書き足した場合の、続きの
                // 文字列）は、書式ごとそのまま残る。
                TextPointer markerEnd = a_p.ContentStart.GetPositionAtOffset(a_markerTextLength) ?? a_p.ContentEnd;
                new TextRange(a_p.ContentStart, markerEnd).Text = "";

                // フォーカス・キャレットのある「生きた」Paragraphオブジェクトを、文書構造から
                // 一度外して別の親（ListItem）へそのまま付け替えると、IMEの内部状態と結び付いて
                // 壊れる可能性がある（このバグの切り分けで確認済み）ため、a_p自身を再利用する
                // のではなく、新しくParagraphを作り、a_pの中身（Inline、書式ごと）だけをそちらへ
                // 移し替える。a_p自体は文書へ一度も戻さず、ここで完全に破棄する。
                var newP = new Paragraph();
                BlockStyles.ApplyHeadingStyle(newP, 0);
                newP.Margin = new Thickness(0);
                foreach (var inline in a_p.Inlines.ToList())
                {
                    a_p.Inlines.Remove(inline);
                    newP.Inlines.Add(inline);
                }

                m_editor.Document.Blocks.Remove(a_p);
                var newLi = new ListItem(newP);

                if (prev is List prevList &&
                    (prevList.MarkerStyle == TextMarkerStyle.Decimal) == a_orderedFlg)
                {
                    prevList.ListItems.Add(newLi);
                }
                else
                {
                    var list = new List
                    {
                        MarkerStyle = a_orderedFlg ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                        Tag = a_orderedFlg ? null : a_marker
                    };
                    list.ListItems.Add(newLi);
                    if (null != next)
                    {
                        m_editor.Document.Blocks.InsertBefore(next, list);
                    }
                    else if (null != prev)
                    {
                        m_editor.Document.Blocks.InsertAfter(prev, list);
                    }
                    else
                    {
                        m_editor.Document.Blocks.Add(list);
                    }
                }
                m_editor.CaretPosition = newP.ContentStart;
            });
            FinishCaretMoveToNewParagraph();
        }

        /// <summary>リスト項目内の段落先頭に入力された"[ ] "/"[x] "をタスクリストのチェックボックスに
        /// 変換する（ライブ入力時用）。MarkdownConverter.BuildNestedListのバッチ変換と対になる処理。</summary>
        /// <param name="a_p">変換する段落（リスト項目直下のものであること）。</param>
        /// <param name="a_checked">"[x] "ならtrue、"[ ] "ならfalse。</param>
        public void ConvertListItemTextToTaskCheckbox(Paragraph a_p, bool a_checked)
        {
            m_runAsProgrammaticChange(() =>
            {
                a_p.Inlines.Clear();
                a_p.Inlines.Add(new InlineUIContainer(BlockStyles.CreateTaskCheckbox(a_checked)));
                m_editor.CaretPosition = a_p.ContentEnd;
            });
            FinishCaretMoveToNewParagraph();
        }

        /// <summary>
        /// その場で新しく作った段落へCaretPositionを合わせた直後に呼ぶ後始末処理。新しく作った
        /// （一度もレイアウトが計算されていない）段落へキャレットを合わせた直後にIME（日本語入力）で
        /// 入力を始めると、変換候補は表示されるのに確定した文字が反映されない不具合があるため、
        /// UpdateLayoutでレイアウトを確定させたうえで、Keyboard.ClearFocus()→Focus()により
        /// フォーカスを一旦外して当て直し、内部のTextEditor/IME関連付けを作り直す（単に
        /// Focus()を呼ぶだけでは、既にフォーカスがある場合は何もしないため効果がない）。
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
        /// 呼び出し元が判定できるようにするため）。</summary>
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

        /// <summary>指定したInlineが画像を含むかどうかを調べる。チェックボックス
        /// （InlineUIContainer＋CheckBox）は対象に含めない。含めてしまうと、文字を持たない
        /// タスク項目（チェックボックスだけの項目）がEnterキーで空項目とみなされず、
        /// リストを抜けられなくなるため。</summary>
        private bool InlineContainsImage(Inline a_inline)
        {
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
        /// 2段目なら2、以下同様）。
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

            // ネストしたリスト（2段目以降）でだけ、新しい項目を作った直後にIME入力を始めると
            // 変換候補が確定できなくなる不具合が確認されている。1段目（ネストしていない）では
            // 発生しないため、1段目は従来どおり同期的にドキュメントを書き換えてキャレットを
            // 移動し、ネストしたリストだけドキュメントの書き換え自体をキー入力の処理から切り
            // 離したタイミングで行う（ImeCaretMoveHelper.ScheduleCaretMove）。
            bool nestedFlg = a_parentList.Parent is ListItem;

            m_originalTextTracker.InvalidateForBlock(a_parentList);

            if (nestedFlg)
            {
                int depth = GetListNestingDepth(a_parentList);
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
                    a_cycleFocus: true);
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
        /// その直後（ContentEnd）へ、そうでなければ段落の先頭（ContentStart）へ合わせる。</summary>
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
                // ListItemCollectionはBlockCollection等と同じくInsertAfterを持つため、新しい
                // 項目1つをa_liの直後に挿入するだけで済み、既存の項目には触れない（Clear()して
                // 全項目を作り直す必要はない）。
                var newPara = new Paragraph { Margin = new Thickness(0) };
                // Enterを押す前の項目がタスクチェックボックス項目だった場合、新しい項目にも
                // 未チェックのチェックボックスをあらかじめ入れておく。
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
        /// 調べる（入れ子のサブリストは見ない）。</summary>
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

            // Listオブジェクトの構築（新規作成・複数のListコレクション間での項目の移動）を
            // mde側で手作りすると、その後そのList内で新しい項目を作成しIME入力を行った際、
            // 最初の1文字目の変換候補が確定されずに固まる不具合が起きる。そのため構造の変更
            // 自体はWPF標準の`EditingCommands.IncreaseIndentation`コマンドに任せ、mde独自の
            // 見た目・データ（ネストした階層を示すMarkerStyle・箇条書き文字を伝えるTag）は、
            // コマンド実行後にできあがった既存のListオブジェクトの**プロパティだけ**を変更する
            // 形で復元する。プロパティの復元は、実際にa_parentListとは別の新しいListオブジェクト
            // が作られた場合のみ行う（そうでない場合に復元すると、字下げされていない項目まで
            // 含むa_parentList自身のMarkerStyleを誤って書き換えてしまう）。
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
                    // マーカーは段数に応じて変える（1段目Disc・2段目Circle・3段目以降Box等）。
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

            // IndentListItemと同じ理由で、構造変更自体はWPF標準の
            // `EditingCommands.DecreaseIndentation`コマンドに任せる。
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
