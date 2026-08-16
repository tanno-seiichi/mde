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
            if (a_liRf != null)
            {
                a_parentListRf = a_liRf.Parent as List;
                return a_parentListRf != null;
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
                var newLiPara = new Paragraph();
                var newLi = new ListItem(newLiPara);

                if (prev is List prevList && (prevList.MarkerStyle == TextMarkerStyle.Decimal) == a_orderedFlg)
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
            m_editor.Focus();
        }

        /// <summary>リスト項目自身のテキストを取得する（入れ子のサブリストは含まない）。
        /// マーカー記号を拾ってしまうTextRangeではなく、Inlinesを直接読むため安全。</summary>
        /// <param name="a_li">調べる項目。</param>
        /// <returns>トリム済みのテキスト。画像だけがあり文字がない場合はゼロ幅スペース1文字。</returns>
        public string GetOwnListItemText(ListItem a_li)
        {
            var ownPara = a_li.Blocks.FirstBlock as Paragraph;
            if (ownPara == null) return "";
            var sb = new StringBuilder();
            AppendPlainInlineText(ownPara.Inlines, sb);
            string t = sb.ToString().Trim();
            if (t.Length == 0 && HasDescendantImage(ownPara)) return "\u200B";
            return t;
        }

        /// <summary>Inlinesコレクションの中の、Runのリテラルテキストだけを連結する
        /// （Spanは再帰的にたどり、LineBreakは改行として扱う）。TextRange.Textと違い、
        /// リスト項目のマーカー記号を文字として拾ってしまうことがない。</summary>
        private void AppendPlainInlineText(InlineCollection a_inlines, StringBuilder a_sb)
        {
            foreach (Inline inline in a_inlines)
            {
                if (inline is Run run) a_sb.Append(run.Text);
                else if (inline is Span span) AppendPlainInlineText(span.Inlines, a_sb);
                else if (inline is LineBreak) a_sb.Append('\n');
            }
        }

        private bool HasDescendantImage(Paragraph a_p)
        {
            foreach (Inline inline in a_p.Inlines)
                if (InlineContainsImage(inline)) return true;
            return false;
        }

        private bool InlineContainsImage(Inline a_inline)
        {
            if (a_inline is InlineUIContainer iuc && iuc.Child is Image) return true;
            if (a_inline is Span span)
                foreach (Inline child in span.Inlines)
                    if (InlineContainsImage(child)) return true;
            return false;
        }

        /// <summary>リスト項目内でのEnterキー処理。項目が空ならリストを抜けて新しい通常の段落を
        /// 作り、空でなければ新しい項目を後ろに追加する。</summary>
        /// <param name="a_li">現在の項目。</param>
        /// <param name="a_parentList">現在の項目を含むList。</param>
        public void HandleListEnter(ListItem a_li, List a_parentList)
        {
            m_originalTextTracker.InvalidateForBlock(a_parentList);
            m_runAsProgrammaticChange(() =>
            {
                bool hasNestedListFlg = a_li.Blocks.Count > 1;
                bool isEmptyFlg = !hasNestedListFlg && GetOwnListItemText(a_li).Length == 0;

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
                    if (a_parentList.ListItems.Count == 0)
                    {
                        RemoveEmptyList(a_parentList);
                    }
                    m_editor.CaretPosition = newPara.ContentStart;
                }
                else
                {
                    var newLi = new ListItem(new Paragraph());
                    var items = a_parentList.ListItems.Cast<ListItem>().ToList();
                    int idx = items.IndexOf(a_li);
                    a_parentList.ListItems.Clear();
                    for (int k = 0; k < items.Count; k++)
                    {
                        a_parentList.ListItems.Add(items[k]);
                        if (k == idx) a_parentList.ListItems.Add(newLi);
                    }
                    m_editor.CaretPosition = ((Paragraph)newLi.Blocks.FirstBlock).ContentStart;
                }
            });
            m_editor.Focus();
        }

        private void RemoveEmptyList(List a_list)
        {
            if (a_list.Parent is ListItem ownerLi)
                ownerLi.Blocks.Remove(a_list);
            else if (a_list.Parent is FlowDocument doc)
                doc.Blocks.Remove(a_list);
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
                if (item == a_li) break;
                prevLi = item;
            }
            if (prevLi == null) return; // 先頭の項目は字下げできない

            m_runAsProgrammaticChange(() =>
            {
                List nestedList = prevLi.Blocks.Count > 1 ? prevLi.Blocks.LastBlock as List : null;
                if (nestedList == null)
                {
                    bool parentOrderedFlg = a_parentList.MarkerStyle == TextMarkerStyle.Decimal;
                    nestedList = new List
                    {
                        MarkerStyle = parentOrderedFlg ? TextMarkerStyle.Decimal : TextMarkerStyle.Circle,
                        Tag = parentOrderedFlg ? null : ((a_parentList.Tag as string) ?? "*")
                    };
                    prevLi.Blocks.Add(nestedList);
                }
                a_parentList.ListItems.Remove(a_li);
                nestedList.ListItems.Add(a_li);

                if (a_li.Blocks.FirstBlock is Paragraph fp) m_editor.CaretPosition = fp.ContentEnd;
            });
            m_editor.Focus();
        }

        /// <summary>Shift+Tabキーでのリスト項目の字下げ解除。自分より後ろの兄弟項目も一緒に
        /// 自分の下の入れ子リストへ移してから、1段上の親リストへ昇格させる。</summary>
        /// <param name="a_li">字下げ解除する項目。</param>
        /// <param name="a_parentList">現在の（解除前の）親List。</param>
        public void OutdentListItem(ListItem a_li, List a_parentList)
        {
            m_originalTextTracker.InvalidateForBlock(a_parentList);
            if (!(a_parentList.Parent is ListItem parentLi)) return; // すでにトップレベル
            if (!(parentLi.Parent is List grandList)) return;

            m_runAsProgrammaticChange(() =>
            {
                var siblings = a_parentList.ListItems.Cast<ListItem>().ToList();
                int idx = siblings.IndexOf(a_li);
                var before = siblings.Take(idx).ToList();
                var after = siblings.Skip(idx + 1).ToList();

                a_parentList.ListItems.Clear();
                foreach (var b in before) a_parentList.ListItems.Add(b);

                if (after.Count > 0)
                {
                    bool parentOrderedFlg = a_parentList.MarkerStyle == TextMarkerStyle.Decimal;
                    List ownNested = new List
                    {
                        MarkerStyle = parentOrderedFlg ? TextMarkerStyle.Decimal : TextMarkerStyle.Circle,
                        Tag = parentOrderedFlg ? null : ((a_parentList.Tag as string) ?? "*")
                    };
                    foreach (var a in after) ownNested.ListItems.Add(a);
                    a_li.Blocks.Add(ownNested);
                }

                var grandItems = grandList.ListItems.Cast<ListItem>().ToList();
                int gIdx = grandItems.IndexOf(parentLi);
                grandList.ListItems.Clear();
                for (int k = 0; k < grandItems.Count; k++)
                {
                    grandList.ListItems.Add(grandItems[k]);
                    if (k == gIdx) grandList.ListItems.Add(a_li);
                }

                if (a_parentList.ListItems.Count == 0)
                {
                    parentLi.Blocks.Remove(a_parentList);
                }

                if (a_li.Blocks.FirstBlock is Paragraph fp) m_editor.CaretPosition = fp.ContentEnd;
            });
            m_editor.Focus();
        }
    }
}
